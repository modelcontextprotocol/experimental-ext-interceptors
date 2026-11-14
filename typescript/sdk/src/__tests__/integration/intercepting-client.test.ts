// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../../protocol/constants.js';
import { McpInterceptorValidationException } from '../../protocol/errors.js';
import { InterceptingMcpClient } from '../../client/intercepting-client.js';
import { connectEchoBackend, connectInterceptorHost, connectRichBackend } from '../fixtures/hosts.js';
import type { RegisteredInterceptor } from '../../server/register-interceptors.js';

describe('InterceptingMcpClient', () => {
  it('applies request-phase mutation before tools/call reaches backend', async () => {
    const mutator: RegisteredInterceptor = {
      descriptor: {
        name: 'arg-renamer',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        priorityHint: 0,
      },
      handler: (params) => {
        const p = params.payload as { name?: string; arguments?: Record<string, unknown> };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            name: p.name ?? 'echo',
            arguments: { ...p.arguments, mutated: true },
          },
        };
      },
    };

    const interceptor = await connectInterceptorHost([mutator]);
    const backend = await connectEchoBackend();

    const gateway = new InterceptingMcpClient(backend.client, {
      interceptorClient: interceptor.client,
      events: [InterceptionEvents.ToolsCall],
    });

    await gateway.callTool('echo', { message: 'hello' });

    expect(backend.lastCall.name).toBe('echo');
    expect(backend.lastCall.arguments).toMatchObject({ message: 'hello', mutated: true });

    await gateway.close();
    await interceptor.close();
    await backend.close();
  });

  it('does not restore arguments a request-phase mutation removed', async () => {
    const redactor: RegisteredInterceptor = {
      descriptor: {
        name: 'arg-stripper',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      },
      handler: (params) => {
        const p = params.payload as { name?: string };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: { name: p.name ?? 'echo' },
        };
      },
    };

    const interceptor = await connectInterceptorHost([redactor]);
    const backend = await connectEchoBackend();

    const gateway = new InterceptingMcpClient(backend.client, {
      interceptorClient: interceptor.client,
      events: [InterceptionEvents.ToolsCall],
    });

    await gateway.callTool('echo', { message: 'secret-pii' });

    expect(backend.lastCall.name).toBe('echo');
    expect(backend.lastCall.arguments).toBeUndefined();

    await gateway.close();
    await interceptor.close();
    await backend.close();
  });

  it('blocks tools/call with validation exception when interceptor rejects', async () => {
    const blocker: RegisteredInterceptor = {
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
          messages: [{ message: 'blocked', severity: 'error' }],
        }) as const,
    };

    const interceptor = await connectInterceptorHost([blocker]);
    const backend = await connectEchoBackend();
    const gateway = new InterceptingMcpClient(backend.client, {
      interceptorClient: interceptor.client,
    });

    await expect(gateway.callTool('echo', {})).rejects.toBeInstanceOf(
      McpInterceptorValidationException,
    );
    expect(backend.lastCall.name).toBe('');

    await gateway.close();
    await interceptor.close();
    await backend.close();
  });

  it('proxies getPrompt through request-phase chain', async () => {
    const mutator: RegisteredInterceptor = {
      descriptor: {
        name: 'prompt-tagger',
        type: 'mutation',
        hooks: [{ events: [InterceptionEvents.PromptsGet], phase: 'request' }],
      },
      handler: (params) => {
        const p = params.payload as { name?: string; arguments?: Record<string, string> };
        return {
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: {
            name: p.name ?? 'greet',
            arguments: { ...p.arguments, tagged: 'yes' },
          },
        };
      },
    };

    const interceptor = await connectInterceptorHost([mutator]);
    const backend = await connectRichBackend();
    const gateway = new InterceptingMcpClient(backend.client, {
      interceptorClient: interceptor.client,
      events: [InterceptionEvents.PromptsGet],
    });

    await gateway.getPrompt('greet', { who: 'world' });

    expect(backend.lastPromptGet.name).toBe('greet');
    expect(backend.lastPromptGet.arguments).toMatchObject({ who: 'world', tagged: 'yes' });

    await gateway.close();
    await interceptor.close();
    await backend.close();
  });

  it('listInterceptors delegates to interceptor host client', async () => {
    const interceptor = await connectInterceptorHost([
      {
        descriptor: {
          name: 'listed',
          type: 'sink',
          hooks: [{ events: [InterceptionEvents.All], phase: 'request' }],
        },
        handler: () => ({ type: 'sink', phase: 'request', recorded: true }),
      },
    ]);
    const backend = await connectEchoBackend();
    const gateway = new InterceptingMcpClient(backend.client, {
      interceptorClient: interceptor.client,
    });

    const listed = await gateway.listInterceptors();
    expect(listed.interceptors.some((i) => i.name === 'listed')).toBe(true);

    await gateway.close();
    await interceptor.close();
    await backend.close();
  });
});
