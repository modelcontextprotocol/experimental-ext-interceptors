// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/** MCP error helpers for the interceptors extension. */
import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";

/** JSON-RPC invalid-params (-32602): an unknown interceptor name was invoked. */
export function interceptorNotFound(name: string): McpError {
  return new McpError(
    ErrorCode.InvalidParams,
    `Interceptor '${name}' not found`,
  );
}
