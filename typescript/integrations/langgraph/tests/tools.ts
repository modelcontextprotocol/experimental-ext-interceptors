// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/** Shared fake tools and canaries for the integration tests (not a suite). */
import { tool } from "@langchain/core/tools";
import type { StructuredToolInterface } from "@langchain/core/tools";
import type { ToolMessage } from "@langchain/core/messages";
import { z } from "zod";

/** Canary credentials, format-identical to the conformance behavior fixtures. */
export const AWS_CANARY = "AKIA5MZXN8QRF3WBY6OE";
export const STRIPE_CANARY = "sk_live_4eC7aRm9Kx2bNw5pQj8sYd";

/** Fake filesystem `read_file` returning a canary secret. */
export function makeReadFile(secret: string): StructuredToolInterface {
  return tool(async ({ path }: { path: string }) => `# ${path}\nKEY=${secret}\n`, {
    name: "read_file",
    description: "Read a file from the workspace filesystem.",
    schema: z.object({ path: z.string() }),
  });
}

/** Fake sqlite `write_query` recording each query into `sink` (the exfil sink). */
export function makeWriteQuery(sink: string[]): StructuredToolInterface {
  return tool(
    async ({ query }: { query: string }) => {
      sink.push(query);
      return "ok: 1 row written";
    },
    {
      name: "write_query",
      description: "Execute a write query against the sqlite database.",
      schema: z.object({ query: z.string() }),
    },
  );
}

/** Fake filesystem `write_file` (same-origin writeback target). */
export function makeWriteFile(sink: string[]): StructuredToolInterface {
  return tool(
    async ({ contents }: { contents: string }) => {
      sink.push(contents);
      return "ok: written";
    },
    {
      name: "write_file",
      description: "Write a file to the workspace filesystem.",
      schema: z.object({ contents: z.string() }),
    },
  );
}

/** Invoke a wrapped tool as a LangGraph tool-call and return the ToolMessage. */
export async function callTool(
  toolToCall: StructuredToolInterface,
  args: Record<string, unknown>,
  id: string,
): Promise<ToolMessage> {
  const message = await toolToCall.invoke({
    name: toolToCall.name,
    args,
    id,
    type: "tool_call",
  });
  return message as ToolMessage;
}
