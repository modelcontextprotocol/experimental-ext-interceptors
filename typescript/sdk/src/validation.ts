// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by a Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Invoker-side validation phase, including validator confirmations.
 *
 * Validators run in parallel. Only `severity: "error"` from an active
 * validator blocks. A blocking result that carries a `confirmation` may be
 * unblocked by a human; every other path that cannot reach a decision
 * blocks.
 */

import { callBinding, computeCallHash, type CallBinding } from './confirmation.js';
import type {
  Confirmation,
  ConfirmationRequest,
  InterceptionEvent,
  InterceptorInvocationParams,
  InterceptorMode,
  InterceptorPhase,
  InvocationContext,
  ValidationResult,
} from './types.js';

/** A validator as seen by the invoker. */
export interface ValidatorEntry {
  name: string;
  /** Default: "active". */
  mode?: InterceptorMode;
  /** Default: false (fail-closed). */
  failOpen?: boolean;
  config?: Record<string, unknown>;
  invoke(params: InterceptorInvocationParams): Promise<ValidationResult>;
}

/** What the invoker should show the person asked. */
export interface ConfirmationPrompt {
  interceptor: string;
  request: ConfirmationRequest;
  event: InterceptionEvent;
  phase: InterceptorPhase;
  target: string;
  /** The exact payload that will be sent or processed if approved. */
  payload: unknown;
}

export interface ConfirmationDecision {
  approved: boolean;
  principal?: Confirmation['principal'];
}

/**
 * Obtains a human decision, e.g. through an MCP `elicitation/create`
 * request. `signal` aborts when the request expires. Throwing blocks.
 */
export type ConfirmationHandler = (
  prompt: ConfirmationPrompt,
  signal: AbortSignal,
) => Promise<ConfirmationDecision>;

export interface ValidationPhaseOptions {
  event: InterceptionEvent;
  phase: InterceptorPhase;
  payload: unknown;
  /** Peer the payload is sent to or came from. Set on every invocation. */
  target: string;
  context?: Omit<InvocationContext, 'target' | 'confirmations' | 'timestamp'>;
  timeoutMs?: number;
  validators: ValidatorEntry[];
  /** Omit to treat every confirmation request as a plain block. */
  confirm?: ConfirmationHandler;
  now?: () => Date;
}

export interface ValidatorFailure {
  interceptor: string;
  mode: InterceptorMode;
  error: unknown;
}

export interface ValidationPhaseOutcome {
  allowed: boolean;
  /** Why the payload was blocked. */
  reason?: string;
  /** Latest result from each validator that returned one, audit included. */
  results: ValidationResult[];
  failures: ValidatorFailure[];
  /** Confirmations given in this phase. */
  confirmations: Confirmation[];
  /**
   * Set when the payload was allowed because of confirmations. Before
   * sending or processing, check the final payload with isConfirmedCall:
   * mutations that run after validation must not change it.
   */
  confirmedCallHash?: string;
}

interface Attempt {
  entry: ValidatorEntry;
  result?: ValidationResult;
  error?: unknown;
}

function isActive(entry: ValidatorEntry): boolean {
  return (entry.mode ?? 'active') === 'active';
}

function isBlockingResult(result: ValidationResult): boolean {
  // A failed validation with no severity is treated as an error (fail-closed).
  return result.severity === 'error' || (!result.valid && result.severity === undefined);
}

function blocks(attempt: Attempt): boolean {
  if (!isActive(attempt.entry)) {
    return false;
  }
  if (attempt.error !== undefined) {
    return !attempt.entry.failOpen;
  }
  return attempt.result !== undefined && isBlockingResult(attempt.result);
}

function describe(attempt: Attempt): string {
  if (attempt.error !== undefined) {
    return `validator "${attempt.entry.name}" failed`;
  }
  const message = attempt.result?.messages?.[0]?.message;
  return `validator "${attempt.entry.name}" blocked${message ? `: ${message}` : ''}`;
}

/**
 * Runs the validators of one phase and decides whether the payload may
 * proceed, asking for confirmation when a validator requires it.
 */
