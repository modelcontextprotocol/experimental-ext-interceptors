// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Pure-engine tests: no LangChain, no network, no LLM. These pin the security
 * semantics the LangGraph surfaces merely wire up.
 */
import { describe, expect, it } from "vitest";
import {
  createInterceptorEngine,
  DECISION,
  exportPublicKeyBase64,
  generateSigningKeyPair,
  INTERCEPTOR_MODE,
  resolveOptions,
  verifyReceipt,
} from "../src/index.ts";
import type { InterceptOptions } from "../src/index.ts";

const CANARY = "sk_live_4eC7aRm9Kx2bNw5pQj8sYd";
const FILE_CONTENT = `db config\nSTRIPE_KEY=${CANARY}\n`;

async function engineWith(
  partial: Omit<InterceptOptions, "issuerKeyPair">,
): Promise<ReturnType<typeof createInterceptorEngine>> {
  const issuerKeyPair = await generateSigningKeyPair();
  return createInterceptorEngine(resolveOptions({ issuerKeyPair, ...partial }));
}

describe("cross-boundary guard (redact: false)", () => {
  it("allows a same-origin writeback (filesystem -> filesystem)", async () => {
    const engine = await engineWith({ redact: false });
    const s = "sess-same";
    await engine.request("read_file", { path: "/a" }, s);
    await engine.response("read_file", FILE_CONTENT, s);
    const decision = await engine.request("write_file", { value: CANARY }, s);
    expect(decision.kind).toBe(DECISION.Allow);
  });

  it("denies a cross-boundary send (filesystem -> sqlite): relaybleed", async () => {
    const engine = await engineWith({ redact: false });
    const s = "sess-cross";
    await engine.request("read_file", { path: "/a" }, s);
    await engine.response("read_file", FILE_CONTENT, s);
    const decision = await engine.request("write_query", { value: CANARY }, s);
    expect(decision.kind).toBe(DECISION.Deny);
    if (decision.kind === DECISION.Deny) {
      expect(decision.reason).toContain("filesystem");
    }
  });

  it("allows a cross-boundary send with no prior read (tracks flows, not values)", async () => {
    const engine = await engineWith({ redact: false });
    const decision = await engine.request("write_query", { value: CANARY }, "s");
    expect(decision.kind).toBe(DECISION.Allow);
  });

  it("isolates taint per session", async () => {
    const engine = await engineWith({ redact: false });
    await engine.request("read_file", { path: "/a" }, "A");
    await engine.response("read_file", FILE_CONTENT, "A");
    const other = await engine.request("write_query", { value: CANARY }, "B");
    expect(other.kind).toBe(DECISION.Allow);
  });
});

describe("secretless redactor (default, redact: true)", () => {
  it("defuses the secret so the composed cross-boundary write is allowed", async () => {
    const engine = await engineWith({});
    const s = "sess-defuse";
    await engine.request("read_file", { path: "/a" }, s);
    await engine.response("read_file", FILE_CONTENT, s);
    const decision = await engine.request("write_query", { value: CANARY }, s);
    expect(decision.kind).toBe(DECISION.Allow);
    if (decision.kind === DECISION.Allow) {
      const serialized = JSON.stringify(decision.args);
      expect(serialized).not.toContain(CANARY);
      expect(serialized).toContain("mcp:secret-ref");
    }
  });
});

describe("audit mode", () => {
  it("never denies, but the receipt records that it would have", async () => {
    const engine = await engineWith({ mode: INTERCEPTOR_MODE.Audit, redact: false });
    const s = "sess-audit";
    await engine.request("read_file", { path: "/a" }, s);
    await engine.response("read_file", FILE_CONTENT, s);
    const decision = await engine.request("write_query", { value: CANARY }, s);
    expect(decision.kind).toBe(DECISION.Allow);
    expect(decision.receipt.info?.["wouldDeny"]).toBe(true);
  });
});

describe("attested decision receipts", () => {
  it("verifies against the pinned issuer key and fails against a wrong key", async () => {
    const issuerKeyPair = await generateSigningKeyPair();
    const engine = createInterceptorEngine(
      resolveOptions({ issuerKeyPair, redact: false }),
    );
    const pinned = await exportPublicKeyBase64(issuerKeyPair.publicKey);
    const wrong = await exportPublicKeyBase64(
      (await generateSigningKeyPair()).publicKey,
    );

    const s = "sess-receipt";
    await engine.request("read_file", { path: "/a" }, s);
    await engine.response("read_file", FILE_CONTENT, s);
    const decision = await engine.request("write_query", { value: CANARY }, s);

    const good = await verifyReceipt(decision.receipt, pinned);
    const bad = await verifyReceipt(decision.receipt, wrong);
    expect(good.ok).toBe(true);
    expect(bad.ok).toBe(false);
    if (!bad.ok) expect(bad.reason).toBe("signature-mismatch");
  });

  it("binds the decision: tampering the receipt breaks verification", async () => {
    const issuerKeyPair = await generateSigningKeyPair();
    const engine = createInterceptorEngine(
      resolveOptions({ issuerKeyPair, redact: false }),
    );
    const pinned = await exportPublicKeyBase64(issuerKeyPair.publicKey);
    const decision = await engine.request("write_query", { value: "x" }, "s");
    const tampered = { ...decision.receipt, valid: !decision.receipt.valid };
    const v = await verifyReceipt(tampered, pinned);
    expect(v.ok).toBe(false);
  });
});
