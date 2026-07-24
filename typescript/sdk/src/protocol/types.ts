// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The interior data contract for the interceptors extension (SEP-2624).
 *
 * RULE 3: `null` for absent, never `undefined`; every field is always present.
 * RULE 6: `readonly` all the way down. RULE 12: results are a tagged union
 * discriminated on `type`. The wire boundary (schemas.ts) maps missing wire
 * fields to `null` so the interior never sees `undefined`.
 */
import { INTERCEPTOR_TYPE } from "./constants.js";
import type {
  InterceptionEvent,
  InterceptorMode,
  InterceptorPhase,
  InterceptorType,
  ValidationSeverity,
} from "./constants.js";

/** Which lifecycle events, on which phase, trigger this interceptor. */
export interface InterceptorHook {
  readonly events: readonly InterceptionEvent[];
  readonly phase: InterceptorPhase;
}

export interface InterceptorCompatibility {
  readonly minProtocol: string;
  readonly maxProtocol: string | null;
}

export interface PriorityHintByPhase {
  readonly request: number | null;
  readonly response: number | null;
}
/** A single number applies to both phases; an object selects per phase. */
export type PriorityHint = number | PriorityHintByPhase;

/** An interceptor descriptor as returned by `interceptors/list`. */
export interface Interceptor {
  readonly name: string;
  readonly version: string | null;
  readonly description: string | null;
  readonly type: InterceptorType;
  readonly hooks: readonly InterceptorHook[];
  /** Default `active`; always present in the interior. */
  readonly mode: InterceptorMode;
  /** Default `false` (fail-closed); always present in the interior. */
  readonly failOpen: boolean;
  readonly priorityHint: PriorityHint | null;
  readonly compat: InterceptorCompatibility | null;
  readonly configSchema: Readonly<Record<string, unknown>> | null;
}

export interface ValidationMessage {
  readonly path: string | null;
  readonly message: string;
  readonly severity: ValidationSeverity;
}

export interface ValidationSuggestion {
  readonly path: string;
  readonly value: unknown;
}

export interface InterceptorPrincipal {
  readonly type: string;
  readonly id: string | null;
  readonly claims: Readonly<Record<string, unknown>> | null;
}

export interface InvokeContext {
  readonly principal: InterceptorPrincipal | null;
  readonly traceId: string | null;
  readonly spanId: string | null;
  readonly timestamp: string | null;
  readonly sessionId: string | null;
}

interface ResultBase {
  readonly interceptor: string | null;
  readonly phase: InterceptorPhase;
  readonly durationMs: number | null;
  readonly info: Readonly<Record<string, unknown>> | null;
}

export interface ValidationResult extends ResultBase {
  readonly type: (typeof INTERCEPTOR_TYPE)["Validation"];
  readonly valid: boolean;
  readonly severity: ValidationSeverity | null;
  readonly messages: readonly ValidationMessage[];
  readonly suggestions: readonly ValidationSuggestion[];
}

export interface MutationResult extends ResultBase {
  readonly type: (typeof INTERCEPTOR_TYPE)["Mutation"];
  readonly modified: boolean;
  readonly payload: unknown;
}

/** RULE 12: the decision an interceptor returns, discriminated on `type`. */
export type InterceptorResult = ValidationResult | MutationResult;

export interface ListInterceptorsResult {
  readonly interceptors: readonly Interceptor[];
  readonly nextCursor: string | null;
}

export interface InvokeParams {
  readonly name: string;
  readonly event: InterceptionEvent;
  readonly phase: InterceptorPhase;
  readonly payload: unknown;
  readonly config: Readonly<Record<string, unknown>> | null;
  readonly timeoutMs: number | null;
  readonly context: InvokeContext | null;
}
