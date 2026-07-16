// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Public options for the LangGraph binding, plus the boundary normalizer that
 * turns caller ergonomics (absent optionals) into a fully-present interior
 * (RULE 3: null for absent, never undefined; RULE 7: normalize once).
 */
import { INTERCEPTOR_MODE } from "@ext-modelcontextprotocol/interceptors";
import type { InterceptorMode } from "@ext-modelcontextprotocol/interceptors";
import type { SigningKeyPair } from "@formalcore/mcp-attested-validation";
import type { DecisionReceipt } from "./receipt.ts";

export { INTERCEPTOR_MODE };
export type { InterceptorMode };

/** A sink for every signed decision receipt the engine produces. */
export type ReceiptSink = (receipt: DecisionReceipt) => void;

export interface InterceptOptions {
  /** Ed25519 signer for decision receipts. Attestation is the point, so it is required. */
  readonly issuerKeyPair: SigningKeyPair;
  /** Called once per intercepted tool call, on both allow and deny. Optional. */
  readonly onReceipt?: ReceiptSink;
  /** `enforce` (default) blocks and redacts; `audit` observes without changing anything. */
  readonly mode?: InterceptorMode;
  /** Redact outbound secrets before the guard runs (default true). Set false for a hard block. */
  readonly redact?: boolean;
}

/** The fully-present interior form: every optional resolved to a value or null. */
export interface ResolvedOptions {
  readonly issuerKeyPair: SigningKeyPair;
  readonly onReceipt: ReceiptSink | null;
  readonly mode: InterceptorMode;
  readonly redact: boolean;
}

export function resolveOptions(options: InterceptOptions): ResolvedOptions {
  return {
    issuerKeyPair: options.issuerKeyPair,
    onReceipt: options.onReceipt ?? null,
    mode: options.mode ?? INTERCEPTOR_MODE.Enforce,
    redact: options.redact ?? true,
  };
}
