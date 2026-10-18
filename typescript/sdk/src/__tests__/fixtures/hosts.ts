// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { Server, InMemoryTransport } from "@modelcontextprotocol/server";
import { Client } from "@modelcontextprotocol/client";

import {
  registerInterceptorsOnServer,
  type RegisteredInterceptor,
} from '../../server/register-interceptors.js';

export async function connectInterceptorHost(
  interceptors: RegisteredInterceptor[],
): Promise<{
  client: Client;
  server: Server;
  close: () => Promise<void>;
}> {
  const server = new Server(
    { name: 'test-interceptor-host', version: '0.0.0' },
    { capabilities: {} },
  );

  registerInterceptorsOnServer(server, interceptors);

  const client = new Client({ name: 'test-client', version: '0.0.0' }, { capabilities: {} });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);

  return {
    client,
    server,
    close: async () => {
      await Promise.all([client.close(), server.close()]);
    },
  };
}

export async function connectEchoBackend(): Promise<{
  client: Client;
  server: Server;
  close: () => Promise<void>;
  lastCall: { name: string; arguments?: Record<string, unknown> };
}> {
  const lastCall = { name: '', arguments: undefined as Record<string, unknown> | undefined };

  const server = new Server(
    { name: 'echo-backend', version: '0.0.0' },
    { capabilities: { tools: {} } },
  );

  server.setRequestHandler('tools/list', () => ({
    tools: [{ name: 'echo', description: 'echo', inputSchema: { type: 'object' } }],
  }));

  server.setRequestHandler('tools/call', (request) => {
    lastCall.name = request.params.name;
    lastCall.arguments = request.params.arguments;
    return {
      content: [{ type: 'text', text: JSON.stringify(request.params.arguments ?? {}) }],
      structuredContent: request.params.arguments ?? {},
    };
  });

  const client = new Client({ name: 'gateway-client', version: '0.0.0' }, { capabilities: {} });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);

  return {
    client,
    server,
    lastCall,
    close: async () => {
      await Promise.all([client.close(), server.close()]);
    },
  };
}

/** Backend with tools, prompts, and subscribable resources for gateway / client E2E tests. */
export async function connectRichBackend(): Promise<{
  client: Client;
  server: Server;
  close: () => Promise<void>;
  lastToolCall: { name: string; arguments?: Record<string, unknown> };
  lastPromptGet: { name: string; arguments?: Record<string, string> };
  subscription: { uri: string };
}> {
  const lastToolCall = { name: '', arguments: undefined as Record<string, unknown> | undefined };
  const lastPromptGet = { name: '', arguments: undefined as Record<string, string> | undefined };
  const subscription = { uri: '' };

  const server = new Server(
    { name: 'rich-backend', version: '0.0.0' },
    {
      capabilities: {
        tools: {},
        prompts: {},
        resources: { subscribe: true },
      },
    },
  );

  server.setRequestHandler('tools/list', () => ({
    tools: [{ name: 'echo', description: 'echo', inputSchema: { type: 'object' } }],
  }));

  server.setRequestHandler('tools/call', (request) => {
    lastToolCall.name = request.params.name;
    lastToolCall.arguments = request.params.arguments;
    const msg = request.params.arguments?.message;
    return {
      content: [{ type: 'text', text: `echo: ${String(msg ?? '')}` }],
    };
  });

  server.setRequestHandler('prompts/list', () => ({
    prompts: [{ name: 'greet', description: 'greet' }],
  }));

  server.setRequestHandler('prompts/get', (request) => {
    lastPromptGet.name = request.params.name;
    lastPromptGet.arguments = request.params.arguments;
    return {
      messages: [
        {
          role: 'user' as const,
          content: { type: 'text' as const, text: `Hello ${request.params.name}` },
        },
      ],
    };
  });

  server.setRequestHandler('resources/list', () => ({
    resources: [{ uri: 'resource://original', name: 'original' }],
  }));

  server.setRequestHandler('resources/read', (request) => ({
    contents: [{ uri: request.params.uri, text: 'content' }],
  }));

  server.setRequestHandler('resources/subscribe', (request) => {
    subscription.uri = request.params.uri;
    return {};
  });

  const client = new Client({ name: 'rich-backend-client', version: '0.0.0' }, { capabilities: {} });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);

  return {
    client,
    server,
    lastToolCall,
    lastPromptGet,
    subscription,
    close: async () => {
      await Promise.all([client.close(), server.close()]);
    },
  };
}
