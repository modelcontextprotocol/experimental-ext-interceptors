// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by a Apache-2.0
// license that can be found in the LICENSE file.

import { describe, it, expect, vi } from 'vitest';

import { requireConfirmation } from './confirmation.js';
import type { InterceptorInvocationParams, ValidationResult } from './types.js';
import {
  isConfirmedCall,
  runValidationPhase,
  type ConfirmationHandler,
  type ValidationPhaseOptions,
  type ValidatorEntry,
} from './validation.js';

const SECRET = 'test-secret';
const PAYLOAD = { name: 'post_issue', arguments: { repo: 'acme/site', body: 'hi' } };

function pass(name: string): ValidatorEntry {
  return {
    name,
    invoke: (p) => Promise.resolve({ interceptor: name, type: 'validation', phase: p.phase, valid: true }),
  };
}

function reject(name: string, extra: Partial<ValidatorEntry> = {}): ValidatorEntry {
  return {
    name,
    invoke: (p): Promise<ValidationResult> =>
      Promise.resolve({
        interceptor: name,
        type: 'validation',
        phase: p.phase,
        valid: false,
        severity: 'error',
        messages: [{ message: 'no', severity: 'error' }],
      }),
    ...extra,
  };
}

function confirming(name = 'public-write-guard', ttlMs?: number): ValidatorEntry & { calls: InterceptorInvocationParams[] } {
  const calls: InterceptorInvocationParams[] = [];
  return {
    name,
    calls,
    invoke: (p) => {
      calls.push(p);
      return Promise.resolve(requireConfirmation(p, { reason: 'Post to a public repo?', secret: SECRET, ttlMs }));
    },
  };
}

function options(overrides: Partial<ValidationPhaseOptions>): ValidationPhaseOptions {
  return {
    event: 'tools/call',
    phase: 'request',
    payload: PAYLOAD,
    target: 'github',
    context: { sessionId: 's1' },
    validators: [],
    ...overrides,
  };
}

const approve: ConfirmationHandler = () => Promise.resolve({ approved: true, principal: { type: 'user', id: 'u1' } });

