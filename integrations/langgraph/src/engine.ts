// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The interception engine: pure, framework-free, testable without LangGraph.
 *
 * It BINDS the reference stack (bind, do not reimplement):
 *  - the cross-boundary guard and secretless redactor from the SDK samples,
 *  - the SDK chain executor for trust-boundary-aware ordering,
 *  - the attested-validation signer for offline-verifiable receipts.
 *
 * One engine owns one guard instance, so causal cross-boundary taint persists
 * across tool calls and is isolated per session by `sessionId` (thread id).
 */
import {
  CHAIN_STATUS,
  createCrossBoundaryGuard,
  createRegistry,
  createSecretlessRedactor,
  CROSS_BOUNDARY_GUARD_NAME,
  executeChain,
  INTERCEPTION_EVENT,
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  serverOf,
} from "@ext-modelcontextprotocol/interceptors";
import type {
  ChainParams,
  InterceptorMode,
  InvokeContext,
  RegisteredInterceptor,
} from "@ext-modelcontextprotocol/interceptors";
import type { ResolvedOptions } from "./options.ts";
import {
  findValidation,
  signReceipt,
  toReceiptPayload,
} from "./receipt.ts";
import type { DecisionReceipt } from "./receipt.ts";

/** Allow or deny, discriminated on `kind` (RULE 12). */
export const DECISION = { Allow: "allow", Deny: "deny" } as const;
export type DecisionKind = (typeof DECISION)[keyof typeof DECISION];

export type Decision =
  | {
      readonly kind: (typeof DECISION)["Allow"];
      /** Arguments to actually pass to the tool (redacted when redaction is on). */
      readonly args: unknown;
      readonly receipt: DecisionReceipt;
    }
  | {
      readonly kind: (typeof DECISION)["Deny"];
      readonly reason: string;
      readonly receipt: DecisionReceipt;
    };

const TOOL_EVENT = INTERCEPTION_EVENT.ToolsCall;

function withMode(
  entry: RegisteredInterceptor,
  mode: InterceptorMode,
): RegisteredInterceptor {
  return {
    descriptor: { ...entry.descriptor, mode },
    handler: entry.handler,
  };
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

function toolPayload(name: string, args: unknown): Record<string, unknown> {
  return { name, arguments: args };
}

function argsOf(payload: unknown, fallback: unknown): unknown {
  if (typeof payload === "object" && payload !== null && "arguments" in payload) {
    return (payload as { arguments: unknown }).arguments;
  }
  return fallback;
}

export interface InterceptorEngine {
  /** Request phase: redact + guard a tool call, returning an allow/deny decision. */
  readonly request: (
    toolName: string,
    args: unknown,
    sessionId: string,
  ) => Promise<Decision>;
  /** Response phase: ingest a tool result so its secrets are tracked as taint. */
  readonly response: (
    toolName: string,
    result: unknown,
    sessionId: string,
  ) => Promise<void>;
}

export function createInterceptorEngine(
  options: ResolvedOptions,
): InterceptorEngine {
  // One guard, one redactor, for the life of the engine: taint accretes here.
  const guard = withMode(createCrossBoundaryGuard(), options.mode);
  const entries: RegisteredInterceptor[] = options.redact
    ? [withMode(createSecretlessRedactor(), options.mode), guard]
    : [guard];
  const registry = createRegistry(entries);

  const runChain = (
    phase: (typeof INTERCEPTOR_PHASE)[keyof typeof INTERCEPTOR_PHASE],
    payload: unknown,
    sessionId: string,
  ) => {
    const params: ChainParams = {
      event: TOOL_EVENT,
      phase,
      payload,
      names: null,
      timeoutMs: null,
      context: contextFor(sessionId),
    };
    return executeChain(registry.descriptors, (p) => registry.invoke(p), params);
  };

  const request = async (
    toolName: string,
    args: unknown,
    sessionId: string,
  ): Promise<Decision> => {
    const payload = toolPayload(toolName, args);
    const target = serverOf({
      name: CROSS_BOUNDARY_GUARD_NAME,
      event: TOOL_EVENT,
      phase: INTERCEPTOR_PHASE.Request,
      payload,
      config: null,
      timeoutMs: null,
      context: contextFor(sessionId),
    });
    const result = await runChain(INTERCEPTOR_PHASE.Request, payload, sessionId);
    const guardResult = findValidation(result.results, CROSS_BOUNDARY_GUARD_NAME);

    // The guard's own verdict is the policy decision, independent of mode; in
    // `audit` the chain never aborts, so `status` alone cannot reveal it.
    const wouldDeny = guardResult !== null && !guardResult.valid;
    const denied = options.mode === INTERCEPTOR_MODE.Enforce && wouldDeny;
    const finalArgs = argsOf(result.finalPayload, args);

    const reason =
      result.abortedAt?.reason ??
      guardResult?.messages[0]?.message ??
      "cross-boundary policy denied the call";

    const info = {
      tool: toolName,
      server: target,
      sessionId,
      mode: options.mode,
      redact: options.redact,
      decision: denied ? DECISION.Deny : DECISION.Allow,
      wouldDeny,
    };
    const payloadForReceipt: DecisionReceipt = guardResult
      ? toReceiptPayload(guardResult, info)
      : {
          interceptor: CROSS_BOUNDARY_GUARD_NAME,
          type: "validation",
          phase: "request",
          valid: !denied,
          messages: [],
          suggestions: [],
          info,
        };
    const receipt = await signReceipt(payloadForReceipt, options.issuerKeyPair);
    options.onReceipt?.(receipt);

    return denied
      ? { kind: DECISION.Deny, reason, receipt }
      : { kind: DECISION.Allow, args: finalArgs, receipt };
  };

  const response = async (
    toolName: string,
    toolResult: unknown,
    sessionId: string,
  ): Promise<void> => {
    // The response payload carries the tool's output; the guard ingests any
    // secrets in it as taint attributed to the just-requested server.
    await runChain(
      INTERCEPTOR_PHASE.Response,
      toolPayload(toolName, toolResult),
      sessionId,
    );
  };

  return { request, response };
}
