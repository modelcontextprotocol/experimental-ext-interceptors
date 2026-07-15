/**
 * Fixture GENERATOR: catalog (typed data) → pure-JSON golden fixtures +
 * traceability manifest. RULE 20/21: the suite is generated, one fixture per
 * catalog entry; nothing under `fixtures/` is hand-authored.
 *
 * Canonical strings are derived with the RFC-8785-aligned `canonicalize` from
 * `@formalcore/mcp-attested-validation` — the SAME canonicalization that
 * attested validation signs over — so byte-equality is one string comparison
 * in any language, and a runner never needs deep-equality logic.
 *
 * The generator is deterministic: same catalog → identical bytes. The
 * conformance meta-test regenerates in-memory and diffs against disk, so a
 * stale or edited fixture fails CI.
 */
import { canonicalize } from "@formalcore/mcp-attested-validation";
import { CATALOG } from "./catalog.ts";
import { fixtureFile, isErrorExpectation, STEP_OP } from "./fixture-types.ts";
import type {
  Fixture,
  FixtureStep,
  Manifest,
} from "./fixture-types.ts";

export const SUITE_VERSION = "0.1.0";
export const SEP_REFERENCE =
  "https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624";

// ── canonical-string enrichment (RULE 2: per-op handlers, no switch) ─────────

type StepEnricher = (step: FixtureStep) => FixtureStep;

const ENRICH_BY_OP: Record<FixtureStep["op"], StepEnricher> = {
  [STEP_OP.List]: (step) =>
    step.op !== STEP_OP.List
      ? step
      : {
          ...step,
          expect: { ...step.expect, canonical: canonicalize(step.expect.result) },
        },
  [STEP_OP.Invoke]: (step) =>
    step.op !== STEP_OP.Invoke || isErrorExpectation(step.expect)
      ? step
      : {
          ...step,
          expect: { ...step.expect, canonical: canonicalize(step.expect.result) },
        },
  [STEP_OP.Chain]: (step) =>
    step.op !== STEP_OP.Chain || step.expect.finalPayload === undefined
      ? step
      : {
          ...step,
          expect: {
            ...step.expect,
            finalPayloadCanonical: canonicalize(step.expect.finalPayload),
          },
        },
};

function enrich(fixture: Fixture): Fixture {
  return { ...fixture, steps: fixture.steps.map((s) => ENRICH_BY_OP[s.op](s)) };
}

// ── emission ─────────────────────────────────────────────────────────────────

export interface GeneratedFile {
  /** Path relative to the conformance package root. */
  readonly path: string;
  /** Exact file contents (pretty JSON + trailing newline). */
  readonly contents: string;
}

function render(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

export function generateManifest(): Manifest {
  return {
    suite: "mcp-interceptors-conformance",
    version: SUITE_VERSION,
    sep: SEP_REFERENCE,
    generatedBy: "conformance/src/generate.ts (from src/catalog.ts)",
    fixtures: CATALOG.map((f) => ({
      id: f.id,
      kind: f.kind,
      file: fixtureFile(f),
      requirement: f.requirement,
      description: f.description,
    })),
  };
}

/** Every file the suite ships: one JSON per catalog entry + the manifest. */
export function generateFiles(): readonly GeneratedFile[] {
  const fixtures = CATALOG.map((f): GeneratedFile => {
    const enriched = enrich(f);
    return { path: fixtureFile(f), contents: render(enriched) };
  });
  return [
    ...fixtures,
    { path: "manifest.json", contents: render(generateManifest()) },
  ];
}
