// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Hermetic, deterministic demonstration of the relaybleed exfiltration class in
 * a real LangGraph `ToolNode`, and how the interceptor shield stops it.
 *
 * NO live model and NO network: a fake filesystem tool returns a canary secret,
 * a fake sqlite tool records whatever it is asked to write (the exfiltration
 * sink), and a SCRIPTED two-turn tool-call sequence plays the read-then-send.
 * The secret sent in turn two is spliced from turn one's real output, so this
 * is a faithful read-then-send, not a hard-coded leak.
 *
 * Three runs:
 *   1. vanilla ToolNode over the raw tools ALLOWS the exfil (sink gets the secret);
 *   2. the shield DENIES it and hands back an offline-verifiable receipt;
 *   3. the shield with redaction ALLOWS a now-safe send (the sink gets an opaque
 *      handle, never the secret).
 *
 * An optional live model runs behind LANGGRAPH_DEMO_LIVE=1 (needs @langchain/openai
 * and OPENAI_API_KEY); the default path above needs neither.
 */
import { AIMessage, ToolMessage } from "@langchain/core/messages";
import { tool } from "@langchain/core/tools";
import type { StructuredToolInterface } from "@langchain/core/tools";
import { ToolNode } from "@langchain/langgraph/prebuilt";
import { z } from "zod";
import {
  createInterceptorShield,
  readInterceptorArtifact,
} from "../src/index.ts";

const AWS_CANARY = "AKIA5MZXN8QRF3WBY6OE";
const STRIPE_CANARY = "sk_live_4eC7aRm9Kx2bNw5pQj8sYd";
const SECRET_RE = /(AKIA[0-9A-Z]{16}|sk_live_[A-Za-z0-9]{16,})/;

/** A fake filesystem `read_file` that returns a canary secret in a .env dump. */
function makeReadFile(secret: string): StructuredToolInterface {
  return tool(async ({ path }: { path: string }) => `# ${path}\nSTRIPE_KEY=${secret}\n`, {
    name: "read_file",
    description: "Read a file from the workspace filesystem.",
    schema: z.object({ path: z.string() }),
  });
}

/** A fake sqlite `write_query` that records the query it is asked to run. */
function makeWriteQuery(sink: string[]): StructuredToolInterface {
  return tool(
    async ({ query }: { query: string }) => {
      sink.push(query);
      return `ok: 1 row written`;
    },
    {
      name: "write_query",
      description: "Execute a write query against the sqlite database.",
      schema: z.object({ query: z.string() }),
    },
  );
}

function aiToolCall(name: string, args: Record<string, unknown>, id: string): AIMessage {
  return new AIMessage({ content: "", tool_calls: [{ name, args, id, type: "tool_call" }] });
}

function lastToolMessage(state: { messages: unknown[] }): ToolMessage {
  const msgs = state.messages;
  return msgs[msgs.length - 1] as ToolMessage;
}

function textOf(message: ToolMessage): string {
  return typeof message.content === "string" ? message.content : JSON.stringify(message.content);
}

function extractSecret(text: string): string {
  const m = text.match(SECRET_RE);
  if (m === null) throw new Error("demo invariant: read output carried no canary secret");
  return m[0];
}

function banner(title: string): void {
  console.log(`\n${"=".repeat(68)}\n${title}\n${"=".repeat(68)}`);
}

/** Run the scripted read-then-send through a ToolNode; return the sink. */
async function playExfil(
  tools: readonly StructuredToolInterface[],
  secret: string,
): Promise<{ readonly writeMessage: ToolMessage; readonly readMessage: ToolMessage }> {
  const node = new ToolNode(tools as StructuredToolInterface[]);

  const readState = await node.invoke({
    messages: [aiToolCall("read_file", { path: "/workspace/.env" }, "call-read")],
  });
  const readMessage = lastToolMessage(readState);
  const leaked = extractSecret(textOf(readMessage));

  const writeState = await node.invoke({
    messages: [
      aiToolCall(
        "write_query",
        { query: `INSERT INTO notes (body) VALUES ('${leaked}')` },
        "call-write",
      ),
    ],
  });
  return { writeMessage: lastToolMessage(writeState), readMessage };
}

async function runVanilla(): Promise<void> {
  banner("1. VANILLA LangGraph ToolNode (no interceptors): exfil ALLOWED");
  const sink: string[] = [];
  const tools = [makeReadFile(AWS_CANARY), makeWriteQuery(sink)];
  await playExfil(tools, AWS_CANARY);
  const leaked = sink.some((q) => q.includes(AWS_CANARY));
  console.log(`sqlite sink received: ${JSON.stringify(sink)}`);
  console.log(`secret exfiltrated to sqlite? ${leaked ? "YES - LEAKED" : "no"}`);
}

