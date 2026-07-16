// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The provenance envelope a wrapped tool emits alongside every tool result.
 *
 * This is the "labels" half of the adoption story: a LangChain / LangGraph tool
 * wrapped by this package carries a provenance label describing the trust
 * boundary (server) that produced the value and whether it carried verbatim
 * secrets. The label rides on the ToolMessage artifact, so downstream nodes,
 * loggers, and auditors read provenance instead of re-deriving it from bytes.
 *
 * The shape is intentionally small, versioned, and serializable (RULE 24): it
 * is data, not behavior, and it is the same envelope regardless of framework.
 */

/** Stable identity of the label format, so consumers can key on it. */
export const PROVENANCE_KIND = "mcp-provenance-label" as const;
export const DENIAL_KIND = "mcp-interceptor-denial" as const;
export const ENVELOPE_VERSION = "0.1" as const;

/**
 * A provenance label attached to an ALLOWED tool result. `server` is the trust
 * boundary the value crossed (derived by the reference `serverOf`); `tainted`
 * is true iff the output carried at least one verbatim secret of a known
 * public format, with those format ids listed (sorted, unique) in
 * `secretFormats`. `emittedAt` is null in deterministic mode (injected clock).
 */
export interface ProvenanceLabel {
  readonly kind: typeof PROVENANCE_KIND;
  readonly version: typeof ENVELOPE_VERSION;
  readonly event: "tools/call";
  readonly tool: string;
  readonly server: string;
  readonly sessionId: string;
  readonly secretFormats: readonly string[];
  readonly tainted: boolean;
  readonly emittedAt: string | null;
}

/** Build a provenance label. Format ids are sorted and de-duplicated. */
export function provenanceLabel(input: {
  readonly tool: string;
  readonly server: string;
  readonly sessionId: string;
  readonly secretFormats: readonly string[];
  readonly emittedAt: string | null;
}): ProvenanceLabel {
  const formats = [...new Set(input.secretFormats)].sort();
  return {
    kind: PROVENANCE_KIND,
    version: ENVELOPE_VERSION,
    event: "tools/call",
    tool: input.tool,
    server: input.server,
    sessionId: input.sessionId,
    secretFormats: formats,
    tainted: formats.length > 0,
    emittedAt: input.emittedAt,
  };
}
