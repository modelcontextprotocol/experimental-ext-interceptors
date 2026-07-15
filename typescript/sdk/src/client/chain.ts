// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The interceptor chain executor — the load-bearing runtime of the SDK.
 *
 * Trust-boundary-aware order (SEP-2624), dispatched on phase (RULE 2), never a
 * `switch`:
 *   - request  (sending):   mutations → validations
 *   - response (receiving): validations → mutations
 *
 * Mutations run sequentially by `resolvePriority` (ascending) with alphabetical
 * tie-break; validations run in parallel. `enforce` blocks; `audit` never does
 * (validators log, mutators compute a *shadow* payload that is NOT applied).
 * `failOpen` governs a crashed/timed-out interceptor (default: fail-closed →
 * block). Payloads are cloned before a mutator sees them
 * (snapshot-simulate-discard, RULE 26), so a handler cannot mutate chain state
 * in place. The executor is pure over an injected `invoke` — no transport here.
 */
import {
  CHAIN_STATUS,
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "../protocol/constants.js";
import { matchesEvent } from "../protocol/match-event.js";
import type {
  ChainStatus,
  InterceptionEvent,
  InterceptorPhase,
  InterceptorType,
  ValidationSeverity,
} from "../protocol/constants.js";
import { resolvePriority } from "../protocol/resolve-priority.js";
import type {
  Interceptor,
  InterceptorResult,
  InvokeContext,
  InvokeParams,
} from "../protocol/types.js";

/** Why a chain aborted. Reuses the interceptor types plus `timeout`. */
export const ABORT_KIND = {
  Validation: INTERCEPTOR_TYPE.Validation,
  Mutation: INTERCEPTOR_TYPE.Mutation,
  Timeout: "timeout",
} as const;
export type AbortKind = (typeof ABORT_KIND)[keyof typeof ABORT_KIND];

export interface AbortInfo {
  readonly interceptor: string;
  readonly reason: string;
  readonly kind: AbortKind;
}

export interface ValidationSummary {
  readonly errors: number;
  readonly warnings: number;
  readonly infos: number;
}

export interface ChainParams {
  readonly event: InterceptionEvent;
  readonly phase: InterceptorPhase;
  readonly payload: unknown;
  /** Restrict the chain to these interceptor names; null = all applicable. */
  readonly names: readonly string[] | null;
  readonly timeoutMs: number | null;
  readonly context: InvokeContext | null;
}

export interface ChainResult {
  readonly status: ChainStatus;
  readonly event: InterceptionEvent;
  readonly phase: InterceptorPhase;
  readonly results: readonly InterceptorResult[];
  readonly finalPayload: unknown;
  readonly validationSummary: ValidationSummary;
  readonly totalDurationMs: number;
  readonly abortedAt: AbortInfo | null;
}

/** Injected transport: run one interceptor and return its (normalized) result. */
export type InterceptorInvoker = (
  params: InvokeParams,
  signal: AbortSignal | null,
) => Promise<InterceptorResult>;

// ── selection primitives (RULE 9) ────────────────────────────────────────────

function isApplicable(descriptor: Interceptor, params: ChainParams): boolean {
  if (params.names !== null && !params.names.includes(descriptor.name)) {
    return false;
  }
  return descriptor.hooks.some(
    (h) => h.phase === params.phase && matchesEvent(h.events, params.event),
  );
}

function orderMutations(
  mutations: readonly Interceptor[],
  phase: InterceptorPhase,
): readonly Interceptor[] {
  return [...mutations].sort(
    (a, b) =>
      resolvePriority(a.priorityHint, phase) -
        resolvePriority(b.priorityHint, phase) || a.name.localeCompare(b.name),
  );
}

// ── invocation primitive ──────────────────────────────────────────────────────

function buildParams(
  descriptor: Interceptor,
  params: ChainParams,
  payload: unknown,
): InvokeParams {
  return {
    name: descriptor.name,
    event: params.event,
    phase: params.phase,
    payload,
    config: null,
    timeoutMs: params.timeoutMs,
    context: params.context,
  };
}

/**
 * The chain owns its deadline: every invocation races the abort signal, so a
 * hung or signal-ignoring interceptor can never stall the chain past timeout.
 */
function abortRace(signal: AbortSignal): Promise<never> {
  return new Promise((_resolve, reject) => {
    const abort = (): void =>
      reject(new DOMException("interceptor chain aborted", "AbortError"));
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
  });
}

async function invokeOne(
  invoke: InterceptorInvoker,
  descriptor: Interceptor,
  params: ChainParams,
  payload: unknown,
  signal: AbortSignal | null,
): Promise<InterceptorResult> {
  const started = Date.now();
  const invoked = invoke(buildParams(descriptor, params, payload), signal);
  const result =
    signal === null ? await invoked : await Promise.race([invoked, abortRace(signal)]);
  return {
    ...result,
    interceptor: descriptor.name,
    phase: params.phase,
    durationMs: Date.now() - started,
  };
}

function isAbort(err: unknown, signal: AbortSignal | null): boolean {
  if (signal?.aborted) return true;
  return err instanceof Error &&
    (err.name === "TimeoutError" || err.name === "AbortError");
}

function chainSignal(
  outer: AbortSignal | null,
  timeoutMs: number | null,
): AbortSignal | null {
  if (timeoutMs === null) return outer;
  const timeout = AbortSignal.timeout(timeoutMs);
  return outer === null ? timeout : AbortSignal.any([outer, timeout]);
}

// ── mutation step (sequential fold) ──────────────────────────────────────────

interface StepOutcome {
  readonly results: readonly InterceptorResult[];
  readonly payload: unknown;
  readonly abort: AbortInfo | null;
}

async function runMutations(
  mutations: readonly Interceptor[],
  invoke: InterceptorInvoker,
  params: ChainParams,
  initialPayload: unknown,
  signal: AbortSignal | null,
): Promise<StepOutcome> {
  const results: InterceptorResult[] = [];
  let payload = initialPayload;

  for (const descriptor of mutations) {
    const isAudit = descriptor.mode === INTERCEPTOR_MODE.Audit;
    try {
      // Snapshot: the handler receives a clone, never the live payload.
      const seen = payload === undefined ? payload : structuredClone(payload);
      const result = await invokeOne(invoke, descriptor, params, seen, signal);
      results.push(result);
      if (
        !isAudit &&
        result.type === INTERCEPTOR_TYPE.Mutation &&
        result.modified
      ) {
        payload = result.payload;
      }
    } catch (err) {
      if (isAbort(err, signal)) throw err;
      if (isAudit || descriptor.failOpen) continue;
      return {
        results,
        payload,
        abort: {
          interceptor: descriptor.name,
          reason: err instanceof Error ? err.message : String(err),
          kind: ABORT_KIND.Mutation,
        },
      };
    }
  }

  return { results, payload, abort: null };
}

// ── validation step (parallel) ───────────────────────────────────────────────

const BUMP: Record<ValidationSeverity, (s: MutableSummary) => void> = {
  [VALIDATION_SEVERITY.Info]: (s) => (s.infos += 1),
  [VALIDATION_SEVERITY.Warn]: (s) => (s.warnings += 1),
  [VALIDATION_SEVERITY.Error]: (s) => (s.errors += 1),
};

interface MutableSummary {
  errors: number;
  warnings: number;
  infos: number;
}

interface ValidationOutcome extends StepOutcome {
  readonly summary: ValidationSummary;
}

/** RULE 12: settled outcomes are a tagged union discriminated on `ok`. */
type Settled =
  | {
      readonly ok: true;
      readonly descriptor: Interceptor;
      readonly result: InterceptorResult;
    }
  | { readonly ok: false; readonly descriptor: Interceptor; readonly error: Error };

async function runValidations(
  validations: readonly Interceptor[],
  invoke: InterceptorInvoker,
  params: ChainParams,
  payload: unknown,
  signal: AbortSignal | null,
): Promise<ValidationOutcome> {
  const settled: Settled[] = await Promise.all(
    validations.map(async (descriptor): Promise<Settled> => {
      try {
        return {
          ok: true,
          descriptor,
          result: await invokeOne(invoke, descriptor, params, payload, signal),
        };
      } catch (err) {
        if (isAbort(err, signal)) throw err;
        return {
          ok: false,
          descriptor,
          error: err instanceof Error ? err : new Error(String(err)),
        };
      }
    }),
  );

  const summary: MutableSummary = { errors: 0, warnings: 0, infos: 0 };
  const results: InterceptorResult[] = [];
  let abort: AbortInfo | null = null;

  // Deterministic pass in declaration order; first enforce-blocker wins.
  for (const s of settled) {
    const isAudit = s.descriptor.mode === INTERCEPTOR_MODE.Audit;
    if (!s.ok) {
      if (!abort && !isAudit && !s.descriptor.failOpen) {
        abort = {
          interceptor: s.descriptor.name,
          reason: s.error.message,
          kind: ABORT_KIND.Validation,
        };
      }
      continue;
    }
    results.push(s.result);
    if (s.result.type !== INTERCEPTOR_TYPE.Validation) continue;
    for (const m of s.result.messages) BUMP[m.severity](summary);
    const blocks =
      !s.result.valid && s.result.severity === VALIDATION_SEVERITY.Error;
    if (!abort && !isAudit && blocks) {
      abort = {
        interceptor: s.descriptor.name,
        reason: s.result.messages[0]?.message ?? "validation failed",
        kind: ABORT_KIND.Validation,
      };
    }
  }

  return { results, payload, summary, abort };
}

// ── the executor ──────────────────────────────────────────────────────────────

/** Per-phase step order (RULE 2): the trust-boundary-aware sequence. */
const STEP_ORDER = {
  [INTERCEPTOR_PHASE.Request]: [INTERCEPTOR_TYPE.Mutation, INTERCEPTOR_TYPE.Validation],
  [INTERCEPTOR_PHASE.Response]: [INTERCEPTOR_TYPE.Validation, INTERCEPTOR_TYPE.Mutation],
} as const;

const ABORT_STATUS: Record<AbortKind, ChainStatus> = {
  [ABORT_KIND.Validation]: CHAIN_STATUS.ValidationFailed,
  [ABORT_KIND.Mutation]: CHAIN_STATUS.MutationFailed,
  [ABORT_KIND.Timeout]: CHAIN_STATUS.Timeout,
};

export async function executeChain(
  interceptors: readonly Interceptor[],
  invoke: InterceptorInvoker,
  params: ChainParams,
  outerSignal: AbortSignal | null = null,
): Promise<ChainResult> {
  const started = Date.now();
  const applicable = interceptors.filter((i) => isApplicable(i, params));
  const mutations = orderMutations(
    applicable.filter((i) => i.type === INTERCEPTOR_TYPE.Mutation),
    params.phase,
  );
  const validations = applicable.filter(
    (i) => i.type === INTERCEPTOR_TYPE.Validation,
  );
  const signal = chainSignal(outerSignal, params.timeoutMs);

  const results: InterceptorResult[] = [];
  let summary: ValidationSummary = { errors: 0, warnings: 0, infos: 0 };
  let payload = params.payload;
  let abort: AbortInfo | null = null;

  const runStep = async (kind: InterceptorType): Promise<boolean> => {
    if (kind === INTERCEPTOR_TYPE.Mutation) {
      const out = await runMutations(mutations, invoke, params, payload, signal);
      results.push(...out.results);
      payload = out.payload;
      abort = out.abort;
    } else {
      const out = await runValidations(validations, invoke, params, payload, signal);
      results.push(...out.results);
      summary = out.summary;
      abort = out.abort;
    }
    return abort === null;
  };

  try {
    for (const kind of STEP_ORDER[params.phase]) {
      const ok = await runStep(kind);
      if (!ok) break;
    }
  } catch (err) {
    if (isAbort(err, signal)) {
      abort = { interceptor: "", reason: "chain timed out", kind: ABORT_KIND.Timeout };
    } else {
      throw err;
    }
  }

  return {
    status: abort === null ? CHAIN_STATUS.Success : ABORT_STATUS[abort.kind],
    event: params.event,
    phase: params.phase,
    results,
    finalPayload: payload,
    validationSummary: summary,
    totalDurationMs: Date.now() - started,
    abortedAt: abort,
  };
}
