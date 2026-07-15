// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * End-to-end tests over the real MCP SDK in-memory transport: interceptor
 * host servers, wire wrappers, multi-host chains, the intercepting client,
 * and the transparent gateway - every layer talking actual JSON-RPC.
 */
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  McpError,
  ToolListChangedNotificationSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { afterEach, describe, expect, it } from "vitest";
import {
  INTERCEPTION_EVENT,
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTORS_CAPABILITY,
} from "./protocol/constants.js";
import {
  ChainBlockedError,
  DuplicateInterceptorNameError,
  executeChainOnClients,
  hasInterceptorsCapability,
  invokeInterceptor,
  listInterceptors,
  createInterceptingClient,
  DUPLICATE_NAME_POLICY,
} from "./client/index.js";
import { createGateway } from "./gateway/index.js";
import {
  apply,
  block,
  createRegistry,
  defineMutator,
  defineValidator,
  keep,
  pass,
  registerInterceptorsOnServer,
} from "./server/index.js";
import type { RegisteredInterceptor } from "./server/index.js";

// ── harness ──────────────────────────────────────────────────────────────────

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  await Promise.all(cleanups.splice(0).map((c) => c()));
});

async function connect(server: Server): Promise<Client> {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test-client", version: "0.0.0" });
  await Promise.all([
    server.connect(serverTransport),
    client.connect(clientTransport),
  ]);
  cleanups.push(async () => {
    await Promise.all([client.close(), server.close()]);
  });
  return client;
}

/** An interceptor host: a Server serving only the SEP extension methods. */
async function hostWith(entries: readonly RegisteredInterceptor[]): Promise<Client> {
  const server = new Server(
    { name: "interceptor-host", version: "0.0.0" },
    { capabilities: {} },
  );
  registerInterceptorsOnServer(server, createRegistry(entries));
  return connect(server);
}

/** A backend MCP server with one echo tool. */
async function backend(): Promise<{ client: Client; server: Server }> {
  const server = new Server(
    { name: "backend", version: "0.0.0" },
    { capabilities: { tools: { listChanged: true } } },
  );
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [
      {
        name: "echo",
        description: "echoes its arguments",
        inputSchema: { type: "object" as const },
      },
    ],
  }));
  server.setRequestHandler(CallToolRequestSchema, (request) => ({
    content: [
      { type: "text" as const, text: JSON.stringify(request.params.arguments ?? {}) },
    ],
  }));
  return { client: await connect(server), server };
}

const guard = (): RegisteredInterceptor =>
  defineValidator({
    name: "forbid-dangerous",
    events: [INTERCEPTION_EVENT.ToolsCall],
    phases: "request",
    validate: (p) =>
      (p.payload as { name?: string }).name === "dangerous"
        ? block("dangerous tool blocked")
        : pass(),
  });

const redactor = (): RegisteredInterceptor =>
  defineMutator({
    name: "redact-secret",
    events: [INTERCEPTION_EVENT.ToolsCall],
    phases: "request",
    mutate: (p) => {
      const payload = p.payload as {
        name: string;
        arguments?: Record<string, unknown>;
      };
      return payload.arguments?.secret === undefined
        ? keep()
        : apply({
            ...payload,
            arguments: { ...payload.arguments, secret: "[REDACTED]" },
          });
    },
  });

// ── wire wrappers against a live host ────────────────────────────────────────

