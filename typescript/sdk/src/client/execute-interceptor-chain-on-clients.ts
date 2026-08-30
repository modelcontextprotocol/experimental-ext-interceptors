// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { executeInterceptorChain } from './chain-orchestrator.js';
import { invokeInterceptor } from './client-extensions.js';
import type { InterceptorChainHost, MergeInterceptorChainEntriesOptions } from './interceptor-chain-entry.js';
import {
  clientByInterceptorName,
  listInterceptorChainEntries,
  mergeInterceptorChainEntries,
} from './merge-interceptor-chain-entries.js';
import type {
  ChainInterceptorEntry,
  ExecuteChainRequestParams,
  InterceptorChainResult,
  InterceptorOverrides,
} from '../protocol/types.js';

export interface ExecuteInterceptorChainOnClientsOptions extends MergeInterceptorChainEntriesOptions {
  signal?: AbortSignal;
  /** Invoker-side execution policy per interceptor name (SEP chain-entry overrides). */
  overrides?: Record<string, InterceptorOverrides>;
}

/**
 * SEP-aligned multi-host chain: discover on each client, merge, then run
 * {@link executeInterceptorChain} with routed `interceptor/invoke` calls.
 * An empty host list is a no-op chain that reports `success`.
 */
export async function executeInterceptorChainOnClients(
  hosts: InterceptorChainHost[],
  params: ExecuteChainRequestParams,
  options?: ExecuteInterceptorChainOnClientsOptions,
): Promise<InterceptorChainResult> {
  const listed = await listInterceptorChainEntries(hosts, { event: params.event });
  const entries = mergeInterceptorChainEntries(listed, {
    duplicateNamePolicy: options?.duplicateNamePolicy,
  });
  const clients = clientByInterceptorName(entries);

  const chainEntries: ChainInterceptorEntry[] = entries.map((entry) => ({
    interceptor: entry.descriptor,
    overrides: entry.overrides ?? options?.overrides?.[entry.descriptor.name],
  }));

  return executeInterceptorChain(
    chainEntries,
    async (invokeParams, invokeSignal) => {
      const client = clients.get(invokeParams.name);
      if (!client) {
        throw new Error(`No host registered for interceptor '${invokeParams.name}'`);
      }
      try {
        return await invokeInterceptor(client, invokeParams, { signal: invokeSignal });
      } catch (err) {
        // The MCP SDK wraps abort reasons in McpError; surface the original
        // abort/timeout so the orchestrator can classify it.
        if (invokeSignal?.aborted && invokeSignal.reason instanceof Error) {
          throw invokeSignal.reason;
        }
        throw err;
      }
    },
    params,
    options?.signal,
  );
}
