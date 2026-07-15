// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Deriving the "server" (trust boundary) an event belongs to — the OPEN tier's
 * heuristic. Real per-request server identity is a gateway/host concern the
 * closed tier consumes directly; here we infer it from the tool name (via a
 * small MCPHunt-server catalog, RULE 8) or a resource URI authority, so the
 * donated guard is demonstrable without extra wiring.
 */
import type { InvokeParams } from "../../protocol/types.js";

/** MCPHunt's server roster: tool name → owning server (RULE 8 data). */
export const TOOL_SERVER: Readonly<Record<string, string>> = {
  read_file: "filesystem",
  write_file: "filesystem",
  move_file: "filesystem",
  list_directory: "filesystem",
  search_files: "filesystem",
  read_query: "sqlite",
  write_query: "sqlite",
  list_tables: "sqlite",
  describe_table: "sqlite",
  git_log: "git",
  git_show: "git",
  git_diff_unstaged: "git",
  read_graph: "memory",
  create_entities: "memory",
  add_observations: "memory",
  fetch: "fetch",
  browser_type: "browser",
  browser_fill_form: "browser",
  browser_snapshot: "browser",
  execute_command: "shell",
};

const UNKNOWN_SERVER = "unknown";

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

function asString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

/** The tool name inside a tools/call payload, whether wrapped in `params` or flat. */
function toolName(payload: Record<string, unknown>): string | null {
  const params = asRecord(payload.params);
  return asString(params.name) ?? asString(payload.name);
}

/** The resource URI inside a resources/* payload. */
function resourceUri(payload: Record<string, unknown>): string | null {
  const params = asRecord(payload.params);
  return asString(params.uri) ?? asString(payload.uri);
}

/**
 * The server an event crosses. Tool → server via the catalog; resource →
 * URI authority (or scheme); otherwise the event's namespace, then `unknown`.
 */
export function serverOf(params: InvokeParams): string {
  const payload = asRecord(params.payload);

  const tool = toolName(payload);
  if (tool !== null) {
    return TOOL_SERVER[tool] ?? tool;
  }

  const uri = resourceUri(payload);
  if (uri !== null) {
    const authority = uri.match(/^[a-z][a-z0-9+.-]*:\/\/([^/]+)/i);
    if (authority !== null) return authority[1] ?? UNKNOWN_SERVER;
    const scheme = uri.match(/^([a-z][a-z0-9+.-]*):/i);
    if (scheme !== null) return scheme[1] ?? UNKNOWN_SERVER;
  }

  const namespace = params.event.split("/")[0];
  return namespace !== undefined && namespace.length > 0
    ? namespace
    : UNKNOWN_SERVER;
}
