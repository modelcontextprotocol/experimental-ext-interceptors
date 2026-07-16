// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * @formalcore/langgraph-interceptors
 *
 * Drop-in for LangChain / LangGraph agents: run every tool call through the
 * reference MCP interceptors (SEP-2624), emit provenance labels, and produce an
 * offline-verifiable attested denial when a read-then-send exfiltration is
 * attempted.
 *
 * Adopter one-liner (wrap your tools):
 *
 * ```ts
 * const shield = await createInterceptorShield({ sessionId });
 * const agent = createReactAgent({ llm, tools: shield.wrap(myTools) });
 * ```
 */
export {
  createInterceptorShield,
  readInterceptorArtifact,
  CrossBoundaryDenied,
  ON_DENY,
} from "./langgraph.ts";
export type {
  Shield,
  ShieldOptions,
  OnDeny,
  ToolInterceptorArtifact,
  AllowedArtifact,
  DenialArtifact,
} from "./langgraph.ts";

export { createPipeline } from "./pipeline.ts";
export type {
  InterceptorPipeline,
  PipelineOptions,
  RequestDecision,
  AllowDecision,
  DenyDecision,
} from "./pipeline.ts";

export {
  provenanceLabel,
  PROVENANCE_KIND,
  DENIAL_KIND,
  ENVELOPE_VERSION,
} from "./provenance.ts";
export type { ProvenanceLabel } from "./provenance.ts";
