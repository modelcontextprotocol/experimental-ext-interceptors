// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Multi-host chain execution: discover interceptors on one or more connected
 * MCP clients, merge them under the SEP's chain-global-unique-name rule, and
 * run {@link executeChain} with `interceptor/invoke` routed to the host that
 * advertised each name. `createChainRunner` adds cached discovery plus default
 * event filter / timeout / context — the shared engine behind the intercepting
 * client and the gateway.
 */
import type { Client } from "@modelcontextprotocol/sdk/client/index.js";
import type { InterceptionEvent, InterceptorPhase } from "../protocol/constants.js";
import { CHAIN_STATUS } from "../protocol/constants.js";
import { matchesEvent } from "../protocol/match-event.js";
import type { Interceptor, InvokeContext } from "../protocol/types.js";
import { executeChain } from "./chain.js";
import type { ChainParams, ChainResult, InterceptorInvoker } from "./chain.js";
import { ChainBlockedError, DuplicateInterceptorNameError } from "./errors.js";
import { invokeInterceptor, listInterceptors } from "./invoke.js";

/** How to resolve the same interceptor name advertised by multiple hosts. */
export const DUPLICATE_NAME_POLICY = {
  Error: "error",
  FirstWins: "first-wins",
} as const;
export type DuplicateNamePolicy =
  (typeof DUPLICATE_NAME_POLICY)[keyof typeof DUPLICATE_NAME_POLICY];

export interface HostedInterceptor {
  readonly descriptor: Interceptor;
  readonly client: Client;
  readonly host: string;
}

function groupByName(
  entries: readonly HostedInterceptor[],
): ReadonlyMap<string, readonly HostedInterceptor[]> {
  const byName = new Map<string, HostedInterceptor[]>();
  for (const e of entries) {
    const group = byName.get(e.descriptor.name) ?? [];
    group.push(e);
    byName.set(e.descriptor.name, group);
  }
  return byName;
}

/** RULE 2: duplicate-name resolution dispatches on policy. */
const APPLY_POLICY: Record<
  DuplicateNamePolicy,
  (entries: readonly HostedInterceptor[]) => readonly HostedInterceptor[]
> = {
  [DUPLICATE_NAME_POLICY.Error]: (entries) => {
    for (const [name, group] of groupByName(entries)) {
      if (group.length > 1) {
        throw new DuplicateInterceptorNameError(
          name,
          group.map((e) => e.host),
        );
      }
    }
    return entries;
  },
  [DUPLICATE_NAME_POLICY.FirstWins]: (entries) =>
    [...groupByName(entries).values()].map((group) => group[0]),
};

/** List every host's interceptors and apply the duplicate-name policy. */
export async function discoverInterceptors(
  hosts: readonly Client[],
  policy: DuplicateNamePolicy = DUPLICATE_NAME_POLICY.Error,
): Promise<readonly HostedInterceptor[]> {
  const perHost = await Promise.all(
    hosts.map(async (client, i) => {
      const listed = await listInterceptors(client);
      return listed.interceptors.map((descriptor) => ({
        descriptor,
        client,
        host: `host-${String(i)}`,
      }));
    }),
  );
  return APPLY_POLICY[policy](perHost.flat());
}

/** Route each `interceptor/invoke` to the host that advertised the name. */
export function routedInvoker(
  hosted: readonly HostedInterceptor[],
): InterceptorInvoker {
  const byName = new Map(hosted.map((h) => [h.descriptor.name, h.client]));
  return (params, signal) => {
    const client = byName.get(params.name);
    if (client === undefined) {
      throw new Error(`no host advertises interceptor '${params.name}'`);
    }
    return invokeInterceptor(client, params, signal);
  };
}

/** One-shot: discover across hosts, then run a single chain. */
export async function executeChainOnClients(
  hosts: readonly Client[],
  params: ChainParams,
  policy: DuplicateNamePolicy = DUPLICATE_NAME_POLICY.Error,
  signal: AbortSignal | null = null,
): Promise<ChainResult> {
  const hosted = await discoverInterceptors(hosts, policy);
  return executeChain(
    hosted.map((h) => h.descriptor),
    routedInvoker(hosted),
    params,
    signal,
  );
}

export interface ChainRunnerSpec {
  readonly hosts: readonly Client[];
  /** Only these events are intercepted; omit to intercept every event. */
  readonly events?: readonly InterceptionEvent[];
  readonly timeoutMs?: number;
  readonly context?: InvokeContext;
  readonly duplicateNames?: DuplicateNamePolicy;
}

export interface ChainRunner {
  readonly shouldIntercept: (event: InterceptionEvent) => boolean;
  readonly run: (
    event: InterceptionEvent,
    phase: InterceptorPhase,
    payload: unknown,
    signal?: AbortSignal | null,
  ) => Promise<ChainResult>;
  /** Like `run`, but throws {@link ChainBlockedError} unless the chain passed. */
  readonly runOrThrow: (
    event: InterceptionEvent,
    phase: InterceptorPhase,
    payload: unknown,
    signal?: AbortSignal | null,
  ) => Promise<unknown>;
  /** Drop the cached discovery; the next run re-lists every host. */
  readonly refresh: () => void;
  readonly interceptors: () => Promise<readonly Interceptor[]>;
}

export function createChainRunner(spec: ChainRunnerSpec): ChainRunner {
  const events = spec.events ?? null;
  const timeoutMs = spec.timeoutMs ?? null;
  const context = spec.context ?? null;
  const policy = spec.duplicateNames ?? DUPLICATE_NAME_POLICY.Error;

  let discovery: Promise<readonly HostedInterceptor[]> | null = null;
  const discovered = (): Promise<readonly HostedInterceptor[]> => {
    discovery ??= discoverInterceptors(spec.hosts, policy).catch((err: unknown) => {
      discovery = null; // a failed discovery must not be cached forever
      throw err;
    });
    return discovery;
  };

  const run: ChainRunner["run"] = async (event, phase, payload, signal = null) => {
    const hosted = await discovered();
    return executeChain(
      hosted.map((h) => h.descriptor),
      routedInvoker(hosted),
      { event, phase, payload, names: null, timeoutMs, context },
      signal,
    );
  };

  return {
    shouldIntercept: (event) =>
      events === null || matchesEvent(events, event),
    run,
    runOrThrow: async (event, phase, payload, signal = null) => {
      const chain = await run(event, phase, payload, signal);
      if (chain.status !== CHAIN_STATUS.Success) {
        throw new ChainBlockedError(chain);
      }
      return chain.finalPayload;
    },
    refresh: () => {
      discovery = null;
    },
    interceptors: async () =>
      (await discovered()).map((h) => h.descriptor),
  };
}
