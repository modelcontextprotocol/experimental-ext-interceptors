/**
 * The conformance CATALOG - the single oracle for every generated fixture
 * (FUNCTIONAL_PATTERNS RULE 8/20/21: scenarios are typed literal data; the
 * suite is generated from them, never hand-authored per case).
 *
 * Expectations are DECLARATIVE. Protocol expectations state the exact SEP
 * wire JSON; the generator only derives mechanical forms from what is
 * declared here (JCS canonical strings via RFC-8785-style canonicalization,
 * and SEP descriptor wire shape via `wireDescriptor`, a data mapping defined
 * in THIS package). No expectation is produced by running any reference
 * implementation.
 *
 * Requirement tags trace each fixture to the SEP-2624 clause it certifies;
 * `manifest.json` is generated from them.
 */
import {
  BEHAVIOR,
  DECISION,
  FIXTURE_KIND,
  HOOK_PHASES,
  INTERCEPTOR_TYPE,
  MODE,
  PHASE,
  STEP_OP,
} from "./fixture-types.ts";
import type {
  DecisionExpectation,
  Fixture,
  FixtureInterceptor,
  FixtureStep,
} from "./fixture-types.ts";

// ── shared payload builders (pure data helpers) ──────────────────────────────

const EVENT_TOOLS_CALL = "tools/call";

function toolCall(tool: string, args: Readonly<Record<string, unknown>>): unknown {
  return { name: tool, arguments: args };
}

function toolResultText(text: string): unknown {
  return { content: [{ type: "text", text }] };
}

/**
 * SEP wire shape of a registered fixture interceptor, as `interceptors/list`
 * MUST return it: defaults (`active`, `failOpen:false`) omitted, absent
 * optionals omitted, `both` expanded request-then-response. This mapping is
 * conformance's own statement of the SEP serialization rules.
 */
export function wireDescriptor(i: FixtureInterceptor): Record<string, unknown> {
  const hooks =
    i.phases === HOOK_PHASES.Both
      ? [
          { events: i.events, phase: PHASE.Request },
          { events: i.events, phase: PHASE.Response },
        ]
      : [{ events: i.events, phase: i.phases }];
  return {
    name: i.name,
    type: i.type,
    hooks,
    ...(i.mode === MODE.Audit ? { mode: MODE.Audit } : {}),
    ...(i.failOpen === true ? { failOpen: true } : {}),
  };
}

// ── the fixture interceptor roster (RULE 8: shared data, declared once) ──────

const ALLOW_ALL: FixtureInterceptor = {
  name: "conf/allow-all",
  type: INTERCEPTOR_TYPE.Validation,
  behavior: BEHAVIOR.AllowAll,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Both,
};

const DENY_ALL: FixtureInterceptor = {
  name: "conf/deny-all",
  type: INTERCEPTOR_TYPE.Validation,
  behavior: BEHAVIOR.DenyAll,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Both,
};

const DENY_ALL_AUDIT: FixtureInterceptor = { ...DENY_ALL, mode: MODE.Audit };

const UPPERCASE_NOTE: FixtureInterceptor = {
  name: "conf/uppercase-note",
  type: INTERCEPTOR_TYPE.Mutation,
  behavior: BEHAVIOR.UppercaseNote,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Both,
};

const REQUIRE_UPPERCASE_NOTE: FixtureInterceptor = {
  name: "conf/require-uppercase-note",
  type: INTERCEPTOR_TYPE.Validation,
  behavior: BEHAVIOR.RequireUppercaseNote,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Both,
};

const WILDCARD_AUDITOR: FixtureInterceptor = {
  name: "conf/wildcard-auditor",
  type: INTERCEPTOR_TYPE.Validation,
  behavior: BEHAVIOR.AllowAll,
  events: ["*"],
  phases: HOOK_PHASES.Both,
};

const CRASH_CLOSED: FixtureInterceptor = {
  name: "conf/crash-closed",
  type: INTERCEPTOR_TYPE.Validation,
  behavior: BEHAVIOR.Crash,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Both,
};

