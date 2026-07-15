// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Opt-in attestation for SDK validators — the wiring between this SDK and
 * `@formalcore/mcp-attested-validation` (the reference implementation of the
 * reserved SEP-2624 `ValidationResult.signature` field).
 *
 * Attestation is a WIRE concern: the signature covers the canonicalized
 * SEP-shape result, so it is attached where the interior result becomes wire
 * JSON, not inside the interior type system. `toWireValidationResult` maps the
 * interior result (null-for-absent) to the SEP optional shape — the same
 * omission semantics as `serializeResult`, but statically typed against the
 * attestation package so a drift between the two shapes fails to compile.
 *
 * This module is consumed as source (like the attestation package itself) and
 * is excluded from the SDK's compiled dist; see tsconfig.json for the
 * resolution setup.
 */
import {
  attestValidationResult,
  verifyAttestedValidationResult,
} from "@formalcore/mcp-attested-validation";
import type {
  AttestationVerification,
  SigningKeyPair,
  ValidationResult as WireValidationResult,
} from "@formalcore/mcp-attested-validation";
import type { InvokeParams, ValidationResult } from "../protocol/types.js";
import { INTERCEPTOR_TYPE } from "../protocol/constants.js";
import type { RegisteredInterceptor } from "../server/define-interceptor.js";

export type {
  AttestationVerification,
  SigningKeyPair,
  WireValidationResult,
};
export {
  exportPrivateKeyBase64,
  exportPublicKeyBase64,
  generateSigningKeyPair,
  importPrivateKeyBase64,
  importPublicKeyBase64,
} from "@formalcore/mcp-attested-validation";

/**
 * Interior → SEP wire shape (optional-not-null), typed against the
 * attestation package. An attestation binds an ISSUER to a decision, so a
 * result that has not been stamped with its interceptor name is not
 * attestable — that is a caller bug, surfaced loudly.
 */
export function toWireValidationResult(
  result: ValidationResult,
): WireValidationResult {
  if (result.interceptor === null) {
    throw new Error(
      "attestation requires an issuer: ValidationResult.interceptor is null",
    );
  }
  return {
    interceptor: result.interceptor,
    type: INTERCEPTOR_TYPE.Validation,
    phase: result.phase,
    valid: result.valid,
    ...(result.severity === null ? {} : { severity: result.severity }),
    ...(result.messages.length === 0
      ? {}
      : {
          messages: result.messages.map((m) => ({
            ...(m.path === null ? {} : { path: m.path }),
            message: m.message,
            severity: m.severity,
          })),
        }),
    ...(result.suggestions.length === 0
      ? {}
      : { suggestions: result.suggestions }),
    ...(result.durationMs === null ? {} : { durationMs: result.durationMs }),
    ...(result.info === null ? {} : { info: { ...result.info } }),
  };
}

/** Sign an interior validation result: returns the SEP wire result with `signature` set. */
export async function attestValidation(
  result: ValidationResult,
  keys: SigningKeyPair,
): Promise<WireValidationResult> {
  return attestValidationResult(toWireValidationResult(result), keys);
}

/**
 * Offline verification against a PINNED issuer key (the embedded key is
 * informational and never trusted). Thin delegate so SDK users have one
 * import surface for both directions.
 */
export async function verifyAttestedValidation(
  result: WireValidationResult,
  trustedPublicKeyBase64: string,
): Promise<AttestationVerification> {
  return verifyAttestedValidationResult(result, trustedPublicKeyBase64);
}

/** A validator invocation whose result leaves signed. */
export type AttestedInvoke = (
  params: InvokeParams,
  signal: AbortSignal | null,
) => Promise<WireValidationResult>;

/**
 * The opt-in: wrap a validator so every decision it returns is an attested
 * wire result. Rejects non-validators at wrap time — mutation results carry
 * payloads, not verdicts, and have no reserved `signature` field to populate.
 */
export function withAttestation(
  entry: RegisteredInterceptor,
  keys: SigningKeyPair,
): AttestedInvoke {
  if (entry.descriptor.type !== INTERCEPTOR_TYPE.Validation) {
    throw new Error(
      `attestation is defined for validators only; '${entry.descriptor.name}' is a ${entry.descriptor.type} interceptor`,
    );
  }
  return async (params, signal) => {
    const result = await entry.handler(params, signal);
    if (result.type !== INTERCEPTOR_TYPE.Validation) {
      throw new Error(
        `validator '${entry.descriptor.name}' returned a ${result.type} result`,
      );
    }
    return attestValidation(result, keys);
  };
}
