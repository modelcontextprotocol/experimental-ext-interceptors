/**
 * Attested validation — the load-bearing implementation of the reserved
 * SEP-2624 `ValidationResult.signature` field.
 *
 * An interceptor signs its decision with an Ed25519 private key; ANY party
 * verifies it later, offline, against a PINNED trusted public key — no callback
 * to the issuer, no shared state. This is what turns a validator's "trust me, I
 * checked" into "check it yourself": the proof outlives the request.
 *
 * Security model (do not weaken):
 *  - The signature covers the canonicalized result with the `signature` field
 *    removed, so it binds every other field. Tampering any field fails verify.
 *  - Verification pins the trusted issuer key supplied by the caller and IGNORES
 *    the key embedded in the receipt. A receipt forged with an attacker's key
 *    (and carrying that key) is internally self-consistent but MUST fail against
 *    the pinned key. That is the whole point.
 *
 * Uses Web Crypto Ed25519 — portable across Node 20+, Deno, and browsers, with
 * no external signing dependency.
 */
import { canonicalize } from "./canonicalize.ts";
import type {
  AttestationVerification,
  AttestedSignature,
  ValidationResult,
} from "./types.ts";

const ED25519 = { name: "Ed25519" } as const;

export interface SigningKeyPair {
  readonly privateKey: CryptoKey;
  readonly publicKey: CryptoKey;
}

// ── base64 helpers (portable; no Buffer dependency) ─────────────────────────
function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

// Return type intentionally inferred: under TS >= 5.7 it is the ArrayBuffer-
// backed `Uint8Array<ArrayBuffer>` that Web Crypto's `BufferSource` demands,
// while the annotation `Uint8Array` would widen to `Uint8Array<ArrayBufferLike>`
// and fail; under TS <= 5.6 the generic syntax does not exist at all.
function fromBase64(b64: string) {
  const binary = atob(b64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}

// ── key management ──────────────────────────────────────────────────────────
export async function generateSigningKeyPair(): Promise<SigningKeyPair> {
  const pair = (await crypto.subtle.generateKey(ED25519, true, [
    "sign",
    "verify",
  ])) as CryptoKeyPair;
  return { privateKey: pair.privateKey, publicKey: pair.publicKey };
}

export async function exportPublicKeyBase64(key: CryptoKey): Promise<string> {
  return toBase64(new Uint8Array(await crypto.subtle.exportKey("raw", key)));
}

export async function importPublicKeyBase64(b64: string): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", fromBase64(b64), ED25519, false, [
    "verify",
  ]);
}

/** Export the private key as PKCS#8 base64 (for KMS/hardware custody, not chat). */
export async function exportPrivateKeyBase64(key: CryptoKey): Promise<string> {
  return toBase64(new Uint8Array(await crypto.subtle.exportKey("pkcs8", key)));
}

export async function importPrivateKeyBase64(b64: string): Promise<CryptoKey> {
  return crypto.subtle.importKey("pkcs8", fromBase64(b64), ED25519, true, [
    "sign",
  ]);
}

// ── attest / verify ─────────────────────────────────────────────────────────
function withoutSignature(
  result: ValidationResult,
): Omit<ValidationResult, "signature"> {
  const { signature: _omit, ...rest } = result;
  return rest;
}

// Inferred for the same BufferSource reason as `fromBase64` above.
function payloadBytes(result: ValidationResult) {
  return new TextEncoder().encode(canonicalize(withoutSignature(result)));
}

/** Sign a validation result, returning a copy with the `signature` field set. */
export async function attestValidationResult(
  result: ValidationResult,
  keys: SigningKeyPair,
): Promise<ValidationResult> {
  const raw = await crypto.subtle.sign(
    ED25519,
    keys.privateKey,
    payloadBytes(result),
  );
  const signature: AttestedSignature = {
    algorithm: "ed25519",
    publicKey: await exportPublicKeyBase64(keys.publicKey),
    value: toBase64(new Uint8Array(raw)),
  };
  return { ...result, signature };
}

/**
 * Verify an attested result OFFLINE against a PINNED trusted issuer key.
 * Never throws on hostile input — any failure resolves to `{ ok: false, reason }`.
 */
export async function verifyAttestedValidationResult(
  result: ValidationResult,
  trustedPublicKeyBase64: string,
): Promise<AttestationVerification> {
  try {
    const sig = result.signature;
    if (!sig) return { ok: false, reason: "missing-signature" };
    if (sig.algorithm !== "ed25519") {
      return { ok: false, reason: "unsupported-algorithm" };
    }
    const key = await importPublicKeyBase64(trustedPublicKeyBase64);
    const verified = await crypto.subtle.verify(
      ED25519,
      key,
      fromBase64(sig.value),
      payloadBytes(result),
    );
    return verified ? { ok: true } : { ok: false, reason: "signature-mismatch" };
  } catch {
    return { ok: false, reason: "verify-error" };
  }
}