export async function runValidationPhase(options: ValidationPhaseOptions): Promise<ValidationPhaseOutcome> {
  const now = options.now ?? (() => new Date());
  const { event, phase, payload, target } = options;
  const callHash = computeCallHash({ event, phase, target, payload });

  const invoke = async (entry: ValidatorEntry, confirmations?: Confirmation[]): Promise<Attempt> => {
    const params: InterceptorInvocationParams = {
      name: entry.name,
      event,
      phase,
      payload,
      config: entry.config,
      timeoutMs: options.timeoutMs,
      context: {
        ...options.context,
        timestamp: now().toISOString(),
        target,
        confirmations,
      },
    };
    try {
      return { entry, result: await entry.invoke(params) };
    } catch (error) {
      return { entry, error };
    }
  };

  const outcome = (
    attempts: Attempt[],
    allowed: boolean,
    reason?: string,
    confirmations: Confirmation[] = [],
  ): ValidationPhaseOutcome => ({
    allowed,
    reason,
    results: attempts.flatMap((a) => (a.result ? [a.result] : [])),
    failures: attempts.flatMap((a) =>
      a.error !== undefined ? [{ interceptor: a.entry.name, mode: a.entry.mode ?? 'active', error: a.error }] : [],
    ),
    confirmations,
    confirmedCallHash: allowed && confirmations.length > 0 ? callHash : undefined,
  });

  const first = await Promise.all(options.validators.map((entry) => invoke(entry)));
  const blocking = first.filter(blocks);
  if (blocking.length === 0) {
    return outcome(first, true);
  }

  // A confirmation never overrides a plain block.
  const plain = blocking.find((a) => !a.result?.confirmation);
  if (plain) {
    return outcome(first, false, describe(plain));
  }
  if (!options.confirm) {
    return outcome(first, false, `${describe(blocking[0])} (confirmation required)`);
  }

  const confirmations: Confirmation[] = [];
  for (const attempt of blocking) {
    const request = attempt.result!.confirmation!;
    const decision = await ask(options.confirm, {
      interceptor: attempt.entry.name,
      request,
      event,
      phase,
      target,
      payload,
    }, callHash, now);
    if (typeof decision === 'string') {
      return outcome(first, false, `validator "${attempt.entry.name}": ${decision}`, confirmations);
    }
    confirmations.push({
      interceptor: attempt.entry.name,
      id: request.id,
      callHash: request.callHash,
      token: request.token,
      decidedAt: now().toISOString(),
      principal: decision.principal,
    });
  }

  // Re-validate with each validator's own confirmation attached.
  const retried = await Promise.all(
    blocking.map((attempt) =>
      invoke(
        attempt.entry,
        confirmations.filter((c) => c.interceptor === attempt.entry.name),
      ),
    ),
  );
  const attempts = first.map((a) => retried.find((r) => r.entry === a.entry) ?? a);
  const stillBlocking = retried.find(blocks);
  if (stillBlocking) {
    return outcome(attempts, false, describe(stillBlocking), confirmations);
  }
  return outcome(attempts, true, undefined, confirmations);
}

/** Asks for one confirmation. Returns the approval, or why it was not obtained. */
async function ask(
  handler: ConfirmationHandler,
  prompt: ConfirmationPrompt,
  callHash: string,
  now: () => Date,
): Promise<ConfirmationDecision | string> {
  const { request } = prompt;
  if (request.callHash !== callHash) {
    return 'confirmation request is not bound to this call';
  }
  const expiresAt = request.expiresAt === undefined ? undefined : Date.parse(request.expiresAt);
  if (expiresAt !== undefined && (Number.isNaN(expiresAt) || now().getTime() >= expiresAt)) {
    return 'confirmation request expired';
  }

  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expired = new Promise<string>((resolve) => {
    if (expiresAt !== undefined) {
      timer = setTimeout(() => {
        controller.abort();
        resolve('no decision before the confirmation request expired');
      }, expiresAt - now().getTime());
    }
  });
  try {
    const decision = await Promise.race([handler(prompt, controller.signal), expired]);
    if (typeof decision === 'string') {
      return decision;
    }
    if (!decision.approved) {
      return 'confirmation denied';
    }
    if (expiresAt !== undefined && now().getTime() >= expiresAt) {
      return 'confirmation given after the request expired';
    }
    return decision;
  } catch {
    return 'confirmation could not be obtained';
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Checks that the payload about to be sent or processed, after any
 * mutations that ran after validation, is the one that was confirmed.
 * If this returns false the invoker MUST block.
 */
export function isConfirmedCall(
  call: CallBinding | Pick<InterceptorInvocationParams, 'event' | 'phase' | 'payload' | 'context'>,
  confirmedCallHash: string,
): boolean {
  const binding = 'target' in call ? call : callBinding(call);
  return computeCallHash(binding) === confirmedCallHash;
}
