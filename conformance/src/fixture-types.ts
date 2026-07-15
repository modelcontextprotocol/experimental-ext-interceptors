/**
 * The language-neutral conformance fixture format (SEP-2624 interceptors).
 *
 * These types are the TypeScript view of `schema/fixture.schema.json`; the
 * JSON files under `fixtures/` are the artifact other SDKs consume. Fixtures
 * are GENERATED from the catalog (catalog.ts) - never edited by hand - and
 * the catalog states expectations declaratively: it is the oracle, not any
 * reference implementation.
 *
 * FUNCTIONAL_PATTERNS RULE 1/5: every finite vocabulary is a const object
 * with a derived union type. RULE 12: steps and expectations are tagged
 * unions (`op` / decision-vs-result shape).
 */

// ── vocabularies ─────────────────────────────────────────────────────────────

export const FIXTURE_KIND = {
  /** Exact wire-protocol byte agreement: list / invoke / chain semantics. */
  Protocol: "protocol",
  /** Security behavior a conformant security interceptor MUST exhibit. */
  Behavior: "behavior",
} as const;
export type FixtureKind = (typeof FIXTURE_KIND)[keyof typeof FIXTURE_KIND];

export const STEP_OP = {
  /** `interceptors/list` (optionally filtered by event). */
  List: "list",
  /** `interceptor/invoke` of one named interceptor. */
  Invoke: "invoke",
  /** One chain execution over the fixture's interceptors. */
  Chain: "chain",
} as const;
export type StepOp = (typeof STEP_OP)[keyof typeof STEP_OP];

export const PHASE = { Request: "request", Response: "response" } as const;
export type Phase = (typeof PHASE)[keyof typeof PHASE];

export const HOOK_PHASES = {
  Request: "request",
  Response: "response",
  Both: "both",
} as const;
export type HookPhases = (typeof HOOK_PHASES)[keyof typeof HOOK_PHASES];

export const MODE = { Enforce: "enforce", Audit: "audit" } as const;
export type Mode = (typeof MODE)[keyof typeof MODE];

export const DECISION = { Allow: "allow", Deny: "deny" } as const;
export type Decision = (typeof DECISION)[keyof typeof DECISION];

/**
 * The closed set of named behaviors an adapter binds (ADAPTER.md defines each
 * one's exact semantics in prose; an adapter implements them in its language).
 */
export const BEHAVIOR = {
  /** Validator: always valid. */
  AllowAll: "allow-all",
  /** Validator: always invalid, severity error, message exactly "denied by policy". */
  DenyAll: "deny-all",
  /** Validator: valid iff `arguments.note` is a non-empty string; else error "note is required". */
  RequireNote: "require-note",
  /** Validator: valid iff `arguments.note` equals its own uppercase; else error "note must be uppercase". */
  RequireUppercaseNote: "require-uppercase-note",
  /** Mutator: uppercases string `arguments.note` (modified=true); otherwise modified=false. */
  UppercaseNote: "uppercase-note",
  /** Handler throws an error (message free-form). Exercises failOpen semantics. */
  Crash: "crash",
  /** The implementation's cross-boundary security VALIDATOR (see ADAPTER.md §security). */
  CrossBoundaryGuard: "cross-boundary-guard",
  /** The implementation's secret-redaction MUTATOR (see ADAPTER.md §security). */
  SecretlessRedactor: "secretless-redactor",
} as const;
export type Behavior = (typeof BEHAVIOR)[keyof typeof BEHAVIOR];

export const INTERCEPTOR_TYPE = {
  Validation: "validation",
  Mutation: "mutation",
} as const;
export type InterceptorType =
  (typeof INTERCEPTOR_TYPE)[keyof typeof INTERCEPTOR_TYPE];

// ── fixture setup ────────────────────────────────────────────────────────────

/** Declarative interceptor the adapter must register before replaying steps. */
export interface FixtureInterceptor {
  readonly name: string;
  readonly type: InterceptorType;
  readonly behavior: Behavior;
  readonly events: readonly string[];
  readonly phases: HookPhases;
  /** Omitted on the wire when the default (`enforce`). */
  readonly mode?: Mode;
  /** Omitted on the wire when the default (`false`). */
  readonly failOpen?: boolean;
}

