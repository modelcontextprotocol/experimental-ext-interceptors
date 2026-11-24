// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The OUTBOUND wire boundary (RULE 7, the other direction from wire.ts): the
 * trusted interior shape → SEP-2624's on-the-wire JSON. Interior `null`/default
 * fields are OMITTED so we emit the spec's optional shape, which every other
 * SDK's stricter (optional-not-nullable) parser accepts. Result serialization
 * dispatches on `type` through a `Record` table (RULE 2), never a `switch`.
 */
import { INTERCEPTOR_MODE, INTERCEPTOR_TYPE } from "./constants.js";
import type { InterceptorType } from "./constants.js";
import type {
  Interceptor,
  InterceptorHook,
  InterceptorResult,
  InvokeParams,
  MutationResult,
  ValidationResult,
} from "./types.js";

type Json = Record<string, unknown>;

/** Build an object, dropping keys whose value is `null` (SEP optional shape). */
function compact(entries: Readonly<Record<string, unknown>>): Json {
  const out: Json = {};
  for (const [k, v] of Object.entries(entries)) {
    if (v !== null) out[k] = v;
  }
  return out;
}

function serializeHook(h: InterceptorHook): Json {
  return { events: [...h.events], phase: h.phase };
}

export function serializeInterceptor(i: Interceptor): Json {
  return compact({
    name: i.name,
    type: i.type,
    hooks: i.hooks.map(serializeHook),
    version: i.version,
    description: i.description,
    // `active` is the SEP default; only emit when it differs (i.e. `audit`).
    mode: i.mode === INTERCEPTOR_MODE.Enforce ? null : i.mode,
    // `false` is the default; only emit when opting into fail-open.
    failOpen: i.failOpen ? true : null,
    priorityHint: i.priorityHint,
    compat: i.compat === null ? null : compact({ ...i.compat }),
    configSchema: i.configSchema,
  });
}

export function serializeInvokeParams(p: InvokeParams): Json {
  return compact({
    name: p.name,
    event: p.event,
    phase: p.phase,
    payload: p.payload ?? null,
    config: p.config,
    timeoutMs: p.timeoutMs,
    context: p.context === null ? null : compact({ ...p.context }),
  });
}

const SERIALIZE_RESULT: Record<InterceptorType, (r: InterceptorResult) => Json> =
  {
    [INTERCEPTOR_TYPE.Validation]: (r) => {
      const v = r as ValidationResult;
      return compact({
        interceptor: v.interceptor,
        type: INTERCEPTOR_TYPE.Validation,
        phase: v.phase,
        valid: v.valid,
        severity: v.severity,
        messages: v.messages.length > 0 ? v.messages.map((m) => compact({ ...m })) : null,
        suggestions: v.suggestions.length > 0 ? [...v.suggestions] : null,
        durationMs: v.durationMs,
        info: v.info,
      });
    },
    [INTERCEPTOR_TYPE.Mutation]: (r) => {
      const m = r as MutationResult;
      return compact({
        interceptor: m.interceptor,
        type: INTERCEPTOR_TYPE.Mutation,
        phase: m.phase,
        modified: m.modified,
        payload: m.payload ?? null,
        durationMs: m.durationMs,
        info: m.info,
      });
    },
  };

export function serializeResult(r: InterceptorResult): Json {
  return SERIALIZE_RESULT[r.type](r);
}
