// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from './constants.js';
import { parseInterceptorResult } from './results.js';
import type { Interceptor } from './types.js';

describe('parseInterceptorResult', () => {
  it('parses validation results', () => {
    const parsed = parseInterceptorResult({
      type: 'validation',
      phase: 'request',
      interceptor: 'pii-validator',
      valid: false,
      severity: 'error',
      messages: [{ message: 'Contains PII', severity: 'error', path: '$.email' }],
    });

    expect(parsed.type).toBe('validation');
    if (parsed.type === 'validation') {
      expect(parsed.valid).toBe(false);
      expect(parsed.interceptor).toBe('pii-validator');
      expect(parsed.messages?.[0]?.path).toBe('$.email');
    }
  });

  it('parses mutation and sink results', () => {
    const mutation = parseInterceptorResult({
      type: 'mutation',
      phase: 'request',
      modified: true,
      payload: { email: '[REDACTED]' },
    });
    expect(mutation.type).toBe('mutation');

    const sink = parseInterceptorResult({
      type: 'sink',
      phase: 'response',
      recorded: true,
      metrics: { latencyMs: 12.5 },
    });
    expect(sink.type).toBe('sink');
    if (sink.type === 'sink') {
      expect(sink.metrics?.latencyMs).toBe(12.5);
    }
  });

  it('rejects non-boolean flags instead of coercing them', () => {
    expect(() =>
      parseInterceptorResult({ type: 'validation', phase: 'request', valid: 'false' }),
    ).toThrow();
    expect(() =>
      parseInterceptorResult({ type: 'mutation', phase: 'request', modified: 1 }),
    ).toThrow();
    expect(() =>
      parseInterceptorResult({ type: 'sink', phase: 'response' }),
    ).toThrow();
  });
});

describe('interceptor descriptor JSON shape', () => {
  it('round-trips minimal fields', () => {
    const interceptor: Interceptor = {
      name: 'pii-validator',
      type: 'validation',
      hooks: [
        {
          events: [InterceptionEvents.ToolsCall],
          phase: 'request',
        },
      ],
    };

    const json = JSON.stringify(interceptor);
    const parsed = JSON.parse(json) as Interceptor;

    expect(parsed.name).toBe('pii-validator');
    expect(parsed.hooks[0]?.events).toContain(InterceptionEvents.ToolsCall);
    expect(json).not.toContain('version');
  });
});
