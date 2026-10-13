// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from './constants.js';
import {
  InterceptorResultSchema,
  InterceptorSchema,
  ListInterceptorsResultSchema,
  MutationInterceptorResultSchema,
  SinkInterceptorResultSchema,
  ValidationInterceptorResultSchema,
} from './zod-schemas.js';
import type { Interceptor, InterceptorChainResult } from './types.js';

function roundTrip<T>(schema: { parse: (v: unknown) => T }, value: T): T {
  return schema.parse(JSON.parse(JSON.stringify(value)));
}

describe('protocol wire shapes (Zod round-trip)', () => {
  it('Interceptor round-trips rich descriptor', () => {
    const interceptor: Interceptor = {
      name: 'pii-validator',
      version: '1.0.0',
      description: 'Validates PII',
      type: 'validation',
      hooks: [
        { events: [InterceptionEvents.ToolsCall], phase: 'request' },
        { events: [InterceptionEvents.ToolsCall], phase: 'response' },
      ],
      priorityHint: -1000,
      compat: { minProtocol: '2024-11-05' },
    };

    const parsed = roundTrip(InterceptorSchema, interceptor);
    expect(parsed.name).toBe('pii-validator');
    expect(parsed.hooks).toHaveLength(2);
    expect(parsed.priorityHint).toBe(-1000);
  });

  it('Interceptor round-trips per-phase priorityHint object', () => {
    const parsed = InterceptorSchema.parse({
      name: 'phase-priority',
      type: 'mutation',
      hooks: [{ events: ['tools/call'], phase: 'request' }],
      priorityHint: { request: -1000, response: 1000 },
    });
    expect(parsed.priorityHint).toEqual({ request: -1000, response: 1000 });
  });

  it('normalizes legacy mode "enforce" to active when parsing Interceptor', () => {
    const parsed = InterceptorSchema.parse({
      name: 'legacy-host',
      type: 'validation',
      hooks: [{ events: ['tools/call'], phase: 'request' }],
      mode: 'enforce',
    });
    expect(parsed.mode).toBe('active');
  });

  it('Interceptor omits unset optional fields in JSON', () => {
    const minimal: Interceptor = {
      name: 'test',
      type: 'sink',
      hooks: [{ events: [InterceptionEvents.All], phase: 'request' }],
    };
    const json = JSON.stringify(minimal);
    const doc = JSON.parse(json) as Record<string, unknown>;
    expect(doc.version).toBeUndefined();
    expect(doc.mode).toBeUndefined();
    expect(doc.failOpen).toBeUndefined();
  });

  it('validation result round-trips', () => {
    const parsed = roundTrip(ValidationInterceptorResultSchema, {
      type: 'validation',
      phase: 'request',
      interceptor: 'val',
      valid: false,
      severity: 'error',
      messages: [{ message: 'bad', severity: 'error', path: '$.x' }],
    });
    expect(parsed.valid).toBe(false);
    expect(parsed.messages?.[0]?.path).toBe('$.x');
  });

  it('mutation and sink results round-trip', () => {
    const mutation = roundTrip(MutationInterceptorResultSchema, {
      type: 'mutation',
      phase: 'request',
      modified: true,
      payload: { email: '[REDACTED]' },
    });
    expect(mutation.modified).toBe(true);

    const sink = roundTrip(SinkInterceptorResultSchema, {
      type: 'sink',
      phase: 'response',
      recorded: true,
      metrics: { latencyMs: 12.5 },
    });
    expect(sink.metrics?.latencyMs).toBe(12.5);
  });

  it('list interceptors result round-trips', () => {
    const parsed = roundTrip(ListInterceptorsResultSchema, {
      interceptors: [
        {
          name: 'a',
          type: 'validation',
          hooks: [{ events: ['tools/call'], phase: 'request' }],
        },
      ],
    });
    expect(parsed.interceptors).toHaveLength(1);
  });

  it('discriminated union parses each interceptor result type', () => {
    const validation = InterceptorResultSchema.parse({
      type: 'validation',
      phase: 'request',
      valid: true,
    });
    expect(validation.type).toBe('validation');

    const mutation = InterceptorResultSchema.parse({
      type: 'mutation',
      phase: 'response',
      modified: false,
    });
    expect(mutation.type).toBe('mutation');
  });

  it('chain status serializes as snake_case strings', () => {
    const sample: InterceptorChainResult = {
      status: 'validation_failed',
      event: InterceptionEvents.ToolsCall,
      phase: 'request',
      results: [],
      finalPayload: {},
      validationSummary: { errors: 1, warnings: 0, infos: 0 },
      totalDurationMs: 1,
    };
    const json = JSON.stringify(sample);
    expect(json).toContain('"validation_failed"');
    const parsed = JSON.parse(json) as { status: string };
    expect(parsed.status).toBe('validation_failed');
  });
});
