// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The LangChain / LangGraph adapter: wrap a set of `StructuredTool`s so every
 * invocation runs through the reference interceptor pipeline. Wrapped tools are
 * ordinary `StructuredToolInterface`s, so they drop straight into a LangGraph
 * `ToolNode`, `createReactAgent`, or any LangChain agent - no other change.
 *
 * On an allowed call the tool executes with the (possibly redacted) effective
 * arguments and the result carries a provenance label on `ToolMessage.artifact`.
 * On a denied cross-boundary flow the call short-circuits: either it returns a
 * denial `ToolMessage` whose artifact carries the offline-verifiable attested
 * receipt (default), or it throws `CrossBoundaryDenied` carrying the same.
 */
import { DynamicStructuredTool } from "@langchain/core/tools";
import type { StructuredToolInterface } from "@langchain/core/tools";
import type { ToolMessage } from "@langchain/core/messages";
import type { RunnableConfig } from "@langchain/core/runnables";
import { createPipeline } from "./pipeline.ts";
import type { InterceptorPipeline, PipelineOptions } from "./pipeline.ts";
import { DENIAL_KIND, ENVELOPE_VERSION } from "./provenance.ts";
import type { ProvenanceLabel } from "./provenance.ts";
import type {
  AttestationVerification,
  ValidationResult as WireValidationResult,
} from "@formalcore/mcp-attested-validation";

/** How a denied cross-boundary flow surfaces to the graph. */
export const ON_DENY = {
  /** Return a denial ToolMessage whose artifact carries the receipt (default). */
  Message: "message",
  /** Throw `CrossBoundaryDenied` carrying the receipt. */
  Throw: "throw",
} as const;
export type OnDeny = (typeof ON_DENY)[keyof typeof ON_DENY];

/** The denial envelope: same receipt whether surfaced as a message or a throw. */
export interface DenialArtifact {
  readonly kind: typeof DENIAL_KIND;
  readonly version: typeof ENVELOPE_VERSION;
  readonly outcome: "denied";
  readonly server: string;
  readonly whyNot: string;
  readonly receipt: WireValidationResult;
}

/** The label envelope attached to an allowed tool result. */
export interface AllowedArtifact {
  readonly outcome: "allowed";
  readonly provenance: ProvenanceLabel;
  readonly redacted: boolean;
}

/** Tagged union carried on `ToolMessage.artifact` by every wrapped tool. */
export type ToolInterceptorArtifact = AllowedArtifact | DenialArtifact;

/** Thrown by a wrapped tool under `onDeny: "throw"`; carries the receipt. */
export class CrossBoundaryDenied extends Error {
  readonly artifact: DenialArtifact;
  constructor(artifact: DenialArtifact) {
    super(`cross-boundary flow denied: ${artifact.whyNot}`);
    this.name = "CrossBoundaryDenied";
    this.artifact = artifact;
  }
}

function contentToText(content: unknown): string {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) {
    return content
      .map((part) =>
        typeof part === "string"
          ? part
          : typeof (part as { text?: unknown }).text === "string"
            ? (part as { text: string }).text
            : JSON.stringify(part),
      )
      .join("");
  }
  return JSON.stringify(content);
}

function asArgs(input: unknown): Record<string, unknown> {
  return typeof input === "object" && input !== null && !Array.isArray(input)
    ? (input as Record<string, unknown>)
    : {};
}

/**
 * Wrap one tool. Reuses the original tool's name, description, and schema, so
 * the model calls it identically; only the execution path changes.
 */
function wrapTool(
  tool: StructuredToolInterface,
  pipeline: InterceptorPipeline,
  sessionId: string,
  onDeny: OnDeny,
): StructuredToolInterface {
  const wrapped = new DynamicStructuredTool({
    name: tool.name,
    description: tool.description,
    // The schema passes through unchanged; the wrapper never reshapes the tool
    // surface, so this is a boundary hand-off to the original contract.
    schema: tool.schema,
    responseFormat: "content_and_artifact",
    func: async (
      input: unknown,
      _runManager: unknown,
      config?: RunnableConfig,
    ): Promise<[string, ToolInterceptorArtifact]> => {
      const decision = await pipeline.guardRequest(
        tool.name,
        asArgs(input),
        sessionId,
      );

      if (decision.decision === "deny") {
        const artifact: DenialArtifact = {
          kind: DENIAL_KIND,
          version: ENVELOPE_VERSION,
          outcome: "denied",
          server: decision.server,
          whyNot: decision.whyNot,
          receipt: decision.receipt,
        };
        if (onDeny === ON_DENY.Throw) throw new CrossBoundaryDenied(artifact);
        return [
          `DENIED by cross-boundary guard: ${decision.whyNot}`,
          artifact,
        ];
      }

      const raw = await tool.invoke(decision.effectiveArgs, config);
      const outputText = contentToText(raw);
      const provenance = await pipeline.ingestResponse(
        tool.name,
        outputText,
        sessionId,
      );
      return [outputText, { outcome: "allowed", provenance, redacted: decision.redacted }];
    },
  });
  return wrapped as unknown as StructuredToolInterface;
}

export interface ShieldOptions extends PipelineOptions {
  /** Session whose taint is threaded across calls; default `"default"`. */
  readonly sessionId?: string;
  /** How denials surface; default `"message"`. */
  readonly onDeny?: OnDeny;
}

/**
 * A configured interceptor shield over one session. `wrap` returns tools ready
 * for a LangGraph `ToolNode` / `createReactAgent`; `verifyReceipt` checks a
 * denial receipt offline against the pinned issuer key.
 */
export interface Shield {
  readonly sessionId: string;
  readonly publicKeyBase64: string;
  readonly pipeline: InterceptorPipeline;
  readonly wrap: (
    tools: readonly StructuredToolInterface[],
  ) => StructuredToolInterface[];
  readonly verifyReceipt: (
    receipt: WireValidationResult,
    pinnedKeyBase64?: string,
  ) => Promise<AttestationVerification>;
  readonly taintSize: () => number;
}

/**
 * Create an interceptor shield. This is the adopter entry point:
 *
 * ```ts
 * const shield = await createInterceptorShield({ sessionId: "user-42" });
 * const agent = createReactAgent({ llm, tools: shield.wrap(myTools) });
 * ```
 */
export async function createInterceptorShield(
  opts: ShieldOptions = {},
): Promise<Shield> {
  const sessionId = opts.sessionId ?? "default";
  const onDeny = opts.onDeny ?? ON_DENY.Message;
  const pipeline = await createPipeline(opts);
  return {
    sessionId,
    publicKeyBase64: pipeline.publicKeyBase64,
    pipeline,
    wrap: (tools) => tools.map((t) => wrapTool(t, pipeline, sessionId, onDeny)),
    verifyReceipt: pipeline.verifyReceipt,
    taintSize: pipeline.taintSize,
  };
}

/** Read the interceptor artifact off a ToolMessage, or null if absent. */
export function readInterceptorArtifact(
  message: ToolMessage,
): ToolInterceptorArtifact | null {
  const artifact = message.artifact as ToolInterceptorArtifact | undefined;
  if (artifact === undefined || artifact === null) return null;
  return artifact;
}
