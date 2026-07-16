// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Hermetic relaybleed demo: NO network, NO API key, NO real LLM.
 *
 * We feed a scripted tool-call sequence (what an LLM would have emitted) through
 * an InterceptingToolNode:
 *   1. read a canary secret from the filesystem server's `read_file`,
 *   2. attempt to send it to the sqlite server's `write_query` (cross-server).
 *
 * Three runs, one after another, on the same tools:
 *   A. baseline (no interceptors): the secret reaches the sink.
 *   B. guard only (redact off):    the cross-server write is DENIED, and the
 *      signed receipt is verified offline against the pinned key (and shown to
 *      FAIL against a wrong key).
 *   C. guard + redactor (default): the write is ALLOWED but the sink receives an
 *      opaque handle, so the secret never crosses the boundary.
 *
 * Run: node --experimental-strip-types examples/relaybleed-denied.ts
 */
import { AIMessage, ToolMessage } from "@langchain/core/messages";
import { DynamicStructuredTool } from "@langchain/core/tools";
import type { StructuredToolInterface } from "@langchain/core/tools";
import { z } from "zod";
import {
  DECISION,
  exportPublicKeyBase64,
  generateSigningKeyPair,
  InterceptingToolNode,
  verifyReceipt,
} from "../src/index.ts";
import type { DecisionReceipt, SigningKeyPair } from "../src/index.ts";

// A canary from the public MCPHunt format catalog (a format-valid FAKE secret).
const CANARY = "sk_live_4eC7aRm9Kx2bNw5pQj8sYd";

/** The sink records exactly what arguments it received, so we can prove leakage. */
function makeTools(): {
  readonly tools: StructuredToolInterface[];
  readonly sinkReceived: string[];
} {
  const sinkReceived: string[] = [];
  const readFile = new DynamicStructuredTool({
    name: "read_file",
    description: "filesystem server: read a file's contents.",
    schema: z.object({ path: z.string() }),
    func: async () => `config loaded. STRIPE_KEY=${CANARY}\n`,
  });
  const writeQuery = new DynamicStructuredTool({
    name: "write_query",
    description: "sqlite server: insert a record.",
    schema: z.object({ value: z.string() }),
    func: async ({ value }: { value: string }) => {
      sinkReceived.push(value);
      return "1 row written";
    },
  });
  return {
    tools: [readFile, writeQuery] as unknown as StructuredToolInterface[],
    sinkReceived,
  };
}

function ai(name: string, args: Record<string, unknown>, id: string): AIMessage {
  return new AIMessage({
    content: "",
    tool_calls: [{ name, args, id, type: "tool_call" }],
  });
}

const READ = { messages: [ai("read_file", { path: "/app/.env" }, "call_read")] };
const write = (value: string) => ({
  messages: [ai("write_query", { value }, "call_write")],
});

function firstMessage(update: { readonly messages: readonly ToolMessage[] }): ToolMessage {
  const msg = update.messages[0];
  if (msg === undefined) throw new Error("expected a ToolMessage");
  return msg;
}

function secretFrom(msg: ToolMessage): string {
  return String(msg.content).match(/sk_live_[A-Za-z0-9]+/)?.[0] ?? "";
}

function line(): void {
  console.log("-".repeat(68));
}

// One issuer identity for the whole demo; RUN B pins its public key for verify.
let sharedKeys: SigningKeyPair | null = null;
async function keyPair(): Promise<SigningKeyPair> {
  if (sharedKeys === null) sharedKeys = await generateSigningKeyPair();
  return sharedKeys;
}

async function baseline(): Promise<void> {
  console.log("\nRUN A: baseline, no interceptors");
  const { tools, sinkReceived } = makeTools();
  const readFile = tools[0]!;
  const writeQuery = tools[1]!;
  const secret = String(await readFile.invoke({ path: "/app/.env" }));
  await writeQuery.invoke({ value: secret });
  const leaked = sinkReceived.some((v) => v.includes(CANARY));
  console.log(`  sink received the verbatim secret: ${String(leaked)}  <- exfiltration`);
}

async function guardOnly(pinnedKey: string, wrongKey: string): Promise<void> {
  console.log("\nRUN B: guard only (redact: false), enforce");
  const { tools, sinkReceived } = makeTools();
  const receipts: DecisionReceipt[] = [];
  const node = new InterceptingToolNode(tools, {
    issuerKeyPair: await keyPair(),
    redact: false,
    onReceipt: (r) => receipts.push(r),
  });
  const cfg = { configurable: { thread_id: "demo-b" } };

  const readMsg = firstMessage(await node.invoke(READ, cfg));
  const writeMsg = firstMessage(await node.invoke(write(secretFrom(readMsg)), cfg));
  console.log(`  read_file   -> ${readMsg.status}`);
  console.log(`  write_query -> ${writeMsg.status}: ${String(writeMsg.content)}`);
  console.log(`  sink received the secret: ${String(sinkReceived.some((v) => v.includes(CANARY)))}`);

  const denyReceipt = receipts[receipts.length - 1]!;
  const okPinned = await verifyReceipt(denyReceipt, pinnedKey);
  const okWrong = await verifyReceipt(denyReceipt, wrongKey);
  console.log("  --- offline receipt verification (auditor, no network) ---");
  console.log(`  verify against PINNED issuer key: ${okPinned.ok ? "PASS" : `FAIL (${okPinned.reason})`}`);
  console.log(`  verify against WRONG key:         ${okWrong.ok ? "PASS" : `FAIL (${okWrong.reason})`}  <- forgery rejected`);
}

async function guardAndRedactor(): Promise<void> {
  console.log("\nRUN C: guard + redactor (default), enforce");
  const { tools, sinkReceived } = makeTools();
  const node = new InterceptingToolNode(tools, { issuerKeyPair: await keyPair() });
  const cfg = { configurable: { thread_id: "demo-c" } };

  const readMsg = firstMessage(await node.invoke(READ, cfg));
  const writeMsg = firstMessage(await node.invoke(write(secretFrom(readMsg)), cfg));
  console.log(`  write_query -> ${writeMsg.status}: ${String(writeMsg.content)}`);
  console.log(`  sink received the verbatim secret: ${String(sinkReceived.some((v) => v.includes(CANARY)))}`);
  console.log(`  sink received an opaque handle:    ${String(sinkReceived.some((v) => v.includes("mcp:secret-ref")))}  <- secret defused`);
}

async function main(): Promise<void> {
  console.log("relaybleed demo: read a secret from filesystem, try to send it to sqlite");
  console.log(`decision vocabulary: allow=${DECISION.Allow} deny=${DECISION.Deny}`);
  line();

  const issuer = await keyPair();
  const pinnedKey = await exportPublicKeyBase64(issuer.publicKey);
  const wrongKey = await exportPublicKeyBase64(
    (await generateSigningKeyPair()).publicKey,
  );

  await baseline();
  await guardOnly(pinnedKey, wrongKey);
  await guardAndRedactor();
  line();
  console.log("done: the secret never crossed the boundary under interceptors.");
}

main().catch((err: unknown) => {
  console.error(err);
  process.exitCode = 1;
});
