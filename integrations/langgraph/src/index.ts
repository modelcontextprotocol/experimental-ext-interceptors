// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * @formalcore/mcp-interceptors-langgraph
 *
 * A one-line LangGraph binding for the MCP interceptors reference security
 * stack (SEP-2624): causal cross-boundary exfiltration blocking, outbound
 * secret redaction, and Ed25519-attested, offline-verifiable decision receipts.
 */
export { InterceptingToolNode, sessionOf } from "./tool-node.ts";
export type { ToolNodeState, ToolNodeUpdate } from "./tool-node.ts";

export { withInterceptors, InterceptorDenied } from "./with-interceptors.ts";

export { createInterceptorEngine, DECISION } from "./engine.ts";
export type { Decision, DecisionKind, InterceptorEngine } from "./engine.ts";

export { INTERCEPTOR_MODE, resolveOptions } from "./options.ts";
export type {
  InterceptOptions,
  InterceptorMode,
  ReceiptSink,
  ResolvedOptions,
} from "./options.ts";

export { signReceipt, verifyReceipt } from "./receipt.ts";
export type { DecisionReceipt } from "./receipt.ts";

// Re-exported so a caller mints an issuer key and verifies receipts without a
// second import (the two ends of "attest, then check it yourself").
export {
  exportPublicKeyBase64,
  generateSigningKeyPair,
} from "@formalcore/mcp-attested-validation";
export type {
  AttestationVerification,
  SigningKeyPair,
} from "@formalcore/mcp-attested-validation";