// ── step expectations (tagged unions) ────────────────────────────────────────

/** Exact-bytes expectation: actual must JCS-canonicalize to `canonical`. */
export interface ResultExpectation {
  readonly result: unknown;
  /**
   * JCS canonical form of `result`. COMPUTED BY THE GENERATOR (absent in the
   * catalog, always present in emitted fixture JSON) so runners in any
   * language compare one string instead of re-implementing deep equality.
   */
  readonly canonical?: string;
}

/** JSON-RPC error expectation (e.g. invoking an unknown interceptor name). */
export interface ErrorExpectation {
  readonly errorCode: number;
}

/**
 * Declarative behavior expectation. `finalPayload` (exact bytes) is only
 * stated where the payload is deterministic across implementations;
 * `forbids` / `requires` assert on the JCS canonical form of the actual
 * payload without prescribing implementation-specific bytes (e.g. redaction
 * handle formats).
 */
export interface DecisionExpectation {
  readonly decision: Decision;
  readonly finalPayload?: unknown;
  /** JCS canonical form of `finalPayload`; computed by the generator. */
  readonly finalPayloadCanonical?: string;
  /** Substrings that MUST NOT appear in the canonicalized final payload. */
  readonly forbids?: readonly string[];
  /** Substrings that MUST appear in the canonicalized final payload. */
  readonly requires?: readonly string[];
}

// ── steps ────────────────────────────────────────────────────────────────────

export interface ListStep {
  readonly op: (typeof STEP_OP)["List"];
  /** Event filter; null lists everything. */
  readonly event: string | null;
  readonly expect: ResultExpectation;
}

export interface InvokeStep {
  readonly op: (typeof STEP_OP)["Invoke"];
  readonly params: {
    readonly name: string;
    readonly event: string;
    readonly phase: Phase;
    readonly payload: unknown;
  };
  readonly expect: ResultExpectation | ErrorExpectation;
}

export interface ChainStep {
  readonly op: (typeof STEP_OP)["Chain"];
  readonly event: string;
  readonly phase: Phase;
  readonly payload: unknown;
  /** Session correlation for stateful (security) interceptors; null = stateless. */
  readonly context: { readonly sessionId: string } | null;
  readonly expect: DecisionExpectation;
}

export type FixtureStep = ListStep | InvokeStep | ChainStep;

// ── the fixture envelope ─────────────────────────────────────────────────────

export interface Fixture {
  /** Stable id, also the fixture's path: `fixtures/<kind>/<slug(id)>.json`. */
  readonly id: string;
  readonly kind: FixtureKind;
  /** SEP-2624 requirement tag this fixture certifies (see manifest.json). */
  readonly requirement: string;
  readonly description: string;
  readonly interceptors: readonly FixtureInterceptor[];
  /** Replayed in order against ONE adapter session (state carries across steps). */
  readonly steps: readonly FixtureStep[];
  /** Keys scrubbed (recursively) from actual results before comparison. */
  readonly volatile: readonly string[];
}

/** Traceability manifest: every fixture, its file, and its requirement. */
export interface ManifestEntry {
  readonly id: string;
  readonly kind: FixtureKind;
  readonly file: string;
  readonly requirement: string;
  readonly description: string;
}

export interface Manifest {
  readonly suite: string;
  readonly version: string;
  readonly sep: string;
  readonly generatedBy: string;
  readonly fixtures: readonly ManifestEntry[];
}

// ── shared helpers ───────────────────────────────────────────────────────────

export function isErrorExpectation(
  e: ResultExpectation | ErrorExpectation,
): e is ErrorExpectation {
  return "errorCode" in e;
}

/** `protocol/list-all` → `list-all.json` under its kind directory. */
export function fixtureFile(fixture: Pick<Fixture, "id" | "kind">): string {
  const slug = fixture.id.replace(`${fixture.kind}/`, "");
  return `fixtures/${fixture.kind}/${slug}.json`;
}
