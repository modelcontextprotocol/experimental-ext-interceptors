// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The signed decision receipt.
 *
 * We do not invent a proof format: a receipt IS the reference security guard's
 * own SEP-2624 ValidationResult, signed with the attested-validation package's
 * Ed25519 signer (bind, do not reimplement). Any party verifies it offline
 * against the pinned issuer public key, with no network and no shared state.
 *
 * The map from the SDK's interior ValidationResult (null for absent) to the
 * attestable shape (optional for absent) is a boundary (RULE 7): it runs once,
 * here, and deliberately DROPS `durationMs` so a receipt is reproducible run to
 * run (the wall-clock duration is not part of the decision being attested).
 */
import {
  attestValidationResult,
  verifyAttestedValidationResult,
} from "@formalcore/mcp-attested-validation";
import type {
  AttestationVerification,
  SigningKeyPair,
  ValidationResult as AttestableValidationResult,
  ValidationMessage as AttestableMessage,
} from "@formalcore/mcp-attested-validation";
import type {
  InterceptorResult,
  ValidationResult as SdkValidationResult,
} from "@ext-modelcontextprotocol/interceptors";
import { INTERCEPTOR_TYPE } from "@ext-modelcontextprotocol/interceptors";

/** A decision receipt is a signed, SEP-2624-shaped attested validation result. */
export type DecisionReceipt = AttestableValidationResult;

function isValidation(result: InterceptorResult): result is SdkValidationResult {
  return result.type === INTERCEPTOR_TYPE.Validation;
}

/** Find a specific validator's result in a chain's result list, or null. */
export function findValidation(
  results: readonly InterceptorResult[],
  interceptorName: string,
): SdkValidationResult | null {
  for (const result of results) {
    if (isValidation(result) && result.interceptor === interceptorName) {
      return result;
    }
  }
  return null;
}

function toAttestableMessage(m: {
  readonly path: string | null;
  readonly message: string;
  readonly severity: AttestableMessage["severity"];
}): AttestableMessage {
  return {
    message: m.message,
    severity: m.severity,
    ...(m.path === null ? {} : { path: m.path }),
  };
}

/**
 * Map the guard's interior ValidationResult to the attestable shape, folding in
 * decision provenance under `info`. `durationMs` is intentionally omitted so the
 * signed bytes are deterministic.
 */
export function toReceiptPayload(
  result: SdkValidationResult,
  info: Readonly<Record<string, unknown>>,
): DecisionReceipt {
  return {
    interceptor: result.interceptor ?? "unknown",
    type: "validation",
    phase: result.phase,
    valid: result.valid,
    ...(result.severity === null ? {} : { severity: result.severity }),
    messages: result.messages.map(toAttestableMessage),
    suggestions: result.suggestions.map((s) => ({ path: s.path, value: s.value })),
    info: { ...(result.info ?? {}), ...info },
  };
}

/** Sign a receipt payload with the issuer key (Ed25519). */
export function signReceipt(
  payload: DecisionReceipt,
  keys: SigningKeyPair,
): Promise<DecisionReceipt> {
  return attestValidationResult(payload, keys);
}

/** Verify a receipt OFFLINE against a pinned trusted issuer public key (base64, raw). */
export function verifyReceipt(
  receipt: DecisionReceipt,
  trustedPublicKeyBase64: string,
): Promise<AttestationVerification> {
  return verifyAttestedValidationResult(receipt, trustedPublicKeyBase64);
}
