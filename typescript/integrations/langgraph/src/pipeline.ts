// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The framework-agnostic secure-tool pipeline: the reusable core that the
 * LangChain / LangGraph wrapper is a thin adapter over.
 *
 * It reuses the reference SDK end to end and adds nothing to the security
 * logic:
 *   - `createCrossBoundaryGuard` (validator) and `createSecretlessRedactor`
 *     (mutator) are the reference interceptors, unmodified;
 *   - `executeChain` runs them in SEP-2624 trust-boundary order (request:
 *     mutations then validations; response: validations then mutations), which
 *     is exactly what makes redaction defuse a would-be denial;
 *   - `attestValidationResult` signs a denial so it verifies offline against a
 *     pinned key.
 *
 * A tool call maps onto two chain executions that share ONE guard instance, so
 * causal taint threads across calls (a secret read from server A in a response
 * taints; a later request that carries it to server B is denied):
 *   1. `guardRequest`  - request phase over the outbound `{name, arguments}`.
 *   2. tool executes with the (possibly redacted) effective arguments.
 *   3. `ingestResponse` - response phase over the tool output; emits the label.
 *
 * Sequential per session by construction (request then execute then response),
 * matching the open-tier guard's documented boundary: concurrent in-flight
 * operations in one session would need host-supplied attribution.
 */
import {
  CHAIN_STATUS,
  CROSS_BOUNDARY_GUARD_NAME,
  createCrossBoundaryGuard,
  createRegistry,
  createSecretlessRedactor,
  executeChain,
  findSecrets,
  INTERCEPTION_EVENT,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  serverOf,
  VALIDATION_SEVERITY,
} from "@formalcore/mcp-interceptors-sdk";
import type {
  ChainParams,
  ChainResult,
  InterceptorInvoker,
  InterceptorPhase,
  InterceptorResult,
  InvokeContext,
  InvokeParams,
  RegisteredInterceptor,
  ValidationResult as InteriorValidationResult,
} from "@formalcore/mcp-interceptors-sdk";
import {
  attestValidationResult,
  exportPublicKeyBase64,
  generateSigningKeyPair,
  verifyAttestedValidationResult,
} from "@formalcore/mcp-attested-validation";
import type {
  AttestationVerification,
  SigningKeyPair,
  ValidationResult as WireValidationResult,
} from "@formalcore/mcp-attested-validation";
import { provenanceLabel } from "./provenance.ts";
import type { ProvenanceLabel } from "./provenance.ts";

/** An outbound tool call the guard allowed; carries the effective arguments. */
export interface AllowDecision {
  readonly decision: "allow";
  readonly server: string;
  /** The arguments to execute with: redacted iff `redacted` is true. */
  readonly effectiveArgs: Record<string, unknown>;
  readonly redacted: boolean;
}

/** An outbound tool call the guard denied; carries the offline-verifiable receipt. */
export interface DenyDecision {
  readonly decision: "deny";
  readonly server: string;
  readonly whyNot: string;
  /** Attested SEP-2624 ValidationResult; verify with `verifyReceipt`. */
  readonly receipt: WireValidationResult;
}

export type RequestDecision = AllowDecision | DenyDecision;

export interface InterceptorPipeline {
  /** Pinned issuer key any party uses to verify a denial receipt offline. */
  readonly publicKeyBase64: string;
  readonly guardRequest: (
    tool: string,
    args: Record<string, unknown>,
    sessionId: string,
  ) => Promise<RequestDecision>;
  readonly ingestResponse: (
    tool: string,
    outputText: string,
    sessionId: string,
  ) => Promise<ProvenanceLabel>;
  readonly verifyReceipt: (
    receipt: WireValidationResult,
    pinnedKeyBase64?: string,
  ) => Promise<AttestationVerification>;
  /** Best-effort gauge of tainted secrets in the most recent session ingest. */
  readonly taintSize: () => number;
}

export interface PipelineOptions {
  /** Include the reference redactor so a would-be denial is defused, not blocked. */
  readonly redact?: boolean;
  /** Bring your own signing key (e.g. from KMS); default generates an ephemeral pair. */
  readonly keys?: SigningKeyPair;
  /** Clock injection for deterministic labels; default emits `null`. */
  readonly now?: () => string | null;
}

// ── boundary helpers ─────────────────────────────────────────────────────────

function invokeParamsFor(tool: string): InvokeParams {
  return {
    name: tool,
    event: INTERCEPTION_EVENT.ToolsCall,
    phase: INTERCEPTOR_PHASE.Request,
    payload: { name: tool },
    config: null,
    timeoutMs: null,
    context: null,
  };
}

function serverForTool(tool: string): string {
  return serverOf(invokeParamsFor(tool));
}

function contextFor(sessionId: string): InvokeContext {
  return {
    principal: null,
    traceId: null,
    spanId: null,
    timestamp: null,
    sessionId,
  };
}

