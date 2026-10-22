// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by a Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Validator confirmations ("Requiring Confirmation" in the SEP).
 *
 * A validator that wants a human to approve a call returns a blocking
 * ValidationResult carrying a ConfirmationRequest. The request is bound to
 * the exact call through `callHash`, so an approval for one call cannot be
 * replayed for another.
 *
 * A confirmation is single use. The SEP makes invokers responsible for not
 * attaching it to a second request; a validator that also enforces it passes
 * a UsedConfirmations store, which remembers accepted ids until they expire.
 */

import { createHash, createHmac, randomUUID, timingSafeEqual } from 'node:crypto';

import { canonicalize } from './jcs.js';
import type {
  Confirmation,
  InterceptionEvent,
  InterceptorInvocationParams,
  InterceptorPhase,
  ValidationMessage,
  ValidationResult,
} from './types.js';

/** The object `callHash` covers. */
export interface CallBinding {
  event: InterceptionEvent;
  phase: InterceptorPhase;
  /** `context.target`, or null if absent. */
  target: string | null;
  payload: unknown;
}

/** Builds the call binding of an invocation. */
export function callBinding(
  params: Pick<InterceptorInvocationParams, 'event' | 'phase' | 'payload' | 'context'>,
): CallBinding {
  return {
    event: params.event,
    phase: params.phase,
    target: params.context?.target ?? null,
    payload: params.payload,
  };
}

/** Returns "sha256:" followed by the lowercase hex SHA-256 of the binding's JCS form. */
export function computeCallHash(binding: CallBinding): string {
  const digest = createHash('sha256').update(canonicalize(binding), 'utf8').digest('hex');
  return `sha256:${digest}`;
}

export type ConfirmationSecret = string | Uint8Array;

const TOKEN_VERSION = 'v1';

interface TokenClaims {
  /** Name of the validator that issued the request. */
  interceptor: string;
  id: string;
  callHash: string;
  sessionId?: string;
  expiresAt?: string;
}

function tokenMac(secret: ConfirmationSecret, claims: TokenClaims): Buffer {
  const message = canonicalize([
    TOKEN_VERSION,
    claims.interceptor,
    claims.id,
    claims.callHash,
    claims.sessionId ?? null,
    claims.expiresAt ?? null,
  ]);
  return createHmac('sha256', secret).update(message, 'utf8').digest();
}

/**
 * Issues a self-verifying token: an HMAC over the issuing validator's name,
 * the request id, callHash, session id and expiry. The validator needs no pending-request state to
 * check it, so a confirmation survives a reconnect.
 */
export function createConfirmationToken(secret: ConfirmationSecret, claims: TokenClaims): string {
  const expiresAt = Buffer.from(claims.expiresAt ?? '', 'utf8').toString('base64url');
  const mac = tokenMac(secret, claims).toString('base64url');
  return `${TOKEN_VERSION}.${expiresAt}.${mac}`;
}

/**
 * Checks a token issued by createConfirmationToken for the given validator,
 * id, callHash and session, and that its embedded expiry has not passed.
 */
export function verifyConfirmationToken(
  secret: ConfirmationSecret,
  token: string,
  claims: Omit<TokenClaims, 'expiresAt'>,
  now: Date = new Date(),
): boolean {
  const parts = token.split('.');
  if (parts.length !== 3 || parts[0] !== TOKEN_VERSION) {
    return false;
  }
  const expiresAt = Buffer.from(parts[1], 'base64url').toString('utf8') || undefined;
  const expected = tokenMac(secret, { ...claims, expiresAt });
  const actual = Buffer.from(parts[2], 'base64url');
  if (actual.length !== expected.length || !timingSafeEqual(actual, expected)) {
    return false;
  }
  return expiresAt === undefined || now.getTime() < Date.parse(expiresAt);
}

/** Reads the expiry embedded in a token from createConfirmationToken, without verifying it. */
function tokenExpiry(token: string | undefined): number | undefined {
  const part = token?.split('.')[1];
  if (!part) {
    return undefined;
  }
  const ms = Date.parse(Buffer.from(part, 'base64url').toString('utf8'));
  return Number.isNaN(ms) ? undefined : ms;
}

/**
 * Remembers the confirmations a validator has accepted, so each allows one
 * invocation. Entries are kept until the confirmation expires, which bounds
 * the state.
 */
export interface UsedConfirmations {
  /** Records the key as used until `expiresAt` (ms since the epoch). Returns false if it was already used. */
  claim(key: string, expiresAt: number): boolean;
}

