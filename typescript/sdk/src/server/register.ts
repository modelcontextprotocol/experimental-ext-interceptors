// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Wires an {@link InterceptorRegistry} into an MCP `Server`: advertises the
 * extension capability (SEP-2133 `extensions` key) and handles the two SEP
 * methods. Inbound params cross the wire boundary through
 * `normalizeInvokeParams`; outbound results leave through `serialize*` - the
 * interior never touches raw JSON.
 */
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { INTERCEPTORS_CAPABILITY, RPC_METHOD } from "../protocol/constants.js";
import {
  InvokeInterceptorRequestSchema,
  ListInterceptorsRequestSchema,
} from "../protocol/rpc-schemas.js";
import { serializeInterceptor, serializeResult } from "../protocol/serialize.js";
import { normalizeInvokeParams } from "../protocol/wire.js";
import type { InterceptorRegistry } from "./registry.js";

/** The capability payload advertised under `extensions`. */
export interface InterceptorsCapability {
  readonly supportedEvents: readonly string[];
  readonly methods: readonly string[];
}

export function interceptorsCapability(
  registry: InterceptorRegistry,
): InterceptorsCapability {
  return {
    supportedEvents: registry.supportedEvents,
    methods: Object.values(RPC_METHOD),
  };
}

export function registerInterceptorsOnServer(
  server: Server,
  registry: InterceptorRegistry,
): void {
  server.registerCapabilities({
    extensions: { [INTERCEPTORS_CAPABILITY]: interceptorsCapability(registry) },
  });

  server.setRequestHandler(ListInterceptorsRequestSchema, (request) => ({
    interceptors: registry
      .list(request.params?.event ?? null)
      .map(serializeInterceptor),
  }));

  server.setRequestHandler(InvokeInterceptorRequestSchema, async (request) =>
    serializeResult(await registry.invoke(normalizeInvokeParams(request.params))),
  );
}
