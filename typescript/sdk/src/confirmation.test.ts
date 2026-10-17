// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by a Apache-2.0
// license that can be found in the LICENSE file.

import { createHash } from 'node:crypto';
import { describe, it, expect } from 'vitest';

import {
  callBinding,
  computeCallHash,
  confirmationRequired,
  createConfirmationToken,
  findConfirmation,
  requireConfirmation,
  verifyConfirmationToken,
} from './confirmation.js';
import { canonicalize } from './jcs.js';
import type { Confirmation, InterceptorInvocationParams } from './types.js';

const SECRET = 'test-secret';

function params(overrides: Partial<InterceptorInvocationParams> = {}): InterceptorInvocationParams {
  return {
    name: 'public-write-guard',
    event: 'tools/call',
    phase: 'request',
    payload: { name: 'post_issue', arguments: { repo: 'acme/site', body: 'hi' } },
    ...overrides,
    context: { timestamp: '2026-10-16T00:00:00Z', target: 'github', sessionId: 's1', ...overrides.context },
  };
}

function confirmationFor(p: InterceptorInvocationParams, at = new Date()): Confirmation {
  const req = confirmationRequired(p, { reason: 'r', secret: SECRET, now: () => at }).confirmation!;
  return { interceptor: p.name, id: req.id, callHash: req.callHash, token: req.token, decidedAt: at.toISOString() };
}

describe('canonicalize', () => {
  it('sorts keys and has no whitespace', () => {
    expect(canonicalize({ b: [1, { d: true, c: null }], a: 'x' })).toBe('{"a":"x","b":[1,{"c":null,"d":true}]}');
  });

  it('serializes numbers as ECMAScript does (RFC 8785 examples)', () => {
    expect(canonicalize([1e21, 1e-7, -0, 0.1, 4.5, 0.002])).toBe('[1e+21,1e-7,0,0.1,4.5,0.002]');
  });

  it('orders keys by UTF-16 code units', () => {
    expect(canonicalize({ '\u20ac': 1, '\ud83d\ude00': 2, '\r': 3, '1': 4 })).toBe(
      '{"\\r":3,"1":4,"\u20ac":1,"\ud83d\ude00":2}',
    );
  });

  it('omits undefined properties', () => {
    expect(canonicalize({ a: undefined, b: 1 })).toBe('{"b":1}');
  });

  it('rejects array holes instead of collapsing them', () => {
    expect(() => canonicalize(new Array(1))).toThrow(TypeError);
    expect(() => canonicalize(Object.assign([], { 1: 2 }))).toThrow(TypeError);
  });

  it('rejects non-plain objects such as Date', () => {
    expect(() => canonicalize({ at: new Date(0) })).toThrow(TypeError);
    expect(() => canonicalize(new Map())).toThrow(TypeError);
    expect(canonicalize(Object.create(null))).toBe('{}');
  });

  it('rejects values without an I-JSON form', () => {
    expect(() => canonicalize(NaN)).toThrow(TypeError);
    expect(() => canonicalize(1n)).toThrow(TypeError);
    expect(() => canonicalize('\ud800')).toThrow(TypeError);
    expect(() => canonicalize([undefined])).toThrow(TypeError);
  });
});

describe('computeCallHash', () => {
  it('is sha256 over the JCS form of { event, phase, target, payload }', () => {
    const p = params();
    const expected = createHash('sha256')
      .update(
        '{"event":"tools/call","payload":{"arguments":{"body":"hi","repo":"acme/site"},"name":"post_issue"},"phase":"request","target":"github"}',
      )
      .digest('hex');
    expect(computeCallHash(callBinding(p))).toBe(`sha256:${expected}`);
  });

  it('uses null when target is absent', () => {
    const p: InterceptorInvocationParams = { name: 'v', event: 'e', phase: 'request', payload: 1 };
    expect(callBinding(p).target).toBeNull();
  });

  it('differs for the same call to a different target', () => {
    const a = computeCallHash(callBinding(params()));
    const b = computeCallHash(callBinding(params({ context: { timestamp: 't', target: 'gitlab' } })));
    expect(a).not.toBe(b);
  });

  it('ignores payload key order', () => {
    const a = params({ payload: { x: 1, y: 2 } });
    const b = params({ payload: { y: 2, x: 1 } });
    expect(computeCallHash(callBinding(a))).toBe(computeCallHash(callBinding(b)));
  });
});

