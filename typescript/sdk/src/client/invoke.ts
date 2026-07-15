// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Thin wrappers over `Client.request` for the two SEP methods. Every response
 * crosses the wire boundary (`normalizeInterceptor` / `normalizeResult`)
 * before the interior sees it; an unknown result `type` therefore surfaces as
 * a thrown `WireError` — which the chain executor treats as an interceptor
 * failure subject to `failOpen`, never a crash.
 */
import type { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { INTERCEPTORS_CAPABILITY, RPC_METHOD } from "../protocol/constants.js";
import type { InterceptionEvent } from "../protocol/constants.js";
import {
  InterceptorResultSchema,
  ListInterceptorsResultSchema,
} from "../protocol/rpc-schemas.js";
import { serializeInvokeParams } from "../protocol/serialize.js";
import { normalizeInterceptor, normalizeResult } from "../protocol/wire.js";
import type {
  InterceptorResult,
  InvokeParams,
  ListInterceptorsResult,
} from "../protocol/types.js";

export interface ListInterceptorsOptions {
  readonly event: InterceptionEvent | null;
  readonly cursor: string | null;
}

const LIST_ALL: ListInterceptorsOptions = { event: null, cursor: null };

export async function listInterceptors(
  client: Client,
  options: ListInterceptorsOptions = LIST_ALL,
  signal: AbortSignal | null = null,
): Promise<ListInterceptorsResult> {
  const params: Record<string, unknown> = {};
  if (options.event !== null) params.event = options.event;
  if (options.cursor !== null) params.cursor = options.cursor;
  const raw = await client.request(
    { method: RPC_METHOD.InterceptorsList, params },
    ListInterceptorsResultSchema,
    signal === null ? undefined : { signal },
  );
  return {
    interceptors: raw.interceptors.map(normalizeInterceptor),
    nextCursor: raw.nextCursor ?? null,
  };
}

export async function invokeInterceptor(
  client: Client,
  params: InvokeParams,
  signal: AbortSignal | null = null,
): Promise<InterceptorResult> {
  const raw = await client.request(
    { method: RPC_METHOD.InterceptorInvoke, params: serializeInvokeParams(params) },
    InterceptorResultSchema,
    signal === null ? undefined : { signal },
  );
  return normalizeResult(raw);
}

/** Did the connected server advertise the interceptors extension? */
export function hasInterceptorsCapability(client: Client): boolean {
  const extensions = client.getServerCapabilities()?.extensions;
  return extensions !== undefined && INTERCEPTORS_CAPABILITY in extensions;
}
