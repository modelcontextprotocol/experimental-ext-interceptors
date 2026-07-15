import { test } from "node:test";
import assert from "node:assert/strict";
import {
  attestValidationResult,
  exportPublicKeyBase64,
  generateSigningKeyPair,
  verifyAttestedValidationResult,
} from "../src/attest.ts";
import type { ValidationResult } from "../src/types.ts";

function sample(): ValidationResult {
  return {
    interceptor: "cross-boundary-guard",
    type: "validation",
    phase: "request",
    valid: false,
    severity: "error",
    messages: [
      {
        path: "params.arguments.query",
        message: "secret read from filesystem may not be written to sqlite",
        severity: "error",
      },
    ],
  };
}

test("a valid attestation verifies against the pinned issuer key", async () => {
  const keys = await generateSigningKeyPair();
  const pinned = await exportPublicKeyBase64(keys.publicKey);
  const attested = await attestValidationResult(sample(), keys);

  assert.equal(attested.signature?.algorithm, "ed25519");
  const v = await verifyAttestedValidationResult(attested, pinned);
  assert.equal(v.ok, true);
});

test("tampering any field breaks verification", async () => {
  const keys = await generateSigningKeyPair();
  const pinned = await exportPublicKeyBase64(keys.publicKey);
  const attested = await attestValidationResult(sample(), keys);

  const flips: ValidationResult[] = [
    { ...attested, valid: true },
    { ...attested, severity: "warn" },
    { ...attested, interceptor: "something-else" },
    { ...attested, phase: "response" },
    {
      ...attested,
      messages: [{ message: "different", severity: "error" }],
    },
  ];
  for (const t of flips) {
    assert.equal((await verifyAttestedValidationResult(t, pinned)).ok, false);
  }
});

test("a receipt forged with an untrusted key fails against the pinned key", async () => {
  const issuer = await generateSigningKeyPair();
  const issuerPinned = await exportPublicKeyBase64(issuer.publicKey);

  const attacker = await generateSigningKeyPair();
  const forged = await attestValidationResult({ ...sample(), valid: true }, attacker);

  // Self-consistent against its own embedded key ...
  assert.equal(
    (await verifyAttestedValidationResult(forged, forged.signature!.publicKey)).ok,
    true,
  );
  // ... but rejected against the PINNED trusted issuer key. This is the H1 fix.
  assert.equal((await verifyAttestedValidationResult(forged, issuerPinned)).ok, false);
});

test("verification is canonical: key order does not matter", async () => {
  const keys = await generateSigningKeyPair();
  const pinned = await exportPublicKeyBase64(keys.publicKey);
  const attested = await attestValidationResult(sample(), keys);

  // Rebuild the same result with keys in a different insertion order.
  const reordered: ValidationResult = {
    phase: "request",
    valid: false,
    type: "validation",
    severity: "error",
    interceptor: "cross-boundary-guard",
    messages: attested.messages,
    signature: attested.signature,
  };
  assert.equal((await verifyAttestedValidationResult(reordered, pinned)).ok, true);
});

test("a missing or wrong-algorithm signature is reported, not thrown", async () => {
  const keys = await generateSigningKeyPair();
  const pinned = await exportPublicKeyBase64(keys.publicKey);

  assert.deepEqual(await verifyAttestedValidationResult(sample(), pinned), {
    ok: false,
    reason: "missing-signature",
  });
});