describe("wire wrappers over in-memory transport", () => {
  it("advertises the extension capability and lists interceptors", async () => {
    const host = await hostWith([guard(), redactor()]);
    expect(hasInterceptorsCapability(host)).toBe(true);

    const listed = await listInterceptors(host);
    expect(listed.interceptors.map((i) => i.name).sort()).toEqual([
      "forbid-dangerous",
      "redact-secret",
    ]);
    // Interior invariants hold after the wire round-trip.
    expect(listed.interceptors.every((i) => i.mode === INTERCEPTOR_MODE.Enforce)).toBe(true);
    expect(listed.interceptors.every((i) => !i.failOpen)).toBe(true);
  });

  it("filters interceptors/list by event", async () => {
    const host = await hostWith([
      guard(),
      defineValidator({
        name: "prompts-only",
        events: [INTERCEPTION_EVENT.PromptsGet],
        validate: () => pass(),
      }),
    ]);
    const listed = await listInterceptors(host, {
      event: INTERCEPTION_EVENT.PromptsGet,
      cursor: null,
    });
    expect(listed.interceptors.map((i) => i.name)).toEqual(["prompts-only"]);
  });

  it("invokes a validator over the wire and normalizes the result", async () => {
    const host = await hostWith([guard()]);
    const result = await invokeInterceptor(host, {
      name: "forbid-dangerous",
      event: INTERCEPTION_EVENT.ToolsCall,
      phase: INTERCEPTOR_PHASE.Request,
      payload: { name: "dangerous" },
      config: null,
      timeoutMs: null,
      context: null,
    });
    expect(result).toMatchObject({
      interceptor: "forbid-dangerous",
      valid: false,
      messages: [{ message: "dangerous tool blocked" }],
    });
  });

  it("invoking an unknown interceptor surfaces MCP invalid-params", async () => {
    const host = await hostWith([guard()]);
    await expect(
      invokeInterceptor(host, {
        name: "ghost",
        event: INTERCEPTION_EVENT.ToolsCall,
        phase: INTERCEPTOR_PHASE.Request,
        payload: null,
        config: null,
        timeoutMs: null,
        context: null,
      }),
    ).rejects.toThrow(McpError);
  });
});

// ── multi-host chains ────────────────────────────────────────────────────────

describe("executeChainOnClients", () => {
  it("merges hosts and routes invocations to the advertising host", async () => {
    const hostA = await hostWith([redactor()]);
    const hostB = await hostWith([guard()]);

    const chain = await executeChainOnClients([hostA, hostB], {
      event: INTERCEPTION_EVENT.ToolsCall,
      phase: INTERCEPTOR_PHASE.Request,
      payload: { name: "echo", arguments: { secret: "hunter2" } },
      names: null,
      timeoutMs: null,
      context: null,
    });

    expect(chain.status).toBe("success");
    expect(chain.results.map((r) => r.interceptor).sort()).toEqual([
      "forbid-dangerous",
      "redact-secret",
    ]);
    expect(chain.finalPayload).toEqual({
      name: "echo",
      arguments: { secret: "[REDACTED]" },
    });
  });

  it("rejects duplicate names across hosts by default, first-wins on request", async () => {
    const hostA = await hostWith([guard()]);
    const hostB = await hostWith([guard()]);
    const params = {
      event: INTERCEPTION_EVENT.ToolsCall,
      phase: INTERCEPTOR_PHASE.Request,
      payload: { name: "echo" },
      names: null,
      timeoutMs: null,
      context: null,
    };

    await expect(executeChainOnClients([hostA, hostB], params)).rejects.toThrow(
      DuplicateInterceptorNameError,
    );
    const chain = await executeChainOnClients(
      [hostA, hostB],
      params,
      DUPLICATE_NAME_POLICY.FirstWins,
    );
    expect(chain.status).toBe("success");
    expect(chain.results).toHaveLength(1);
  });
});

// ── intercepting client ──────────────────────────────────────────────────────

describe("createInterceptingClient", () => {
  it("mutates the request before the backend sees it", async () => {
    const wrapped = createInterceptingClient({
      backend: (await backend()).client,
      hosts: [await hostWith([redactor()])],
    });
    const result = await wrapped.callTool({
      name: "echo",
      arguments: { secret: "hunter2", keep: "me" },
    });
    const text = (result.content[0] as { text: string }).text;
    expect(JSON.parse(text)).toEqual({ secret: "[REDACTED]", keep: "me" });
  });

  it("blocks a forbidden call with ChainBlockedError", async () => {
    const wrapped = createInterceptingClient({
      backend: (await backend()).client,
      hosts: [await hostWith([guard()])],
    });
    await expect(wrapped.callTool({ name: "dangerous" })).rejects.toThrow(
      ChainBlockedError,
    );
  });

  it("audit-mode interceptors never block the call", async () => {
    const auditGuard = defineValidator({
      name: "audit-guard",
      events: [INTERCEPTION_EVENT.ToolsCall],
      mode: INTERCEPTOR_MODE.Audit,
      validate: () => block("would block in enforce"),
    });
    const wrapped = createInterceptingClient({
      backend: (await backend()).client,
      hosts: [await hostWith([auditGuard])],
    });
    const result = await wrapped.callTool({ name: "dangerous" });
    expect(result.content).toHaveLength(1);
  });

  it("the events filter bypasses the chain for other operations", async () => {
    const wrapped = createInterceptingClient({
      backend: (await backend()).client,
      hosts: [await hostWith([guard()])],
      events: [INTERCEPTION_EVENT.PromptsGet],
    });
    // guard would block this, but tools/call is not in the intercepted set.
    const result = await wrapped.callTool({ name: "dangerous" });
    expect(result.content).toHaveLength(1);
  });

  it("lists tools through the chain and aggregates interceptors across hosts", async () => {
    const wrapped = createInterceptingClient({
      backend: (await backend()).client,
      hosts: [await hostWith([guard()]), await hostWith([redactor()])],
    });
    const tools = await wrapped.listTools();
    expect(tools.tools.map((t) => t.name)).toEqual(["echo"]);
    const listed = await wrapped.listInterceptors();
    expect(listed.interceptors.map((i) => i.name).sort()).toEqual([
      "forbid-dangerous",
      "redact-secret",
    ]);
  });
});