const CRASH_OPEN: FixtureInterceptor = { ...CRASH_CLOSED, name: "conf/crash-open", failOpen: true };

const GUARD: FixtureInterceptor = {
  name: "security/cross-boundary-guard",
  type: INTERCEPTOR_TYPE.Validation,
  behavior: BEHAVIOR.CrossBoundaryGuard,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Both,
};

const REDACTOR: FixtureInterceptor = {
  name: "security/secretless-redactor",
  type: INTERCEPTOR_TYPE.Mutation,
  behavior: BEHAVIOR.SecretlessRedactor,
  events: [EVENT_TOOLS_CALL],
  phases: HOOK_PHASES.Request,
};

// ── canonical secrets (public credential FORMATS; clean-room examples) ───────

export interface CanonicalSecret {
  readonly formatId: string;
  readonly value: string;
}

const STRIPE_SECRET: CanonicalSecret = {
  formatId: "stripe_secret_live",
  value: "sk_live_4eC7aRm9Kx2bNw5pQj8sYd",
};
const GITHUB_PAT: CanonicalSecret = {
  formatId: "github_pat",
  value: "ghp_Xa2bC3dEf4gH5iJk6Lm7nN8oP",
};
const AWS_ACCESS_KEY: CanonicalSecret = {
  formatId: "aws_access_key",
  value: "AKIA5MZXN8QRF3WBY6OE",
};

export const CANONICAL_SECRETS: readonly CanonicalSecret[] = [
  STRIPE_SECRET,
  GITHUB_PAT,
  AWS_ACCESS_KEY,
] as const;

// ── expectation shorthands ───────────────────────────────────────────────────

const VOLATILE = ["durationMs", "totalDurationMs", "info"] as const;

function allow(constraints: Omit<DecisionExpectation, "decision"> = {}): DecisionExpectation {
  return { decision: DECISION.Allow, ...constraints };
}

function deny(): DecisionExpectation {
  return { decision: DECISION.Deny };
}

/** The exact SEP ValidationResult wire JSON for a pass, minus volatile keys. */
function validationPass(name: string, phase: (typeof PHASE)[keyof typeof PHASE]): unknown {
  return { interceptor: name, type: INTERCEPTOR_TYPE.Validation, phase, valid: true };
}

// ── relaybleed steps (shared by the per-format behavior fixtures) ────────────

/**
 * The canonical composed exfiltration: read a secret from the filesystem
 * server, then attempt to send it to the sqlite server. Every single call is
 * individually authorized; only the cross-call, cross-server flow is the
 * violation. A conformant security interceptor MUST deny step 3.
 */
function relaybleedSteps(secret: string, sessionId: string): readonly FixtureStep[] {
  return [
    {
      op: STEP_OP.Apply,
      event: EVENT_TOOLS_CALL,
      phase: PHASE.Request,
      payload: toolCall("read_file", { path: "/workspace/.env" }),
      context: { sessionId },
      expect: allow(),
    },
    {
      op: STEP_OP.Apply,
      event: EVENT_TOOLS_CALL,
      phase: PHASE.Response,
      payload: toolResultText(`STRIPE_KEY=${secret}`),
      context: { sessionId },
      expect: allow(),
    },
    {
      op: STEP_OP.Apply,
      event: EVENT_TOOLS_CALL,
      phase: PHASE.Request,
      payload: toolCall("write_query", {
        query: `INSERT INTO notes (body) VALUES ('${secret}')`,
      }),
      context: { sessionId },
      expect: deny(),
    },
  ];
}

// ── the catalog ──────────────────────────────────────────────────────────────

