// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Client } from "@modelcontextprotocol/client";

import type { Interceptor, InterceptorOverrides } from '../protocol/types.js';

/** One interceptor descriptor and the MCP client for the host that registered it. */
export interface InterceptorChainEntry {
  descriptor: Interceptor;
  client: Client;
  /** Diagnostic label for this host (duplicate-name errors, logging). */
  hostLabel: string;
  /** Invoker-side execution policy for this entry (SEP chain-entry overrides). */
  overrides?: InterceptorOverrides;
}

/** Identifies an interceptor host when listing or merging chain entries. */
export interface InterceptorChainHost {
  client: Client;
  /** Shown in errors; defaults to `host-0`, `host-1`, … by array index. */
  label?: string;
}

/**
 * How to handle the same interceptor `name` from more than one host after merge.
 * - `error` (default): throw before the chain runs (SEP-global unique name).
 * - `first-wins`: keep the first entry in host order; ignore later duplicates.
 */
export type DuplicateInterceptorNamePolicy = 'error' | 'first-wins';

export interface MergeInterceptorChainEntriesOptions {
  duplicateNamePolicy?: DuplicateInterceptorNamePolicy;
}