// ── gateway ──────────────────────────────────────────────────────────────────

async function gatewayFront(spec: {
  hosts: readonly Client[];
  exposeInterceptorProtocol?: boolean;
}): Promise<{ front: Client; backendServer: Server }> {
  const { client: backendClient, server: backendServer } = await backend();
  const gateway = createGateway({ backend: backendClient, ...spec });
  const proxy = new Server({ name: "gateway", version: "0.0.0" }, { capabilities: {} });
  gateway.configureServer(proxy);
  const front = await connect(proxy);
  const dispose = gateway.forwardNotifications(proxy);
  cleanups.push(() => {
    dispose();
    return Promise.resolve();
  });
  return { front, backendServer };
}

describe("createGateway", () => {
  it("mirrors backend capabilities on the proxy", async () => {
    const { front } = await gatewayFront({ hosts: [await hostWith([guard()])] });
    const caps = front.getServerCapabilities();
    expect(caps?.tools).toEqual({ listChanged: true });
    expect(caps?.prompts).toBeUndefined();
    expect(caps?.resources).toBeUndefined();
  });

  it("proxies tool calls through the chain: mutation applied", async () => {
    const { front } = await gatewayFront({ hosts: [await hostWith([redactor()])] });
    const result = await front.callTool({
      name: "echo",
      arguments: { secret: "hunter2" },
    });
    // Raw Client.callTool types content as unknown without a result schema.
    const content = result.content as { text: string }[];
    const text = content[0].text;
    expect(JSON.parse(text)).toEqual({ secret: "[REDACTED]" });
  });

  it("a blocked call surfaces as an MCP error to the front client", async () => {
    const { front } = await gatewayFront({ hosts: [await hostWith([guard()])] });
    await expect(front.callTool({ name: "dangerous" })).rejects.toThrow(
      /dangerous tool blocked/,
    );
  });

  it("exposes the interceptor protocol when configured", async () => {
    const { front } = await gatewayFront({
      hosts: [await hostWith([guard()]), await hostWith([redactor()])],
      exposeInterceptorProtocol: true,
    });
    expect(front.getServerCapabilities()?.extensions).toHaveProperty(
      INTERCEPTORS_CAPABILITY,
    );
    const listed = await listInterceptors(front);
    expect(listed.interceptors.map((i) => i.name).sort()).toEqual([
      "forbid-dangerous",
      "redact-secret",
    ]);
    const invoked = await invokeInterceptor(front, {
      name: "forbid-dangerous",
      event: INTERCEPTION_EVENT.ToolsCall,
      phase: INTERCEPTOR_PHASE.Request,
      payload: { name: "dangerous" },
      config: null,
      timeoutMs: null,
      context: null,
    });
    expect(invoked).toMatchObject({ valid: false });
  });

  it("forwards backend list-changed notifications to front clients", async () => {
    const { front, backendServer } = await gatewayFront({
      hosts: [await hostWith([guard()])],
    });
    const received = new Promise<void>((resolve) => {
      front.setNotificationHandler(ToolListChangedNotificationSchema, () => {
        resolve();
      });
    });
    await backendServer.sendToolListChanged();
    await expect(
      Promise.race([
        received.then(() => "received"),
        new Promise((resolve) => setTimeout(() => resolve("timeout"), 2_000)),
      ]),
    ).resolves.toBe("received");
  });
});
