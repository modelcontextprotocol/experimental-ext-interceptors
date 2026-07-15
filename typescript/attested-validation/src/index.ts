/**
 * @formalcore/mcp-attested-validation - a reference implementation of the
 * reserved SEP-2624 `ValidationResult.signature` field: Ed25519-signed,
 * offline-verifiable attested interceptor decisions.
 */
export type {
  AttestationVerification,
  AttestedSignature,
  Severity,
  ValidationMessage,
  ValidationResult,
  ValidationSuggestion,
} from "./types.ts";

export { canonicalize } from "./canonicalize.ts";

export {
  attestValidationResult,
  exportPrivateKeyBase64,
  exportPublicKeyBase64,
  generateSigningKeyPair,
  importPrivateKeyBase64,
  importPublicKeyBase64,
  type SigningKeyPair,
  verifyAttestedValidationResult,
} from "./attest.ts";
