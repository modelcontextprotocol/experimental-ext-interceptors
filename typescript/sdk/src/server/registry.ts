// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * In-memory interceptor registry: the pure core behind `interceptors/list`
 * and `interceptor/invoke`. A closure-based factory (no class), fail-fast on
 * duplicate names (SEP names are chain-global), transport-agnostic.
 */
import type { InterceptionEvent } from "../protocol/constants.js";
import { interceptorNotFound } from "../protocol/errors.js";
import { interceptsEvent } from "../protocol/match-event.js";
import type { Interceptor, InterceptorResult, InvokeParams } from "../protocol/types.js";
import type { RegisteredInterceptor } from "./define-interceptor.js";

export interface InterceptorRegistry {
  readonly descriptors: readonly Interceptor[];
  /** Union of all hook events, for the capability advertisement. */
  readonly supportedEvents: readonly InterceptionEvent[];
  readonly list: (event: InterceptionEvent | null) => readonly Interceptor[];
  readonly invoke: (params: InvokeParams) => Promise<InterceptorResult>;
}

export function createRegistry(
  entries: readonly RegisteredInterceptor[],
): InterceptorRegistry {
  const byName = new Map(entries.map((e) => [e.descriptor.name, e]));
  if (byName.size !== entries.length) {
    const seen = new Set<string>();
    const dup = entries
      .map((e) => e.descriptor.name)
      .find((n) => seen.has(n) || (seen.add(n), false));
    throw new Error(`duplicate interceptor name: '${String(dup)}'`);
  }

  const descriptors = entries.map((e) => e.descriptor);
  const supportedEvents = [
    ...new Set(descriptors.flatMap((d) => d.hooks.flatMap((h) => h.events))),
  ];

  return {
    descriptors,
    supportedEvents,
    list: (event) =>
      event === null
        ? descriptors
        : descriptors.filter((d) => interceptsEvent(d, event)),
    invoke: async (params) => {
      const entry = byName.get(params.name);
      if (entry === undefined) throw interceptorNotFound(params.name);
      const signal =
        params.timeoutMs === null ? null : AbortSignal.timeout(params.timeoutMs);
      const started = Date.now();
      try {
        const result = await entry.handler(params, signal);
        return { ...result, durationMs: Date.now() - started };
      } catch (err) {
        if (signal?.aborted) {
          throw new Error(
            `Interceptor '${params.name}' timed out after ${String(params.timeoutMs)}ms`,
          );
        }
        throw err;
      }
    },
  };
}
