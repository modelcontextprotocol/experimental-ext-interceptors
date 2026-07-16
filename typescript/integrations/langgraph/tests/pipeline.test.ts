// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Framework-agnostic pipeline tests. These pin behavior to the conformance
 * BEHAVIOR fixtures (relaybleed-*, redaction-defuses-relaybleed,
 * same-origin-writeback-allowed, no-prior-read-allowed, session-isolation), so
 * the LangGraph integration cannot drift from the certified security semantics.
 */
import { describe, expect, it } from "vitest";
import { createPipeline } from "../src/pipeline.ts";
import { AWS_CANARY, STRIPE_CANARY } from "./tools.ts";

const READ_ARGS = { path: "/workspace/.env" };
const writeQuery = (secret: string): Record<string, unknown> => ({
  query: `INSERT INTO notes (body) VALUES ('${secret}')`,
});

describe("relaybleed: read from filesystem then send to sqlite is denied", () => {
  it("denies the cross-boundary send and attests an offline-verifiable receipt", async () => {
    const p = await createPipeline();
    const session = "relaybleed-aws";

    const read = await p.guardRequest("read_file", READ_ARGS, session);
    expect(read.decision).toBe("allow");
    await p.ingestResponse("read_file", `KEY=${AWS_CANARY}`, session);

    const send = await p.guardRequest("write_query", writeQuery(AWS_CANARY), session);
    expect(send.decision).toBe("deny");
    if (send.decision !== "deny") return;

    expect(send.server).toBe("sqlite");
    expect(send.whyNot).toContain("filesystem");
    expect(send.whyNot).toContain("sqlite");

    const good = await p.verifyReceipt(send.receipt);
    expect(good.ok).toBe(true);
  });

  it("the receipt fails verification against a different (wrong) issuer key", async () => {
    const p = await createPipeline();
    const session = "relaybleed-stripe";
    await p.guardRequest("read_file", READ_ARGS, session);
    await p.ingestResponse("read_file", STRIPE_CANARY, session);
    const send = await p.guardRequest("write_query", writeQuery(STRIPE_CANARY), session);
    expect(send.decision).toBe("deny");
    if (send.decision !== "deny") return;

    const wrongKey = (await createPipeline()).publicKeyBase64;
    const bad = await p.verifyReceipt(send.receipt, wrongKey);
    expect(bad.ok).toBe(false);
    if (bad.ok) return;
    expect(bad.reason).toBe("signature-mismatch");
  });

  it("tampering with the attested receipt fails verification", async () => {
    const p = await createPipeline();
    const session = "relaybleed-tamper";
    await p.guardRequest("read_file", READ_ARGS, session);
    await p.ingestResponse("read_file", AWS_CANARY, session);
    const send = await p.guardRequest("write_query", writeQuery(AWS_CANARY), session);
    if (send.decision !== "deny") throw new Error("expected deny");

    const tampered = { ...send.receipt, valid: true };
    const check = await p.verifyReceipt(tampered);
    expect(check.ok).toBe(false);
  });
});

describe("redaction defuses relaybleed", () => {
  it("allows the send but strips the verbatim secret to an opaque handle", async () => {
    const p = await createPipeline({ redact: true });
    const session = "compose";

    await p.guardRequest("read_file", READ_ARGS, session);
    await p.ingestResponse("read_file", STRIPE_CANARY, session);

    const send = await p.guardRequest("write_query", writeQuery(STRIPE_CANARY), session);
    expect(send.decision).toBe("allow");
    if (send.decision !== "allow") return;

    expect(send.redacted).toBe(true);
    const query = String(send.effectiveArgs.query);
    expect(query).not.toContain(STRIPE_CANARY);
    expect(query).toContain("<mcp:secret-ref:stripe_secret_live:");
  });
});

describe("flows, not values: only the composed cross-boundary case denies", () => {
  it("same-origin writeback is allowed (filesystem read then filesystem write)", async () => {
    const p = await createPipeline();
    const session = "same-origin";
    await p.guardRequest("read_file", READ_ARGS, session);
    await p.ingestResponse("read_file", AWS_CANARY, session);
    const send = await p.guardRequest(
      "write_file",
      { contents: `KEY=${AWS_CANARY}` },
      session,
    );
    expect(send.decision).toBe("allow");
  });

  it("a secret sent with no prior cross-boundary read is allowed", async () => {
    const p = await createPipeline();
    const send = await p.guardRequest("write_query", writeQuery(AWS_CANARY), "no-prior");
    expect(send.decision).toBe("allow");
  });

  it("taint is isolated per session", async () => {
    const p = await createPipeline();
    await p.guardRequest("read_file", READ_ARGS, "session-a");
    await p.ingestResponse("read_file", AWS_CANARY, "session-a");

    const inB = await p.guardRequest("write_query", writeQuery(AWS_CANARY), "session-b");
    expect(inB.decision).toBe("allow");

    const inA = await p.guardRequest("write_query", writeQuery(AWS_CANARY), "session-a");
    expect(inA.decision).toBe("deny");
  });
});

describe("provenance labels", () => {
  it("labels a tool output with its origin server and detected secret formats", async () => {
    const p = await createPipeline();
    const label = await p.ingestResponse("read_file", `KEY=${AWS_CANARY}`, "label");
    expect(label.kind).toBe("mcp-provenance-label");
    expect(label.server).toBe("filesystem");
    expect(label.tainted).toBe(true);
    expect(label.secretFormats).toContain("aws_access_key");
    expect(label.emittedAt).toBeNull();
  });

  it("labels a clean output as untainted", async () => {
    const p = await createPipeline();
    const label = await p.ingestResponse("read_file", "nothing secret here", "clean");
    expect(label.tainted).toBe(false);
    expect(label.secretFormats).toEqual([]);
  });
});
