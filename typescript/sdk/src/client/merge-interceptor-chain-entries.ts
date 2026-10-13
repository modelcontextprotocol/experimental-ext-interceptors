// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import type { Interceptor } from '../protocol/types.js';
import { DuplicateInterceptorNameError } from '../protocol/errors.js';
import { listAllInterceptors } from './client-extensions.js';
import type {
  InterceptorChainEntry,
  InterceptorChainHost,
  MergeInterceptorChainEntriesOptions,
} from './interceptor-chain-entry.js';

function hostLabel(host: InterceptorChainHost, index: number): string {
  return host.label?.trim() || `host-${index}`;
}

/**
 * Lists interceptors from each host and returns flat entries (no duplicate check).
 */
export async function listInterceptorChainEntries(
  hosts: InterceptorChainHost[],
  listParams?: { event?: string },
): Promise<InterceptorChainEntry[]> {
  const listedByHost = await Promise.all(
    hosts.map(async (host, i) => {
      if (!host) {
        return [] as InterceptorChainEntry[];
      }
      const label = hostLabel(host, i);
      const listed = await listAllInterceptors(host.client, listParams);
      return listed.interceptors.map((descriptor) => ({
        descriptor,
        client: host.client,
        hostLabel: label,
      }));
    }),
  );

  return listedByHost.flat();
}

/**
 * Applies duplicate-name policy after {@link listInterceptorChainEntries}.
 */
export function mergeInterceptorChainEntries(
  entries: InterceptorChainEntry[],
  options?: MergeInterceptorChainEntriesOptions,
): InterceptorChainEntry[] {
  const policy = options?.duplicateNamePolicy ?? 'error';
  const byName = new Map<string, InterceptorChainEntry[]>();

  for (const entry of entries) {
    const name = entry.descriptor.name;
    let group = byName.get(name);
    if (!group) {
      group = [];
      byName.set(name, group);
    }
    group.push(entry);
  }

  const conflicts: Array<{ name: string; hosts: string[] }> = [];
  for (const [name, group] of byName) {
    if (group.length > 1) {
      conflicts.push({ name, hosts: group.map((e) => e.hostLabel) });
    }
  }

  if (policy === 'error' && conflicts.length > 0) {
    throw new DuplicateInterceptorNameError(conflicts);
  }

  if (policy === 'first-wins') {
    const merged: InterceptorChainEntry[] = [];
    const seen = new Set<string>();
    for (const entry of entries) {
      if (seen.has(entry.descriptor.name)) {
        continue;
      }
      seen.add(entry.descriptor.name);
      merged.push(entry);
    }
    return merged;
  }

  return entries;
}

export function interceptorsFromEntries(entries: InterceptorChainEntry[]): Interceptor[] {
  return entries.map((e) => e.descriptor);
}

export function clientByInterceptorName(
  entries: InterceptorChainEntry[],
): Map<string, InterceptorChainEntry['client']> {
  return new Map(entries.map((e) => [e.descriptor.name, e.client]));
}