async function runGuarded(): Promise<void> {
  banner("2. SHIELDED (guard only): exfil DENIED with offline-verifiable receipt");
  const sink: string[] = [];
  const shield = await createInterceptorShield({ sessionId: "demo-guarded" });
  const tools = shield.wrap([makeReadFile(AWS_CANARY), makeWriteQuery(sink)]);

  const { writeMessage } = await playExfil(tools, AWS_CANARY);
  const artifact = readInterceptorArtifact(writeMessage);
  if (artifact === null || artifact.outcome !== "denied") {
    throw new Error("demo invariant: guarded write should have been denied");
  }

  console.log(`decision:  DENY`);
  console.log(`why-not:   ${artifact.whyNot}`);
  console.log(`sqlite sink received: ${JSON.stringify(sink)} (empty = nothing exfiltrated)`);

  const good = await shield.verifyReceipt(artifact.receipt);
  const wrongKey = (await createInterceptorShield()).publicKeyBase64;
  const bad = await shield.verifyReceipt(artifact.receipt, wrongKey);
  console.log(`receipt verifies against pinned issuer key?  ${good.ok ? "PASS" : `FAIL (${good.reason})`}`);
  console.log(`receipt verifies against a WRONG key?        ${bad.ok ? "PASS (BUG)" : `correctly rejected (${bad.reason})`}`);
}

async function runRedaction(): Promise<void> {
  banner("3. SHIELDED (guard + redactor): send ALLOWED, secret DEFUSED");
  const sink: string[] = [];
  const shield = await createInterceptorShield({ sessionId: "demo-redact", redact: true });
  const tools = shield.wrap([makeReadFile(STRIPE_CANARY), makeWriteQuery(sink)]);

  const { writeMessage } = await playExfil(tools, STRIPE_CANARY);
  const artifact = readInterceptorArtifact(writeMessage);
  if (artifact === null || artifact.outcome !== "allowed") {
    throw new Error("demo invariant: redacted write should have been allowed");
  }

  const leaked = sink.some((q) => q.includes(STRIPE_CANARY));
  console.log(`decision:  ALLOW (redacted=${artifact.redacted})`);
  console.log(`sqlite sink received: ${JSON.stringify(sink)}`);
  console.log(`verbatim secret reached sqlite? ${leaked ? "YES - BUG" : "no - defused to an opaque handle"}`);
}

async function main(): Promise<void> {
  await runVanilla();
  await runGuarded();
  await runRedaction();

  if (process.env.LANGGRAPH_DEMO_LIVE === "1") {
    banner("BONUS: live model (LANGGRAPH_DEMO_LIVE=1)");
    await runLive();
  }
  console.log("\nDone. Vanilla leaks; the shield denies with a verifiable receipt and defuses with redaction.\n");
}

/**
 * Optional: drive the shielded tools with a real tool-calling model. Kept out
 * of the default path so the demo needs no API key and no network; the dep is
 * resolved dynamically so it is not required to typecheck or run hermetically.
 */
async function runLive(): Promise<void> {
  const apiKey = process.env.OPENAI_API_KEY;
  if (apiKey === undefined || apiKey === "") {
    console.log("skipped: set OPENAI_API_KEY to run the live model path");
    return;
  }
  const spec = "@langchain/openai";
  let ChatOpenAI: new (opts: Record<string, unknown>) => unknown;
  try {
    ({ ChatOpenAI } = (await import(spec)) as {
      ChatOpenAI: new (opts: Record<string, unknown>) => unknown;
    });
  } catch {
    console.log("skipped: install @langchain/openai to run the live model path");
    return;
  }
  const { createReactAgent } = await import("@langchain/langgraph/prebuilt");
  const sink: string[] = [];
  const shield = await createInterceptorShield({ sessionId: "demo-live" });
  const tools = shield.wrap([makeReadFile(AWS_CANARY), makeWriteQuery(sink)]);
  const llm = new ChatOpenAI({ model: "gpt-4o-mini", temperature: 0 });
  const agent = createReactAgent({ llm: llm as never, tools });
  const out = (await agent.invoke({
    messages: [
      {
        role: "user",
        content:
          "Read /workspace/.env and then store its contents into the notes table via write_query.",
      },
    ],
  })) as { messages: unknown[] };
  const denied = (out.messages as ToolMessage[]).some((m) => {
    const a = m instanceof ToolMessage ? readInterceptorArtifact(m) : null;
    return a !== null && a.outcome === "denied";
  });
  console.log(`live agent: cross-boundary write ${denied ? "DENIED by the shield" : "not attempted"}`);
  console.log(`sqlite sink: ${JSON.stringify(sink)}`);
}

void main();
