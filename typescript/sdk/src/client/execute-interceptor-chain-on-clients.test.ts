// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../protocol/constants.js';
import { DuplicateInterceptorNameError } from '../protocol/errors.js';
import { connectInterceptorHost } from '../__tests__/fixtures/hosts.js';
import type { RegisteredInterceptor } from '../server/register-interceptors.js';
import { executeInterceptorChainOnClients } from './execute-interceptor-chain-on-clients.js';
import {
  listInterceptorChainEntries,
  mergeInterceptorChainEntries,
} from './merge-interceptor-chain-entries.js';

function mutator(
  name: string,
  priorityHint: number,
  prefix: string,
): RegisteredInterceptor {
  return {
    descriptor: {
      name,
      type: 'mutation',
      hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      priorityHint,
    },
    handler: (params) => {
      const p = params.payload as { arguments?: { message?: string } };
      const msg = String(p.arguments?.message ?? '');
      return {
        type: 'mutation',
        phase: params.phase,
        modified: true,
        payload: { ...p, arguments: { ...p.arguments, message: `${prefix}${msg}` } },
      };
    },
  };
}

describe('executeInterceptorChainOnClients', () => {
  it('orders mutations by global priorityHint across hosts', async () => {
    const low = await connectInterceptorHost([mutator('low-priority', 100, 'L:')]);
    const high = await connectInterceptorHost([mutator('high-priority', -100, 'H:')]);

    const result = await executeInterceptorChainOnClients(
      [
        { client: low.client, label: 'low-host' },
        { client: high.client, label: 'high-host' },
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { name: 'echo', arguments: { message: 'x' } },
      },
    );

    expect(result.status).toBe('success');
    const payload = result.finalPayload as { arguments?: { message?: string } };
    expect(payload.arguments?.message).toBe('L:H:x');

    await low.close();
    await high.close();
  });

  it('throws DuplicateInterceptorNameError by default', async () => {
    const hostA = await connectInterceptorHost([mutator('dup', 0, 'A:')]);
    const hostB = await connectInterceptorHost([mutator('dup', 0, 'B:')]);

    const entries = await listInterceptorChainEntries([
      { client: hostA.client, label: 'a' },
      { client: hostB.client, label: 'b' },
    ]);

    expect(() => mergeInterceptorChainEntries(entries)).toThrow(DuplicateInterceptorNameError);

    await hostA.close();
    await hostB.close();
  });

  it('supports first-wins duplicate policy', async () => {
    const hostA = await connectInterceptorHost([mutator('dup', 0, 'A:')]);
    const hostB = await connectInterceptorHost([mutator('dup', 0, 'B:')]);

    const result = await executeInterceptorChainOnClients(
      [
        { client: hostA.client, label: 'a' },
        { client: hostB.client, label: 'b' },
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { name: 'echo', arguments: { message: 'x' } },
      },
      { duplicateNamePolicy: 'first-wins' },
    );

    expect(result.status).toBe('success');
    const payload = result.finalPayload as { arguments?: { message?: string } };
    expect(payload.arguments?.message).toBe('A:x');

    await hostA.close();
    await hostB.close();
  });

  it('returns an empty success chain for an empty host list', async () => {
    const result = await executeInterceptorChainOnClients([], {
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { name: 'echo' },
    });
    expect(result.status).toBe('success');
    expect(result.results).toHaveLength(0);
  });

  it('enforces chain timeoutMs against a hung interceptor host', async () => {
    const hung = await connectInterceptorHost([
      {
        descriptor: {
          name: 'hung-validator',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: (params) =>
          new Promise((resolve) =>
            setTimeout(() => resolve({ type: 'validation', phase: params.phase, valid: true }), 2_000),
          ),
      },
    ]);

    const started = Date.now();
    const result = await executeInterceptorChainOnClients(
      [{ client: hung.client, label: 'hung-host' }],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { name: 'echo' },
        timeoutMs: 150,
      },
    );

    expect(result.status).toBe('timeout');
    expect(Date.now() - started).toBeLessThan(1_500);
    await hung.close();
  });

  it('applies overrides by interceptor name from options', async () => {
    const host = await connectInterceptorHost([
      {
        descriptor: {
          name: 'strict-validator',
          type: 'validation',
          hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
        },
        handler: (params) => ({
          type: 'validation',
          phase: params.phase,
          valid: false,
          severity: 'error',
          messages: [{ message: 'blocked', severity: 'error' }],
        }),
      },
    ]);

    const blocked = await executeInterceptorChainOnClients(
      [{ client: host.client, label: 'host' }],
      { event: InterceptionEvents.ToolsCall, phase: 'request', payload: { name: 'echo' } },
    );
    expect(blocked.status).toBe('validation_failed');

    const audited = await executeInterceptorChainOnClients(
      [{ client: host.client, label: 'host' }],
      { event: InterceptionEvents.ToolsCall, phase: 'request', payload: { name: 'echo' } },
      { overrides: { 'strict-validator': { mode: 'audit' } } },
    );
    expect(audited.status).toBe('success');

    await host.close();
  });
});
