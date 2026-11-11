// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Client } from "@modelcontextprotocol/client";

import type { McpInterceptorServerConnectionOptions } from './mcp-interceptor-server-connection-options.js';
import { connectInterceptorClient } from './connect-interceptor-client.js';
import { GatewayResolvedInterceptorClients } from './gateway-resolved-interceptor-clients.js';

/**
 * Reject as soon as `signal` aborts, without disturbing `promise`.
 *
 * A pooled connection outlives the request that happened to open it, so one caller
 * walking away must not cancel the connect the others are waiting on.
 */
function raceSignal<T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> {
  if (!signal) {
    return promise;
  }
  if (signal.aborted) {
    return Promise.reject(signal.reason);
  }
  return new Promise<T>((resolve, reject) => {
    const onAbort = () => reject(signal.reason);
    signal.addEventListener('abort', onAbort, { once: true });
    promise.then(
      (value) => {
        signal.removeEventListener('abort', onAbort);
        resolve(value);
      },
      (err) => {
        signal.removeEventListener('abort', onAbort);
        reject(err);
      },
    );
  });
}

export class GatewayInterceptorClientPool {
  private readonly cache = new Map<string, Promise<Client>>();

  async resolveClients(
    connections: McpInterceptorServerConnectionOptions[],
    signal?: AbortSignal,
  ): Promise<GatewayResolvedInterceptorClients> {
    if (connections.length === 0) {
      return new GatewayResolvedInterceptorClients([]);
    }

    const clients: Client[] = [];
    const owned: Client[] = [];

    try {
      for (const connection of connections) {
        if (!connection.transport) {
          throw new Error('transport is required on interceptor server connection options');
        }

        const connectionId = connection.connectionId?.trim();
        if (!connectionId) {
          const client = await connectInterceptorClient(connection, signal);
          clients.push(client);
          owned.push(client);
          continue;
        }

        let pending = this.cache.get(connectionId);
        if (!pending) {
          // No per-request signal: the connection is shared, so it is not tied to
          // the lifetime of whichever request opened it.
          pending = connectInterceptorClient(connection);
          this.cache.set(connectionId, pending);
          pending.catch(() => {
            this.cache.delete(connectionId);
          });
        }

        // A failed connect is evicted by the `catch` above; an abort here belongs to
        // this request alone, so neither case should drop a healthy shared entry.
        clients.push(await raceSignal(pending, signal));
      }
    } catch (error) {
      // A later connection failed: close the owned clients already connected in
      // this batch so each failed resolve does not leak live transports.
      await Promise.allSettled(owned.map((client) => client.close()));
      throw error;
    }

    return new GatewayResolvedInterceptorClients(
      clients,
      owned.map((client) => ({
        dispose: () => client.close(),
      })),
    );
  }

  async dispose(): Promise<void> {
    const pending = [...this.cache.values()];
    this.cache.clear();
    const clients = await Promise.all(
      pending.map(async (p) => {
        try {
          return await p;
        } catch {
          return undefined;
        }
      }),
    );
    await Promise.all(clients.filter((c): c is Client => c !== undefined).map((c) => c.close()));
  }
}
