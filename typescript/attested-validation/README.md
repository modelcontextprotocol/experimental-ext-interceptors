# mcp-attested-validation

**A reference implementation of the reserved SEP-2624 `ValidationResult.signature` field** - Ed25519-signed, offline-verifiable attested interceptor decisions.

SEP-2624 defines a `signature` object on `ValidationResult` and marks it *"reserved for future use to enable cryptographic verification of validation results at trust boundaries."* This module implements that field for real, so a validating interceptor's decision stops being "trust me, I checked" and becomes "check it yourself."

## Why this exists

A gateway/validator's guarantee is normally *"I ran, and I say this is fine."* You have to trust it was in the path, running, and honest. An **attested** validation result carries a signature that **any party - the client, the server, another gateway, an auditor six months later, a regulator on a laptop with no access to your systems - can verify offline.** The proof outlives the request; the enforcer becomes replaceable.

This maps directly onto compliance regimes that require tamper-evident records of automated decisions (e.g. EU AI Act Article 12/14-style logging): the authorization object and the audit artifact become the *same signed bytes*.

## Security model (do not weaken)

- The signature covers the **canonicalized result with the `signature` field removed**, binding every other field. Tamper any field → verification fails.
- Verification pins the **trusted issuer key supplied by the caller** and **ignores the key embedded in the receipt**. A receipt forged with an attacker's key (and carrying that key) is internally self-consistent but **fails against the pinned key**. Verifying against the embedded key would let a forgery verify itself - so we never do.
- The signer's **private key** is the root of trust. In production it belongs in a key-management service or hardware module and is never exported to logs, repos, or chat. Publish only the **public** key, pinned out of band.

## Usage

```ts
import {
  generateSigningKeyPair,
  attestValidationResult,
  verifyAttestedValidationResult,
  exportPublicKeyBase64,
} from "@formalcore/mcp-attested-validation";

const keys = await generateSigningKeyPair();
const pinned = await exportPublicKeyBase64(keys.publicKey); // publish this

// An interceptor signs its decision:
const attested = await attestValidationResult(
  {
    interceptor: "cross-boundary-guard",
    type: "validation",
    phase: "request",
    valid: false,
    severity: "error",
    messages: [{ message: "secret may not cross a server boundary", severity: "error" }],
  },
  keys,
);

// Anyone verifies it later, offline, against the pinned key:
const v = await verifyAttestedValidationResult(attested, pinned);
// v.ok === true   (or { ok: false, reason } - why-not provenance, never a bare false)
```

## Offline verifier CLI

```bash
node --experimental-strip-types cli/verify.ts result.json @issuer-pubkey.txt
# PASS - attestation verifies against the pinned issuer key
```

## Portability

Pure Web Crypto Ed25519 - runs on Node 20+, Deno, and modern browsers with no signing dependency.

## Status

Reference implementation for discussion in the Interceptors Working Group (SEP-2624). Canonicalization is RFC 8785 (JCS)-aligned for the value types interceptor results contain (strings, booleans, integers, arrays, objects); float serialization is out of scope until a result type needs it. The final package scope/name is a WG decision.
