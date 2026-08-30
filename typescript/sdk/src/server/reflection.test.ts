// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../protocol/constants.js';
import { validationSuccess } from '../protocol/results.js';
import type { InvokeInterceptorRequestParams } from '../protocol/types.js';
import { buildInterceptorDescriptor } from './interceptor-definition.js';
import { defineInterceptor, invokeHandlerFunction, type InterceptorHandlerFn } from './reflection.js';

describe('defineInterceptor / reflection', () => {
  it('builds descriptor metadata from options', () => {
    const d = buildInterceptorDescriptor({
      name: 'bool-validator',
      type: 'validation',
      events: [InterceptionEvents.ToolsCall],
      phase: 'request',
    });
    expect(d.name).toBe('bool-validator');
    expect(d.hooks).toHaveLength(1);
    expect(d.hooks[0]?.events).toContain(InterceptionEvents.ToolsCall);
  });

  it('expands phase both to request and response hooks', () => {
    const d = buildInterceptorDescriptor({
      name: 'sink',
      type: 'sink',
      phase: 'both',
    });
    expect(d.hooks).toHaveLength(2);
    expect(d.hooks.map((h) => h.phase)).toEqual(['request', 'response']);
  });

  it('wraps boolean return as validation result', async () => {
    const reg = defineInterceptor(
      { name: 'bool-validator', type: 'validation', events: [InterceptionEvents.ToolsCall] },
      (payload: unknown) => (payload as { valid?: boolean }).valid === true,
    );

    const ok = await reg.handler({
      name: 'bool-validator',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { valid: true },
    });
    expect(ok.type).toBe('validation');
    if (ok.type === 'validation') {
      expect(ok.valid).toBe(true);
    }

    const bad = await reg.handler({
      name: 'bool-validator',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { valid: false },
    });
    if (bad.type === 'validation') {
      expect(bad.valid).toBe(false);
    }
  });

  it('binds named parameters (payload, event, phase)', async () => {
    const handler: InterceptorHandlerFn = (payload, event, phase) =>
      validationSuccess(phase as 'request' | 'response');

    const result = await invokeHandlerFunction(handler, 'validation', {
      name: 'x',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: {},
    });
    expect(result.type).toBe('validation');
  });

  it('binds destructuring parameters from invoke params', async () => {
    const reg = defineInterceptor(
      { name: 'destructure-val', type: 'validation', events: [InterceptionEvents.ToolsCall] },
      (({ payload, phase }: InvokeInterceptorRequestParams) => {
        if (payload && typeof payload === 'object' && 'ok' in payload) {
          return validationSuccess(phase);
        }
        return {
          type: 'validation' as const,
          phase,
          valid: false,
          severity: 'error' as const,
          messages: [{ message: 'bad', severity: 'error' as const }],
        };
      }) as InterceptorHandlerFn,
    );

    const ok = await reg.handler({
      name: 'destructure-val',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { ok: true },
    });
    if (ok.type === 'validation') {
      expect(ok.valid).toBe(true);
    }
  });

  it('binds a parameter named `signal` to the abort signal', async () => {
    const controller = new AbortController();
    let seenSignal: unknown = 'unset';

    const handler: InterceptorHandlerFn = ((
      payload: unknown,
      event: unknown,
      phase: unknown,
      context: unknown,
      signal: unknown,
    ) => {
      seenSignal = signal;
      return validationSuccess(phase as 'request' | 'response');
    }) as InterceptorHandlerFn;

    await invokeHandlerFunction(
      handler,
      'validation',
      {
        name: 'x',
        event: InterceptionEvents.ToolsCall,
        phase: 'request',
        payload: {},
      },
      controller.signal,
    );
    expect(seenSignal).toBe(controller.signal);
  });

  it('binds named parameters that carry default values', async () => {
    let seenPayload: unknown = 'unset';

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const handler: InterceptorHandlerFn = ((payload: any = {}, phase = 'request') => {
      seenPayload = payload;
      return validationSuccess(phase as 'request' | 'response');
    }) as InterceptorHandlerFn;

    const result = await invokeHandlerFunction(handler, 'validation', {
      name: 'x',
      event: InterceptionEvents.ToolsCall,
      phase: 'response',
      payload: { marker: 42 },
    });
    expect(seenPayload).toEqual({ marker: 42 });
    expect(result.phase).toBe('response');
  });

  it('binds the full request to a handler that takes `params`', async () => {
    let seen: unknown = 'unset';

    const handler: InterceptorHandlerFn = ((params: InvokeInterceptorRequestParams) => {
      seen = params;
      return validationSuccess(params.phase);
    }) as InterceptorHandlerFn;

    await invokeHandlerFunction(handler, 'validation', {
      name: 'x',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { marker: 1 },
      context: { sessionId: 'sess-1' },
    });
    expect(seen).toMatchObject({
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { marker: 1 },
      context: { sessionId: 'sess-1' },
    });
  });

  it('falls back to positional binding when no parameter name is recognized', async () => {
    const seen: unknown[] = [];

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const handler: InterceptorHandlerFn = ((first: any, second: any) => {
      seen.push(first, second);
      return validationSuccess('request');
    }) as InterceptorHandlerFn;

    await invokeHandlerFunction(handler, 'validation', {
      name: 'x',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { marker: 2 },
    });
    expect(seen).toEqual([{ marker: 2 }, InterceptionEvents.ToolsCall]);
  });

  // Built from source text because the transpiler prints `(params) =>` for every
  // arrow in this file, so a parenthesis-free parameter cannot be written directly.
  const bareArrow = (body: string): InterceptorHandlerFn =>
    // eslint-disable-next-line @typescript-eslint/no-implied-eval
    new Function(`return ${body}`)() as InterceptorHandlerFn;

  it('binds a bare arrow parameter written without parentheses', async () => {
    const handler = bareArrow(
      'params => ({ type: "validation", phase: "request", valid: true, info: { seen: params } })',
    );

    const result = await invokeHandlerFunction(handler, 'validation', {
      name: 'x',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { marker: 3 },
    });
    expect(result.info?.seen).toMatchObject({
      event: InterceptionEvents.ToolsCall,
      payload: { marker: 3 },
    });
  });

  it('does not read an object literal in a bare arrow body as destructuring', async () => {
    const handler = bareArrow(
      'payload => ({ type: "validation", phase: "request", valid: true, info: { seen: payload } })',
    );

    const result = await invokeHandlerFunction(handler, 'validation', {
      name: 'x',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: { marker: 4 },
    });
    expect(result.info?.seen).toEqual({ marker: 4 });
  });

  it('supports async handlers', async () => {
    const reg = defineInterceptor(
      { name: 'async-val', type: 'validation' },
      async () => {
        await Promise.resolve();
        return validationSuccess('request');
      },
    );
    const result = await reg.handler({
      name: 'async-val',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      payload: {},
    });
    expect(result.type).toBe('validation');
  });
});
