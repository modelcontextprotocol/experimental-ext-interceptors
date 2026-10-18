// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

import { InterceptionEvents } from '../protocol/constants.js';
import { InterceptorOverrideHookError } from '../protocol/errors.js';
import { resolvePriority } from '../protocol/resolve-priority.js';
import {
  isMutationResult,
  isValidationResult,
} from '../protocol/results.js';
import type {
  ChainAbortInfo,
  ChainInterceptorEntry,
  ChainValidationSummary,
  ExecuteChainRequestParams,
  Interceptor,
  InterceptorChainResult,
  InterceptorChainStatus,
  InterceptorHook,
  InterceptorResult,
  InvokeInterceptorRequestParams,
  SinkInterceptorResult,
} from '../protocol/types.js';

export type InterceptorInvoker = (
  params: InvokeInterceptorRequestParams,
  signal?: AbortSignal,
) => Promise<InterceptorResult>;

export function matchesEvent(hookEvents: string[], requestEvent: string): boolean {
  for (const ev of hookEvents) {
    if (ev === InterceptionEvents.All || ev === requestEvent) {
      return true;
    }
  }
  return false;
}

/** Entry with invoker policy resolved against the descriptor's declared defaults. */
interface ResolvedChainEntry {
  descriptor: Interceptor;
  isAudit: boolean;
  failOpen: boolean;
  /** Per-interceptor timeout (override only); the chain timeout is separate. */
  timeoutMs?: number;
  priority: number;
  hooks: InterceptorHook[];
}

function assertHooksNarrow(descriptor: Interceptor, overrideHooks: InterceptorHook[]): void {
  for (const oh of overrideHooks) {
    const declared = descriptor.hooks.filter((h) => h.phase === oh.phase);
    for (const ev of oh.events) {
      const covered = declared.some(
        (h) => h.events.includes(ev) || (ev !== InterceptionEvents.All && matchesEvent(h.events, ev)),
      );
      if (!covered) {
        throw new InterceptorOverrideHookError(
          descriptor.name,
          `event '${ev}' (phase '${oh.phase}') is not declared`,
        );
      }
    }
  }
}

function resolveEntries(
  interceptors: Array<Interceptor | ChainInterceptorEntry>,
  chainParams: ExecuteChainRequestParams,
): ResolvedChainEntry[] {
  const nameFilter = chainParams.interceptors;
  const out: ResolvedChainEntry[] = [];

  for (const item of interceptors) {
    const entry: ChainInterceptorEntry = 'interceptor' in item ? item : { interceptor: item };
    const descriptor = entry.interceptor;
    const overrides = entry.overrides;

    if (overrides?.hooks) {
      assertHooksNarrow(descriptor, overrides.hooks);
    }

    if (nameFilter && nameFilter.length > 0 && !nameFilter.includes(descriptor.name)) {
      continue;
    }

    const hooks = overrides?.hooks ?? descriptor.hooks;
    const matchesHook = hooks.some(
      (hook) => hook.phase === chainParams.phase && matchesEvent(hook.events, chainParams.event),
    );
    if (!matchesHook) {
      continue;
    }

    out.push({
      descriptor,
      isAudit: (overrides?.mode ?? descriptor.mode) === 'audit',
      failOpen: (overrides?.failOpen ?? descriptor.failOpen) === true,
      timeoutMs: overrides?.timeoutMs,
      priority: resolvePriority(descriptor, chainParams.phase, overrides),
      hooks,
    });
  }

  return out;
}

function createInvokeParams(
  entry: ResolvedChainEntry,
  chainParams: ExecuteChainRequestParams,
  currentPayload: unknown,
): InvokeInterceptorRequestParams {
  return {
    name: entry.descriptor.name,
    event: chainParams.event,
    phase: chainParams.phase,
    payload: currentPayload,
    config: chainParams.config?.[entry.descriptor.name],
    context: chainParams.context,
    // Per-interceptor budget only; the chain-aggregate timeout is enforced via
    // the chain signal, not forwarded per invoke.
    timeoutMs: entry.timeoutMs,
  };
}

function clonePayload(payload: unknown): unknown {
  if (payload === undefined) {
    return payload;
  }
  return structuredClone(payload);
}

function isAbortLike(err: unknown): boolean {
  return err instanceof Error && (err.name === 'AbortError' || err.name === 'TimeoutError');
}

