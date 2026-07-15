/**
 * The TypeScript reference ADAPTER: binds the conformance behavior vocabulary
 * to the reference SDK (`typescript/sdk`). This file is simultaneously
 * (a) the proof that every generated fixture is satisfiable, and
 * (b) the template a Python/C#/Go adapter ports — the language-specific part
 * is only the BEHAVIOR table plus the four session calls.
 */
import {
  block,
  CHAIN_STATUS,
  createCrossBoundaryGuard,
  createRegistry,
  createSecretlessRedactor,
  defineMutator,
  defineValidator,
  executeChain,
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  keep,
  pass,
  serializeInterceptor,
  serializeResult,
} from "@formalcore/mcp-interceptors-sdk";
import type {
  ChainParams,
  InvokeContext,
  RegisteredInterceptor,
} from "@formalcore/mcp-interceptors-sdk";
import { BEHAVIOR, DECISION, HOOK_PHASES } from "./fixture-types.ts";
import type { Behavior, FixtureInterceptor } from "./fixture-types.ts";
import type { Adapter, AdapterSession } from "./runner.ts";

// ── behavior bindings (RULE 2: the whole vocabulary, exhaustively) ───────────

/**
 * Common spec fields for `defineValidator`/`defineMutator`. Optional fields
 * are spread in only when present — the catalog's absence semantics survive
 * `exactOptionalPropertyTypes`.
 */
function common(i: FixtureInterceptor) {
  return {
    name: i.name,
    events: i.events,
    phases: i.phases,
    ...(i.mode === undefined ? {} : { mode: i.mode }),
    ...(i.failOpen === undefined ? {} : { failOpen: i.failOpen }),
  };
}

function note(payload: unknown): string | null {
  const args = (payload as { arguments?: { note?: unknown } } | null)?.arguments;
  return typeof args?.note === "string" ? args.note : null;
}

const BIND: Record<Behavior, (i: FixtureInterceptor) => RegisteredInterceptor> = {
  [BEHAVIOR.AllowAll]: (i) =>
    defineValidator({ ...common(i), validate: () => pass() }),
  [BEHAVIOR.DenyAll]: (i) =>
    defineValidator({ ...common(i), validate: () => block("denied by policy") }),
  [BEHAVIOR.RequireNote]: (i) =>
    defineValidator({
      ...common(i),
      validate: (p) =>
        note(p.payload) !== null && note(p.payload) !== ""
          ? pass()
          : block("note is required"),
    }),
  [BEHAVIOR.RequireUppercaseNote]: (i) =>
    defineValidator({
      ...common(i),
      validate: (p) => {
        const n = note(p.payload);
        return n !== null && n === n.toUpperCase()
          ? pass()
          : block("note must be uppercase");
      },
    }),
  [BEHAVIOR.UppercaseNote]: (i) =>
    defineMutator({
      ...common(i),
      mutate: (p) => {
        const n = note(p.payload);
        if (n === null) return keep();
        const payload = p.payload as { arguments: Record<string, unknown> };
        return {
          modified: true,
          payload: {
            ...payload,
            arguments: { ...payload.arguments, note: n.toUpperCase() },
          },
        };
      },
    }),
  [BEHAVIOR.Crash]: (i) =>
    defineValidator({
      ...common(i),
      validate: () => {
        throw new Error("interceptor crashed");
      },
    }),
  // The security behaviors are the SDK's reference security interceptors,
  // re-hooked to the fixture's name/events (chain identity is by name).
  [BEHAVIOR.CrossBoundaryGuard]: (i) => rebind(createCrossBoundaryGuard(), i),
  [BEHAVIOR.SecretlessRedactor]: (i) => rebind(createSecretlessRedactor(), i),
};

function rebind(
  entry: RegisteredInterceptor,
  i: FixtureInterceptor,
): RegisteredInterceptor {
  const hooks =
    i.phases === HOOK_PHASES.Both
      ? [
          { events: i.events, phase: INTERCEPTOR_PHASE.Request },
          { events: i.events, phase: INTERCEPTOR_PHASE.Response },
        ]
      : [
          {
            events: i.events,
            phase:
              i.phases === HOOK_PHASES.Request
                ? INTERCEPTOR_PHASE.Request
                : INTERCEPTOR_PHASE.Response,
          },
        ];
  return {
    descriptor: {
      ...entry.descriptor,
      name: i.name,
      version: null,
      description: null,
      hooks,
      mode: i.mode ?? INTERCEPTOR_MODE.Enforce,
      failOpen: i.failOpen ?? false,
    },
    handler: (params, signal) => entry.handler({ ...params, name: i.name }, signal),
  };
}

// ── the adapter ──────────────────────────────────────────────────────────────

function context(sessionId: string | null): InvokeContext | null {
  return sessionId === null
    ? null
    : { principal: null, traceId: null, spanId: null, timestamp: null, sessionId };
}

export const REFERENCE_ADAPTER: Adapter = {
  name: "typescript-reference-sdk",
  createSession: (interceptors) => {
    const registry = createRegistry(interceptors.map((i) => BIND[i.behavior](i)));
    const session: AdapterSession = {
      list: (event) =>
        Promise.resolve({
          interceptors: registry.list(event).map(serializeInterceptor),
        }),
      invoke: async (params) => {
        try {
          const result = await registry.invoke({
            ...params,
            config: null,
            timeoutMs: null,
            context: null,
          });
          return { ok: true, result: serializeResult(result) };
        } catch (err) {
          const code = (err as { code?: unknown }).code;
          return { ok: false, errorCode: typeof code === "number" ? code : -32603 };
        }
      },
      chain: async (params) => {
        const chainParams: ChainParams = {
          event: params.event,
          phase: params.phase,
          payload: params.payload,
          names: null,
          timeoutMs: null,
          context: context(params.sessionId),
        };
        const result = await executeChain(
          registry.descriptors,
          (p) => registry.invoke(p),
          chainParams,
        );
        return {
          decision:
            result.status === CHAIN_STATUS.Success ? DECISION.Allow : DECISION.Deny,
          finalPayload: result.finalPayload,
        };
      },
    };
    return Promise.resolve(session);
  },
};
