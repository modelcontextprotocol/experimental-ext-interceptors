// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

/** Per-request context passed to {@link McpInterceptorGatewayOptions.interceptorServerConnectionResolver}. */
export interface GatewayMessageContext {
  /** JSON-RPC method for the proxied MCP request (e.g. `tools/call`). */
  method: string;
  params?: unknown;
  /** From MCP server `RequestHandlerExtra` when present. */
  authInfo?: unknown;
  sessionId?: string;
}