/** In-process UsedConfirmations. Use a shared store if several validator processes accept the same confirmations. */
export class MemoryUsedConfirmations implements UsedConfirmations {
  private readonly used = new Map<string, number>();

  constructor(private readonly now: () => number = Date.now) {}

  claim(key: string, expiresAt: number): boolean {
    const now = this.now();
    for (const [k, until] of this.used) {
      if (until <= now) {
        this.used.delete(k);
      }
    }
    if (this.used.has(key)) {
      return false;
    }
    this.used.set(key, expiresAt);
    return true;
  }

  /** Number of confirmations remembered (for tests and monitoring). */
  get size(): number {
    return this.used.size;
  }
}

export interface ConfirmationOptions {
  /** Human-readable explanation, shown to the person asked. */
  reason: string;
  /**
   * When set, the request carries a self-verifying token and only
   * confirmations returning a valid token are accepted. Recommended.
   */
  secret?: ConfirmationSecret;
  /** Lifetime of the request. Default: 5 minutes. */
  ttlMs?: number;
  /** Additional messages for the blocking result. */
  messages?: ValidationMessage[];
  /**
   * Makes requireConfirmation accept each confirmation once: a second use,
   * for example when an invoker retries, asks for a new confirmation.
   * Recommended.
   */
  used?: UsedConfirmations;
  now?: () => Date;
}

const DEFAULT_TTL_MS = 5 * 60 * 1000;

/**
 * Returns the blocking result a validator sends to ask for confirmation of
 * this invocation: `valid: false`, `severity: "error"` and `confirmation`.
 * An invoker that does not implement confirmation simply blocks.
 */
export function confirmationRequired(
  params: InterceptorInvocationParams,
  options: ConfirmationOptions,
): ValidationResult {
  const now = options.now?.() ?? new Date();
  const id = randomUUID();
  const callHash = computeCallHash(callBinding(params));
  const expiresAt = new Date(now.getTime() + (options.ttlMs ?? DEFAULT_TTL_MS)).toISOString();
  const token =
    options.secret === undefined
      ? undefined
      : createConfirmationToken(options.secret, {
          interceptor: params.name,
          id,
          callHash,
          sessionId: params.context?.sessionId,
          expiresAt,
        });
  return {
    interceptor: params.name,
    type: 'validation',
    phase: params.phase,
    valid: false,
    severity: 'error',
    messages: [{ message: options.reason, severity: 'error' }, ...(options.messages ?? [])],
    confirmation: { id, reason: options.reason, callHash, expiresAt, token },
  };
}

/**
 * Returns the confirmation in `context.confirmations` that this validator
 * can accept for this invocation, if any: one addressed to this validator
 * whose callHash matches the invocation's call binding (including target)
 * and, if `secret` is given, whose token is valid and unexpired. It does not
 * record the confirmation as used; requireConfirmation does, given `used`.
 */
export function findConfirmation(
  params: InterceptorInvocationParams,
  options: Pick<ConfirmationOptions, 'secret' | 'now'> = {},
): Confirmation | undefined {
  const confirmations = params.context?.confirmations;
  if (!confirmations?.length) {
    return undefined;
  }
  const callHash = computeCallHash(callBinding(params));
  const now = options.now?.() ?? new Date();
  return confirmations.find((c) => {
    if (c.interceptor !== params.name || c.callHash !== callHash) {
      return false;
    }
    if (options.secret === undefined) {
      return true;
    }
    return (
      c.token !== undefined &&
      verifyConfirmationToken(
        options.secret,
        c.token,
        { interceptor: params.name, id: c.id, callHash, sessionId: params.context?.sessionId },
        now,
      )
    );
  });
}

/**
 * Convenience for validators: passes if the invocation carries an
 * acceptable confirmation, otherwise asks for one. With `used`, each
 * confirmation passes once.
 */
export function requireConfirmation(
  params: InterceptorInvocationParams,
  options: ConfirmationOptions,
): ValidationResult {
  const c = findConfirmation(params, options);
  if (c && (!options.used || claim(options.used, params.name, c, options))) {
    return { interceptor: params.name, type: 'validation', phase: params.phase, valid: true };
  }
  return confirmationRequired(params, options);
}

function claim(used: UsedConfirmations, interceptor: string, c: Confirmation, options: ConfirmationOptions): boolean {
  const now = (options.now?.() ?? new Date()).getTime();
  // Without a token there is no embedded expiry; keep the id for a full request lifetime.
  const expiresAt = tokenExpiry(c.token) ?? now + (options.ttlMs ?? DEFAULT_TTL_MS);
  return used.claim(`${interceptor}\n${c.id}`, expiresAt);
}
