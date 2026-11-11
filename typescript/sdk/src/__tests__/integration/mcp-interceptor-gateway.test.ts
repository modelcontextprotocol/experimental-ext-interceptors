// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { Server, InMemoryTransport, ProtocolError } from "@modelcontextprotocol/server";
import { Client } from "@modelcontextprotocol/client";
import { describe, it, expect } from 'vitest';
import type { InterceptorClientTransport } from '../../gateway/mcp-interceptor-server-connection-options.js';
import { InterceptionEvents } from '../../protocol/constants.js';
import { validationFailure, validationSuccess } from '../../protocol/results.js';
import { invokeInterceptor, listInterceptors } from '../../client/client-extensions.js';
import { McpInterceptorGateway } from '../../gateway/mcp-interceptor-gateway.js';
import {
  connectEchoBackend,
  connectInterceptorHost,
  connectRichBackend,
} from '../fixtures/hosts.js';
import {
  registerInterceptorsOnServer,
  type RegisteredInterceptor,
} from '../../server/register-interceptors.js';

async function startInterceptorHostTransport(
  interceptors: RegisteredInterceptor[],
): Promise<{ transport: InterceptorClientTransport; close: () => Promise<void> }> {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: 'test-interceptor-host', version: '0.0.0' },
    { capabilities: {} },
  );
  registerInterceptorsOnServer(server, interceptors);
  await server.connect(serverTransport);
  return {
    transport: clientTransport,
    close: async () => {
      await server.close();
    },
  };
}

async function connectGatewayProxy(gateway: McpInterceptorGateway): Promise<{
  proxyClient: Client;
  close: () => Promise<void>;
}> {
  const proxyServer = new Server(
    { name: 'test-proxy', version: '0.0.0' },
    { capabilities: {} },
  );

  gateway.configureServer(proxyServer);

  const proxyClient = new Client({ name: 'proxy-client', version: '0.0.0' }, { capabilities: {} });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([proxyServer.connect(serverTransport), proxyClient.connect(clientTransport)]);
  gateway.registerNotificationForwarding(proxyServer);

  return {
    proxyClient,
    close: async () => {
      gateway.disposeNotificationForwarding();
      await Promise.all([proxyClient.close(), proxyServer.close()]);
    },
  };
}

