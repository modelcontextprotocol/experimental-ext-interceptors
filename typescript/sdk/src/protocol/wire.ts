// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The wire boundary (FUNCTIONAL_PATTERNS RULE 7): the ONE place untrusted
 * JSON from another SDK/server is read and normalized into the trusted
 * interior shape. Optionals collapse to `null`, defaults are applied
 * (`mode=enforce`, `failOpen=false`), and result parsing dispatches on `type`
 * through a `Record` table (RULE 2) - never a `switch`. The interior
 * (types.ts) is trusted after this point and never re-validates.
 */
import {
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "./constants.js";
import type {
  InterceptorMode,
  InterceptorPhase,
  InterceptorType,
  ValidationSeverity,
} from "./constants.js";
import type {
  Interceptor,
  InterceptorCompatibility,
  InterceptorHook,
  InterceptorResult,
  InvokeContext,
  InvokeParams,
  PriorityHint,
  ValidationMessage,
  ValidationSuggestion,
} from "./types.js";

/** Thrown only at the wire boundary when required, closed-set fields are invalid. */
export class WireError extends Error {}

const PHASES: ReadonlySet<string> = new Set(Object.values(INTERCEPTOR_PHASE));
const TYPES: ReadonlySet<string> = new Set(Object.values(INTERCEPTOR_TYPE));
const MODES: ReadonlySet<string> = new Set(Object.values(INTERCEPTOR_MODE));
const SEVERITIES: ReadonlySet<string> = new Set(
  Object.values(VALIDATION_SEVERITY),
);

const rec = (v: unknown): Record<string, unknown> =>
  typeof v === "object" && v !== null && !Array.isArray(v)
    ? (v as Record<string, unknown>)
    : {};
const recOrNull = (v: unknown): Record<string, unknown> | null =>
  typeof v === "object" && v !== null && !Array.isArray(v)
    ? (v as Record<string, unknown>)
    : null;
const str = (v: unknown): string | null => (typeof v === "string" ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" ? v : null);
const list = (v: unknown): readonly unknown[] => (Array.isArray(v) ? v : []);

function phase(v: unknown): InterceptorPhase {
  if (typeof v === "string" && PHASES.has(v)) return v as InterceptorPhase;
  throw new WireError(`invalid interceptor phase: ${String(v)}`);
}
function interceptorType(v: unknown): InterceptorType {
  if (typeof v === "string" && TYPES.has(v)) return v as InterceptorType;
  throw new WireError(`invalid interceptor type: ${String(v)}`);
}
function mode(v: unknown): InterceptorMode {
  return typeof v === "string" && MODES.has(v)
    ? (v as InterceptorMode)
    : INTERCEPTOR_MODE.Enforce;
}
function severity(v: unknown): ValidationSeverity | null {
  return typeof v === "string" && SEVERITIES.has(v)
    ? (v as ValidationSeverity)
    : null;
}

function priorityHint(v: unknown): PriorityHint | null {
  if (typeof v === "number") return v;
  if (typeof v === "object" && v !== null) {
    const o = v as Record<string, unknown>;
    return { request: num(o.request), response: num(o.response) };
  }
  return null;
}

function hook(v: unknown): InterceptorHook {
  const o = rec(v);
  return {
    events: list(o.events).filter((e): e is string => typeof e === "string"),
    phase: phase(o.phase),
  };
}

function compat(v: unknown): InterceptorCompatibility | null {
  const o = recOrNull(v);
  if (o === null) return null;
  const minProtocol = str(o.minProtocol);
  if (minProtocol === null) return null;
  return { minProtocol, maxProtocol: str(o.maxProtocol) };
}

function message(v: unknown): ValidationMessage {
  const o = rec(v);
  return {
    path: str(o.path),
    message: str(o.message) ?? "",
    severity: severity(o.severity) ?? VALIDATION_SEVERITY.Error,
  };
}

function suggestion(v: unknown): ValidationSuggestion | null {
  const o = rec(v);
  const path = str(o.path);
  return path === null ? null : { path, value: o.value ?? null };
}

/** Normalize one interceptor descriptor from `interceptors/list`. */
export function normalizeInterceptor(raw: unknown): Interceptor {
  const o = rec(raw);
  const name = str(o.name);
  if (name === null) throw new WireError("interceptor.name is required");
  return {
    name,
    version: str(o.version),
    description: str(o.description),
    type: interceptorType(o.type),
    hooks: list(o.hooks).map(hook),
    mode: mode(o.mode),
    failOpen: o.failOpen === true,
    priorityHint: priorityHint(o.priorityHint),
    compat: compat(o.compat),
    configSchema: recOrNull(o.configSchema),
  };
}

interface ResultBase {
  readonly interceptor: string | null;
  readonly phase: InterceptorPhase;
  readonly durationMs: number | null;
  readonly info: Readonly<Record<string, unknown>> | null;
}

/** RULE 2: result normalization dispatches on `type`, not a `switch`. */
const RESULT_BY_TYPE: Record<
  InterceptorType,
  (raw: Record<string, unknown>, base: ResultBase) => InterceptorResult
> = {
  [INTERCEPTOR_TYPE.Validation]: (raw, base) => ({
    ...base,
    type: INTERCEPTOR_TYPE.Validation,
    valid: raw.valid === true,
    severity: severity(raw.severity),
    messages: list(raw.messages).map(message),
    suggestions: list(raw.suggestions)
      .map(suggestion)
      .filter((s): s is ValidationSuggestion => s !== null),
  }),
  [INTERCEPTOR_TYPE.Mutation]: (raw, base) => ({
    ...base,
    type: INTERCEPTOR_TYPE.Mutation,
    modified: raw.modified === true,
    payload: raw.payload ?? null,
  }),
};

/** Normalize one interceptor result from `interceptor/invoke`. */
export function normalizeResult(raw: unknown): InterceptorResult {
  const o = rec(raw);
  const base: ResultBase = {
    interceptor: str(o.interceptor),
    phase: phase(o.phase),
    durationMs: num(o.durationMs),
    info: recOrNull(o.info),
  };
  return RESULT_BY_TYPE[interceptorType(o.type)](o, base);
}

function context(v: unknown): InvokeContext | null {
  const o = recOrNull(v);
  if (o === null) return null;
  const p = recOrNull(o.principal);
  return {
    principal:
      p === null
        ? null
        : { type: str(p.type) ?? "anonymous", id: str(p.id), claims: recOrNull(p.claims) },
    traceId: str(o.traceId),
    spanId: str(o.spanId),
    timestamp: str(o.timestamp),
    sessionId: str(o.sessionId),
  };
}

/** Normalize inbound `interceptor/invoke` params (server side). */
export function normalizeInvokeParams(raw: unknown): InvokeParams {
  const o = rec(raw);
  const name = str(o.name);
  const event = str(o.event);
  if (name === null) throw new WireError("invoke params.name is required");
  if (event === null) throw new WireError("invoke params.event is required");
  return {
    name,
    event,
    phase: phase(o.phase),
    payload: o.payload ?? null,
    config: recOrNull(o.config),
    timeoutMs: num(o.timeoutMs),
    context: context(o.context),
  };
}
