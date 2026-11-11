// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Client, Implementation } from "@modelcontextprotocol/client";

export type InterceptorClientTransport = Parameters<Client['connect']>[0];

/** Options for connecting the gateway to an external interceptor host. */
export interface McpInterceptorServerConnectionOptions {
  /**
   * Stable id for reusing a connected client across requests.
   * When omitted, a client is created and closed after each resolution.
   */
  connectionId?: string;
  transport: InterceptorClientTransport;
  clientInfo?: Implementation;
  clientOptions?: ConstructorParameters<typeof Client>[1];
}
