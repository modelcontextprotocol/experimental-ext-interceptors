// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Server, ServerCapabilities } from "@modelcontextprotocol/server";

import { InterceptionEvents, InterceptorExtensionCapabilityKey } from '../protocol/constants.js';
import type { Interceptor, InterceptorsCapability } from '../protocol/types.js';

export function collectSupportedEvents(interceptors: Interceptor[]): string[] {
  const events = new Set<string>();
  for (const interceptor of interceptors) {
    for (const hook of interceptor.hooks) {
      for (const ev of hook.events) {
        events.add(ev);
      }
    }
  }
  return [...events].sort();
}

export function buildInterceptorsCapability(interceptors: Interceptor[]): InterceptorsCapability {
  const supported = collectSupportedEvents(interceptors);
  return {
    supportedEvents: supported.length > 0 ? supported : [InterceptionEvents.All],
  };
}

/** Merge SEP `capabilities.extensions["io.modelcontextprotocol/interceptors"]` onto an MCP server. */
export function registerInterceptorCapabilities(
  server: Server,
  interceptors: Interceptor[],
): void {
  const capability = buildInterceptorsCapability(interceptors);
  server.registerCapabilities({
    extensions: {
      [InterceptorExtensionCapabilityKey]: capability,
    },
  } as unknown as ServerCapabilities);
}
