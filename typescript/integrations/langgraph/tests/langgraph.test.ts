// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The LangChain / LangGraph wrapper tests: vanilla tools leak; wrapped tools
 * deny the cross-boundary send with an offline-verifiable receipt on the
 * ToolMessage; redaction defuses; and `onDeny: "throw"` throws the receipt.
 */
import { describe, expect, it } from "vitest";
import {
  createInterceptorShield,
  CrossBoundaryDenied,
  readInterceptorArtifact,
} from "../src/index.ts";
import {
  AWS_CANARY,
  STRIPE_CANARY,
  callTool,
  makeReadFile,
  makeWriteQuery,
} from "./tools.ts";

const readArgs = { path: "/workspace/.env" };
const writeArgs = (secret: string): Record<string, unknown> => ({
  query: `INSERT INTO notes (body) VALUES ('${secret}')`,
});

describe("vanilla (unwrapped) tools leak the secret", () => {
  it("the raw sqlite tool records the verbatim secret", async () => {
    const sink: string[] = [];
    const write = makeWriteQuery(sink);
    await callTool(write, writeArgs(AWS_CANARY), "w1");
    expect(sink.some((q) => q.includes(AWS_CANARY))).toBe(true);
  });
});

describe("wrapped tools deny the cross-boundary send", () => {
  it("returns a denial ToolMessage carrying an offline-verifiable receipt; nothing exfiltrates", async () => {
    const sink: string[] = [];
    const shield = await createInterceptorShield({ sessionId: "wrap-deny" });
    const [readFile, writeQuery] = shield.wrap([
      makeReadFile(AWS_CANARY),
      makeWriteQuery(sink),
    ]);

    const readMsg = await callTool(readFile, readArgs, "r1");
    const readArtifact = readInterceptorArtifact(readMsg);
    expect(readArtifact?.outcome).toBe("allowed");

    const writeMsg = await callTool(writeQuery, writeArgs(AWS_CANARY), "w1");
    const artifact = readInterceptorArtifact(writeMsg);
    expect(artifact?.outcome).toBe("denied");
    if (artifact === null || artifact.outcome !== "denied") return;

    expect(sink).toEqual([]);
    expect(String(writeMsg.content)).toContain("DENIED");

    const good = await shield.verifyReceipt(artifact.receipt);
    expect(good.ok).toBe(true);

    const wrongKey = (await createInterceptorShield()).publicKeyBase64;
    const bad = await shield.verifyReceipt(artifact.receipt, wrongKey);
    expect(bad.ok).toBe(false);
  });

  it("emits a provenance label on the allowed read (origin filesystem, tainted)", async () => {
    const shield = await createInterceptorShield({ sessionId: "wrap-label" });
    const [readFile] = shield.wrap([makeReadFile(AWS_CANARY)]);
    const msg = await callTool(readFile, readArgs, "r1");
    const artifact = readInterceptorArtifact(msg);
    expect(artifact?.outcome).toBe("allowed");
    if (artifact === null || artifact.outcome !== "allowed") return;
    expect(artifact.provenance.server).toBe("filesystem");
    expect(artifact.provenance.tainted).toBe(true);
    expect(artifact.provenance.secretFormats).toContain("aws_access_key");
  });
});

describe("redaction defuses the send through the wrapper", () => {
  it("allows the write but the sink receives an opaque handle, never the secret", async () => {
    const sink: string[] = [];
    const shield = await createInterceptorShield({ sessionId: "wrap-redact", redact: true });
    const [readFile, writeQuery] = shield.wrap([
      makeReadFile(STRIPE_CANARY),
      makeWriteQuery(sink),
    ]);

    await callTool(readFile, readArgs, "r1");
    const writeMsg = await callTool(writeQuery, writeArgs(STRIPE_CANARY), "w1");
    const artifact = readInterceptorArtifact(writeMsg);
    expect(artifact?.outcome).toBe("allowed");

    expect(sink).toHaveLength(1);
    expect(sink[0]).not.toContain(STRIPE_CANARY);
    expect(sink[0]).toContain("<mcp:secret-ref:stripe_secret_live:");
  });
});

describe("onDeny: throw", () => {
  it("throws CrossBoundaryDenied carrying the receipt", async () => {
    const shield = await createInterceptorShield({ sessionId: "wrap-throw", onDeny: "throw" });
    const [readFile, writeQuery] = shield.wrap([
      makeReadFile(AWS_CANARY),
      makeWriteQuery([]),
    ]);
    await callTool(readFile, readArgs, "r1");

    await expect(callTool(writeQuery, writeArgs(AWS_CANARY), "w1")).rejects.toThrow(
      CrossBoundaryDenied,
    );

    try {
      await callTool(writeQuery, writeArgs(AWS_CANARY), "w2");
      throw new Error("expected a throw");
    } catch (err) {
      expect(err).toBeInstanceOf(CrossBoundaryDenied);
      const denied = err as CrossBoundaryDenied;
      const ok = await shield.verifyReceipt(denied.artifact.receipt);
      expect(ok.ok).toBe(true);
    }
  });
});
