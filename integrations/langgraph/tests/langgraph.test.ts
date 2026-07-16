// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * LangGraph-surface tests: the InterceptingToolNode and withInterceptors wrapper
 * over real @langchain/core tools. Still fully hermetic: no network, no LLM, the
 * tool-call sequence is scripted the way a model would emit it.
 */
import { AIMessage, ToolMessage } from "@langchain/core/messages";
import { DynamicStructuredTool } from "@langchain/core/tools";
import type { StructuredToolInterface } from "@langchain/core/tools";
import { z } from "zod";
import { describe, expect, it } from "vitest";
import {
  generateSigningKeyPair,
  InterceptingToolNode,
  InterceptorDenied,
  withInterceptors,
} from "../src/index.ts";
import type { DecisionReceipt } from "../src/index.ts";

const CANARY = "sk_live_4eC7aRm9Kx2bNw5pQj8sYd";

function tools(): {
  readonly list: StructuredToolInterface[];
  readonly sink: string[];
} {
  const sink: string[] = [];
  const readFile = new DynamicStructuredTool({
    name: "read_file",
    description: "filesystem read",
    schema: z.object({ path: z.string() }),
    func: async () => `STRIPE_KEY=${CANARY}`,
  });
  const writeQuery = new DynamicStructuredTool({
    name: "write_query",
    description: "sqlite write",
    schema: z.object({ value: z.string() }),
    func: async ({ value }: { value: string }) => {
      sink.push(value);
      return "ok";
    },
  });
  return {
    list: [readFile, writeQuery] as unknown as StructuredToolInterface[],
    sink,
  };
}

function state(name: string, args: Record<string, unknown>, id: string): {
  readonly messages: readonly AIMessage[];
} {
  return {
    messages: [
      new AIMessage({ content: "", tool_calls: [{ name, args, id, type: "tool_call" }] }),
    ],
  };
}
const READ = state("read_file", { path: "/e" }, "r");
const write = (value: string) => state("write_query", { value }, "w");

async function keys() {
  return generateSigningKeyPair();
}

describe("InterceptingToolNode", () => {
  it("denies the cross-boundary write and emits an error ToolMessage (redact off)", async () => {
    const { list, sink } = tools();
    const node = new InterceptingToolNode(list, {
      issuerKeyPair: await keys(),
      redact: false,
    });
    const cfg = { configurable: { thread_id: "t1" } };

    const read = await node.invoke(READ, cfg);
    const secret = String((read.messages[0] as ToolMessage).content);
    const wr = await node.invoke(write(secret), cfg);
    const msg = wr.messages[0] as ToolMessage;

    expect(msg.status).toBe("error");
    expect(String(msg.content)).toContain("DENIED");
    expect(sink.some((v) => v.includes(CANARY))).toBe(false);
    expect(msg.additional_kwargs["interceptor_receipt"]).toBeTruthy();
  });

  it("defuses the secret and allows the write (default redact)", async () => {
    const { list, sink } = tools();
    const node = new InterceptingToolNode(list, { issuerKeyPair: await keys() });
    const cfg = { configurable: { thread_id: "t2" } };

    const read = await node.invoke(READ, cfg);
    const secret = String((read.messages[0] as ToolMessage).content);
    const wr = await node.invoke(write(secret), cfg);

    expect((wr.messages[0] as ToolMessage).status).toBe("success");
    expect(sink.some((v) => v.includes(CANARY))).toBe(false);
    expect(sink.some((v) => v.includes("mcp:secret-ref"))).toBe(true);
  });

  it("emits a receipt per intercepted call via onReceipt", async () => {
    const { list } = tools();
    const receipts: DecisionReceipt[] = [];
    const node = new InterceptingToolNode(list, {
      issuerKeyPair: await keys(),
      redact: false,
      onReceipt: (r) => receipts.push(r),
    });
    const cfg = { configurable: { thread_id: "t3" } };
    await node.invoke(READ, cfg);
    await node.invoke(write(CANARY), cfg);
    expect(receipts.length).toBe(2);
    expect(receipts.every((r) => r.type === "validation")).toBe(true);
  });
});

describe("withInterceptors", () => {
  it("keeps tool identity and throws InterceptorDenied on a cross-boundary send", async () => {
    const { list, sink } = tools();
    const wrapped = withInterceptors(list, {
      issuerKeyPair: await keys(),
      redact: false,
    });
    expect(wrapped.map((t) => t.name)).toEqual(["read_file", "write_query"]);

    const cfg = { configurable: { thread_id: "w1" } };
    const readFile = wrapped[0]!;
    const writeQuery = wrapped[1]!;
    const secret = String(await readFile.invoke({ path: "/e" }, cfg));
    await expect(writeQuery.invoke({ value: secret }, cfg)).rejects.toBeInstanceOf(
      InterceptorDenied,
    );
    expect(sink.some((v) => v.includes(CANARY))).toBe(false);
  });
});
