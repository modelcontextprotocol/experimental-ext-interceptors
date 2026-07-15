// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * End-to-end attestation wiring: a cross-boundary-guard DENIAL leaves as a
 * signed SEP wire result that verifies OFFLINE against a pinned issuer key,
 * fails on any tamper, and fails against a forged (attacker-signed) receipt.
 * This is the "check it yourself" property — the proof outlives the request.
 */
import { describe, expect, it } from "vitest";
import {
  exportPublicKeyBase64,
  generateSigningKeyPair,
  toWireValidationResult,
  verifyAttestedValidation,
  withAttestation,
} from "./attested-validator.js";
import type { WireValidationResult } from "./attested-validator.js";
import { attestValidationResult } from "@formalcore/mcp-attested-validation";
import {
  INTERCEPTION_EVENT,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "../protocol/constants.js";
import type { InterceptorPhase } from "../protocol/constants.js";
import type { InvokeContext, InvokeParams, ValidationResult } from "../protocol/types.js";
import {
  createCrossBoundaryGuard,
  CROSS_BOUNDARY_GUARD_NAME,
} from "../samples/security/cross-boundary-guard.js";
import { SECRET_FORMATS } from "../samples/security/secret-formats.js";
import { createSecretlessRedactor } from "../samples/security/secretless-redactor.js";

const SESSION = "attest-session";

function ctx(): InvokeContext {
  return { principal: null, traceId: null, spanId: null, timestamp: null, sessionId: SESSION };
}

function params(phase: InterceptorPhase, payload: unknown): InvokeParams {
  return {
    name: CROSS_BOUNDARY_GUARD_NAME,
    event: INTERCEPTION_EVENT.ToolsCall,
    phase,
    payload,
    config: null,
    timeoutMs: null,
    context: ctx(),
  };
}

const SECRET = SECRET_FORMATS[0].example;

/** Taint the session: read_file request → response carrying the secret. */
async function primeGuard(guard: ReturnType<typeof createCrossBoundaryGuard>): Promise<void> {
  await guard.handler(
    params(INTERCEPTOR_PHASE.Request, { name: "read_file", arguments: { path: "/tmp/env" } }),
    null,
  );
  await guard.handler(
    params(INTERCEPTOR_PHASE.Response, { content: [{ type: "text", text: SECRET }] }),
    null,
  );
}

const EXFIL_PAYLOAD = {
  name: "write_query",
  arguments: { query: `INSERT INTO notes VALUES ('${SECRET}')` },
};

async function attestedDenial(): Promise<{
  result: WireValidationResult;
  pinnedKey: string;
}> {
  const guard = createCrossBoundaryGuard();
  await primeGuard(guard);
  const keys = await generateSigningKeyPair();
  const attestedGuard = withAttestation(guard, keys);
  const result = await attestedGuard(params(INTERCEPTOR_PHASE.Request, EXFIL_PAYLOAD), null);
  return { result, pinnedKey: await exportPublicKeyBase64(keys.publicKey) };
}

describe("attested cross-boundary-guard denial", () => {
  it("returns a DENIAL carrying an ed25519 signature", async () => {
    const { result } = await attestedDenial();
    expect(result.valid).toBe(false);
    expect(result.severity).toBe(VALIDATION_SEVERITY.Error);
    expect(result.interceptor).toBe(CROSS_BOUNDARY_GUARD_NAME);
    expect(result.signature?.algorithm).toBe("ed25519");
    expect(result.signature?.value.length).toBeGreaterThan(0);
  });

  it("verifies OFFLINE against the pinned issuer key", async () => {
    const { result, pinnedKey } = await attestedDenial();
    expect(await verifyAttestedValidation(result, pinnedKey)).toEqual({ ok: true });
  });

  it("fails verification when the DECISION is tampered (valid flipped to true)", async () => {
    const { result, pinnedKey } = await attestedDenial();
    const tampered: WireValidationResult = { ...result, valid: true };
    expect(await verifyAttestedValidation(tampered, pinnedKey)).toEqual({
      ok: false,
      reason: "signature-mismatch",
    });
  });

  it("fails verification when the MESSAGE is tampered", async () => {
    const { result, pinnedKey } = await attestedDenial();
    const tampered: WireValidationResult = {
      ...result,
      messages: [{ message: "nothing to see here", severity: VALIDATION_SEVERITY.Info }],
    };
    expect(await verifyAttestedValidation(tampered, pinnedKey)).toEqual({
      ok: false,
      reason: "signature-mismatch",
    });
  });

  it("rejects a FORGED receipt: attacker re-signs with their own key", async () => {
    const { result, pinnedKey } = await attestedDenial();
    const attacker = await generateSigningKeyPair();
    // Internally self-consistent — signed by the attacker, carrying the
    // attacker's public key — but it MUST fail against the pinned issuer key.
    const forged = await attestValidationResult({ ...result, valid: true }, attacker);
    expect(await verifyAttestedValidation(forged, pinnedKey)).toEqual({
      ok: false,
      reason: "signature-mismatch",
    });
  });

  it("rejects an unsigned result", async () => {
    const { result, pinnedKey } = await attestedDenial();
    const { signature: _omit, ...unsigned } = result;
    expect(await verifyAttestedValidation(unsigned, pinnedKey)).toEqual({
      ok: false,
      reason: "missing-signature",
    });
  });
});

describe("wiring seams", () => {
  it("maps the interior result to the SEP wire shape (null → omitted)", () => {
    const interior: ValidationResult = {
      type: INTERCEPTOR_TYPE.Validation,
      interceptor: "g",
      phase: INTERCEPTOR_PHASE.Request,
      durationMs: null,
      info: null,
      valid: true,
      severity: null,
      messages: [],
      suggestions: [],
    };
    const wire = toWireValidationResult(interior);
    expect(wire).toEqual({
      interceptor: "g",
      type: INTERCEPTOR_TYPE.Validation,
      phase: INTERCEPTOR_PHASE.Request,
      valid: true,
    });
    expect(Object.keys(wire)).not.toContain("severity");
    expect(Object.keys(wire)).not.toContain("durationMs");
  });

  it("refuses to attest a result with no issuer", () => {
    const anonymous: ValidationResult = {
      type: INTERCEPTOR_TYPE.Validation,
      interceptor: null,
      phase: INTERCEPTOR_PHASE.Request,
      durationMs: null,
      info: null,
      valid: false,
      severity: VALIDATION_SEVERITY.Error,
      messages: [],
      suggestions: [],
    };
    expect(() => toWireValidationResult(anonymous)).toThrow(/issuer/);
  });

  it("refuses to wrap a mutator", async () => {
    const keys = await generateSigningKeyPair();
    expect(() => withAttestation(createSecretlessRedactor(), keys)).toThrow(
      /validators only/,
    );
  });
});
