/**
 * The conformance RUNNER: replays fixtures against an ADAPTER and diffs
 * canonical JSON. The runner knows nothing about any SDK - everything
 * implementation-specific lives behind the ~4-method adapter contract
 * (ADAPTER.md), which is how Python/C#/Go SDKs reuse this suite without
 * re-authoring tests.
 *
 * Comparison model (identical in every language):
 *   1. scrub volatile keys (fixture.volatile) recursively from the ACTUAL;
 *   2. JCS-canonicalize the scrubbed actual;
 *   3. string-compare against the fixture's precomputed `canonical`.
 * Behavior expectations additionally check decision equality and
 * forbids/requires substrings over the canonical final payload.
 */
import { canonicalize } from "./canonical.ts";
import { isErrorExpectation, STEP_OP } from "./fixture-types.ts";
import type {
  ApplyStep,
  DecisionExpectation,
  Fixture,
  FixtureInterceptor,
  FixtureStep,
  InvokeStep,
  ListStep,
} from "./fixture-types.ts";

// ── the adapter contract (see ADAPTER.md for the prose version) ──────────────

export type InvokeOutcome =
  | { readonly ok: true; readonly result: unknown }
  | { readonly ok: false; readonly errorCode: number };

export interface ChainOutcome {
  /** `allow` iff the chain completed; `deny` iff it was blocked/aborted. */
  readonly decision: "allow" | "deny";
  /** The payload after all applied mutations (meaningful when allowed). */
  readonly finalPayload: unknown;
}

export interface AdapterSession {
  /** Wire JSON of `interceptors/list` (event filter or null). */
  readonly list: (event: string | null) => Promise<unknown>;
  /** Wire JSON of `interceptor/invoke`, or the JSON-RPC error code. */
  readonly invoke: (params: {
    readonly name: string;
    readonly event: string;
    readonly phase: "request" | "response";
    readonly payload: unknown;
  }) => Promise<InvokeOutcome>;
  /** One chain execution across ALL of the session's interceptors. */
  readonly chain: (params: {
    readonly event: string;
    readonly phase: "request" | "response";
    readonly payload: unknown;
    readonly sessionId: string | null;
  }) => Promise<ChainOutcome>;
}

export interface Adapter {
  readonly name: string;
  /** Fresh implementation state with these interceptors registered. */
  readonly createSession: (
    interceptors: readonly FixtureInterceptor[],
  ) => Promise<AdapterSession>;
}

// ── scrubbing + canonical comparison ─────────────────────────────────────────

/** Recursively drop volatile keys (timings, impl-specific info) from actuals. */
export function scrub(value: unknown, volatile: readonly string[]): unknown {
  if (Array.isArray(value)) return value.map((v) => scrub(v, volatile));
  if (typeof value === "object" && value !== null) {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .filter(([k]) => !volatile.includes(k))
        .map(([k, v]) => [k, scrub(v, volatile)]),
    );
  }
  return value;
}

interface Comparison {
  readonly passed: boolean;
  readonly detail: string;
}

function compareCanonical(
  actual: unknown,
  expectedCanonical: string,
  volatile: readonly string[],
): Comparison {
  const actualCanonical = canonicalize(scrub(actual, volatile));
  return actualCanonical === expectedCanonical
    ? { passed: true, detail: "canonical match" }
    : {
        passed: false,
        detail: `expected ${expectedCanonical}\n       actual ${actualCanonical}`,
      };
}

// ── per-op step execution (RULE 2: dispatch table, no switch) ────────────────

export interface StepReport {
  readonly index: number;
  readonly op: FixtureStep["op"];
  readonly passed: boolean;
  readonly detail: string;
}

type StepRunner = (
  step: FixtureStep,
  session: AdapterSession,
  fixture: Fixture,
  index: number,
) => Promise<StepReport>;

async function runListStep(
  step: ListStep,
  session: AdapterSession,
  fixture: Fixture,
  index: number,
): Promise<StepReport> {
  const actual = await session.list(step.event);
  const expected = step.expect.canonical ?? canonicalize(step.expect.result);
  const cmp = compareCanonical(actual, expected, fixture.volatile);
  return { index, op: step.op, ...cmp };
}

async function runInvokeStep(
  step: InvokeStep,
  session: AdapterSession,
  fixture: Fixture,
  index: number,
): Promise<StepReport> {
  const outcome = await session.invoke(step.params);
  if (isErrorExpectation(step.expect)) {
    const passed = !outcome.ok && outcome.errorCode === step.expect.errorCode;
    return {
      index,
      op: step.op,
      passed,
      detail: passed
        ? `error ${String(step.expect.errorCode)} as required`
        : `expected error ${String(step.expect.errorCode)}, got ${
            outcome.ok ? "a result" : String(outcome.errorCode)
          }`,
    };
  }
  if (!outcome.ok) {
    return {
      index,
      op: step.op,
      passed: false,
      detail: `expected a result, got error ${String(outcome.errorCode)}`,
    };
  }
  const expected = step.expect.canonical ?? canonicalize(step.expect.result);
  const cmp = compareCanonical(outcome.result, expected, fixture.volatile);
  return { index, op: step.op, ...cmp };
}