describe('McpInterceptorGateway', () => {
  it('proxies tools/list from the backend', async () => {
    const backend = await connectEchoBackend();
    const interceptor = await connectInterceptorHost([]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
    });

    const proxy = await connectGatewayProxy(gateway);
    const tools = await proxy.proxyClient.listTools();
    expect(tools.tools).toHaveLength(1);
    expect(tools.tools[0]?.name).toBe('echo');

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('runs tools/call through interceptor chains before the backend', async () => {
    const mutator: RegisteredInterceptor = {
      descriptor: {
        name: 'arg-tagger',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
      handler: (params) => {
        const p = params.payload as { name?: string; arguments?: Record<string, unknown> };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            name: p.name ?? 'echo',
            arguments: { ...p.arguments, viaGateway: true },
          },
        };
      },
    };

    const backend = await connectEchoBackend();
    const interceptor = await connectInterceptorHost([mutator]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
      events: [InterceptionEvents.ToolsCall],
    });

    const proxy = await connectGatewayProxy(gateway);
    await proxy.proxyClient.callTool({ name: 'echo', arguments: { message: 'hi' } });

    expect(backend.lastCall.arguments).toMatchObject({ message: 'hi', viaGateway: true });

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('blocks tools/call when validation interceptors abort', async () => {
    const blocker: RegisteredInterceptor = {
      descriptor: {
        name: 'blocker',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
      handler: () =>
        validationFailure('request', {
          severity: 'error',
          message: 'blocked by gateway test',
        }),
    };

    const backend = await connectEchoBackend();
    const interceptor = await connectInterceptorHost([blocker]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
    });

    const proxy = await connectGatewayProxy(gateway);

    await expect(
      proxy.proxyClient.callTool({ name: 'echo', arguments: {} }),
    ).rejects.toSatisfy((err: unknown) => {
      expect(err).toBeInstanceOf(ProtocolError);
      expect((err as ProtocolError).message).toMatch(/blocker.*reported invalid/i);
      expect((err as ProtocolError).message).toContain('blocked by gateway test');
      return true;
    });

    expect(backend.lastCall.name).toBe('');

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('exposes aggregated interceptors/list when exposeInterceptorProtocol is true', async () => {
    const listed: RegisteredInterceptor = {
      descriptor: {
        name: 'visible',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
      handler: () => validationSuccess('request'),
    };

    const backend = await connectEchoBackend();
    const interceptor = await connectInterceptorHost([listed]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
      exposeInterceptorProtocol: true,
    });

    const proxy = await connectGatewayProxy(gateway);
    const result = await listInterceptors(proxy.proxyClient);
    expect(result.interceptors.some((i) => i.name === 'visible')).toBe(true);

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('chains multiple interceptor hosts in order on tools/call', async () => {
    const prependA: RegisteredInterceptor = {
      descriptor: {
        name: 'prepend-a',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        priorityHint: 0,
      },
      handler: (params) => {
        const p = params.payload as { name?: string; arguments?: Record<string, unknown> };
        const msg = String(p.arguments?.message ?? '');
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            name: p.name ?? 'echo',
            arguments: { ...p.arguments, message: `A:${msg}` },
          },
        };
      },
    };

    const prependB: RegisteredInterceptor = {
      descriptor: {
        name: 'prepend-b',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        priorityHint: 0,
      },
      handler: (params) => {
        const p = params.payload as { name?: string; arguments?: Record<string, unknown> };
        const msg = String(p.arguments?.message ?? '');
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            name: p.name ?? 'echo',
            arguments: { ...p.arguments, message: `B:${msg}` },
          },
        };
      },
    };

    const hostA = await connectInterceptorHost([prependA]);
    const hostB = await connectInterceptorHost([prependB]);
    const backend = await connectRichBackend();

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [hostA.client, hostB.client],
      events: [InterceptionEvents.ToolsCall],
    });

    const proxy = await connectGatewayProxy(gateway);
    const result = await proxy.proxyClient.callTool({
      name: 'echo',
      arguments: { message: 'hello' },
    });

    const blocks = result.content as Array<{ type: string; text?: string }> | undefined;
    expect(blocks?.[0]?.text ?? '').toContain('B:A:hello');

    await proxy.close();
    await hostA.close();
    await hostB.close();
    await backend.close();
  });

  it('mirrors backend prompts capability and proxies getPrompt with interception', async () => {
    const mutator: RegisteredInterceptor = {
      descriptor: {
        name: 'prompt-mutator',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.PromptsGet], phase: 'request' }],
      },
      handler: (params) => {
        const p = params.payload as { name?: string };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: { name: `${p.name}-mutated` },
        };
      },
    };

    const backend = await connectRichBackend();
    const interceptor = await connectInterceptorHost([mutator]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
      events: [InterceptionEvents.PromptsGet],
    });

    const proxy = await connectGatewayProxy(gateway);
    await proxy.proxyClient.getPrompt({ name: 'greet' });

    expect(backend.lastPromptGet.name).toBe('greet-mutated');

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('rewrites resources/subscribe payload before backend receives it', async () => {
    const rewriter: RegisteredInterceptor = {
      descriptor: {
        name: 'uri-rewriter',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ResourcesSubscribe], phase: 'request' }],
      },
      handler: (params) => {
        const p = params.payload as { uri?: string };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            uri: (p.uri ?? '').replace('resource://original', 'resource://rewritten'),
          },
        };
      },
    };

    const backend = await connectRichBackend();
    const interceptor = await connectInterceptorHost([rewriter]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
      events: [InterceptionEvents.ResourcesSubscribe],
    });

    const proxy = await connectGatewayProxy(gateway);
    await proxy.proxyClient.subscribeResource({ uri: 'resource://original' });

    expect(backend.subscription.uri).toBe('resource://rewritten');

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('does not expose interceptors/list on proxy by default', async () => {
    const backend = await connectEchoBackend();
    const interceptor = await connectInterceptorHost([
      {
        descriptor: {
          name: 'hidden',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: () => validationSuccess('request'),
      },
    ]);

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [interceptor.client],
    });

    const proxy = await connectGatewayProxy(gateway);

    await expect(listInterceptors(proxy.proxyClient)).rejects.toThrow();

    await proxy.close();
    await interceptor.close();
    await backend.close();
  });

  it('createAsync connects interceptorServerConnections', async () => {
    const listed: RegisteredInterceptor = {
      descriptor: {
        name: 'connected-via-transport',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
      handler: () => validationSuccess('request'),
    };

    const host = await startInterceptorHostTransport([listed]);
    const backend = await connectEchoBackend();

    const gateway = await McpInterceptorGateway.createAsync({
      backendClient: backend.client,
      interceptorServerConnections: [{ transport: host.transport }],
      exposeInterceptorProtocol: true,
    });

    const proxy = await connectGatewayProxy(gateway);
    const result = await listInterceptors(proxy.proxyClient);
    expect(result.interceptors.some((i) => i.name === 'connected-via-transport')).toBe(true);

    await proxy.close();
    await gateway.dispose();
    await host.close();
    await backend.close();
  });

  it('rejects interceptorServerConnections on the constructor', () => {
    expect(
      () =>
        new McpInterceptorGateway({
          backendClient: {} as Client,
          interceptorServerConnections: [{ transport: {} as InterceptorClientTransport }],
        }),
    ).toThrow(/createAsync/i);
  });

  it('runs tools/call via resolver-only transparent mode', async () => {
    const mutator: RegisteredInterceptor = {
      descriptor: {
        name: 'resolver-mutator',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
      handler: (params) => {
        const p = params.payload as { name?: string; arguments?: Record<string, unknown> };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            name: p.name ?? 'echo',
            arguments: { ...p.arguments, viaResolver: true },
          },
        };
      },
    };

    const host = await startInterceptorHostTransport([mutator]);
    const backend = await connectEchoBackend();

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorServerConnectionResolver: (_context, event) =>
        Promise.resolve(
          event === InterceptionEvents.ToolsCall
            ? [{ transport: host.transport, connectionId: 'test-host' }]
            : [],
        ),
      events: [InterceptionEvents.ToolsCall],
    });

    const proxy = await connectGatewayProxy(gateway);
    await proxy.proxyClient.callTool({ name: 'echo', arguments: { message: 'hi' } });

    expect(backend.lastCall.arguments).toMatchObject({ message: 'hi', viaResolver: true });

    await proxy.close();
    await gateway.dispose();
    await host.close();
    await backend.close();
  });

  it('rejects resolver with exposeInterceptorProtocol', () => {
    expect(
      () =>
        new McpInterceptorGateway({
          backendClient: {} as Client,
          interceptorClients: [{} as Client],
          interceptorServerConnectionResolver: () => Promise.resolve([]),
          exposeInterceptorProtocol: true,
        }),
    ).toThrow(/exposeInterceptorProtocol/i);
  });

  it('aggregates interceptors from two hosts when exposeInterceptorProtocol is true', async () => {
    const host1 = await connectInterceptorHost([
      {
        descriptor: {
          name: 'validator-1',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: () => validationSuccess('request'),
      },
    ]);
    const host2 = await connectInterceptorHost([
      {
        descriptor: {
          name: 'mutator-1',
          type: 'mutation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: (params) => ({
          type: 'mutation',
          phase: params.phase,
          modified: false,
          payload: params.payload,
        }),
      },
    ]);
    const backend = await connectEchoBackend();

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [host1.client, host2.client],
      exposeInterceptorProtocol: true,
    });

    const proxy = await connectGatewayProxy(gateway);
    const result = await listInterceptors(proxy.proxyClient);
    expect(result.interceptors).toHaveLength(2);
    expect(result.interceptors.map((i) => i.name).sort()).toEqual(['mutator-1', 'validator-1']);

    await proxy.close();
    await host1.close();
    await host2.close();
    await backend.close();
  });

  it('rejects interceptor/invoke when two hosts registered the same name', async () => {
    const descriptor = {
      name: 'shared-name',
      type: 'validation' as const,
      hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' as const }],
    };
    const host1 = await connectInterceptorHost([
      { descriptor, handler: () => validationSuccess('request') },
    ]);
    const host2 = await connectInterceptorHost([
      { descriptor: { ...descriptor }, handler: () => validationSuccess('request') },
      {
        descriptor: { ...descriptor, name: 'unique-name' },
        handler: () => validationSuccess('request'),
      },
    ]);
    const backend = await connectEchoBackend();

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [host1.client, host2.client],
      exposeInterceptorProtocol: true,
    });

    const proxy = await connectGatewayProxy(gateway);

    await expect(
      invokeInterceptor(proxy.proxyClient, {
        name: 'shared-name',
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { name: 'echo', arguments: {} },
      }),
    ).rejects.toThrow(/registered on more than one host/i);

    // A name that is unique across hosts still routes.
    const ok = await invokeInterceptor(proxy.proxyClient, {
      name: 'unique-name',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { name: 'echo', arguments: {} },
    });
    expect(ok.type).toBe('validation');

    await proxy.close();
    await host1.close();
    await host2.close();
    await backend.close();
  });

  it('passes requests through when the resolver returns no connections', async () => {
    const backend = await connectEchoBackend();

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorServerConnectionResolver: () => Promise.resolve([]),
    });

    const proxy = await connectGatewayProxy(gateway);
    const tools = await proxy.proxyClient.listTools();
    expect(tools.tools.map((t) => t.name)).toEqual(['echo']);

    const result = await proxy.proxyClient.callTool({
      name: 'echo',
      arguments: { message: 'hi' },
    });
    expect(result.content).toBeDefined();
    expect(backend.lastCall.name).toBe('echo');

    await proxy.close();
    await backend.close();
  });

  it('forwards backend resources/updated notifications to proxied subscribers', async () => {
    const passthrough: RegisteredInterceptor = {
      descriptor: {
        name: 'observer',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.ResourcesSubscribe], phase: 'request' }],
      },
      handler: (params) => validationSuccess(params.phase),
    };

    const host = await connectInterceptorHost([passthrough]);
    const backend = await connectRichBackend();

    const gateway = new McpInterceptorGateway({
      backendClient: backend.client,
      interceptorClients: [host.client],
    });

    const proxy = await connectGatewayProxy(gateway);

    const updated = new Promise<{ uri: string }>((resolve) => {
      proxy.proxyClient.setNotificationHandler('notifications/resources/updated', (n) => {
        resolve({ uri: n.params.uri });
      });
    });

    await proxy.proxyClient.subscribeResource({ uri: 'resource://original' });
    expect(backend.subscription.uri).toBe('resource://original');

    await backend.server.sendResourceUpdated({ uri: 'resource://original' });
    await expect(updated).resolves.toEqual({ uri: 'resource://original' });

    await proxy.close();
    await host.close();
    await backend.close();
  });
});