const PROTOCOL_FIXTURES: readonly Fixture[] = [
  {
    id: "protocol/list-all",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptors-list",
    description:
      "interceptors/list with no filter returns every registered descriptor, in registration order, in SEP wire shape (defaults omitted).",
    interceptors: [ALLOW_ALL, UPPERCASE_NOTE],
    steps: [
      {
        op: STEP_OP.List,
        event: null,
        expect: {
          result: { interceptors: [wireDescriptor(ALLOW_ALL), wireDescriptor(UPPERCASE_NOTE)] },
        },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/list-filtered-by-event",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptors-list.event-filter",
    description:
      "interceptors/list?event= returns only interceptors hooking that event.",
    interceptors: [ALLOW_ALL, WILDCARD_AUDITOR],
    steps: [
      {
        op: STEP_OP.List,
        event: "prompts/get",
        expect: {
          result: { interceptors: [wireDescriptor(WILDCARD_AUDITOR)] },
        },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/invoke-validation-pass",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptor-invoke.validation",
    description:
      "interceptor/invoke of a passing validator returns a SEP ValidationResult with valid=true and no severity.",
    interceptors: [ALLOW_ALL],
    steps: [
      {
        op: STEP_OP.Invoke,
        params: {
          name: ALLOW_ALL.name,
          event: EVENT_TOOLS_CALL,
          phase: PHASE.Request,
          payload: toolCall("echo", { note: "hi" }),
        },
        expect: { result: validationPass(ALLOW_ALL.name, PHASE.Request) },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/invoke-validation-deny",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptor-invoke.validation",
    description:
      "interceptor/invoke of a denying validator returns valid=false, severity=error, and the exact message.",
    interceptors: [DENY_ALL],
    steps: [
      {
        op: STEP_OP.Invoke,
        params: {
          name: DENY_ALL.name,
          event: EVENT_TOOLS_CALL,
          phase: PHASE.Request,
          payload: toolCall("echo", { note: "hi" }),
        },
        expect: {
          result: {
            interceptor: DENY_ALL.name,
            type: INTERCEPTOR_TYPE.Validation,
            phase: PHASE.Request,
            valid: false,
            severity: "error",
            messages: [{ message: "denied by policy", severity: "error" }],
          },
        },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/invoke-mutation-modified",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptor-invoke.mutation",
    description:
      "interceptor/invoke of a mutator that changes the payload returns modified=true and the transformed payload.",
    interceptors: [UPPERCASE_NOTE],
    steps: [
      {
        op: STEP_OP.Invoke,
        params: {
          name: UPPERCASE_NOTE.name,
          event: EVENT_TOOLS_CALL,
          phase: PHASE.Request,
          payload: toolCall("echo", { note: "hello" }),
        },
        expect: {
          result: {
            interceptor: UPPERCASE_NOTE.name,
            type: INTERCEPTOR_TYPE.Mutation,
            phase: PHASE.Request,
            modified: true,
            payload: toolCall("echo", { note: "HELLO" }),
          },
        },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/invoke-mutation-unmodified",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptor-invoke.mutation",
    description:
      "interceptor/invoke of a mutator that declines returns modified=false with the original payload passed through.",
    interceptors: [UPPERCASE_NOTE],
    steps: [
      {
        op: STEP_OP.Invoke,
        params: {
          name: UPPERCASE_NOTE.name,
          event: EVENT_TOOLS_CALL,
          phase: PHASE.Request,
          payload: toolCall("echo", { other: 1 }),
        },
        expect: {
          result: {
            interceptor: UPPERCASE_NOTE.name,
            type: INTERCEPTOR_TYPE.Mutation,
            phase: PHASE.Request,
            modified: false,
            payload: toolCall("echo", { other: 1 }),
          },
        },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/invoke-unknown-name",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/interceptor-invoke.unknown-name",
    description:
      "interceptor/invoke of an unregistered name fails with JSON-RPC invalid params (-32602).",
    interceptors: [ALLOW_ALL],
    steps: [
      {
        op: STEP_OP.Invoke,
        params: {
          name: "conf/does-not-exist",
          event: EVENT_TOOLS_CALL,
          phase: PHASE.Request,
          payload: {},
        },
        expect: { errorCode: -32602 },
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-request-order-mutate-then-validate",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.request-order",
    description:
      "Request phase runs mutations BEFORE validations: the validator must see the mutated payload (allow); validating first would deny.",
    interceptors: [REQUIRE_UPPERCASE_NOTE, UPPERCASE_NOTE],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("echo", { note: "hello" }),
        context: null,
        expect: allow({ finalPayload: toolCall("echo", { note: "HELLO" }) }),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-response-order-validate-then-mutate",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.response-order",
    description:
      "Response phase runs validations BEFORE mutations: the validator must see the raw payload (deny); mutating first would allow.",
    interceptors: [REQUIRE_UPPERCASE_NOTE, UPPERCASE_NOTE],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Response,
        payload: toolCall("echo", { note: "hello" }),
        context: null,
        expect: deny(),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-validation-blocks",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.active-blocks",
    description: "An active-mode validator returning severity=error aborts the chain.",
    interceptors: [DENY_ALL],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("echo", { note: "hi" }),
        context: null,
        expect: deny(),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-audit-never-blocks",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.audit-nonblocking",
    description:
      "An audit-mode validator NEVER blocks: the same denial that aborts in active mode passes through in audit mode.",
    interceptors: [DENY_ALL_AUDIT],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("echo", { note: "hi" }),
        context: null,
        expect: allow({ finalPayload: toolCall("echo", { note: "hi" }) }),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-audit-mutation-shadowed",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.audit-nonblocking",
    description:
      "An audit-mode mutator computes but MUST NOT apply: the final payload is unchanged.",
    interceptors: [{ ...UPPERCASE_NOTE, mode: MODE.Audit }],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("echo", { note: "hello" }),
        context: null,
        expect: allow({ finalPayload: toolCall("echo", { note: "hello" }) }),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-fail-closed",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.fail-closed-default",
    description:
      "A crashing interceptor with failOpen unset (default false) blocks the chain.",
    interceptors: [CRASH_CLOSED],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("echo", { note: "hi" }),
        context: null,
        expect: deny(),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "protocol/chain-fail-open",
    kind: FIXTURE_KIND.Protocol,
    requirement: "SEP-2624/chain.fail-open",
    description: "A crashing interceptor with failOpen=true is skipped; the chain proceeds.",
    interceptors: [CRASH_OPEN],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("echo", { note: "hi" }),
        context: null,
        expect: allow({ finalPayload: toolCall("echo", { note: "hi" }) }),
      },
    ],
    volatile: [...VOLATILE],
  },
] as const;

const BEHAVIOR_FIXTURES: readonly Fixture[] = [
  // The canonical relaybleed fixture, generated per public credential format
  // (RULE 20/21: one scenario shape × the format table).
  ...CANONICAL_SECRETS.map(
    (secret): Fixture => ({
      id: `behavior/relaybleed-${secret.formatId}`,
      kind: FIXTURE_KIND.Behavior,
      requirement: "SEC/cross-boundary-exfiltration",
      description:
        `relaybleed (${secret.formatId}): a secret read from the filesystem server must not ` +
        "be sent to the sqlite server - every individual call is authorized; the composed " +
        "cross-server flow MUST be denied.",
      interceptors: [GUARD],
      steps: relaybleedSteps(secret.value, `relaybleed-${secret.formatId}`),
      volatile: [...VOLATILE],
    }),
  ),
  {
    id: "behavior/same-origin-writeback-allowed",
    kind: FIXTURE_KIND.Behavior,
    requirement: "SEC/cross-boundary-exfiltration.same-origin",
    description:
      "Writing a secret back to the server it was read from is NOT a cross-boundary flow and must be allowed (no false positive).",
    interceptors: [GUARD],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("read_file", { path: "/workspace/.env" }),
        context: { sessionId: "same-origin" },
        expect: allow(),
      },
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Response,
        payload: toolResultText(`KEY=${STRIPE_SECRET.value}`),
        context: { sessionId: "same-origin" },
        expect: allow(),
      },
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("write_file", {
          path: "/workspace/.env.bak",
          content: STRIPE_SECRET.value,
        }),
        context: { sessionId: "same-origin" },
        expect: allow(),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "behavior/no-prior-read-allowed",
    kind: FIXTURE_KIND.Behavior,
    requirement: "SEC/cross-boundary-exfiltration.causality",
    description:
      "A secret-shaped value sent WITHOUT a strictly-prior cross-boundary read must be allowed: the guard tracks flows, not values (no false positive).",
    interceptors: [GUARD],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("write_query", {
          query: `INSERT INTO notes (body) VALUES ('${GITHUB_PAT.value}')`,
        }),
        context: { sessionId: "no-prior-read" },
        expect: allow(),
      },
    ],
    volatile: [...VOLATILE],
  },
  {
    id: "behavior/session-isolation",
    kind: FIXTURE_KIND.Behavior,
    requirement: "SEC/cross-boundary-exfiltration.session-isolation",
    description:
      "Taint recorded in one session must not affect another: session B may send a value session A read (no cross-session bleed).",
    interceptors: [GUARD],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("read_file", { path: "/workspace/.env" }),
        context: { sessionId: "session-a" },
        expect: allow(),
      },
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Response,
        payload: toolResultText(AWS_ACCESS_KEY.value),
        context: { sessionId: "session-a" },
        expect: allow(),
      },
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("write_query", { query: AWS_ACCESS_KEY.value }),
        context: { sessionId: "session-b" },
        expect: allow(),
      },
    ],
    volatile: [...VOLATILE],
  },
  // Redaction, generated per public credential format.
  ...CANONICAL_SECRETS.map(
    (secret): Fixture => ({
      id: `behavior/redaction-${secret.formatId}`,
      kind: FIXTURE_KIND.Behavior,
      requirement: "SEC/outbound-secret-redaction",
      description:
        `redaction (${secret.formatId}): an outbound payload carrying a verbatim secret must ` +
        "leave the chain WITHOUT the secret (handle format is implementation-defined; the " +
        "absence of the verbatim value is the requirement).",
      interceptors: [REDACTOR],
      steps: [
        {
          op: STEP_OP.Apply,
          event: EVENT_TOOLS_CALL,
          phase: PHASE.Request,
          payload: toolCall("write_query", {
            query: `INSERT INTO notes (body) VALUES ('${secret.value}')`,
          }),
          context: { sessionId: `redaction-${secret.formatId}` },
          expect: allow({ forbids: [secret.value] }),
        },
      ],
      volatile: [...VOLATILE],
    }),
  ),
  {
    id: "behavior/redaction-defuses-relaybleed",
    kind: FIXTURE_KIND.Behavior,
    requirement: "SEC/outbound-secret-redaction.composition",
    description:
      "Guard + redactor composed: request-phase mutation order means the redactor strips the secret BEFORE the guard checks, so the flow is allowed WITHOUT the verbatim secret leaving.",
    interceptors: [GUARD, REDACTOR],
    steps: [
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("read_file", { path: "/workspace/.env" }),
        context: { sessionId: "compose" },
        expect: allow(),
      },
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Response,
        payload: toolResultText(STRIPE_SECRET.value),
        context: { sessionId: "compose" },
        expect: allow(),
      },
      {
        op: STEP_OP.Apply,
        event: EVENT_TOOLS_CALL,
        phase: PHASE.Request,
        payload: toolCall("write_query", {
          query: `INSERT INTO notes (body) VALUES ('${STRIPE_SECRET.value}')`,
        }),
        context: { sessionId: "compose" },
        expect: allow({ forbids: [STRIPE_SECRET.value] }),
      },
    ],
    volatile: [...VOLATILE],
  },
] as const;

export const CATALOG: readonly Fixture[] = [...PROTOCOL_FIXTURES, ...BEHAVIOR_FIXTURES];
