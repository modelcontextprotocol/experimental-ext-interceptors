// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import {
  McpInterceptorChainException,
  McpInterceptorValidationException,
  collectValidationErrorMessages,
  formatValidationFailedChainMessage,
  throwChainFailure,
} from './errors.js';
import type { InterceptorChainResult } from './types.js';

function validationFailedResult(
  overrides: Partial<InterceptorChainResult> = {},
): InterceptorChainResult {
  return {
    status: 'validation_failed',
    event: 'tools/call',
    phase: 'request',
    results: [
      {
        type: 'validation',
        phase: 'request',
        interceptor: 'pii-validator',
        valid: false,
        severity: 'error',
        messages: [
          {
            path: '$.arguments',
            message: 'Payload may contain Social Security Number data',
            severity: 'error',
          },
        ],
      },
    ],
    validationSummary: { errors: 1, warnings: 0, infos: 0 },
    totalDurationMs: 1,
    abortedAt: {
      interceptor: 'pii-validator',
      reason: 'Payload may contain Social Security Number data',
      type: 'validation',
    },
    ...overrides,
  };
}

describe('formatValidationFailedChainMessage', () => {
  it('names the interceptor and reported-invalid outcome', () => {
    const chainResult = validationFailedResult();
    const message = formatValidationFailedChainMessage('tools/call', chainResult);
    expect(message).toContain('tools/call');
    expect(message).toContain('request phase');
    expect(message).toContain('validation interceptor "pii-validator" reported invalid');
    expect(message).toContain('Payload may contain Social Security Number data');
    expect(message).not.toMatch(/validation failed for tools\/call/i);
  });
});

describe('collectValidationErrorMessages', () => {
  it('collects messages from invalid validation results', () => {
    const messages = collectValidationErrorMessages(validationFailedResult());
    expect(messages).toHaveLength(1);
    expect(messages[0]?.message).toContain('Social Security');
  });
});

describe('throwChainFailure', () => {
  it('throws McpInterceptorValidationException with SEP-aligned message', () => {
    const chainResult = validationFailedResult();
    expect(() => throwChainFailure('tools/call', 'request', 'validation_failed', chainResult)).toThrow(
      McpInterceptorValidationException,
    );

    try {
      throwChainFailure('tools/call', 'request', 'validation_failed', chainResult);
    } catch (err) {
      const ex = err as McpInterceptorValidationException;
      expect(ex.message).toContain('pii-validator');
      expect(ex.message).toContain('reported invalid');
      expect(ex.message).toMatch(/validation_failed|reported invalid/);
      expect(ex.message).not.toMatch(/validation failed for tools\/call/i);
      expect(ex.validationMessages).toHaveLength(1);
      expect(ex.chainResult?.status).toBe('validation_failed');
      expect(ex.chainResult?.abortedAt?.type).toBe('validation');
    }
  });

  it('throws McpInterceptorChainException for mutation_failed with interceptor context', () => {
    const chainResult: InterceptorChainResult = {
      status: 'mutation_failed',
      event: 'tools/call',
      phase: 'response',
      results: [],
      validationSummary: { errors: 0, warnings: 0, infos: 0 },
      totalDurationMs: 1,
      abortedAt: {
        interceptor: 'bad-mutator',
        reason: 'boom',
        type: 'mutation',
      },
    };

    expect(() => throwChainFailure('tools/call', 'response', 'mutation_failed', chainResult)).toThrow(
      McpInterceptorChainException,
    );

    try {
      throwChainFailure('tools/call', 'response', 'mutation_failed', chainResult);
    } catch (err) {
      const ex = err as McpInterceptorChainException;
      expect(ex.status).toBe('mutation_failed');
      expect(ex.message).toContain('bad-mutator');
      expect(ex.message).toContain('mutation interceptor');
    }
  });
});
