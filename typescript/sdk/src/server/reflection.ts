// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { validationSuccess } from '../protocol/results.js';
import type {
  InterceptorResult,
  InterceptorType,
  InvokeInterceptorRequestParams,
} from '../protocol/types.js';
import {
  buildInterceptorDescriptor,
  type InterceptorDefinitionOptions,
} from './interceptor-definition.js';
import type { RegisteredInterceptor } from './register-interceptors.js';

export type InterceptorHandlerFn = (...args: unknown[]) =>
  | InterceptorResult
  | Promise<InterceptorResult>
  | boolean
  | Promise<boolean>;

/**
 * Build a {@link RegisteredInterceptor} from a handler function and definition options
 * (TypeScript equivalent of C# `[McpServerInterceptor]` + `ReflectionMcpServerInterceptor`).
 *
 * **Parameter binding (not full reflection):** handlers are invoked using parameter-name
 * heuristics from `Function.prototype.toString`, or positional arity fallback. This is weaker
 * than C# `MethodInfo` reflection. Supported shapes:
 *
 * - `(params) =>` where `params` receives the full {@link InvokeInterceptorRequestParams}
 * - `({ payload, phase, context }) =>` — destructuring from that same object
 * - `(payload, event, phase, context, signal) =>` — any subset, matched by name
 *
 * Handlers whose parameter names are all unrecognized fall back to positional binding
 * by arity: payload, event, phase, context, signal.
 *
 * Avoid `(...rest) =>` and default-parameter-only signatures; use `(params) =>` instead.
 */
export function defineInterceptor(
  options: InterceptorDefinitionOptions,
  fn: InterceptorHandlerFn,
): RegisteredInterceptor {
  return {
    descriptor: buildInterceptorDescriptor(options),
    handler: (params, signal) => invokeHandlerFunction(fn, options.type, params, signal),
  };
}

export async function invokeHandlerFunction(
  fn: InterceptorHandlerFn,
  interceptorType: InterceptorType,
  request: InvokeInterceptorRequestParams,
  signal?: AbortSignal,
): Promise<InterceptorResult> {
  const args = bindHandlerArguments(fn, request, signal);
  let result: unknown = fn(...args);

  if (result && typeof (result as Promise<unknown>).then === 'function') {
    result = await (result as Promise<unknown>);
  }

  return normalizeHandlerResult(result, request.phase, interceptorType);
}

/** `payload => …` — a single parameter written without parentheses. */
const BARE_ARROW_PARAM = /^\s*(?:async\s+)?([A-Za-z_$][\w$]*)\s*=>/;

function bindHandlerArguments(
  fn: InterceptorHandlerFn,
  request: InvokeInterceptorRequestParams,
  signal?: AbortSignal,
): unknown[] {
  const src = fn.toString();
  // A bare arrow has no parameter list to inspect; skipping the paren match keeps
  // an object literal in the body from being read as destructured parameters.
  const parenMatch = BARE_ARROW_PARAM.test(src) ? null : src.match(/^[^(]*\(([^)]*)\)/);
  const paramsList = parenMatch?.[1]?.trim() ?? '';

  // Destructuring: ({ payload, phase }) => … — pass the full invoke params object.
  if (paramsList.startsWith('{')) {
    return [request];
  }

  const params = new Map<string, unknown>([
    ['params', request],
    ['request', request],
    ['payload', request.payload],
    ['config', request.config],
    ['event', request.event],
    ['eventname', request.event],
    ['phase', request.phase],
    ['context', request.context],
    ['signal', signal],
    ['cancellationtoken', signal],
    ['ct', signal],
  ]);

  const paramNames = getParameterNames(fn);
  // Bind by name only when at least one name is known; otherwise the handler
  // uses its own naming and every argument would come through as undefined.
  if (paramNames.some((name) => params.has(name.toLowerCase()))) {
    return paramNames.map((name) => params.get(name.toLowerCase()));
  }

  // Positional fallback (arity-based)
  const arity = fn.length;
  const positional: unknown[] = [request.payload];
  if (arity >= 2) {
    positional.push(request.event);
  }
  if (arity >= 3) {
    positional.push(request.phase);
  }
  if (arity >= 4) {
    positional.push(request.context);
  }
  if (arity >= 5) {
    positional.push(signal);
  }
  return positional.slice(0, arity);
}

function getParameterNames(fn: InterceptorHandlerFn): string[] {
  const src = fn.toString();
  // Matched first so the parenthesised pattern below cannot read the arguments of
  // the first call in the body.
  const bare = src.match(BARE_ARROW_PARAM);
  if (bare?.[1]) {
    return [bare[1]];
  }
  const match = src.match(/^[^(]*\(([^)]*)\)/);
  if (!match?.[1]?.trim()) {
    return [];
  }
  return match[1]
    .split(',')
    .map(
      (p) =>
        p
          .trim()
          .replace(/^\.\.\./, '')
          // Cut at the first `=` or whitespace so default values (`payload={}`,
          // `phase = 'request'`) don't leak into the parameter name.
          .split(/[=\s]/)[0]
          ?.replace(/[?[\]]/g, '') ?? '',
    )
    .filter((n) => n.length > 0 && n !== '');
}

function normalizeHandlerResult(
  result: unknown,
  phase: InvokeInterceptorRequestParams['phase'],
  interceptorType: InterceptorType,
): InterceptorResult {
  if (typeof result === 'boolean') {
    if (interceptorType !== 'validation') {
      throw new Error(`Boolean return is only supported for validation interceptors`);
    }
    return result
      ? validationSuccess(phase)
      : {
          type: 'validation',
          phase,
          valid: false,
          severity: 'error',
          messages: [{ message: 'Validation failed', severity: 'error' }],
        };
  }

  if (typeof result !== 'object' || result === null || !('type' in result)) {
    throw new Error(
      `Interceptor handler must return InterceptorResult or boolean, got ${typeof result}`,
    );
  }

  const typed = result as InterceptorResult;
  typed.phase = phase;
  return typed;
}
