// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * `InterceptingToolNode`: a drop-in StateGraph tools node.
 *
 *   new StateGraph(MessagesAnnotation)
 *     .addNode("tools", new InterceptingToolNode(tools, { issuerKeyPair }))
 *
 * It mirrors a LangGraph `ToolNode` (`invoke({ messages })` -> `{ messages }`),
 * but every tool call is routed through the interception engine first: outbound
 * secrets are redacted, cross-boundary exfiltration is blocked, tool output is
 * ingested as causal taint, and each decision yields a signed receipt. A denial
 * surfaces as a ToolMessage with `status: "error"`, never a silent success.
 *
 * Session isolation is by `config.configurable.thread_id`, so concurrent graph
 * threads never share taint.
 */
import {
  AIMessage,
  isAIMessage,
  isBaseMessage,
  ToolMessage,
} from "@langchain/core/messages";
import type { BaseMessage } from "@langchain/core/messages";
import { Runnable } from "@langchain/core/runnables";
import type { RunnableConfig } from "@langchain/core/runnables";
import type { StructuredToolInterface } from "@langchain/core/tools";
import { createInterceptorEngine, DECISION } from "./engine.ts";
import type { InterceptorEngine } from "./engine.ts";
import { resolveOptions } from "./options.ts";
import type { InterceptOptions } from "./options.ts";

/** Any tool exposing the common structured-tool contract (name, schema, invoke). */
type AnyTool = StructuredToolInterface;

const DEFAULT_SESSION = "default";

/** thread_id is the session key; absence falls back to a single default session. */
export function sessionOf(config: RunnableConfig | undefined): string {
  const threadId = config?.configurable?.["thread_id"];
  return typeof threadId === "string" && threadId.length > 0
    ? threadId
    : DEFAULT_SESSION;
}

function outputToContent(output: unknown): string {
  if (typeof output === "string") return output;
  if (output !== null && typeof output === "object" && "content" in output) {
    const content = (output as { content: unknown }).content;
    return typeof content === "string" ? content : JSON.stringify(content);
  }
  return JSON.stringify(output);
}

function messagesOf(input: unknown): readonly BaseMessage[] {
  if (Array.isArray(input) && input.every(isBaseMessage)) return input;
  if (
    typeof input === "object" &&
    input !== null &&
    "messages" in input &&
    Array.isArray((input as { messages: unknown }).messages)
  ) {
    return (input as { messages: BaseMessage[] }).messages;
  }
  throw new Error(
    "InterceptingToolNode expects BaseMessage[] or { messages: BaseMessage[] }.",
  );
}

function lastAiMessage(messages: readonly BaseMessage[]): AIMessage {
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    const message = messages[i];
    if (message !== undefined && isAIMessage(message)) return message;
  }
  throw new Error("InterceptingToolNode found no AIMessage with tool calls.");
}

export interface ToolNodeState {
  readonly messages: readonly BaseMessage[];
}
export interface ToolNodeUpdate {
  readonly messages: readonly ToolMessage[];
}

export class InterceptingToolNode extends Runnable<
  ToolNodeState | readonly BaseMessage[],
  ToolNodeUpdate
> {
  static lc_name(): string {
    return "InterceptingToolNode";
  }

  lc_namespace = ["formalcore", "mcp-interceptors", "langgraph"];

  private readonly toolsByName: ReadonlyMap<string, AnyTool>;

  private readonly engine: InterceptorEngine;

  constructor(tools: readonly AnyTool[], options: InterceptOptions) {
    super();
    this.toolsByName = new Map(tools.map((tool) => [tool.name, tool]));
    this.engine = createInterceptorEngine(resolveOptions(options));
  }

  private async runCall(
    call: { readonly name: string; readonly args: unknown; readonly id?: string },
    config: RunnableConfig,
  ): Promise<ToolMessage> {
    const sessionId = sessionOf(config);
    const toolCallId = call.id ?? "";
    const decision = await this.engine.request(call.name, call.args, sessionId);

    if (decision.kind === DECISION.Deny) {
      return new ToolMessage({
        status: "error",
        name: call.name,
        content: `DENIED by interceptor: ${decision.reason}`,
        tool_call_id: toolCallId,
        additional_kwargs: { interceptor_receipt: decision.receipt },
      });
    }

    const tool = this.toolsByName.get(call.name);
    if (tool === undefined) {
      return new ToolMessage({
        status: "error",
        name: call.name,
        content: `Tool "${call.name}" not found.`,
        tool_call_id: toolCallId,
        additional_kwargs: { interceptor_receipt: decision.receipt },
      });
    }

    const output = await tool.invoke(decision.args as never, config);
    const content = outputToContent(output);
    await this.engine.response(call.name, content, sessionId);

    return new ToolMessage({
      status: "success",
      name: call.name,
      content,
      tool_call_id: toolCallId,
      additional_kwargs: { interceptor_receipt: decision.receipt },
    });
  }

  private async run(
    input: ToolNodeState | readonly BaseMessage[],
    config: RunnableConfig,
  ): Promise<ToolNodeUpdate> {
    const aiMessage = lastAiMessage(messagesOf(input));
    const calls = aiMessage.tool_calls ?? [];
    const messages = await Promise.all(
      calls.map((call) => this.runCall(call, config)),
    );
    return { messages };
  }

  async invoke(
    input: ToolNodeState | readonly BaseMessage[],
    options?: Partial<RunnableConfig>,
  ): Promise<ToolNodeUpdate> {
    return this._callWithConfig(
      (innerInput, config) => this.run(innerInput, config ?? {}),
      input,
      { ...options, runType: "tool" },
    );
  }
}
