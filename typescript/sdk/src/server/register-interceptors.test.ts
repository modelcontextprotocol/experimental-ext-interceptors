// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../protocol/constants.js';
import { validationSuccess } from '../protocol/results.js';
import { listInterceptors, invokeInterceptor } from '../client/client-extensions.js';
import { buildInterceptorsCapability, collectSupportedEvents } from './capabilities.js';
import { connectInterceptorHost } from '../__tests__/fixtures/hosts.js';
import { registerInterceptorsOnServer, type RegisteredInterceptor } from './register-interceptors.js';
import { ProtocolErrorCode, Server } from '@modelcontextprotocol/server';

const toolsValidator: RegisteredInterceptor = {
  descriptor: {
    name: 'tools-only',
    type: 'validation',
    hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
  },
  handler: () => validationSuccess('request'),
};

const promptsValidator: RegisteredInterceptor = {
  descriptor: {
    name: 'prompts-only',
    type: 'validation',
    hooks: [{ events: [InterceptionEvents.PromptsGet], phase: 'request' }],
  },
  handler: () => validationSuccess('request'),
};

describe('registerInterceptorsOnServer', () => {
  it('advertises the interceptors extensions capability from hook events', () => {
    const descriptors = [toolsValidator.descriptor, promptsValidator.descriptor];
    const events = collectSupportedEvents(descriptors);
    expect(events).toContain(InterceptionEvents.ToolsCall);
    expect(events).toContain(InterceptionEvents.PromptsGet);

    const capability = buildInterceptorsCapability(descriptors);
    expect(capability.supportedEvents).toContain(InterceptionEvents.ToolsCall);
    expect(capability.supportedEvents).toContain(InterceptionEvents.PromptsGet);
  });

  it('lists and filters by event', async () => {
    const { client, close } = await connectInterceptorHost([
      toolsValidator,
      promptsValidator,
    ]);

    const all = await listInterceptors(client);
    expect(all.interceptors).toHaveLength(2);

    const toolsOnly = await listInterceptors(client, { event: InterceptionEvents.ToolsCall });
    expect(toolsOnly.interceptors).toHaveLength(1);
    expect(toolsOnly.interceptors[0]?.name).toBe('tools-only');

    await close();
  });

  it('invokes registered handler', async () => {
    const { client, close } = await connectInterceptorHost([
      {
        descriptor: {
          name: 'echo-val',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.All], phase: 'request' }],
        },
        handler: (params) => ({
          type: 'validation',
          phase: params.phase,
          valid: true,
        }),
      },
    ]);

    const result = await invokeInterceptor(client, {
      name: 'echo-val',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { test: 1 },
    });

    expect(result.type).toBe('validation');
    expect(result.interceptor).toBe('echo-val');

    await close();
  });

  it('rejects duplicate interceptor names at registration time', () => {
    const server = new Server({ name: 'dup-host', version: '0.0.0' }, { capabilities: {} });
    const duplicate: RegisteredInterceptor = {
      descriptor: { ...toolsValidator.descriptor },
      handler: () => validationSuccess('request'),
    };

    expect(() => registerInterceptorsOnServer(server, [toolsValidator, duplicate])).toThrow(
      /duplicate interceptor name: 'tools-only'/i,
    );
  });

  it('invoke rejects a negative timeoutMs with -32602 before reaching the handler', async () => {
    const { client, close } = await connectInterceptorHost([toolsValidator]);

    await expect(
      invokeInterceptor(client, {
        name: 'tools-only',
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
        timeoutMs: -5,
      }),
    ).rejects.toMatchObject({ code: ProtocolErrorCode.InvalidParams });

    await close();
  });

  it('invoke throws -32602 when interceptor name is unknown', async () => {
    const { client, close } = await connectInterceptorHost([toolsValidator]);

    await expect(
      invokeInterceptor(client, {
        name: 'missing',
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      }),
    ).rejects.toMatchObject({ code: ProtocolErrorCode.InvalidParams, message: /not found/i });

    await close();
  });

  it('invoke times out slow handlers', async () => {
    const slow: RegisteredInterceptor = {
      descriptor: {
        name: 'slow',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.All], phase: 'request' }],
      },
      handler: async (_params, signal) => {
        await new Promise<void>((resolve, reject) => {
          const timer = setTimeout(resolve, 500);
          signal?.addEventListener(
            'abort',
            () => {
              clearTimeout(timer);
              reject(new Error('aborted'));
            },
            { once: true },
          );
        });
        return validationSuccess('request');
      },
    };

    const { client, close } = await connectInterceptorHost([slow]);

    await expect(
      invokeInterceptor(client, {
        name: 'slow',
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
        timeoutMs: 40,
      }),
    ).rejects.toThrow(/timed out/i);

    await close();
  });

  it('invoke times out handlers that ignore the abort signal (SEP -32000)', async () => {
    const stubborn: RegisteredInterceptor = {
      descriptor: {
        name: 'stubborn',
        type: 'validation',
        hooks: [{ events: [InterceptionEvents.All], phase: 'request' }],
      },
      handler: async () => {
        await new Promise<void>((resolve) => setTimeout(resolve, 1_000));
        return validationSuccess('request');
      },
    };

    const { client, close } = await connectInterceptorHost([stubborn]);

    const started = Date.now();
    await expect(
      invokeInterceptor(client, {
        name: 'stubborn',
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
        timeoutMs: 40,
      }),
    ).rejects.toMatchObject({ code: -32000, message: /timed out after 40ms/i });
    expect(Date.now() - started).toBeLessThan(800);

    await close();
  });
});
