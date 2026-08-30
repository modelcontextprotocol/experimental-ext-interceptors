// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * `secretless-redactor` - the reference security MUTATOR (open tier).
 *
 * Replaces every verbatim secret value in an outbound payload with an opaque
 * handle token, so downstream servers receive a stable reference instead of
 * the credential. The handle is deterministic (same secret → same handle
 * within and across payloads) but non-invertible from the token alone.
 *
 * Composes with `cross-boundary-guard`: on the request phase mutations run
 * BEFORE validations (SEP-2624 trust-boundary order), so a payload redacted
 * here no longer carries the verbatim secret when the guard checks it -
 * redaction is the remediation, blocking is the backstop.
 */
import { INTERCEPTION_EVENT, INTERCEPTOR_PHASE } from "../../protocol/constants.js";
import { apply, defineMutator, keep } from "../../server/define-interceptor.js";
import type { RegisteredInterceptor } from "../../server/define-interceptor.js";
import { findSecrets } from "./secret-formats.js";

export const SECRETLESS_REDACTOR_NAME = "formalcore/secretless-redactor";

/**
 * FNV-1a 32-bit over the secret value - deterministic, dependency-free, and
 * enough to disambiguate handles. This is an OPAQUE REFERENCE, not a
 * commitment: the handle only disambiguates secrets and is not relied upon to
 * be unforgeable.
 */
function fnv1a(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}

export function handleFor(formatId: string, value: string): string {
  return `<mcp:secret-ref:${formatId}:${fnv1a(value)}>`;
}

function redactText(text: string): string {
  return findSecrets(text).reduce(
    (acc, hit) => acc.split(hit.value).join(handleFor(hit.formatId, hit.value)),
    text,
  );
}

/** Deep, pure JSON transform: strings are redacted, structure is preserved. */
function redactValue(value: unknown): unknown {
  if (typeof value === "string") return redactText(value);
  if (Array.isArray(value)) return value.map(redactValue);
  if (typeof value === "object" && value !== null) {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>).map(([k, v]) => [
        k,
        redactValue(v),
      ]),
    );
  }
  return value;
}

export function createSecretlessRedactor(): RegisteredInterceptor {
  return defineMutator({
    name: SECRETLESS_REDACTOR_NAME,
    description:
      "Replaces verbatim secret values in outbound payloads with opaque, " +
      "deterministic handle tokens (open tier).",
    version: "0.1.0",
    events: [
      INTERCEPTION_EVENT.ToolsCall,
      INTERCEPTION_EVENT.SamplingCreateMessage,
      INTERCEPTION_EVENT.ElicitationCreate,
    ],
    phases: INTERCEPTOR_PHASE.Request,
    mutate: (params) => {
      const hits = findSecrets(
        params.payload === undefined ? "" : JSON.stringify(params.payload),
      );
      if (hits.length === 0) return keep();
      return apply(redactValue(params.payload), {
        redacted: hits.length,
        formats: [...new Set(hits.map((h) => h.formatId))],
      });
    },
  });
}
