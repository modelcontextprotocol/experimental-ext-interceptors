// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../protocol/constants.js';
import { validationFailure, validationSuccess } from '../protocol/results.js';
import type {
  ExecuteChainRequestParams,
  Interceptor,
  InterceptorPhase,
  InterceptorResult,
  InterceptorType,
  InvokeInterceptorRequestParams,
} from '../protocol/types.js';
import { executeInterceptorChain, type InterceptorInvoker } from './chain-orchestrator.js';
import { InterceptorOverrideHookError } from '../protocol/errors.js';
import type { InterceptorOverrides } from '../protocol/types.js';

type Handler = InterceptorInvoker;

function runChain(
  entries: Array<{ descriptor: Interceptor; handler: Handler }>,
  chainParams: ExecuteChainRequestParams,
  signal?: AbortSignal,
) {
  const byName = new Map(entries.map((e) => [e.descriptor.name, e.handler]));
  return executeInterceptorChain(
    entries.map((e) => e.descriptor),
    (req, s) => byName.get(req.name)!(req, s),
    chainParams,
    signal,
  );
}

function createEntry(
  name: string,
  type: InterceptorType,
  handler: (
    req: InvokeInterceptorRequestParams,
    signal?: AbortSignal,
  ) => InterceptorResult | Promise<InterceptorResult>,
  options: {
    priorityHint?: Interceptor['priorityHint'];
    events?: string[];
    phase?: InterceptorPhase | 'both';
    mode?: Interceptor['mode'];
    failOpen?: boolean;
  } = {},
) {
  const events = options.events ?? [InterceptionEvents.All];
  const phase = options.phase ?? 'both';
  const hooks =
    phase === 'both'
      ? [
          { events: [...events], phase: 'request' as const },
          { events: [...events], phase: 'response' as const },
        ]
      : [{ events: [...events], phase }];

  const descriptor: Interceptor = {
    name,
    type,
    hooks,
    mode: options.mode,
    failOpen: options.failOpen,
    priorityHint: options.priorityHint,
  };

  const wrapped: Handler = async (req, signal) => {
    const result = await handler(req, signal);
    result.phase = req.phase;
    return result;
  };

  return { descriptor, handler: wrapped };
}