function extractArgs(
  payload: unknown,
  fallback: Record<string, unknown>,
): Record<string, unknown> {
  if (typeof payload === "object" && payload !== null && "arguments" in payload) {
    const args = (payload as { arguments?: unknown }).arguments;
    if (typeof args === "object" && args !== null && !Array.isArray(args)) {
      return args as Record<string, unknown>;
    }
  }
  return fallback;
}

function isInteriorValidation(
  result: InterceptorResult,
): result is InteriorValidationResult {
  return result.type === INTERCEPTOR_TYPE.Validation;
}

function isBlockingValidation(
  result: InterceptorResult,
): result is InteriorValidationResult {
  return (
    isInteriorValidation(result) &&
    !result.valid &&
    result.severity === VALIDATION_SEVERITY.Error
  );
}

/**
 * Interior -> SEP wire shape (optional-not-null), so the signature binds the
 * exact serialized decision. Mirrors the SDK's `toWireValidationResult`; kept
 * local so the integration depends only on the two public packages.
 */
function toWireValidationResult(
  result: InteriorValidationResult,
): WireValidationResult {
  return {
    interceptor: result.interceptor ?? CROSS_BOUNDARY_GUARD_NAME,
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
    ...(result.suggestions.length === 0 ? {} : { suggestions: result.suggestions }),
    ...(result.durationMs === null ? {} : { durationMs: result.durationMs }),
    ...(result.info === null ? {} : { info: { ...result.info } }),
  };
}

function taintOf(result: InterceptorResult | undefined): number | null {
  if (result === undefined || !isInteriorValidation(result)) return null;
  const tainted = result.info?.tainted;
  return typeof tainted === "number" ? tainted : null;
}

// ── the pipeline ─────────────────────────────────────────────────────────────

export async function createPipeline(
  opts: PipelineOptions = {},
): Promise<InterceptorPipeline> {
  const keys = opts.keys ?? (await generateSigningKeyPair());
  const publicKeyBase64 = await exportPublicKeyBase64(keys.publicKey);
  const clock = opts.now ?? ((): string | null => null);

  // ONE guard instance: its per-session taint is the thread that connects a
  // read on server A to a later send to server B. The redactor is request-only.
  const guard = createCrossBoundaryGuard();
  const entries: readonly RegisteredInterceptor[] =
    opts.redact === true ? [guard, createSecretlessRedactor()] : [guard];
  const registry = createRegistry(entries);
  const invoke: InterceptorInvoker = (params) => registry.invoke(params);

  let lastTaint = 0;

  const runChain = (
    phase: InterceptorPhase,
    payload: unknown,
    sessionId: string,
  ): Promise<ChainResult> => {
    const params: ChainParams = {
      event: INTERCEPTION_EVENT.ToolsCall,
      phase,
      payload,
      names: null,
      timeoutMs: null,
      context: contextFor(sessionId),
    };
    return executeChain(registry.descriptors, invoke, params);
  };

  const guardRequest = async (
    tool: string,
    args: Record<string, unknown>,
    sessionId: string,
  ): Promise<RequestDecision> => {
    const server = serverForTool(tool);
    const result = await runChain(
      INTERCEPTOR_PHASE.Request,
      { name: tool, arguments: args },
      sessionId,
    );

    if (result.status === CHAIN_STATUS.Success) {
      const redacted = result.results.some(
        (r) => r.type === INTERCEPTOR_TYPE.Mutation && r.modified,
      );
      return {
        decision: "allow",
        server,
        effectiveArgs: extractArgs(result.finalPayload, args),
        redacted,
      };
    }

    const blocking = result.results.find(isBlockingValidation);
    if (blocking === undefined) {
      // Deny with no validation verdict would mean a mutator failed, which the
      // reference redactor never does; surface the invariant loudly.
      throw new Error(
        `interceptor chain denied '${tool}' without a validation verdict ` +
          `(status ${result.status}); cannot attest a receipt`,
      );
    }
    const whyNot = blocking.messages[0]?.message ?? "cross-boundary policy denial";
    const receipt = await attestValidationResult(
      toWireValidationResult(blocking),
      keys,
    );
    return { decision: "deny", server, whyNot, receipt };
  };

  const ingestResponse = async (
    tool: string,
    outputText: string,
    sessionId: string,
  ): Promise<ProvenanceLabel> => {
    const result = await runChain(
      INTERCEPTOR_PHASE.Response,
      { content: [{ type: "text", text: outputText }] },
      sessionId,
    );
    const gauge = taintOf(result.results.find(isInteriorValidation));
    if (gauge !== null) lastTaint = gauge;

    const secretFormats = findSecrets(outputText).map((hit) => hit.formatId);
    return provenanceLabel({
      tool,
      server: serverForTool(tool),
      sessionId,
      secretFormats,
      emittedAt: clock(),
    });
  };

  const verifyReceipt = (
    receipt: WireValidationResult,
    pinnedKeyBase64?: string,
  ): Promise<AttestationVerification> =>
    verifyAttestedValidationResult(receipt, pinnedKeyBase64 ?? publicKeyBase64);

  return {
    publicKeyBase64,
    guardRequest,
    ingestResponse,
    verifyReceipt,
    taintSize: () => lastTaint,
  };
}