describe('confirmation tokens', () => {
  const claims = { interceptor: 'v', id: 'r1', callHash: 'sha256:aa', sessionId: 's1', expiresAt: '2026-10-16T00:05:00.000Z' };
  const before = new Date('2026-10-16T00:00:00Z');

  it('verifies for the same id, callHash and session before expiry', () => {
    const token = createConfirmationToken(SECRET, claims);
    expect(verifyConfirmationToken(SECRET, token, claims, before)).toBe(true);
  });

  it('rejects after expiry', () => {
    const token = createConfirmationToken(SECRET, claims);
    expect(verifyConfirmationToken(SECRET, token, claims, new Date('2026-10-16T00:05:00Z'))).toBe(false);
  });

  it('rejects other validators, ids, calls, sessions and secrets', () => {
    const token = createConfirmationToken(SECRET, claims);
    expect(verifyConfirmationToken(SECRET, token, { ...claims, interceptor: 'w' }, before)).toBe(false);
    expect(verifyConfirmationToken(SECRET, token, { ...claims, id: 'r2' }, before)).toBe(false);
    expect(verifyConfirmationToken(SECRET, token, { ...claims, callHash: 'sha256:bb' }, before)).toBe(false);
    expect(verifyConfirmationToken(SECRET, token, { ...claims, sessionId: 's2' }, before)).toBe(false);
    expect(verifyConfirmationToken('other', token, claims, before)).toBe(false);
  });

  it('rejects a tampered expiry', () => {
    const [v, , mac] = createConfirmationToken(SECRET, claims).split('.');
    const later = Buffer.from('2099-01-01T00:00:00.000Z').toString('base64url');
    expect(verifyConfirmationToken(SECRET, `${v}.${later}.${mac}`, claims, before)).toBe(false);
  });

  it('rejects malformed tokens', () => {
    expect(verifyConfirmationToken(SECRET, 'garbage', claims, before)).toBe(false);
    expect(verifyConfirmationToken(SECRET, 'v1.x.y', claims, before)).toBe(false);
  });
});

describe('confirmationRequired', () => {
  it('returns a blocking error carrying the request', () => {
    const now = new Date('2026-10-16T00:00:00Z');
    const p = params();
    const result = confirmationRequired(p, { reason: 'Post publicly?', secret: SECRET, ttlMs: 60_000, now: () => now });
    expect(result).toMatchObject({ interceptor: p.name, type: 'validation', phase: 'request', valid: false, severity: 'error' });
    expect(result.confirmation).toMatchObject({
      reason: 'Post publicly?',
      callHash: computeCallHash(callBinding(p)),
      expiresAt: '2026-10-16T00:01:00.000Z',
    });
    expect(result.confirmation!.token).toBeTypeOf('string');
  });

  it('issues a fresh id per request', () => {
    const p = params();
    expect(confirmationRequired(p, { reason: 'r' }).confirmation!.id).not.toBe(
      confirmationRequired(p, { reason: 'r' }).confirmation!.id,
    );
  });
});

describe('findConfirmation / requireConfirmation', () => {
  it('accepts a confirmation for the exact call', () => {
    const p = params();
    const c = confirmationFor(p);
    const withC = params({ context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [c] } });
    expect(findConfirmation(withC, { secret: SECRET })).toEqual(c);
    expect(requireConfirmation(withC, { reason: 'r', secret: SECRET }).valid).toBe(true);
  });

  it('rejects a confirmation for different arguments', () => {
    const c = confirmationFor(params());
    const other = params({
      payload: { name: 'post_issue', arguments: { repo: 'acme/site', body: 'secrets' } },
      context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [c] },
    });
    expect(findConfirmation(other, { secret: SECRET })).toBeUndefined();
    expect(requireConfirmation(other, { reason: 'r', secret: SECRET }).confirmation).toBeDefined();
  });

  it('rejects a confirmation for another target', () => {
    const c = confirmationFor(params());
    const other = params({ context: { timestamp: 't', target: 'gitlab', sessionId: 's1', confirmations: [c] } });
    expect(findConfirmation(other, { secret: SECRET })).toBeUndefined();
  });

  it('rejects a confirmation from another session', () => {
    const c = confirmationFor(params());
    const other = params({ context: { timestamp: 't', target: 'github', sessionId: 's2', confirmations: [c] } });
    expect(findConfirmation(other, { secret: SECRET })).toBeUndefined();
  });

  it('rejects a confirmation addressed to another validator', () => {
    const c = { ...confirmationFor(params()), interceptor: 'someone-else' };
    const withC = params({ context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [c] } });
    expect(findConfirmation(withC, { secret: SECRET })).toBeUndefined();
  });

  it('rejects a confirmation relabeled for another validator sharing the secret', () => {
    const c = confirmationFor(params({ name: 'a' }));
    const asB = params({
      name: 'b',
      context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [{ ...c, interceptor: 'b' }] },
    });
    expect(findConfirmation(asB, { secret: SECRET })).toBeUndefined();
  });

  it('rejects an expired confirmation', () => {
    const c = confirmationFor(params(), new Date('2026-10-16T00:00:00Z'));
    const withC = params({ context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [c] } });
    expect(findConfirmation(withC, { secret: SECRET, now: () => new Date('2026-10-16T01:00:00Z') })).toBeUndefined();
  });

  it('rejects a missing or forged token when a secret is set', () => {
    const c = confirmationFor(params());
    const noToken = params({
      context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [{ ...c, token: undefined }] },
    });
    const forged = params({
      context: { timestamp: 't', target: 'github', sessionId: 's1', confirmations: [{ ...c, id: 'other' }] },
    });
    expect(findConfirmation(noToken, { secret: SECRET })).toBeUndefined();
    expect(findConfirmation(forged, { secret: SECRET })).toBeUndefined();
  });
});
