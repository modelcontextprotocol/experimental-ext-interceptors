// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Frozen constant sets for the interceptors extension (SEP-2624).
 *
 * Every finite set of string values is a `const` object; its union type is
 * DERIVED from it (FUNCTIONAL_PATTERNS RULE 1 / RULE 5). Runtime code
 * references the constant, never a raw string literal, so a typo cannot
 * compile and the wire value and the type share one source of truth.
 */

/** Interceptor operation type. SEP-2624 defines exactly two. */
export const INTERCEPTOR_TYPE = {
  Validation: "validation",
  Mutation: "mutation",
} as const;
export type InterceptorType =
  (typeof INTERCEPTOR_TYPE)[keyof typeof INTERCEPTOR_TYPE];

/** Execution phase of a lifecycle event. */
export const INTERCEPTOR_PHASE = {
  Request: "request",
  Response: "response",
} as const;
export type InterceptorPhase =
  (typeof INTERCEPTOR_PHASE)[keyof typeof INTERCEPTOR_PHASE];

/**
 * Execution mode. The SEP wire value is `enforce` (normal blocking /
 * transforming) or `audit` (non-blocking: validators log without blocking,
 * mutators compute without applying). `enforce` is the default when omitted.
 * (Note: the C# SDK shipped `active` instead of `enforce` — see issue #15.
 * This SDK uses the SEP value.)
 */
export const INTERCEPTOR_MODE = {
  Enforce: "enforce",
  Audit: "audit",
} as const;
export type InterceptorMode =
  (typeof INTERCEPTOR_MODE)[keyof typeof INTERCEPTOR_MODE];

/** Validation severity. Only `error` blocks; `warn` and `info` never block. */
export const VALIDATION_SEVERITY = {
  Info: "info",
  Warn: "warn",
  Error: "error",
} as const;
export type ValidationSeverity =
  (typeof VALIDATION_SEVERITY)[keyof typeof VALIDATION_SEVERITY];

/** Terminal status of a chain execution. */
export const CHAIN_STATUS = {
  Success: "success",
  ValidationFailed: "validation_failed",
  MutationFailed: "mutation_failed",
  Timeout: "timeout",
} as const;
export type ChainStatus = (typeof CHAIN_STATUS)[keyof typeof CHAIN_STATUS];

/** JSON-RPC methods introduced by the extension. */
export const RPC_METHOD = {
  InterceptorsList: "interceptors/list",
  InterceptorInvoke: "interceptor/invoke",
} as const;
export type RpcMethod = (typeof RPC_METHOD)[keyof typeof RPC_METHOD];

/**
 * Well-known lifecycle events (SEP-2624). `*` matches all events.
 * The SEP explicitly permits custom `namespace/operation` events, so a hook's
 * event is the open `InterceptionEvent` below — a closed set of known members
 * with a documented extension point (RULE 5: closed set, open extension).
 */
export const INTERCEPTION_EVENT = {
  ToolsList: "tools/list",
  ToolsCall: "tools/call",
  PromptsList: "prompts/list",
  PromptsGet: "prompts/get",
  ResourcesList: "resources/list",
  ResourcesRead: "resources/read",
  ResourcesSubscribe: "resources/subscribe",
  SamplingCreateMessage: "sampling/createMessage",
  ElicitationCreate: "elicitation/create",
  RootsList: "roots/list",
  LlmCompletion: "llm/completion",
  All: "*",
} as const;
export type KnownInterceptionEvent =
  (typeof INTERCEPTION_EVENT)[keyof typeof INTERCEPTION_EVENT];
/**
 * A known event or any custom `namespace/operation` string. Intersecting with
 * an empty-key record preserves literal autocomplete for the known members
 * while the SEP-mandated extension point stays open. This is the ONE
 * sanctioned widening (the spec requires custom events); every other set
 * stays strictly closed.
 */
export type InterceptionEvent =
  | KnownInterceptionEvent
  | (string & Record<never, never>);

/** Capability key for extension negotiation (SEP-2133 extensions format). */
export const INTERCEPTORS_CAPABILITY =
  "io.modelcontextprotocol/interceptors" as const;
