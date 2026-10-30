// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Calibration tests for the reference security interceptors (open tier).
 *
 * RULE 15: core behaviors are asserted exhaustively over the ENTIRE
 * secret-format catalog, not a sampled subset. RULE 18 (forbidden output):
 * a redacted payload must never contain a secret value, and a denial message
 * must never echo the secret it denied.
 */
import { describe, expect, it } from "vitest";
import {
  CHAIN_STATUS,
  INTERCEPTION_EVENT,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "../../protocol/constants.js";
import type { InterceptionEvent, InterceptorPhase } from "../../protocol/constants.js";
import type { InvokeContext, InvokeParams, ValidationResult } from "../../protocol/types.js";
import { executeChain } from "../../client/chain.js";
import { createRegistry } from "../../server/registry.js";
import type { RegisteredInterceptor } from "../../server/define-interceptor.js";
import { createCrossBoundaryGuard, CROSS_BOUNDARY_GUARD_NAME } from "./cross-boundary-guard.js";
import { createSecretlessRedactor, handleFor, SECRETLESS_REDACTOR_NAME } from "./secretless-redactor.js";
import { findSecrets, SECRET_FORMATS } from "./secret-formats.js";
import { serverOf, TOOL_SERVER } from "./server-of.js";

// ── helpers ───────────────────────────────────────────────────────────────────

function ctx(sessionId: string): InvokeContext {
  return {
    principal: null,
    traceId: null,
    spanId: null,
    timestamp: null,
    sessionId,
  };
}

function invokeParams(
  name: string,
  event: InterceptionEvent,
  phase: InterceptorPhase,
  payload: unknown,
  sessionId: string,
): InvokeParams {
  return {
    name,
    event,
    phase,
    payload,
    config: null,
    timeoutMs: null,
    context: ctx(sessionId),
  };
}

function toolCall(tool: string, args: Readonly<Record<string, unknown>>): unknown {
  return { name: tool, arguments: args };
}

function toolResultText(text: string): unknown {
  return { content: [{ type: "text", text }] };
}

async function invokeGuard(
  guard: RegisteredInterceptor,
  phase: InterceptorPhase,
  payload: unknown,
  sessionId: string,
): Promise<ValidationResult> {
  const result = await guard.handler(
    invokeParams(CROSS_BOUNDARY_GUARD_NAME, INTERCEPTION_EVENT.ToolsCall, phase, payload, sessionId),
    null,
  );
  if (result.type !== INTERCEPTOR_TYPE.Validation) throw new Error("expected validation");
  return result;
}

/** Drive one read→respond round-trip so `secret` becomes tainted from `tool`'s server. */
async function readSecretVia(
  guard: RegisteredInterceptor,
  tool: string,
  secret: string,
  sessionId: string,
): Promise<void> {
  const request = await invokeGuard(
    guard,
    INTERCEPTOR_PHASE.Request,
    toolCall(tool, { path: "/tmp/x" }),
    sessionId,
  );
  expect(request.valid).toBe(true);
  const response = await invokeGuard(
    guard,
    INTERCEPTOR_PHASE.Response,
    toolResultText(`config loaded: ${secret}`),
    sessionId,
  );
  expect(response.valid).toBe(true);
}

// ── secret-format catalog (RULE 8 integrity, exhaustive) ─────────────────────

describe("SECRET_FORMATS catalog", () => {
  it("has unique ids and a sourced origin on every entry", () => {
    const ids = SECRET_FORMATS.map((f) => f.id);
    expect(new Set(ids).size).toBe(ids.length);
    for (const f of SECRET_FORMATS) expect(f.origin.length).toBeGreaterThan(0);
  });

  it.each(SECRET_FORMATS.map((f) => [f.id, f] as const))(
    "%s: example matches its own pattern and is found by findSecrets",
    (_id, format) => {
      expect(format.example).toMatch(new RegExp(format.pattern.source));
      const hits = findSecrets(`prefix ${format.example} suffix`);
      expect(hits).toContainEqual({ formatId: format.id, value: format.example });
    },
  );

  it("finds multiple distinct secrets in one text", () => {
    const [a, b] = [SECRET_FORMATS[0], SECRET_FORMATS[4]];
    const hits = findSecrets(`${a.example} and ${b.example}`);
    expect(hits.map((h) => h.formatId).sort()).toEqual([a.id, b.id].sort());
  });

  it("is stateless across scans (no global-regex lastIndex leak)", () => {
    const text = SECRET_FORMATS[0].example;
    expect(findSecrets(text)).toEqual(findSecrets(text));
  });

  it("finds nothing in benign text", () => {
    expect(findSecrets("please write the summary to notes.txt")).toEqual([]);
  });
});

// ── server attribution ────────────────────────────────────────────────────────

describe("serverOf", () => {
  it("maps every cataloged tool to its server", () => {
    for (const [tool, server] of Object.entries(TOOL_SERVER)) {
      const params = invokeParams(
        "x",
        INTERCEPTION_EVENT.ToolsCall,
        INTERCEPTOR_PHASE.Request,
        toolCall(tool, {}),
        "s",
      );
      expect(serverOf(params)).toBe(server);
    }
  });

  it("falls back to the tool name for an uncataloged tool", () => {
    const params = invokeParams(
      "x",
      INTERCEPTION_EVENT.ToolsCall,
      INTERCEPTOR_PHASE.Request,
      toolCall("send_email", { to: "a@b.c" }),
      "s",
    );
    expect(serverOf(params)).toBe("send_email");
  });

  it("uses the URI authority for resources, scheme when authority-less", () => {
    const withAuthority = invokeParams(
      "x",
      INTERCEPTION_EVENT.ResourcesRead,
      INTERCEPTOR_PHASE.Request,
      { uri: "postgres://db.internal/table" },
      "s",
    );
    expect(serverOf(withAuthority)).toBe("db.internal");
    const schemeOnly = invokeParams(
      "x",
      INTERCEPTION_EVENT.ResourcesRead,
      INTERCEPTOR_PHASE.Request,
      { uri: "file:/etc/passwd" },
      "s",
    );
    expect(serverOf(schemeOnly)).toBe("file");
  });

  it("falls back to the event namespace when the payload names nothing", () => {
    const params = invokeParams(
      "x",
      INTERCEPTION_EVENT.SamplingCreateMessage,
      INTERCEPTOR_PHASE.Request,
      { messages: [] },
      "s",
    );
    expect(serverOf(params)).toBe("sampling");
  });
});

// ── cross-boundary-guard ──────────────────────────────────────────────────────

describe("cross-boundary-guard", () => {
  it.each(SECRET_FORMATS.map((f) => [f.id, f] as const))(
    "relaybleed [%s]: read from filesystem → send to sqlite is blocked, without echoing the secret",
    async (_id, format) => {
      const guard = createCrossBoundaryGuard();
      await readSecretVia(guard, "read_file", format.example, "s1");

      const denial = await invokeGuard(
        guard,
        INTERCEPTOR_PHASE.Request,
        toolCall("write_query", { query: `INSERT INTO logs VALUES ('${format.example}')` }),
        "s1",
      );
      expect(denial.valid).toBe(false);
      expect(denial.severity).toBe(VALIDATION_SEVERITY.Error);
      const message = denial.messages[0].message;
      expect(message).toContain("filesystem");
      expect(message).toContain("sqlite");
      // RULE 18 forbidden output: the denial must not leak the secret itself.
      expect(message).not.toContain(format.example);
    },
  );

  it("allows writing the secret back to the server it came from", async () => {
    const guard = createCrossBoundaryGuard();
    const secret = SECRET_FORMATS[0].example;
    await readSecretVia(guard, "read_file", secret, "s1");
    const verdict = await invokeGuard(
      guard,
      INTERCEPTOR_PHASE.Request,
      toolCall("write_file", { path: "/tmp/copy", content: secret }),
      "s1",
    );
    expect(verdict.valid).toBe(true);
  });

  it("allows a secret with no strictly-prior read (causality, not moralizing)", async () => {
    const guard = createCrossBoundaryGuard();
    const verdict = await invokeGuard(
      guard,
      INTERCEPTOR_PHASE.Request,
      toolCall("write_query", { query: SECRET_FORMATS[0].example }),
      "fresh-session",
    );
    expect(verdict.valid).toBe(true);
  });

  it("isolates taint per session", async () => {
    const guard = createCrossBoundaryGuard();
    const secret = SECRET_FORMATS[2].example;
    await readSecretVia(guard, "read_file", secret, "session-a");
    const verdict = await invokeGuard(
      guard,
      INTERCEPTOR_PHASE.Request,
      toolCall("write_query", { query: secret }),
      "session-b",
    );
    expect(verdict.valid).toBe(true);
  });

  it("keeps the FIRST origin when the same secret is later seen elsewhere", async () => {
    const guard = createCrossBoundaryGuard();
    const secret = SECRET_FORMATS[0].example;
    await readSecretVia(guard, "read_file", secret, "s1");
    await readSecretVia(guard, "read_query", secret, "s1"); // seen again via sqlite
    const toSqlite = await invokeGuard(
      guard,
      INTERCEPTOR_PHASE.Request,
      toolCall("write_query", { query: secret }),
      "s1",
    );
    expect(toSqlite.valid).toBe(false); // origin is still filesystem
    const toFilesystem = await invokeGuard(
      guard,
      INTERCEPTOR_PHASE.Request,
      toolCall("write_file", { path: "/tmp/x", content: secret }),
      "s1",
    );
    expect(toFilesystem.valid).toBe(true);
  });

  it("aborts an enforce chain with validation_failed naming the guard", async () => {
    const guard = createCrossBoundaryGuard();
    const registry = createRegistry([guard]);
    const secret = SECRET_FORMATS[0].example;
    await readSecretVia(guard, "read_file", secret, "chain-session");

    const chain = await executeChain(registry.descriptors, (p) => registry.invoke(p), {
      event: INTERCEPTION_EVENT.ToolsCall,
      phase: INTERCEPTOR_PHASE.Request,
      payload: toolCall("write_query", { query: secret }),
      names: null,
      timeoutMs: null,
      context: ctx("chain-session"),
    });
    expect(chain.status).toBe(CHAIN_STATUS.ValidationFailed);
    expect(chain.abortedAt?.interceptor).toBe(CROSS_BOUNDARY_GUARD_NAME);
    expect(chain.validationSummary.errors).toBe(1);
  });
});

// ── secretless-redactor ───────────────────────────────────────────────────────

describe("secretless-redactor", () => {
  const redactor = createSecretlessRedactor();

  async function redact(payload: unknown): Promise<{ modified: boolean; payload: unknown }> {
    const result = await redactor.handler(
      invokeParams(
        SECRETLESS_REDACTOR_NAME,
        INTERCEPTION_EVENT.ToolsCall,
        INTERCEPTOR_PHASE.Request,
        payload,
        "s",
      ),
      null,
    );
    if (result.type !== INTERCEPTOR_TYPE.Mutation) throw new Error("expected mutation");
    return { modified: result.modified, payload: result.payload };
  }

  it.each(SECRET_FORMATS.map((f) => [f.id, f] as const))(
    "replaces a %s value with its opaque handle and never emits the secret",
    async (_id, format) => {
      const out = await redact(toolCall("write_query", { query: `key=${format.example}` }));
      expect(out.modified).toBe(true);
      const text = JSON.stringify(out.payload);
      // RULE 18 forbidden output: the verbatim secret must be gone…
      expect(text).not.toContain(format.example);
      // …and the deterministic handle must be present in its place.
      expect(text).toContain(handleFor(format.id, format.example));
    },
  );

  it("keeps a payload with no secrets (modified=false, payload passthrough)", async () => {
    const payload = toolCall("write_file", { path: "/tmp/notes", content: "benign" });
    const out = await redact(payload);
    expect(out.modified).toBe(false);
    expect(out.payload).toEqual(payload);
  });

  it("redacts inside nested objects and arrays", async () => {
    const secret = SECRET_FORMATS[5].example;
    const out = await redact({
      name: "add_observations",
      arguments: { notes: [{ body: `token: ${secret}` }, "clean"] },
    });
    const text = JSON.stringify(out.payload);
    expect(out.modified).toBe(true);
    expect(text).not.toContain(secret);
    expect(text).toContain(handleFor(SECRET_FORMATS[5].id, secret));
  });

  it("derives handles deterministically and distinctly", () => {
    const [a, b] = [SECRET_FORMATS[0], SECRET_FORMATS[1]];
    expect(handleFor(a.id, a.example)).toBe(handleFor(a.id, a.example));
    expect(handleFor(a.id, a.example)).not.toBe(handleFor(b.id, b.example));
  });
});

// ── composition: redaction is the remediation, blocking is the backstop ───────

describe("guard + redactor composed on one chain", () => {
  it("request phase runs the redactor first, so the guard passes a redacted payload", async () => {
    const guard = createCrossBoundaryGuard();
    const registry = createRegistry([guard, createSecretlessRedactor()]);
    const secret = SECRET_FORMATS[0].example;
    await readSecretVia(guard, "read_file", secret, "compose");

    const chain = await executeChain(registry.descriptors, (p) => registry.invoke(p), {
      event: INTERCEPTION_EVENT.ToolsCall,
      phase: INTERCEPTOR_PHASE.Request,
      payload: toolCall("write_query", { query: `INSERT ${secret}` }),
      names: null,
      timeoutMs: null,
      context: ctx("compose"),
    });

    expect(chain.status).toBe(CHAIN_STATUS.Success);
    const finalText = JSON.stringify(chain.finalPayload);
    expect(finalText).not.toContain(secret); // RULE 18: nothing verbatim leaves
    expect(finalText).toContain(handleFor(SECRET_FORMATS[0].id, secret));
  });
});
