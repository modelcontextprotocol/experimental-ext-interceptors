// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The thin JSON-RPC handshake schemas the MCP SDK needs to route
 * `interceptors/list` and `interceptor/invoke`. These stay deliberately
 * permissive - the REAL normalization is wire.ts (`normalizeInterceptor`,
 * `normalizeResult`, `normalizeInvokeParams`). Validating twice would duplicate
 * the boundary (the exact bloat we removed), so here we only pin the method
 * literals the SDK dispatches on and let the trusted interior do the rest.
 */
import * as z from "zod/v4";
import { RequestSchema, ResultSchema } from "@modelcontextprotocol/sdk/types.js";
import { RPC_METHOD } from "./constants.js";

export const ListInterceptorsRequestSchema = RequestSchema.extend({
  method: z.literal(RPC_METHOD.InterceptorsList),
  params: z
    .looseObject({
      cursor: z.string().optional(),
      event: z.string().optional(),
    })
    .optional(),
});

export const InvokeInterceptorRequestSchema = RequestSchema.extend({
  method: z.literal(RPC_METHOD.InterceptorInvoke),
  params: z.looseObject({
    name: z.string(),
    event: z.string(),
    phase: z.string(),
  }),
});

export const ListInterceptorsResultSchema = ResultSchema.extend({
  interceptors: z.array(z.unknown()),
  nextCursor: z.string().optional(),
});

export const InterceptorResultSchema = ResultSchema.extend({
  type: z.string(),
  phase: z.string(),
});
