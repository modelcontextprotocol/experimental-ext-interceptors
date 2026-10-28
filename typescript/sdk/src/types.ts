// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by a Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Wire types from SEP-2624 (docs/sep.md) used by this SDK.
 */

/** A Lifecycle Event, e.g. "tools/call", or a wildcard such as "*". */
export type InterceptionEvent = string;

export type InterceptorPhase = 'request' | 'response';

export type InterceptorMode = 'active' | 'audit';

export type ValidationSeverity = 'info' | 'warn' | 'error';

/**
 * Returned by a validator, together with `valid: false` and
 * `severity: "error"`, when the payload may proceed only if a human
 * confirms it. See "Requiring Confirmation" in the SEP.
 */
export interface ConfirmationRequest {
  /** Unique per request; identifies the pending invocation this confirmation is for. */
  id: string;
  /** Human-readable explanation, suitable to show to the person asked. */
  reason: string;
  /** Call binding hash: "sha256:" + lowercase hex SHA-256 of the JCS form of the binding. */
  callHash: string;
  /** ISO 8601; after this, a confirmation for this request MUST NOT be accepted. */
  expiresAt?: string;
  /** Opaque value the invoker MUST return unchanged with the confirmation. */
  token?: string;
}

/** A human decision, returned to the validator in `context.confirmations`. */
export interface Confirmation {
  /** Name of the validator that requested it. */
  interceptor: string;
  /** From the ConfirmationRequest. */
  id: string;
  /** From the ConfirmationRequest. */
  callHash: string;
  /** From the ConfirmationRequest, unchanged. */
  token?: string;
  /** ISO 8601. */
  decidedAt: string;
  /** Who confirmed, if known. */
  principal?: { type: 'user' | 'service'; id?: string };
}

export interface ValidationMessage {
  path?: string;
  message: string;
  severity: ValidationSeverity;
}

export interface ValidationResult {
  interceptor: string;
  type: 'validation';
  phase: InterceptorPhase;
  durationMs?: number;
  info?: Record<string, unknown>;

  valid: boolean;
  severity?: ValidationSeverity;
  messages?: ValidationMessage[];
  suggestions?: Array<{ path: string; value: unknown }>;
  /** Only meaningful with `valid: false` and `severity: "error"`. */
  confirmation?: ConfirmationRequest;
}

export interface InvocationContext {
  principal?: {
    type: 'user' | 'service' | 'anonymous';
    id?: string;
    claims?: Record<string, unknown>;
  };
  traceId?: string;
  spanId?: string;
  /** ISO 8601. */
  timestamp: string;
  sessionId?: string;
  /**
   * Peer the payload is sent to or, when receiving, came from (e.g. a server
   * name). Part of the call binding for confirmations.
   */
  target?: string;
  /** Human confirmations for this exact payload. */
  confirmations?: Confirmation[];
}

export interface InterceptorInvocationParams {
  name: string;
  event: InterceptionEvent;
  phase: InterceptorPhase;
  payload: unknown;
  config?: Record<string, unknown>;
  timeoutMs?: number;
  context?: InvocationContext;
}
