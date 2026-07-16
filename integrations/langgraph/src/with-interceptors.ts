// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * `withInterceptors(tools, options)`: wrap an array of tools so each one
 * self-enforces. The returned tools keep their name, description, and schema,
 * so they drop into `createReactAgent({ tools })` or any `ToolNode` unchanged.
 *
 * Per call: outbound args are redacted, cross-boundary exfiltration is checked,
 * the underlying tool runs with the redacted args, its output is ingested as
 * causal taint, and a signed receipt is emitted via `onReceipt`. A denied call
 * THROWS, so a standard ToolNode turns it into a `status: "error"` ToolMessage
 * (never a silent success).
 *
 * `InterceptingToolNode` is the primary, most explicit surface; this wrapper is
 * for callers who cannot swap their tools node.
 */
import { DynamicStructuredTool } from "@langchain/core/tools";
import type { StructuredToolInterface } from "@langchain/core/tools";
import type { RunnableConfig } from "@langchain/core/runnables";
import { createInterceptorEngine, DECISION } from "./engine.ts";
import { resolveOptions } from "./options.ts";
import type { InterceptOptions } from "./options.ts";
import { sessionOf } from "./tool-node.ts";

/** Raised when the interceptor denies a tool call; a ToolNode maps it to an error. */
export class InterceptorDenied extends Error {
  readonly toolName: string;

  constructor(toolName: string, reason: string) {
    super(`DENIED by interceptor: ${reason}`);
    this.name = "InterceptorDenied";
    this.toolName = toolName;
  }
}

function outputToContent(output: unknown): string {
  if (typeof output === "string") return output;
  if (output !== null && typeof output === "object" && "content" in output) {
    const content = (output as { content: unknown }).content;
    return typeof content === "string" ? content : JSON.stringify(content);
  }
  return JSON.stringify(output);
}

export function withInterceptors(
  tools: readonly StructuredToolInterface[],
  options: InterceptOptions,
): readonly StructuredToolInterface[] {
  const resolved = resolveOptions(options);
  const engine = createInterceptorEngine(resolved);

  return tools.map((tool) => {
    const wrapped = async (
      args: unknown,
      config?: RunnableConfig,
    ): Promise<unknown> => {
      const sessionId = sessionOf(config);
      // The engine emits the signed receipt via options.onReceipt itself.
      const decision = await engine.request(tool.name, args, sessionId);
      if (decision.kind === DECISION.Deny) {
        throw new InterceptorDenied(tool.name, decision.reason);
      }
      const output = await tool.invoke(decision.args as never, config);
      await engine.response(tool.name, outputToContent(output), sessionId);
      return output;
    };

    return new DynamicStructuredTool({
      name: tool.name,
      description: tool.description,
      // Reuse the wrapped tool's own schema so the model-facing contract is identical.
      schema: tool.schema as never,
      func: wrapped as never,
    }) as unknown as StructuredToolInterface;
  });
}
