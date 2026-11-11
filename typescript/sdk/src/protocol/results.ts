// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

import { InterceptorResultSchema } from './zod-schemas.js';
import type {
  InterceptorPhase,
  InterceptorResult,
  MutationInterceptorResult,
  SinkInterceptorResult,
  ValidationInterceptorResult,
  ValidationMessage,
} from './types.js';

export function validationSuccess(phase: InterceptorPhase): ValidationInterceptorResult {
  return { type: 'validation', phase, valid: true };
}

export function validationFailure(
  phase: InterceptorPhase,
  ...messages: ValidationMessage[]
): ValidationInterceptorResult {
  return {
    type: 'validation',
    phase,
    valid: false,
    severity: 'error',
    messages,
  };
}

export function isValidationResult(r: InterceptorResult): r is ValidationInterceptorResult {
  return r.type === 'validation';
}

export function isMutationResult(r: InterceptorResult): r is MutationInterceptorResult {
  return r.type === 'mutation';
}

export function isSinkResult(r: InterceptorResult): r is SinkInterceptorResult {
  return r.type === 'sink';
}

/**
 * Parse a wire JSON value into a discriminated interceptor result.
 *
 * Delegates to {@link InterceptorResultSchema}, the same schema `interceptor/invoke`
 * responses are validated against, so both paths reject the same malformed input.
 */
export function parseInterceptorResult(value: unknown): InterceptorResult {
  return InterceptorResultSchema.parse(value) as InterceptorResult;
}
