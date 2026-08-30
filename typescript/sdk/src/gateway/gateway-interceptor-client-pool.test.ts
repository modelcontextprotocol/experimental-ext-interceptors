// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { InMemoryTransport, Server } from '@modelcontextprotocol/server';
import { describe, it, expect } from 'vitest';
import { GatewayInterceptorClientPool } from './gateway-interceptor-client-pool.js';
import type { McpInterceptorServerConnectionOptions } from './mcp-interceptor-server-connection-options.js';
import { registerInterceptorsOnServer } from '../server/register-interceptors.js';

async function startHost(): Promise<{
  connection: McpInterceptorServerConnectionOptions;
  close: () => Promise<void>;
}> {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server({ name: 'pool-host', version: '0.0.0' }, { capabilities: {} });
  registerInterceptorsOnServer(server, []);
  await server.connect(serverTransport);
  return {
    connection: { connectionId: 'shared', transport: clientTransport },
    close: () => server.close(),
  };
}

describe('GatewayInterceptorClientPool', () => {
  it('does not let one aborted request cancel the shared connection', async () => {
    const host = await startHost();
    const pool = new GatewayInterceptorClientPool();

    const aborted = new AbortController();
    aborted.abort();

    // Both calls reach the cache before either awaits, so they share one entry.
    const first = pool.resolveClients([host.connection], aborted.signal);
    const second = pool.resolveClients([host.connection]);

    await expect(first).rejects.toThrow();

    const resolved = await second;
    expect(resolved.clients).toHaveLength(1);
    expect(resolved.clients[0]?.getServerVersion()?.name).toBe('pool-host');

    await pool.dispose();
    await host.close();
  });
});
