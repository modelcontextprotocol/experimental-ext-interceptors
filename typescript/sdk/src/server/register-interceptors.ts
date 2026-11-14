// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Server } from '@modelcontextprotocol/server';
import { matchesEvent } from '../client/chain-orchestrator.js';
import { InterceptorRequestMethods } from '../protocol/constants.js';
import type {
  Interceptor,
  InterceptorResult,
  InvokeInterceptorRequestParams,
} from '../protocol/types.js';
import {
  InterceptorResultSchema,
  InvokeInterceptorParamsSchema,
  InvokeInterceptorRequestSchema,
  ListInterceptorsParamsSchema,
  ListInterceptorsRequestSchema,
  ListInterceptorsResultSchema,
} from '../protocol/zod-schemas.js';
import { interceptorNotFoundError, interceptorTimeoutError } from '../protocol/mcp-errors.js';
import { registerInterceptorCapabilities } from './capabilities.js';

export type InterceptorHandler = (
  params: InvokeInterceptorRequestParams,
  signal?: AbortSignal,
) => InterceptorResult | Promise<InterceptorResult>;

export interface RegisteredInterceptor {
  descriptor: Interceptor;
  handler: InterceptorHandler;
}

export interface RegisterInterceptorsOptions {
  /** When true (default), merge the interceptors extensions capability from registered hooks. */
  registerCapabilities?: boolean;
}

export function registerInterceptorsOnServer(
  server: Server,
  interceptors: RegisteredInterceptor[],
  options?: RegisterInterceptorsOptions,
): void {
  const registerCaps = options?.registerCapabilities !== false;
  const descriptors = interceptors.map((e) => e.descriptor);
  const byName = new Map<string, RegisteredInterceptor>();
  for (const entry of interceptors) {
    if (byName.has(entry.descriptor.name)) {
      // A silent Map overwrite would make interceptor/invoke dispatch to the
      // wrong handler; duplicate names are a registration-time bug.
      throw new Error(`Duplicate interceptor name: '${entry.descriptor.name}'`);
    }
    byName.set(entry.descriptor.name, entry);
  }

  if (registerCaps) {
    registerInterceptorCapabilities(server, descriptors);
  }

  server.setRequestHandler(
    InterceptorRequestMethods.InterceptorsList,
    { params: ListInterceptorsParamsSchema.optional() },
    (params) => {
      const eventFilter = params?.event;
      const listed: Interceptor[] = [];

      for (const entry of interceptors) {
        if (eventFilter) {
          const matchesAnyHook = entry.descriptor.hooks.some((hook) =>
            matchesEvent(hook.events, eventFilter),
          );
          if (!matchesAnyHook) {
            continue;
          }
        }
        listed.push(entry.descriptor);
      }

      return { interceptors: listed };
    },
  );

  server.setRequestHandler(
    InterceptorRequestMethods.InterceptorInvoke,
    { params: InvokeInterceptorParamsSchema },
    async (params) => {
      const invokeParams = params as InvokeInterceptorRequestParams;
      const entry = byName.get(invokeParams.name);
      if (!entry) {
        throw interceptorNotFoundError(invokeParams.name);
      }

      const signal =
        invokeParams.timeoutMs != null ? AbortSignal.timeout(invokeParams.timeoutMs) : undefined;

      try {
        // Race the handler against the timeout signal so a handler that ignores
        // the signal cannot hold the request past timeoutMs.
        const result = await raceWithSignal(
          Promise.resolve(entry.handler(invokeParams, signal)),
          signal,
        );
        result.interceptor = entry.descriptor.name;
        result.phase = invokeParams.phase;
        return result as unknown as Record<string, unknown>;
      } catch (err) {
        if (signal?.aborted && invokeParams.timeoutMs != null) {
          throw interceptorTimeoutError(invokeParams.name, invokeParams.timeoutMs, invokeParams.phase);
        }
        throw err;
      }
    },
  );
}

function raceWithSignal<T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> {
  if (!signal) {
    return promise;
  }
  return new Promise<T>((resolve, reject) => {
    const onAbort = () => reject(signal.reason);
    if (signal.aborted) {
      onAbort();
    } else {
      signal.addEventListener('abort', onAbort, { once: true });
    }
    promise.then(
      (value) => {
        signal.removeEventListener('abort', onAbort);
        resolve(value);
      },
      (err) => {
        signal.removeEventListener('abort', onAbort);
        reject(err);
      },
    );
  });
}

/** @internal For tests validating handler registration schemas. */
export const interceptorWireSchemas = {
  ListInterceptorsRequestSchema,
  ListInterceptorsResultSchema,
  InvokeInterceptorRequestSchema,
  InterceptorResultSchema,
};