function checkDecision(
  expect: DecisionExpectation,
  outcome: ChainOutcome,
  volatile: readonly string[],
): Comparison {
  if (outcome.decision !== expect.decision) {
    return {
      passed: false,
      detail: `expected decision '${expect.decision}', got '${outcome.decision}'`,
    };
  }
  const finalCanonical =
    outcome.finalPayload === undefined
      ? "null"
      : canonicalize(scrub(outcome.finalPayload, volatile));
  if (expect.finalPayload !== undefined) {
    const expected =
      expect.finalPayloadCanonical ?? canonicalize(expect.finalPayload);
    if (finalCanonical !== expected) {
      return {
        passed: false,
        detail: `final payload mismatch:\n       expected ${expected}\n       actual ${finalCanonical}`,
      };
    }
  }
  const leaked = (expect.forbids ?? []).find((s) => finalCanonical.includes(s));
  if (leaked !== undefined) {
    return { passed: false, detail: `forbidden content present: ${leaked}` };
  }
  const missing = (expect.requires ?? []).find((s) => !finalCanonical.includes(s));
  if (missing !== undefined) {
    return { passed: false, detail: `required content absent: ${missing}` };
  }
  return { passed: true, detail: `decision '${expect.decision}' as required` };
}

async function runApplyStep(
  step: ApplyStep,
  session: AdapterSession,
  fixture: Fixture,
  index: number,
): Promise<StepReport> {
  const outcome = await session.chain({
    event: step.event,
    phase: step.phase,
    payload: step.payload,
    sessionId: step.context?.sessionId ?? null,
  });
  const cmp = checkDecision(step.expect, outcome, fixture.volatile);
  return { index, op: step.op, ...cmp };
}

const RUN_BY_OP: Record<FixtureStep["op"], StepRunner> = {
  [STEP_OP.List]: (step, ...rest) => runListStep(step as ListStep, ...rest),
  [STEP_OP.Invoke]: (step, ...rest) => runInvokeStep(step as InvokeStep, ...rest),
  [STEP_OP.Apply]: (step, ...rest) => runApplyStep(step as ApplyStep, ...rest),
};

// ── fixture + suite execution ────────────────────────────────────────────────

export interface FixtureReport {
  readonly id: string;
  readonly requirement: string;
  readonly passed: boolean;
  readonly steps: readonly StepReport[];
}

export interface ComplianceReport {
  readonly adapter: string;
  readonly total: number;
  readonly passed: number;
  /** passed / total, rounded to one decimal place. */
  readonly compliancePercent: number;
  readonly fixtures: readonly FixtureReport[];
}

export async function runFixture(
  fixture: Fixture,
  adapter: Adapter,
): Promise<FixtureReport> {
  const session = await adapter.createSession(fixture.interceptors);
  const steps: StepReport[] = [];
  for (const [index, step] of fixture.steps.entries()) {
    try {
      steps.push(await RUN_BY_OP[step.op](step, session, fixture, index));
    } catch (err) {
      steps.push({
        index,
        op: step.op,
        passed: false,
        detail: `adapter threw: ${err instanceof Error ? err.message : String(err)}`,
      });
    }
  }
  return {
    id: fixture.id,
    requirement: fixture.requirement,
    passed: steps.every((s) => s.passed),
    steps,
  };
}

export async function runSuite(
  fixtures: readonly Fixture[],
  adapter: Adapter,
): Promise<ComplianceReport> {
  const reports: FixtureReport[] = [];
  for (const fixture of fixtures) {
    reports.push(await runFixture(fixture, adapter));
  }
  const passed = reports.filter((r) => r.passed).length;
  return {
    adapter: adapter.name,
    total: reports.length,
    passed,
    compliancePercent:
      reports.length === 0 ? 100 : Math.round((passed / reports.length) * 1000) / 10,
    fixtures: reports,
  };
}

/** Human-readable report: per-fixture status and the compliance percentage. */
export function formatReport(report: ComplianceReport): string {
  const lines = report.fixtures.map((f) => {
    const mark = f.passed ? "PASS" : "FAIL";
    const failures = f.steps
      .filter((s) => !s.passed)
      .map((s) => `\n       step ${String(s.index)} (${s.op}): ${s.detail}`)
      .join("");
    return `  ${mark}  ${f.id} [${f.requirement}]${failures}`;
  });
  return [
    `MCP interceptors conformance - adapter: ${report.adapter}`,
    ...lines,
    `  ${String(report.passed)}/${String(report.total)} fixtures passed - compliance ${String(report.compliancePercent)}%`,
  ].join("\n");
}
