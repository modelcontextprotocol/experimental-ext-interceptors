// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../../protocol/constants.js';
import { validationSuccess } from '../../protocol/results.js';
import {
  executeInterceptorChainOnClient,
  invokeInterceptor,
  listAllInterceptors,
  listInterceptors,
} from '../../client/client-extensions.js';
import { Server, InMemoryTransport } from "@modelcontextprotocol/server";
import { Client } from "@modelcontextprotocol/client";
import { connectInterceptorHost } from '../fixtures/hosts.js';
import { ListInterceptorsParamsSchema } from '../../protocol/zod-schemas.js';
import type { Interceptor } from '../../protocol/types.js';

describe('client extensions integration', () => {
  it('lists and invokes interceptors over InMemoryTransport', async () => {
    const { client, close } = await connectInterceptorHost([
      {
        descriptor: {
          name: 'echo-validator',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: () => validationSuccess('request'),
      },
    ]);

    const list = await listInterceptors(client);
    expect(list.interceptors).toHaveLength(1);
    expect(list.interceptors[0]?.name).toBe('echo-validator');

    const result = await invokeInterceptor(client, {
      name: 'echo-validator',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { x: 1 },
    });

    expect(result.type).toBe('validation');
    if (result.type === 'validation') {
      expect(result.valid).toBe(true);
    }

    await close();
  });

  it('executeInterceptorChainOnClient runs chain via list + invoke', async () => {
    const { client, close } = await connectInterceptorHost([
      {
        descriptor: {
          name: 'mutator',
          type: 'mutation',
          hooks: [{ events: [InterceptionEvents.All], phase: 'request' }],
          priorityHint: 0,
        },
        handler: (params) => ({
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: { mutated: true },
        }),
      },
    ]);

    const chain = await executeInterceptorChainOnClient(client, {
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: {},
    });

    expect(chain.status).toBe('success');
    expect(chain.finalPayload).toEqual({ mutated: true });
    expect(chain.results).toHaveLength(1);

    await close();
  });

  it('executeInterceptorChainOnClient aborts when validation fails', async () => {
    const { client, close } = await connectInterceptorHost([
      {
        descriptor: {
          name: 'blocker',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: () =>
          ({
            type: 'validation',
            phase: 'request',
            valid: false,
            severity: 'error',
            messages: [{ message: 'nope', severity: 'error' }],
          }) as const,
      },
    ]);

    const chain = await executeInterceptorChainOnClient(client, {
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: {},
    });

    expect(chain.status).toBe('validation_failed');
    await close();
  });

  it('listAllInterceptors drains every page from a paginating host', async () => {
    const pageOne: Interceptor[] = [
      {
        name: 'page1-validator',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
    ];
    const pageTwo: Interceptor[] = [
      {
        name: 'page2-validator',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
    ];

    const server = new Server(
      { name: 'paginating-host', version: '0.0.0' },
      { capabilities: {} },
    );
    server.setRequestHandler(
      'interceptors/list',
      { params: ListInterceptorsParamsSchema.optional() },
      (params) =>
        params?.cursor === 'page-2'
          ? { interceptors: pageTwo }
          : { interceptors: pageOne, nextCursor: 'page-2' },
    );

    const client = new Client({ name: 'test-client', version: '0.0.0' }, { capabilities: {} });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);

    const all = await listAllInterceptors(client);
    expect(all.interceptors.map((i) => i.name)).toEqual(['page1-validator', 'page2-validator']);
    expect(all.nextCursor).toBeUndefined();

    await Promise.all([client.close(), server.close()]);
  });
});
