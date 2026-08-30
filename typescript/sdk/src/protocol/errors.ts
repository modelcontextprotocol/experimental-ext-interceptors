// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { isValidationResult } from './results.js';
import type {
  InterceptorChainResult,
  InterceptorChainStatus,
  InterceptorPhase,
  ValidationMessage,
} from './types.js';

export class McpInterceptorValidationException extends Error {
  readonly validationMessages: readonly ValidationMessage[];
  readonly chainResult?: InterceptorChainResult;

  constructor(
    message: string,
    validationMessages: readonly ValidationMessage[] = [],
    chainResult?: InterceptorChainResult,
  ) {
    super(message);
    this.name = 'McpInterceptorValidationException';
    this.validationMessages = validationMessages;
    this.chainResult = chainResult;
  }
}

export class McpInterceptorChainException extends Error {
  readonly operation: string;
  readonly phase: InterceptorPhase;
  readonly status: InterceptorChainStatus;

  constructor(message: string, operation: string, phase: InterceptorPhase, status: InterceptorChainStatus) {
    super(message);
    this.name = 'McpInterceptorChainException';
    this.operation = operation;
    this.phase = phase;
    this.status = status;
  }
}

export class DuplicateInterceptorNameError extends Error {
  readonly conflicts: ReadonlyArray<{ name: string; hosts: readonly string[] }>;

  constructor(conflicts: Array<{ name: string; hosts: string[] }>) {
    const detail = conflicts
      .map((c) => `'${c.name}' on ${c.hosts.map((h) => `'${h}'`).join(', ')}`)
      .join('; ');
    super(
      `Duplicate interceptor name(s) across hosts: ${detail}. ` +
        'Use globally unique interceptor names or duplicateNamePolicy: "first-wins".',
    );
    this.name = 'DuplicateInterceptorNameError';
    this.conflicts = conflicts;
  }
}

/**
 * Thrown when chain-entry `overrides.hooks` widen beyond the interceptor's
 * declared hooks (SEP-2624: implementations MUST reject widening overrides).
 */
export class InterceptorOverrideHookError extends Error {
  readonly interceptor: string;

  constructor(interceptor: string, detail: string) {
    super(`Override hooks for interceptor '${interceptor}' widen beyond its declared hooks: ${detail}`);
    this.name = 'InterceptorOverrideHookError';
    this.interceptor = interceptor;
  }
}

/** Validation messages from chain results where the interceptor reported `valid: false`. */
export function collectValidationErrorMessages(
  chainResult: InterceptorChainResult,
): ValidationMessage[] {
  const messages: ValidationMessage[] = [];
  for (const result of chainResult.results) {
    if (isValidationResult(result) && !result.valid && result.messages) {
      messages.push(...result.messages);
    }
  }
  return messages;
}

function phaseLabel(phase: InterceptorPhase): string {
  return `${phase} phase`;
}

function eventLabel(operation: string, chainResult?: InterceptorChainResult): string {
  return chainResult?.event ?? operation;
}

/**
 * SEP-aligned message: validator ran, reported invalid, chain status `validation_failed`.
 */
export function formatValidationFailedChainMessage(
  operation: string,
  chainResult: InterceptorChainResult,
): string {
  const event = eventLabel(operation, chainResult);
  const phase = chainResult.phase;
  const messages = collectValidationErrorMessages(chainResult);
  const aborted = chainResult.abortedAt;

  const interceptorName =
    aborted?.type === 'validation'
      ? aborted.interceptor
      : chainResult.results.find((r) => isValidationResult(r) && !r.valid)?.interceptor;

  const nameClause = interceptorName
    ? `validation interceptor "${interceptorName}" reported invalid`
    : 'validation interceptor reported invalid';

  const policyReason = messages.map((m) => m.message).filter(Boolean).join('; ');
  const reason =
    policyReason ||
    (aborted?.type === 'validation' ? aborted.reason : undefined) ||
    '';

  const detail = reason ? ` — ${reason}` : '';

  return `${event} (${phaseLabel(phase)}): ${nameClause}${detail}`;
}

function formatMutationFailedChainMessage(
  operation: string,
  phase: InterceptorPhase,
  chainResult?: InterceptorChainResult,
): string {
  const event = eventLabel(operation, chainResult);
  const aborted = chainResult?.abortedAt;
  if (aborted?.type === 'mutation') {
    return `${event} (${phaseLabel(phase)}): mutation interceptor "${aborted.interceptor}" failed — ${aborted.reason}`;
  }
  return `${event} (${phaseLabel(phase)}): interceptor chain aborted with status 'mutation_failed'.`;
}

function formatTimeoutChainMessage(
  operation: string,
  phase: InterceptorPhase,
  chainResult?: InterceptorChainResult,
): string {
  const event = eventLabel(operation, chainResult);
  const aborted = chainResult?.abortedAt;
  if (aborted?.type === 'timeout') {
    return `${event} (${phaseLabel(phase)}): interceptor chain timed out at "${aborted.interceptor}" — ${aborted.reason}`;
  }
  return `${event} (${phaseLabel(phase)}): interceptor chain timed out.`;
}

export function throwChainFailure(
  operation: string,
  phase: InterceptorPhase,
  status: InterceptorChainStatus,
  chainResult?: InterceptorChainResult,
): never {
  if (status === 'validation_failed') {
    const messages = chainResult ? collectValidationErrorMessages(chainResult) : [];
    const message = chainResult
      ? formatValidationFailedChainMessage(operation, { ...chainResult, phase })
      : `${operation} (${phaseLabel(phase)}): validation interceptor reported invalid — chain status validation_failed`;
    throw new McpInterceptorValidationException(message, messages, chainResult);
  }

  const message =
    status === 'mutation_failed'
      ? formatMutationFailedChainMessage(operation, phase, chainResult)
      : status === 'timeout'
        ? formatTimeoutChainMessage(operation, phase, chainResult)
        : `${eventLabel(operation, chainResult)} (${phaseLabel(phase)}): interceptor chain aborted with status '${status}'.`;

  throw new McpInterceptorChainException(message, operation, phase, status);
}