function combineSignals(...signals: Array<AbortSignal | undefined>): AbortSignal | undefined {
  const present = signals.filter((s): s is AbortSignal => s != null);
  if (present.length === 0) {
    return undefined;
  }
  if (present.length === 1) {
    return present[0];
  }
  return AbortSignal.any(present);
}

/** Signal for one invoke: chain signal plus the entry's per-interceptor timeout. */
function entrySignal(chainCt: AbortSignal | undefined, entry: ResolvedChainEntry): AbortSignal | undefined {
  const perTimeout = entry.timeoutMs != null ? AbortSignal.timeout(entry.timeoutMs) : undefined;
  return combineSignals(chainCt, perTimeout);
}

export async function executeInterceptorChain(
  interceptors: Array<Interceptor | ChainInterceptorEntry>,
  invoker: InterceptorInvoker,
  chainParams: ExecuteChainRequestParams,
  signal?: AbortSignal,
): Promise<InterceptorChainResult> {
  const started = Date.now();
  const results: InterceptorResult[] = [];
  const summary: ChainValidationSummary = { errors: 0, warnings: 0, infos: 0 };
  let currentPayload = chainParams.payload;
  let abortInfo: ChainAbortInfo | undefined;
  let status: InterceptorChainStatus = 'success';

  const applicable = resolveEntries(interceptors, chainParams);
  const mutations = applicable
    .filter((e) => e.descriptor.type === 'mutation')
    .sort((a, b) => a.priority - b.priority || a.descriptor.name.localeCompare(b.descriptor.name));
  const validations = applicable.filter((e) => e.descriptor.type === 'validation');
  const sinks = applicable.filter((e) => e.descriptor.type === 'sink');

  const timeoutSignal = chainParams.timeoutMs != null ? AbortSignal.timeout(chainParams.timeoutMs) : undefined;
  const ct = combineSignals(signal, timeoutSignal);

  try {
    if (chainParams.phase === 'request') {
      const mut = await executeMutations(mutations, invoker, chainParams, currentPayload, results, ct);
      currentPayload = mut.payload;
      status = mut.status;
      abortInfo = mut.abortInfo;
      if (status !== 'success') {
        return finish();
      }

      const val = await executeValidations(validations, invoker, chainParams, currentPayload, results, summary, ct);
      status = val.status;
      abortInfo = val.abortInfo;
      if (status !== 'success') {
        return finish();
      }

      await executeSinks(sinks, invoker, chainParams, currentPayload, results, ct);
    } else {
      const val = await executeValidations(validations, invoker, chainParams, currentPayload, results, summary, ct);
      status = val.status;
      abortInfo = val.abortInfo;
      if (status !== 'success') {
        return finish();
      }

      await executeSinks(sinks, invoker, chainParams, currentPayload, results, ct);

      const mut = await executeMutations(mutations, invoker, chainParams, currentPayload, results, ct);
      currentPayload = mut.payload;
      status = mut.status;
      abortInfo = mut.abortInfo;
    }
  } catch (err) {
    // Chain-timeout aborts map to status 'timeout'; caller cancellation (outer
    // signal) and unexpected errors surface to the caller unchanged.
    if (isAbortLike(err) && timeoutSignal?.aborted) {
      status = 'timeout';
    } else {
      throw err;
    }
  }

  return finish();

  function finish(): InterceptorChainResult {
    return {
      status,
      event: chainParams.event,
      phase: chainParams.phase,
      results,
      finalPayload: currentPayload,
      validationSummary: summary,
      totalDurationMs: Date.now() - started,
      abortedAt: abortInfo,
    };
  }
}