describe('executeInterceptorChain', () => {
  it('request phase runs mutations before validations before sinks', async () => {
    const order: string[] = [];

    const result = await runChain(
      [
        createEntry('mut-1', 'mutation', () => {
          order.push('mutation');
          return { type: 'mutation', phase: 'request', modified: true, payload: { mutated: true } };
        }),
        createEntry('val-1', 'validation', () => {
          order.push('validation');
          return validationSuccess('request');
        }),
        createEntry('sink-1', 'sink', () => {
          order.push('sink');
          return { type: 'sink', phase: 'request', recorded: true };
        }),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { original: true },
      },
    );

    expect(result.status).toBe('success');
    expect(order).toEqual(['mutation', 'validation', 'sink']);
    expect(result.finalPayload).toEqual({ mutated: true });
  });

  it('response phase runs validations before sinks before mutations', async () => {
    const order: string[] = [];

    await runChain(
      [
        createEntry('mut-1', 'mutation', () => {
          order.push('mutation');
          return { type: 'mutation', phase: 'response', modified: false };
        }),
        createEntry('val-1', 'validation', () => {
          order.push('validation');
          return validationSuccess('response');
        }),
        createEntry('sink-1', 'sink', () => {
          order.push('sink');
          return { type: 'sink', phase: 'response', recorded: true };
        }),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'response',
        payload: { test: true },
      },
    );

    expect(order).toEqual(['validation', 'sink', 'mutation']);
  });

  it('orders mutations by priorityHint then name', async () => {
    const order: string[] = [];

    await runChain(
      [
        createEntry(
          'mut-high',
          'mutation',
          () => {
            order.push('high');
            return { type: 'mutation', phase: 'request', modified: false };
          },
          { priorityHint: 100 },
        ),
        createEntry(
          'mut-low',
          'mutation',
          () => {
            order.push('low');
            return { type: 'mutation', phase: 'request', modified: false };
          },
          { priorityHint: -100 },
        ),
        createEntry(
          'mut-default',
          'mutation',
          () => {
            order.push('default');
            return { type: 'mutation', phase: 'request', modified: false };
          },
          { priorityHint: 0 },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(order).toEqual(['low', 'default', 'high']);
  });

  it('orders mutations by phase-specific priorityHint', async () => {
    const requestOrder: string[] = [];
    const responseOrder: string[] = [];

    await runChain(
      [
        createEntry(
          'mut-a',
          'mutation',
          () => {
            requestOrder.push('a');
            return { type: 'mutation', phase: 'request', modified: false };
          },
          { priorityHint: { request: 100, response: 100 } },
        ),
        createEntry(
          'mut-b',
          'mutation',
          () => {
            requestOrder.push('b');
            return { type: 'mutation', phase: 'request', modified: false };
          },
          { priorityHint: { request: -100, response: -100 } },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(requestOrder).toEqual(['b', 'a']);

    await runChain(
      [
        createEntry(
          'mut-a',
          'mutation',
          () => {
            responseOrder.push('a');
            return { type: 'mutation', phase: 'response', modified: false };
          },
          { priorityHint: { request: 100, response: 100 } },
        ),
        createEntry(
          'mut-b',
          'mutation',
          () => {
            responseOrder.push('b');
            return { type: 'mutation', phase: 'response', modified: false };
          },
          { priorityHint: { request: -100, response: -100 } },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'response',
        payload: {},
      },
    );

    expect(responseOrder).toEqual(['b', 'a']);
  });

  it('aborts on validation error in active mode', async () => {
    const result = await runChain(
      [
        createEntry('strict', 'validation', () =>
          validationFailure('request', {
            message: 'Required field missing',
            severity: 'error',
          }),
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('validation_failed');
    expect(result.abortedAt?.interceptor).toBe('strict');
    expect(result.abortedAt?.type).toBe('validation');
  });

  it('audit mutation records but does not apply payload', async () => {
    const result = await runChain(
      [
        createEntry(
          'shadow',
          'mutation',
          () => ({
            type: 'mutation',
            phase: 'request',
            modified: true,
            payload: { shadowed: true },
          }),
          { mode: 'audit' },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { original: true },
      },
    );

    expect(result.status).toBe('success');
    expect(result.finalPayload).toEqual({ original: true });
    expect(result.results[0]).toMatchObject({ type: 'mutation', modified: true });
  });

  it('fail-open mutation continues after crash', async () => {
    const result = await runChain(
      [
        createEntry(
          'crashing',
          'mutation',
          () => {
            throw new Error('boom');
          },
          { failOpen: true },
        ),
        createEntry(
          'following',
          'mutation',
          (req) => {
            const payload = { ...(req.payload as object), reached: true };
            return { type: 'mutation', phase: 'request', modified: true, payload };
          },
          { priorityHint: 1 },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('success');
    expect(result.finalPayload).toMatchObject({ reached: true });
  });

  it('swallows sink failures', async () => {
    const result = await runChain(
      [
        createEntry('failing-sink', 'sink', () => {
          throw new Error('sink failure');
        }),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('success');
    expect(result.results).toHaveLength(1);
    expect(result.results[0]).toMatchObject({ type: 'sink', recorded: false });
  });

  it('filters by event and phase', async () => {
    const result = await runChain(
      [
        createEntry(
          'tools-only',
          'validation',
          () => validationSuccess('request'),
          { events: [InterceptionEvents.ToolsCall], phase: 'request' },
        ),
        createEntry(
          'prompts-only',
          'validation',
          () => validationSuccess('request'),
          { events: [InterceptionEvents.PromptsGet], phase: 'request' },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.results).toHaveLength(1);
    expect(result.results[0]?.interceptor).toBe('tools-only');
  });

  it('filters interceptors by phase only', async () => {
    const result = await runChain(
      [
        createEntry(
          'request-only',
          'validation',
          () => validationSuccess('request'),
          { phase: 'request' },
        ),
        createEntry(
          'response-only',
          'validation',
          () => validationSuccess('response'),
          { phase: 'response' },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.results).toHaveLength(1);
    expect(result.results[0]?.interceptor).toBe('request-only');
  });

  it('chains mutation payloads sequentially', async () => {
    const result = await runChain(
      [
        createEntry(
          'mut-1',
          'mutation',
          (req) => {
            const p = { ...(req.payload as object), step1: true };
            return { type: 'mutation', phase: 'request', modified: true, payload: p };
          },
          { priorityHint: 0 },
        ),
        createEntry(
          'mut-2',
          'mutation',
          (req) => {
            expect((req.payload as { step1?: boolean }).step1).toBe(true);
            const p = { ...(req.payload as object), step2: true };
            return { type: 'mutation', phase: 'request', modified: true, payload: p };
          },
          { priorityHint: 1 },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: { original: true },
      },
    );

    expect(result.status).toBe('success');
    expect(result.finalPayload).toMatchObject({ original: true, step1: true, step2: true });
  });

  it('audit validation does not block on error', async () => {
    const result = await runChain(
      [
        createEntry(
          'auditor',
          'validation',
          () =>
            validationFailure('request', {
              message: 'Audit-only violation',
              severity: 'error',
            }),
          { mode: 'audit' },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('success');
    expect(result.validationSummary?.errors).toBe(1);
    expect(result.abortedAt).toBeUndefined();
  });

  it('mutation interceptor returning validation failure blocks chain', async () => {
    const result = await runChain(
      [
        createEntry(
          'policy-mut',
          'mutation',
          () =>
            validationFailure('request', {
              message: 'Blocked by policy',
              severity: 'error',
            }),
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('validation_failed');
    expect(result.abortedAt?.interceptor).toBe('policy-mut');
    expect(result.abortedAt?.reason).toBe('Blocked by policy');
  });

  it('fail-closed mutation halts chain on crash', async () => {
    const result = await runChain(
      [
        createEntry('crashing', 'mutation', () => {
          throw new Error('boom');
        }),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('mutation_failed');
    expect(result.abortedAt?.interceptor).toBe('crashing');
    expect(result.abortedAt?.type).toBe('mutation');
  });

  it('fail-open validation continues after crash', async () => {
    const result = await runChain(
      [
        createEntry(
          'crashing-validator',
          'validation',
          () => {
            throw new Error('validator boom');
          },
          { failOpen: true },
        ),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('success');
  });

  it('fail-closed validation halts chain on crash', async () => {
    const result = await runChain(
      [
        createEntry('crashing-validator', 'validation', () => {
          throw new Error('validator boom');
        }),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('validation_failed');
    expect(result.abortedAt?.interceptor).toBe('crashing-validator');
  });

  it('validation summary counts severities', async () => {
    const result = await runChain(
      [
        createEntry('val', 'validation', () => ({
          type: 'validation',
          phase: 'request',
          valid: true,
          messages: [
            { message: 'Info', severity: 'info' },
            { message: 'Warn 1', severity: 'warn' },
            { message: 'Warn 2', severity: 'warn' },
          ],
        })),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
    );

    expect(result.status).toBe('success');
    expect(result.validationSummary).toEqual({ errors: 0, warnings: 2, infos: 1 });
  });

  it('aborts with timeout status when chain timeout elapses', async () => {
    const result = await runChain(
      [
        createEntry('slow-mut', 'mutation', (_req, signal) => {
          return new Promise<InterceptorResult>((_resolve, reject) => {
            if (!signal) {
              reject(new Error('expected abort signal'));
              return;
            }
            signal.addEventListener(
              'abort',
              () => reject(signal.reason ?? new Error('timeout')),
              { once: true },
            );
          });
        }),
      ],
      {
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
        timeoutMs: 50,
      },
    );

    expect(result.status).toBe('timeout');
  });
});

describe('executeInterceptorChain overrides (SEP capability vs policy)', () => {
  function runWithOverrides(
    entries: Array<{ descriptor: Interceptor; handler: Handler; overrides?: InterceptorOverrides }>,
    chainParams: ExecuteChainRequestParams,
    signal?: AbortSignal,
  ) {
    const byName = new Map(entries.map((e) => [e.descriptor.name, e.handler]));
    return executeInterceptorChain(
      entries.map((e) => ({ interceptor: e.descriptor, overrides: e.overrides })),
      (req, s) => byName.get(req.name)!(req, s),
      chainParams,
      signal,
    );
  }

  const req: ExecuteChainRequestParams = {
    event: InterceptionEvents.ToolsCall,
    phase: 'request',
    payload: { name: 'echo' },
  };

  it('mode override to audit stops a failing validator from blocking', async () => {
    const failing = createEntry('strict', 'validation', () =>
      validationFailure('request', { message: 'nope', severity: 'error' }),
    );
    const blocked = await runWithOverrides([{ ...failing }], req);
    expect(blocked.status).toBe('validation_failed');

    const audited = await runWithOverrides([{ ...failing, overrides: { mode: 'audit' } }], req);
    expect(audited.status).toBe('success');
  });

  it('failOpen override lets a throwing mutator be skipped', async () => {
    const boom = createEntry('boom', 'mutation', () => {
      throw new Error('exploded');
    });
    const closed = await runWithOverrides([{ ...boom }], req);
    expect(closed.status).toBe('mutation_failed');

    const open = await runWithOverrides([{ ...boom, overrides: { failOpen: true } }], req);
    expect(open.status).toBe('success');
  });

  it('priorityHint override reorders mutations', async () => {
    const order: string[] = [];
    const first = createEntry(
      'a-mutator',
      'mutation',
      () => {
        order.push('a');
        return { type: 'mutation', phase: 'request', modified: false };
      },
      { priorityHint: 10 },
    );
    const second = createEntry(
      'b-mutator',
      'mutation',
      () => {
        order.push('b');
        return { type: 'mutation', phase: 'request', modified: false };
      },
      { priorityHint: 20 },
    );

    await runWithOverrides([{ ...first }, { ...second, overrides: { priorityHint: -1 } }], req);
    expect(order).toEqual(['b', 'a']);
  });

  it('hook narrowing excludes the interceptor from non-matching events', async () => {
    let invoked = false;
    const validator = createEntry(
      'narrowed',
      'validation',
      () => {
        invoked = true;
        return validationSuccess('request');
      },
      { events: [InterceptionEvents.ToolsCall, InterceptionEvents.PromptsGet] },
    );

    const result = await runWithOverrides(
      [
        {
          ...validator,
          overrides: {
            hooks: [{ events: [InterceptionEvents.PromptsGet], phase: 'request' }],
          },
        },
      ],
      req,
    );
    expect(result.status).toBe('success');
    expect(invoked).toBe(false);
  });

  it('rejects override hooks that widen beyond declared hooks', async () => {
    const validator = createEntry('widened', 'validation', () => validationSuccess('request'), {
      events: [InterceptionEvents.ToolsCall],
      phase: 'request',
    });

    await expect(
      runWithOverrides(
        [
          {
            ...validator,
            overrides: {
              hooks: [{ events: [InterceptionEvents.ResourcesRead], phase: 'request' }],
            },
          },
        ],
        req,
      ),
    ).rejects.toBeInstanceOf(InterceptorOverrideHookError);
  });

  it('per-interceptor timeout override blocks a hung fail-closed mutator', async () => {
    const hung = createEntry(
      'hung',
      'mutation',
      (_, signal) =>
        new Promise((_resolve, reject) => {
          signal?.addEventListener('abort', () =>
            reject(new DOMException('timed out', 'TimeoutError')),
          );
        }),
    );

    const result = await runWithOverrides([{ ...hung, overrides: { timeoutMs: 50 } }], req);
    expect(result.status).toBe('mutation_failed');
    expect(result.abortedAt?.interceptor).toBe('hung');
  });

  it('forwards per-interceptor config from chain params to invoke params', async () => {
    let seen: unknown;
    const validator = createEntry('configured', 'validation', (invokeParams) => {
      seen = invokeParams.config;
      return validationSuccess('request');
    });

    await runWithOverrides([{ ...validator }], {
      ...req,
      config: { configured: { redact: true } },
    });
    expect(seen).toEqual({ redact: true });
  });

  it('completes all validations before rejecting and aggregates every result', async () => {
    const failing = createEntry('failing', 'validation', () =>
      validationFailure('request', { message: 'blocked', severity: 'error' }),
    );
    const warning = createEntry('warning', 'validation', () => ({
      type: 'validation',
      phase: 'request',
      valid: true,
      messages: [{ message: 'heads up', severity: 'warn' }],
    }));

    const result = await runWithOverrides([{ ...failing }, { ...warning }], req);
    expect(result.status).toBe('validation_failed');
    expect(result.results.map((r) => r.interceptor).sort()).toEqual(['failing', 'warning']);
    expect(result.validationSummary).toEqual({ errors: 1, warnings: 1, infos: 0 });
  });

  it('caller cancellation rejects instead of reporting chain timeout', async () => {
    const controller = new AbortController();
    const slow = createEntry(
      'slow',
      'validation',
      (_, signal) =>
        new Promise((_resolve, reject) => {
          signal?.addEventListener('abort', () => reject(signal.reason));
        }),
    );

    const pending = runWithOverrides([{ ...slow }], req, controller.signal);
    controller.abort();
    await expect(pending).rejects.toThrow();
  });
});
