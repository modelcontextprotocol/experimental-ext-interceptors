// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Authoring surface for interceptor implementations. This is a boundary
 * (RULE 7): specs written by SDK consumers may omit fields for ergonomics, and
 * `defineValidator` / `defineMutator` normalize them ONCE into the trusted
 * interior `Interceptor` + handler pair. Two explicit constructors — one per
 * SEP interceptor type — instead of one function that sniffs its input.
 */
import {
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "../protocol/constants.js";
import type {
  InterceptionEvent,
  InterceptorMode,
  ValidationSeverity,
} from "../protocol/constants.js";
import type {
  Interceptor,
  InterceptorHook,
  InterceptorResult,
  InvokeParams,
  MutationResult,
  PriorityHint,
  ValidationMessage,
  ValidationResult,
  ValidationSuggestion,
} from "../protocol/types.js";

/** Which phases a spec hooks. `both` expands to two hooks. */
export const HOOK_PHASES = {
  Request: INTERCEPTOR_PHASE.Request,
  Response: INTERCEPTOR_PHASE.Response,
  Both: "both",
} as const;
export type HookPhases = (typeof HOOK_PHASES)[keyof typeof HOOK_PHASES];

/** RULE 2: phase-spec → hooks, one entry per member of HOOK_PHASES. */
const HOOKS_BY_PHASES: Record<
  HookPhases,
  (events: readonly InterceptionEvent[]) => readonly InterceptorHook[]
> = {
  [HOOK_PHASES.Request]: (events) => [
    { events, phase: INTERCEPTOR_PHASE.Request },
  ],
  [HOOK_PHASES.Response]: (events) => [
    { events, phase: INTERCEPTOR_PHASE.Response },
  ],
  [HOOK_PHASES.Both]: (events) => [
    { events, phase: INTERCEPTOR_PHASE.Request },
    { events, phase: INTERCEPTOR_PHASE.Response },
  ],
};

interface CommonSpec {
  readonly name: string;
  readonly events: readonly InterceptionEvent[];
  /** Default: `both`. */
  readonly phases?: HookPhases;
  /** Default: `enforce`. */
  readonly mode?: InterceptorMode;
  /** Default: `false` (fail-closed). */
  readonly failOpen?: boolean;
  readonly priorityHint?: PriorityHint;
  readonly version?: string;
  readonly description?: string;
}

/** What a validator handler returns; normalized into a full ValidationResult. */
export interface ValidationVerdict {
  readonly valid: boolean;
  readonly severity?: ValidationSeverity;
  readonly messages?: readonly (string | ValidationMessage)[];
  readonly suggestions?: readonly ValidationSuggestion[];
  readonly info?: Readonly<Record<string, unknown>>;
}

/** What a mutator handler returns; normalized into a full MutationResult. */
export interface MutationDecision {
  readonly modified: boolean;
  readonly payload?: unknown;
  readonly info?: Readonly<Record<string, unknown>>;
}

// ── verdict constructors (RULE 9: the whole authoring vocabulary) ───────────

export function pass(info?: Readonly<Record<string, unknown>>): ValidationVerdict {
  return { valid: true, info };
}

export function block(
  message: string,
  severity: ValidationSeverity = VALIDATION_SEVERITY.Error,
): ValidationVerdict {
  return { valid: false, severity, messages: [message] };
}

export function apply(
  payload: unknown,
  info?: Readonly<Record<string, unknown>>,
): MutationDecision {
  return { modified: true, payload, info };
}

export function keep(info?: Readonly<Record<string, unknown>>): MutationDecision {
  return { modified: false, info };
}

// ── spec → interior normalization ────────────────────────────────────────────

export interface ValidatorSpec extends CommonSpec {
  readonly validate: (
    params: InvokeParams,
    signal: AbortSignal | null,
  ) => ValidationVerdict | Promise<ValidationVerdict>;
}

export interface MutatorSpec extends CommonSpec {
  readonly mutate: (
    params: InvokeParams,
    signal: AbortSignal | null,
  ) => MutationDecision | Promise<MutationDecision>;
}

export type InterceptorHandler = (
  params: InvokeParams,
  signal: AbortSignal | null,
) => Promise<InterceptorResult>;

export interface RegisteredInterceptor {
  readonly descriptor: Interceptor;
  readonly handler: InterceptorHandler;
}

function descriptorFrom(
  spec: CommonSpec,
  type: Interceptor["type"],
): Interceptor {
  return {
    name: spec.name,
    version: spec.version ?? null,
    description: spec.description ?? null,
    type,
    hooks: HOOKS_BY_PHASES[spec.phases ?? HOOK_PHASES.Both](spec.events),
    mode: spec.mode ?? INTERCEPTOR_MODE.Enforce,
    failOpen: spec.failOpen ?? false,
    priorityHint: spec.priorityHint ?? null,
    compat: null,
    configSchema: null,
  };
}

function normalizeMessage(
  m: string | ValidationMessage,
  fallback: ValidationSeverity,
): ValidationMessage {
  return typeof m === "string"
    ? { path: null, message: m, severity: fallback }
    : m;
}

export function defineValidator(spec: ValidatorSpec): RegisteredInterceptor {
  const descriptor = descriptorFrom(spec, INTERCEPTOR_TYPE.Validation);
  const handler: InterceptorHandler = async (params, signal) => {
    const verdict = await spec.validate(params, signal);
    const severity =
      verdict.severity ?? (verdict.valid ? null : VALIDATION_SEVERITY.Error);
    const result: ValidationResult = {
      type: INTERCEPTOR_TYPE.Validation,
      interceptor: descriptor.name,
      phase: params.phase,
      durationMs: null,
      info: verdict.info ?? null,
      valid: verdict.valid,
      severity,
      messages: (verdict.messages ?? []).map((m) =>
        normalizeMessage(m, severity ?? VALIDATION_SEVERITY.Error),
      ),
      suggestions: verdict.suggestions ?? [],
    };
    return result;
  };
  return { descriptor, handler };
}

export function defineMutator(spec: MutatorSpec): RegisteredInterceptor {
  const descriptor = descriptorFrom(spec, INTERCEPTOR_TYPE.Mutation);
  const handler: InterceptorHandler = async (params, signal) => {
    const decision = await spec.mutate(params, signal);
    const result: MutationResult = {
      type: INTERCEPTOR_TYPE.Mutation,
      interceptor: descriptor.name,
      phase: params.phase,
      durationMs: null,
      info: decision.info ?? null,
      modified: decision.modified,
      payload: decision.modified ? (decision.payload ?? null) : params.payload,
    };
    return result;
  };
  return { descriptor, handler };
}
