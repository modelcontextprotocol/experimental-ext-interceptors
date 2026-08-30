/**
 * Integration smoke test: v2 MCP SDK server registration for interceptors/list and SEP capabilities.
 */
import { describe, it, expect } from 'vitest';
import * as z from 'zod/v4';
import { ResultSchema } from '@modelcontextprotocol/core';
import { Server, InMemoryTransport } from '@modelcontextprotocol/server';
import type { ServerCapabilities } from '@modelcontextprotocol/server';
import { Client } from '@modelcontextprotocol/client';

const InterceptorsListParamsSchema = z
  .object({
    event: z.string().optional(),
  })
  .optional();

const InterceptorsListResultSchema = ResultSchema.extend({
  interceptors: z.array(
    z.object({
      name: z.string(),
      type: z.literal('validation'),
    }),
  ),
});

describe('MCP SDK v2 server wiring', () => {
  it('handles interceptors/list; extensions capability survives v2 client initialize parsing', async () => {
    const server = new Server(
      { name: 'spike-interceptor-server', version: '0.0.0' },
      { capabilities: {} },
    );

    server.registerCapabilities({
      extensions: {
        'io.modelcontextprotocol/interceptors': {
          supportedEvents: ['tools/call'],
        },
      },
    } as ServerCapabilities);

    server.setRequestHandler(
      'interceptors/list',
      { params: InterceptorsListParamsSchema },
      () => ({
        interceptors: [{ name: 'test-validator', type: 'validation' as const }],
      }),
    );

    const client = new Client(
      { name: 'spike-client', version: '0.0.0' },
      { capabilities: {} },
    );

    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await Promise.all([
      server.connect(serverTransport),
      client.connect(clientTransport),
    ]);

    // The v2 ServerCapabilitiesSchema has a typed `extensions` record, so the SEP-2133
    // extensions capability round-trips through the stock v2 Client (a top-level
    // `interceptor` key would be stripped by initialize parsing).
    const caps = client.getServerCapabilities() as
      | (ServerCapabilities & { extensions?: Record<string, { supportedEvents?: string[] }> })
      | undefined;
    expect(caps?.extensions?.['io.modelcontextprotocol/interceptors']?.supportedEvents).toEqual([
      'tools/call',
    ]);

    const listResult = await client.request(
      { method: 'interceptors/list', params: {} },
      InterceptorsListResultSchema,
    );

    expect(listResult.interceptors).toHaveLength(1);
    expect(listResult.interceptors[0]?.name).toBe('test-validator');

    await Promise.all([client.close(), server.close()]);
  });
});
