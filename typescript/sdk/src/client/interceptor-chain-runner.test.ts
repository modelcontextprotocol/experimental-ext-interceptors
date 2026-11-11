// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../protocol/constants.js';
import { McpInterceptorValidationException } from '../protocol/errors.js';
import { connectInterceptorHost } from '../__tests__/fixtures/hosts.js';
import { InterceptorChainRunner } from './interceptor-chain-runner.js';

describe('InterceptorChainRunner', () => {
  it('respects events filter via shouldIntercept', () => {
    const runner = new InterceptorChainRunner({
      interceptorClients: [],
      events: [InterceptionEvents.ToolsCall],
    });
    expect(runner.shouldIntercept(InterceptionEvents.ToolsCall)).toBe(true);
    expect(runner.shouldIntercept(InterceptionEvents.PromptsGet)).toBe(false);
  });

  it('treats the wildcard event as intercept-everything', () => {
    const runner = new InterceptorChainRunner({
      interceptorClients: [],
      events: [InterceptionEvents.All],
    });
    expect(runner.shouldIntercept(InterceptionEvents.ToolsCall)).toBe(true);
    expect(runner.shouldIntercept(InterceptionEvents.ResourcesRead)).toBe(true);
  });

  it('runChainPhaseOrThrow throws validation exception in-process', async () => {
    const host = await connectInterceptorHost([
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
            messages: [{ message: 'blocked', severity: 'error' }],
          }) as const,
      },
    ]);

    const runner = new InterceptorChainRunner({
      interceptorClients: [host.client],
    });

    const err = await runner
      .runChainPhaseOrThrow('tools/call', InterceptionEvents.ToolsCall, 'request', {})
      .catch((e: unknown) => e);
    expect(err).toBeInstanceOf(McpInterceptorValidationException);
    expect((err as McpInterceptorValidationException).message).toMatch(
      /blocker.*reported invalid/i,
    );

    await host.close();
  });

  it('runs multiple interceptor clients in order', async () => {
    const first = await connectInterceptorHost([
      {
        descriptor: {
          name: 'first',
          type: 'mutation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
          priorityHint: 0,
        },
        handler: (params) => ({
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: { ...(params.payload as object), first: true },
        }),
      },
    ]);

    const second = await connectInterceptorHost([
      {
        descriptor: {
          name: 'second',
          type: 'mutation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
          priorityHint: 0,
        },
        handler: (params) => ({
          type: 'mutation',
          phase: params.phase,
          modified: true,
          payload: { ...(params.payload as object), second: true },
        }),
      },
    ]);

    const runner = new InterceptorChainRunner({
      interceptorClients: [first.client, second.client],
    });

    const payload = await runner.runChainPhaseOrThrow(
      'tools/call',
      InterceptionEvents.ToolsCall,
      'request',
      {},
    );

    expect(payload).toMatchObject({ first: true, second: true });

    await first.close();
    await second.close();
  });
});