async function executeMutations(
  mutations: ResolvedChainEntry[],
  invoker: InterceptorInvoker,
  chainParams: ExecuteChainRequestParams,
  initialPayload: unknown,
  results: InterceptorResult[],
  chainCt?: AbortSignal,
): Promise<{ payload: unknown; status: InterceptorChainStatus; abortInfo?: ChainAbortInfo }> {
  let currentPayload = initialPayload;

  for (const entry of mutations) {
    const { descriptor, isAudit, failOpen } = entry;

    try {
      chainCt?.throwIfAborted();
      const invokeParams = createInvokeParams(entry, chainParams, currentPayload);
      const sw = Date.now();
      const result = await invoker(invokeParams, entrySignal(chainCt, entry));
      result.interceptor = descriptor.name;
      result.durationMs = Date.now() - sw;
      results.push(result);

      if (!isAudit && isValidationResult(result)) {
        if (!result.valid && result.severity === 'error') {
          return {
            payload: currentPayload,
            status: 'validation_failed',
            abortInfo: {
              interceptor: descriptor.name,
              reason: result.messages?.[0]?.message ?? 'Validation failed',
              type: 'validation',
            },
          };
        }
        continue;
      }

      if (!isAudit && isMutationResult(result) && result.modified && result.payload !== undefined) {
        currentPayload = clonePayload(result.payload);
      }
    } catch (err) {
      if (isAbortLike(err) && chainCt?.aborted) {
        throw err;
      }
      // Anything else — including this entry's own timeout — is an
      // interceptor-level failure routed through audit/failOpen policy.
      if (isAudit || failOpen) {
        continue;
      }
      return {
        payload: currentPayload,
        status: 'mutation_failed',
        abortInfo: {
          interceptor: descriptor.name,
          reason: err instanceof Error ? err.message : String(err),
          type: 'mutation',
        },
      };
    }
  }

  return { payload: currentPayload, status: 'success' };
}

async function executeValidations(
  validations: ResolvedChainEntry[],
  invoker: InterceptorInvoker,
  chainParams: ExecuteChainRequestParams,
  currentPayload: unknown,
  results: InterceptorResult[],
  summary: ChainValidationSummary,
  chainCt?: AbortSignal,
): Promise<{ status: InterceptorChainStatus; abortInfo?: ChainAbortInfo }> {
  const completed = await Promise.all(
    validations.map(async (entry) => {
      try {
        chainCt?.throwIfAborted();
        const invokeParams = createInvokeParams(entry, chainParams, currentPayload);
        const sw = Date.now();
        const result = await invoker(invokeParams, entrySignal(chainCt, entry));
        result.interceptor = entry.descriptor.name;
        result.durationMs = Date.now() - sw;
        return { entry, result, error: null as Error | null };
      } catch (err) {
        if (isAbortLike(err) && chainCt?.aborted) {
          throw err;
        }
        return {
          entry,
          result: null as InterceptorResult | null,
          error: err instanceof Error ? err : new Error(String(err)),
        };
      }
    }),
  );

  // SEP: all validations complete, then reject. Aggregate every result before
  // returning so blocked chains still carry the full diagnostic picture.
  let blocking: ChainAbortInfo | undefined;

  for (const { entry, result, error } of completed) {
    const { descriptor, isAudit, failOpen } = entry;

    if (error) {
      if (isAudit || failOpen) {
        continue;
      }
      summary.errors++;
      blocking ??= {
        interceptor: descriptor.name,
        reason: error.message,
        type: 'validation',
      };
      continue;
    }

    if (!result) {
      continue;
    }
    results.push(result);

    if (isValidationResult(result)) {
      if (result.messages) {
        for (const msg of result.messages) {
          switch (msg.severity) {
            case 'error':
              summary.errors++;
              break;
            case 'warn':
              summary.warnings++;
              break;
            case 'info':
              summary.infos++;
              break;
          }
        }
      }

      if (!isAudit && !result.valid && result.severity === 'error') {
        blocking ??= {
          interceptor: descriptor.name,
          reason: result.messages?.[0]?.message ?? 'Validation failed',
          type: 'validation',
        };
      }
    }
  }

  if (blocking) {
    return { status: 'validation_failed', abortInfo: blocking };
  }
  return { status: 'success' };
}

async function executeSinks(
  sinks: ResolvedChainEntry[],
  invoker: InterceptorInvoker,
  chainParams: ExecuteChainRequestParams,
  currentPayload: unknown,
  results: InterceptorResult[],
  chainCt?: AbortSignal,
): Promise<void> {
  const completed = await Promise.all(
    sinks.map(async (entry) => {
      try {
        chainCt?.throwIfAborted();
        const invokeParams = createInvokeParams(entry, chainParams, currentPayload);
        const sw = Date.now();
        const result = await invoker(invokeParams, entrySignal(chainCt, entry));
        result.interceptor = entry.descriptor.name;
        result.durationMs = Date.now() - sw;
        return result;
      } catch {
        const fallback: SinkInterceptorResult = {
          type: 'sink',
          phase: chainParams.phase,
          interceptor: entry.descriptor.name,
          recorded: false,
        };
        return fallback;
      }
    }),
  );

  results.push(...completed);
}
