/**
 * SEP-2624-aligned validation result plus the attested `signature`.
 *
 * The `ValidationResult` shape mirrors the interceptor SEP exactly so an attested
 * result is a *superset-compatible* validation result: any SEP-2624 verifier that
 * ignores unknown-but-reserved fields still reads it, and any attestation-aware
 * verifier can additionally check the `signature`.
 *
 * Wire compatibility with the spec takes precedence over house style, so fields
 * use optional (`?`) exactly as SEP-2624 declares them; `readonly` is layered on
 * for immutability without changing the serialized shape.
 */

export type Severity = "info" | "warn" | "error";

export interface ValidationMessage {
  readonly path?: string;
  readonly message: string;
  readonly severity: Severity;
}

export interface ValidationSuggestion {
  readonly path: string;
  readonly value: unknown;
}

/**
 * The reserved SEP-2624 `signature` object, implemented for real.
 * `publicKey` is INFORMATIONAL - a verifier MUST pin the trusted issuer key out
 * of band and MUST NOT trust this embedded value (see `verifyAttestedValidationResult`).
 */
export interface AttestedSignature {
  readonly algorithm: "ed25519";
  /** Raw Ed25519 public key of the signer, base64. Informational only. */
  readonly publicKey: string;
  /** Ed25519 signature over the canonicalized result (minus `signature`), base64. */
  readonly value: string;
}

export interface ValidationResult {
  readonly interceptor: string;
  readonly type: "validation";
  readonly phase: "request" | "response";
  readonly durationMs?: number;
  readonly info?: Record<string, unknown>;
  readonly valid: boolean;
  readonly severity?: Severity;
  readonly messages?: readonly ValidationMessage[];
  readonly suggestions?: readonly ValidationSuggestion[];
  /** Reserved SEP-2624 field, populated by `attestValidationResult`. */
  readonly signature?: AttestedSignature;
}

/**
 * Result of an offline verification. A tagged union so a denial names *why* it
 * failed (why-not provenance), never just `false`.
 */
export type AttestationVerification =
  | { readonly ok: true }
  | {
      readonly ok: false;
      readonly reason:
        | "missing-signature"
        | "unsupported-algorithm"
        | "signature-mismatch"
        | "verify-error";
    };