describe('runValidationPhase', () => {
  it('allows when every validator passes, without asking', async () => {
    const confirm = vi.fn(approve);
    const out = await runValidationPhase(options({ validators: [pass('a'), pass('b')], confirm }));
    expect(out.allowed).toBe(true);
    expect(out.confirmedCallHash).toBeUndefined();
    expect(confirm).not.toHaveBeenCalled();
  });

  it('sets context.target on every invocation', async () => {
    const v = confirming();
    await runValidationPhase(options({ validators: [v] }));
    expect(v.calls[0].context?.target).toBe('github');
  });

  it('blocks a confirmation request when the invoker has no handler (fail-closed)', async () => {
    const out = await runValidationPhase(options({ validators: [confirming()] }));
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/confirmation required/);
  });

  it('asks, re-validates with the confirmation, and allows on approval', async () => {
    const v = confirming();
    const confirm = vi.fn(approve);
    const out = await runValidationPhase(options({ validators: [v, pass('other')], confirm }));

    expect(out.allowed).toBe(true);
    expect(confirm).toHaveBeenCalledOnce();
    const prompt = confirm.mock.calls[0][0];
    expect(prompt).toMatchObject({ interceptor: v.name, target: 'github', payload: PAYLOAD });
    expect(prompt.request.reason).toBe('Post to a public repo?');

    expect(v.calls).toHaveLength(2);
    expect(v.calls[1].context?.confirmations).toEqual([
      expect.objectContaining({
        interceptor: v.name,
        id: prompt.request.id,
        callHash: prompt.request.callHash,
        token: prompt.request.token,
        principal: { type: 'user', id: 'u1' },
      }),
    ]);
    expect(out.confirmations).toHaveLength(1);
    expect(out.confirmedCallHash).toBe(prompt.request.callHash);
    expect(out.results.every((r) => r.valid)).toBe(true);
  });

  it('blocks without asking when another validator blocks plainly', async () => {
    const confirm = vi.fn(approve);
    const out = await runValidationPhase(options({ validators: [confirming(), reject('pii')], confirm }));
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/"pii" blocked/);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('blocks without asking when another validator fails closed', async () => {
    const confirm = vi.fn(approve);
    const broken: ValidatorEntry = { name: 'broken', invoke: () => Promise.reject(new Error('boom')) };
    const out = await runValidationPhase(options({ validators: [confirming(), broken], confirm }));
    expect(out.allowed).toBe(false);
    expect(out.failures).toHaveLength(1);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('blocks when a validator rejects with an undefined reason (fail-closed)', async () => {
    const broken: ValidatorEntry = { name: 'broken', invoke: () => Promise.reject(undefined) };
    const out = await runValidationPhase(options({ validators: [broken] }));
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/"broken" failed/);
    expect(out.failures).toEqual([{ interceptor: 'broken', mode: 'active', error: undefined }]);
  });

  it('blocks when the person denies', async () => {
    const out = await runValidationPhase(
      options({ validators: [confirming()], confirm: () => Promise.resolve({ approved: false }) }),
    );
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/denied/);
  });

  it('blocks when the handler cannot reach anyone', async () => {
    const out = await runValidationPhase(
      options({ validators: [confirming()], confirm: () => Promise.reject(new Error('no client')) }),
    );
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/could not be obtained/);
  });

  it('blocks and aborts the handler when no decision arrives before expiry', async () => {
    let signal: AbortSignal | undefined;
    const out = await runValidationPhase(
      options({
        validators: [confirming('public-write-guard', 20)],
        confirm: (_p, s) => {
          signal = s;
          return new Promise(() => {});
        },
      }),
    );
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/expired/);
    expect(signal?.aborted).toBe(true);
  });

  it('blocks when an earlier approval expires while a later prompt is pending', async () => {
    const out = await runValidationPhase(
      options({
        validators: [confirming('a', 30), confirming('b', 1_000)],
        confirm: async (prompt) => {
          if (prompt.interceptor === 'b') {
            await new Promise((r) => setTimeout(r, 60));
          }
          return { approved: true };
        },
      }),
    );
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/"a": confirmation expired before re-validation/);
    expect(out.confirmedCallHash).toBeUndefined();
  });

  it('blocks a request whose callHash is not bound to this call', async () => {
    const confirm = vi.fn(approve);
    const wrongTarget: ValidatorEntry = {
      name: 'v',
      invoke: (p) =>
        Promise.resolve(
          requireConfirmation({ ...p, context: { ...p.context!, target: 'elsewhere' } }, { reason: 'r' }),
        ),
    };
    const out = await runValidationPhase(options({ validators: [wrongTarget], confirm }));
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/not bound/);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('blocks when the validator still blocks after confirmation', async () => {
    const confirm = vi.fn(approve);
    let calls = 0;
    const stubborn: ValidatorEntry = {
      name: 'stubborn',
      invoke: (p) =>
        calls++ === 0
          ? Promise.resolve(requireConfirmation(p, { reason: 'r', secret: SECRET }))
          : reject('stubborn').invoke(p),
    };
    const out = await runValidationPhase(options({ validators: [stubborn], confirm }));
    expect(out.allowed).toBe(false);
    expect(out.reason).toMatch(/"stubborn" blocked/);
    expect(out.confirmedCallHash).toBeUndefined();
    expect(confirm).toHaveBeenCalledOnce();
  });

  it('does not re-use a confirmation across phases', async () => {
    const v = confirming();
    const first = await runValidationPhase(options({ validators: [v], confirm: approve }));
    expect(first.allowed).toBe(true);
    const second = await runValidationPhase(options({ validators: [v] }));
    expect(second.allowed).toBe(false);
  });

  it('attaches each confirmation only to the validator that asked', async () => {
    const a = confirming('a');
    const b = confirming('b');
    const out = await runValidationPhase(options({ validators: [a, b], confirm: approve }));
    expect(out.allowed).toBe(true);
    expect(a.calls[1].context?.confirmations?.map((c) => c.interceptor)).toEqual(['a']);
    expect(b.calls[1].context?.confirmations?.map((c) => c.interceptor)).toEqual(['b']);
  });

  it('in audit mode, records the request but neither asks nor blocks', async () => {
    const confirm = vi.fn(approve);
    const v = { ...confirming(), mode: 'audit' as const };
    const out = await runValidationPhase(options({ validators: [v], confirm }));
    expect(out.allowed).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
    expect(out.results[0].confirmation).toBeDefined();
  });
});

describe('isConfirmedCall', () => {
  it('detects a payload changed after confirmation', async () => {
    const out = await runValidationPhase(options({ validators: [confirming()], confirm: approve }));
    const hash = out.confirmedCallHash!;
    const same = { event: 'tools/call', phase: 'request' as const, target: 'github', payload: PAYLOAD };
    expect(isConfirmedCall(same, hash)).toBe(true);
    expect(isConfirmedCall({ ...same, payload: { ...PAYLOAD, arguments: { repo: 'x' } } }, hash)).toBe(false);
    expect(isConfirmedCall({ ...same, target: 'gitlab' }, hash)).toBe(false);
  });
});
