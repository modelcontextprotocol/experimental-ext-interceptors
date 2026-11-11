// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { ProtocolError, ProtocolErrorCode } from "@modelcontextprotocol/server";


/** JSON-RPC invalid params (-32602), aligned with C# `InterceptorMessageFilter`. */
export function interceptorNotFoundError(interceptorName: string): ProtocolError {
  return new ProtocolError(ProtocolErrorCode.InvalidParams, `Interceptor '${interceptorName}' not found`);
}

/** SEP-2624 interceptor timeout: -32000 with `{ interceptor, timeoutMs, phase }` data. */
export function interceptorTimeoutError(
  interceptor: string,
  timeoutMs: number,
  phase: string,
): ProtocolError {
  return new ProtocolError(
    -32000,
    `Interceptor '${interceptor}' timed out after ${timeoutMs}ms`,
    { interceptor, timeoutMs, phase },
  );
}

export function isInvalidParamsError(err: unknown): boolean {
  return (
    typeof err === 'object' &&
    err !== null &&
    'code' in err &&
    (err as { code: unknown }).code === ProtocolErrorCode.InvalidParams
  );
}
