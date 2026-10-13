// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import type { Interceptor, InterceptorOverrides, InterceptorPhase } from './types.js';

/**
 * Resolves mutation `priorityHint` for the chain phase (SEP priority resolution).
 * An override `priorityHint` takes precedence over the interceptor's declared
 * default. Validation interceptors ignore priority; only call this when sorting
 * mutations.
 */
export function resolvePriority(
  interceptor: Pick<Interceptor, 'priorityHint'>,
  phase: InterceptorPhase,
  overrides?: Pick<InterceptorOverrides, 'priorityHint'>,
): number {
  const hint = overrides?.priorityHint ?? interceptor.priorityHint;
  if (hint === undefined) {
    return 0;
  }
  if (typeof hint === 'number') {
    return hint;
  }
  return hint[phase] ?? 0;
}
