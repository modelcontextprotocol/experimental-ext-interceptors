# MCP Interceptors TypeScript SDK — Design and Implementation

## Introduction

This document is the **authoritative design reference** for the TypeScript Interceptor SDK shipped from `/typescript/sdk` as **`mcp-ext-interceptors`**. It defines what the package does, how it is structured, and how it integrates with the official MCP TypeScript SDK.

Readers should use this document to implement or review the SDK. Normative interceptor protocol behavior is defined in [SEP-2624](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624) ([`docs/sep.md`](../../docs/sep.md) in this repository). Behavioral parity with the in-repo **C# Interceptor SDK** ([`csharp/sdk`](../../csharp/sdk)) is a primary goal.

The plan distinguishes two relationships to the MCP TypeScript SDK:

| Role | What | Where |
|------|------|--------|
| **Runtime dependency** | Code this package **imports at build and run time** | npm **MCP TypeScript SDK v2 packages**: `@modelcontextprotocol/client`, `@modelcontextprotocol/server` (`Client`, `Server`, transports, protocol types) and `@modelcontextprotocol/core` (spec Zod schemas) |
| **Structural reference** | How a mature MCP TypeScript SDK **organizes** client, server, protocol, tests, and public exports | Sibling repo **`typescript-sdk`** on **main** ([`../typescript-sdk`](../../typescript-sdk) when checked out beside this repo)—**not** a dependency |

Implementation targets the **v2** package split directly (the original v1 implementation followed v2-shaped module boundaries, which made the migration adapter rewrites rather than a redesign).

---

## 1. Scope and requirements

### 1.1 Product scope

- Implement the interceptor protocol from [SEP-2624](/docs/sep.md): wire methods, execution semantics, capability advertisement, and SDK conveniences (chain orchestration, hosting interceptors on an **interceptor host**, client helpers, and a **transparent gateway**).
- Provide **client**, **server**, and **gateway** APIs in one npm package, comparable in depth to the C# Interceptor SDK in this repository.
- Include **integration tests** where a client built with this SDK talks to a server built with this SDK over an in-process or equivalent transport, covering list, invoke, and chain execution.
- Ship **runnable examples** under `examples/`, modeled on the C# SDK’s [`csharp/sdk/samples`](../../csharp/sdk/samples) (see §10)—not part of the published npm artifact.

### 1.2 Package and tooling

- **Location:** `/typescript/sdk`, package name **`mcp-ext-interceptors`**.
- **Preserve** existing project configuration unless there is a strong, documented reason to change it: Node **≥22**, ESM, **`tsc`** → `dist/`, **Vitest**, **ESLint**, single root **`exports`** entry.
- **Publishing:** One npm package with logical modules under `src/` (`protocol`, `client`, `server`, `gateway`), not a multi-package monorepo like upstream `typescript-sdk`.
- **Dependencies:** **`peerDependencies`** on `@modelcontextprotocol/client` and `@modelcontextprotocol/server` **^2.0.0** (with matching **`devDependencies`** for reproducible CI and local tests); regular **`dependencies`** on `@modelcontextprotocol/core` (spec Zod `*Schema` constants) and `zod` **^4.2.0**, both imported directly by `src/protocol`.

### 1.3 MCP TypeScript SDK strategy

**Runtime (v2):** Import and use the **v2 packages**: `@modelcontextprotocol/client`, `@modelcontextprotocol/server`, and `@modelcontextprotocol/core`. Do not depend on the legacy v1 monolith (`@modelcontextprotocol/sdk`) or on `@modelcontextprotocol/core-internal`. Use the MCP SDK for JSON-RPC sessions, transports, `Client` / `Server`, and standard MCP types—do not reimplement core protocol plumbing.

**Structure:** When making layout, naming, export, or handler-registration choices, align with conventions on **`typescript-sdk` main**: separate client vs server concerns, curated public `index` exports, Vitest layout, and the v2 3-arg `setRequestHandler(method, { params, result? }, handler)` form for non-spec JSON-RPC methods. Reconcile with that repo as it evolves; do not vendor or link it as a dependency.

**Adapter seams:** Keep interceptor-specific logic free of MCP SDK types in `src/protocol` and `src/client/chain-orchestrator.ts`. Confine SDK-specific typing and handler registration to **`src/server/register-interceptors.ts`**, **`src/server/capabilities.ts`**, and **`src/client/client-extensions.ts`**. Avoid subclassing MCP SDK types in public interceptor APIs. SEP DTOs, chain ordering, and gateway orchestration concepts are SDK-version independent.

### 1.4 Capability advertisement (TypeScript default)

**Interceptor hosts** advertise support per the SEP: **`capabilities.extensions["io.modelcontextprotocol/interceptors"]`** with **`supportedEvents`** (SEP-2133 extensions format). The C# SDK in this repo advertises the same key, so the two SDKs interoperate directly (see §3).

---

## 2. Interceptor model

Per SEP-2624 (with terminology clarified for this SDK):

### 2.1 Primitive vs hosts

- An **interceptor** is an MCP **primitive** (governance logic for context operations)—analogous to tools, resources, and prompts, but with a different invocation model (see SEP).
- Interceptors are **discoverable** and **invocable** via JSON-RPC (`interceptors/list`, `interceptor/invoke`) on an **interceptor host**: an MCP-protocol **endpoint** that speaks the normal MCP session stack (`initialize`, JSON-RPC, transports) and advertises **`capabilities.extensions["io.modelcontextprotocol/interceptors"]`**. The SEP says interceptors are “hosted on MCP servers”; here **interceptor host** means that protocol role without implying the host is your **application MCP server**.
- An **application (backend) MCP server** is the server clients usually connect to for **tools**, **resources**, **prompts**, and related lifecycle events. It is a **different role** from an interceptor host. Deployments often use **client → interceptor host(s) → backend server** (see C# **`McpInterceptorGateway`** and SEP sidecar/proxy narrative). An interceptor host may expose **only** interceptor methods plus minimal MCP plumbing, or colocate interceptors with a backend—still two concerns: **governance primitives** vs **agent-facing capabilities**.
- **Validators** return pass/fail with severity and messages. **Mutators** return possibly modified payloads. **Sinks** are observe-only and non-blocking; the C# SDK treats **`sink`** as a first-class `InterceptorType`.
- Interceptors attach to **lifecycle events** (e.g. `tools/call`, `resources/read`, `prompts/get`, `llm/completion`) and a **phase**. The wire vocabulary for `hooks[].phase` is exactly the SEP's — `request` | `response` — and this SDK's `InterceptorPhase` matches it. **`both` is never a wire value:** it exists only as an authoring convenience (`InterceptorPhaseOption` on `buildInterceptorDescriptor`, also the default when `phase` is omitted), and is expanded into two hook entries — one `request`, one `response` — before the interceptor is advertised. The C# attribute layer does the same with `InterceptorPhase.Both`.

### 2.2 Chain execution

- **Chain execution** calls **`interceptors/list`** on one or more interceptor hosts, then **`interceptor/invoke`** on the host that registered each interceptor, following the SEP trust-boundary-aware ordering:
  - **Request (sending):** mutations (sequential by ascending `priorityHint`, name tie-break) → validations (parallel) → sinks (fire-and-forget).
  - **Response (receiving):** validations (parallel) → sinks (fire-and-forget) → mutations (sequential).
- **`mode`:** `active` vs `audit` (shadow validation / mutation). **`failOpen`:** whether failures allow the message to proceed (per SEP rules).
- **Overrides (capability vs policy):** each chain entry may carry invoker-declared `InterceptorOverrides` (`failOpen`, `priorityHint`, `mode`, `timeoutMs`, hook narrowing) that take precedence over the interceptor's declared defaults. Override hooks may only narrow declared hooks; widening is rejected with `InterceptorOverrideHookError` per the SEP MUST. Pass overrides per entry to `executeInterceptorChain`, or keyed by interceptor name via `executeInterceptorChainOnClients` / `InterceptorChainRunner` options. A per-entry `timeoutMs` cancels just that invoke and routes through resolved `failOpen`; the chain-aggregate `timeoutMs` on `ExecuteChainRequestParams` cancels the whole chain with status `timeout`.

If the SEP text conflicts with itself, follow **normative** sections of [`docs/sep.md`](../../docs/sep.md) for wire methods and payloads. Where the SEP is silent or ambiguous, match behavior of the **C# reference** (e.g. **`interceptors/list`**, not `interceptor/list`).

---

## 3. Wire protocol and capabilities

### 3.1 JSON-RPC methods

| Method | Params | Result |
|--------|--------|--------|
| `interceptors/list` | Optional `{ event?: string }` | `{ interceptors: Interceptor[] }` |
| `interceptor/invoke` | `name`, `event`, `phase`, `payload`, optional `config`, `context`, `timeoutMs` | Polymorphic result: `validation` \| `mutation` \| `sink` |

### 3.2 Interceptor host capability (`initialize`)

Per the SEP (SEP-2133 extensions format), interceptor hosts include:

```json
{
  "capabilities": {
    "extensions": {
      "io.modelcontextprotocol/interceptors": {
        "supportedEvents": ["tools/call", "..."]
      }
    }
  }
}
```

The key is exported as **`InterceptorExtensionCapabilityKey`**. The C# Interceptor SDK advertises the same shape under the same key, so mixed deployments interoperate without dual-read logic.

**Discovery with the stock `@modelcontextprotocol/client` Client:** The server should set capability via `registerCapabilities` (see §5). The stock **`Client`** parses `initialize` with `ServerCapabilitiesSchema`, which includes a typed **`extensions`** record, so **`getServerCapabilities().extensions["io.modelcontextprotocol/interceptors"]`** survives parsing and is a reliable discovery path. **`interceptors/list`** (or handling a standard JSON-RPC error when unsupported) remains a valid fallback.

---

## 4. Reference material in this repository

### 4.1 C# Interceptor SDK

Primary behavioral reference for parity:

| Area | C# concept |
|------|------------|
| Wire methods | `interceptors/list`, `interceptor/invoke` |
| Protocol DTOs | `Protocol/*` — descriptors, invoke/chain params, polymorphic `InterceptorResult`, events, phases, LLM payloads |
| Client | `McpClientInterceptorExtensions`, `InterceptorChainOrchestrator`, `InterceptingMcpClient` |
| Server | `InterceptorMessageFilter`, `McpServerInterceptorBuilderExtensions`, `ReflectionMcpServerInterceptor` |
| Gateway | `McpInterceptorGateway`, `InterceptorChainRunner`, transparent proxy + optional SEP passthrough |
| Init capability | `Extensions["io.modelcontextprotocol/interceptors"]` (SEP-2133 extensions format, not SEP’s top-level `interceptor` field) |

TypeScript uses **`Server.setRequestHandler`** for extension methods where C# uses incoming **message filters**, because the TypeScript SDK exposes handler registration publicly.

### 4.2 TypeScript package today

The SDK is **implemented** end-to-end: protocol types, client extensions and chain orchestration (including multi-host merge), interceptor host registration, reflection helpers, transparent gateway, runnable examples, and package **README**. **103 Vitest tests**; `npm run build`, `npm test`, and `npm run lint` are green.

Build: `tsc -p tsconfig.build.json` → `dist/`; lint uses `tsconfig.eslint.json` (includes test files).

**Optional / deferred:** golden JSON protocol fixtures vs C# `ProtocolTypesSerializationTests.cs`; full C# gateway test matrix parity (~33 cases in C#; TypeScript gateway integration has 13 cases in `mcp-interceptor-gateway.test.ts`).

### 4.3 Known gaps vs C# (intentional or subset)

| Area | C# | TypeScript today |
|------|-----|------------------|
| Server registration | `InterceptorMessageFilter` on incoming messages | `Server.setRequestHandler` for extension methods (§4.1) |
| Builder / host helpers | `IMcpServerBuilder`, filter pipeline | `registerInterceptorsOnServer` only (no separate `interceptor-host.ts` helper) |
| `InterceptingMcpClient` tests | Broad gateway-overlap scenarios | One E2E: `tools/call` request mutation; API covers list/prompts/resources/subscribe |
| `McpInterceptorGateway` | ASP.NET `WithInterceptorGateway` builder extensions | `createAsync`, `interceptorServerConnections`, `interceptorServerConnectionResolver`, `dispose` (no DI builder) |
| Gateway tests | `McpInterceptorGatewayTests` + `GatewayComponentsTests` | 13 gateway integration tests (subset of full C# matrix) |
| Validation over transparent proxy | In-process exception types | JSON-RPC `ProtocolError` to connecting clients (not `McpInterceptorValidationException`) |
| `serverInfo` override | `McpServerOptions.ServerInfo` | `McpInterceptorGatewayOptions.serverInfo` documented; `Server` identity is fixed at `new Server(...)` construction |
| `GatewayChainSample` | Two stdio interceptor clients + nested `InterceptingMcpClient` | `examples/gateway-chain` is a **simplified** walkthrough; use `McpInterceptorGateway` with `interceptorClients: [first, second]` for ordered multi-host chains |
| LLM completion | Protocol + samples | `LlmCompletion*` **types** only; no `llm/completion` client/gateway wiring |
| Examples packaging | Per-sample `.csproj` | `interceptor-server` and `interceptor-client` have `package.json`; other examples are single `src/index.ts` + root `npm run example:*` |

### 4.3.1 Differences from the C# SDK (intentional)

#### Interceptor `mode`: `active` (SEP / TypeScript / C#)

| | Wire / API value | Meaning |
|---|------------------|--------|
| **SEP-2624** | `active` \| `audit` | `active` = normal blocking and mutation application; `audit` = shadow / non-blocking |
| **C# Interceptor SDK** | `active` \| `audit` | `InterceptorMode.Active` serializes as `"active"` |
| **TypeScript SDK** | `active` \| `audit` | Matches the SEP and C# on the wire and in `InterceptorMode` |

**Interop:** Earlier drafts of this SDK used `enforce` as the canonical value; Zod still accepts **`mode: "enforce"`** on read and normalizes it to **`active`** before chain execution. New TypeScript hosts and samples should emit **`active`** or omit `mode` (orchestrator treats omitted as active). Do not emit `enforce` from TypeScript servers.

#### `priorityHint` per phase (SEP) vs scalar only (C#)

| | `priorityHint` shape | Mutation ordering |
|---|----------------------|-------------------|
| **SEP-2624** | `number` **or** `{ request?: number; response?: number }` | `resolvePriority(interceptor, phase)`; missing side → `0`; validations ignore priority |
| **C# Interceptor SDK** | `int?` only | `.OrderBy(i => i.PriorityHint ?? 0)` — same value for both phases |
| **TypeScript SDK** | `PriorityHint` union + Zod parse | `resolvePriority()` in `chain-orchestrator` when sorting mutations |

**Why TypeScript implements the object form:** The SEP allows different mutation order on request vs response (e.g. redact early on request, sanitize late on response). Scalar `priorityHint` still works unchanged. **`resolvePriority`** is exported from the package for hosts/tools that need the same rule.

**C# team:** Add a `PriorityHint` DTO or `JsonElement` on `Interceptor`, implement the same `resolvePriority` in `InterceptorChainOrchestrator`, and optionally extend `McpServerInterceptorAttribute` if reflection should set per-phase values.

### 4.4 MCP TypeScript SDK v2 (`typescript-sdk`) as structural reference

Sibling repository **`../typescript-sdk`** (official MCP TypeScript SDK **v2** on **main**). Use it only for **conventions**, not as a runtime dependency.

Relevant patterns to mirror:

- **Modules:** `protocol`-like types in `src/protocol`; client patterns in `src/client`; server patterns in `src/server`; gateway in `src/gateway`.
- **Public API:** Named exports only from the package entry; no wildcard re-exports; new exports are API commitments (see `packages/client/src/index.ts` and `packages/server/src/index.ts`).
- **Custom methods (v2 shape):** `setRequestHandler('method', { params, result }, handler)` on `Protocol` / `Server`—the form this package uses for `interceptors/list` and `interceptor/invoke`.
- **Tooling reference:** Vitest workspace, TypeScript 5.9.x, ESLint 9—upgrade this package’s Vitest only when there is clear benefit.

Upstream publishes **multiple** packages (`@modelcontextprotocol/client`, `@modelcontextprotocol/server`, `@modelcontextprotocol/core`, private `@modelcontextprotocol/core-internal`). This interceptor package stays **one** artifact with v2-**shaped** folders inside `src/`.

---

## 5. Integration with the MCP TypeScript SDK v2 packages

Pinned baseline for design decisions: **v2.0.0** (`@modelcontextprotocol/client` / `@modelcontextprotocol/server` / `@modelcontextprotocol/core`).

### 5.1 Surfaces used

| Concern | v2 API | Interceptor usage |
|---------|--------|-------------------|
| Client | `Client` from `@modelcontextprotocol/client`, transports (`InMemoryTransport`, `@modelcontextprotocol/client/stdio`, HTTP as needed) | Extension requests; `InterceptingMcpClient` to backend + interceptor hosts |
| Interceptor host | `Server` from `@modelcontextprotocol/server` | `interceptors/list`, `interceptor/invoke`; capability merge on `initialize` |
| Types | `RequestSchema` / `ResultSchema` from `@modelcontextprotocol/core`; MCP tool/resource/prompt types from `@modelcontextprotocol/client` or `/server` | Payloads and Zod schemas for extension methods |
| Out of scope | — | JSON-RPC framing, session lifecycle, core MCP method dispatch |

Stdio transports live on the `./stdio` subpaths (`@modelcontextprotocol/client/stdio`, `@modelcontextprotocol/server/stdio`); the package roots stay runtime-neutral. `InMemoryTransport` is exported from both packages — both halves of a linked pair must come from the same package's import.

### 5.2 Registering extension methods on the server

v2 **`Server.setRequestHandler`** registers custom (non-spec) methods with the three-argument `(method, { params, result? }, handler)` form; the handler receives the **parsed params**, not the request envelope.

```ts
import * as z from 'zod/v4';
import { Server } from '@modelcontextprotocol/server';

const InterceptorsListParamsSchema = z
  .object({ event: z.string().optional() })
  .optional();

server.setRequestHandler(
  'interceptors/list',
  { params: InterceptorsListParamsSchema },
  async (params) => {
    return { interceptors: [] };
  },
);
```

Apply the same pattern for **`interceptor/invoke`** with a params schema aligned to SEP and `src/protocol` types (`ListInterceptorsParamsSchema` / `InvokeInterceptorParamsSchema` in `src/protocol/zod-schemas.ts`).

**Capability checks:** custom method names are outside the spec-method capability switch and require **no** extra capability flag for handler registration to succeed.

Implement registration in **`src/server/register-interceptors.ts`** only.

### 5.3 Advertising the interceptors extensions capability

```ts
import type { ServerCapabilities } from '@modelcontextprotocol/server';
import { InterceptorExtensionCapabilityKey } from 'mcp-ext-interceptors';

server.registerCapabilities({
  extensions: {
    [InterceptorExtensionCapabilityKey]: { supportedEvents: ['tools/call'] },
  },
} as ServerCapabilities);
```

`mergeCapabilities` shallow-merges into internal server state (the `extensions` record merges key-wise, so other extensions are preserved); **`initialize`** returns `getCapabilities()` unchanged, so the capability appears on the wire. Confine the `ServerCapabilities` assertion to **`src/server/capabilities.ts`**.

Do **not** advertise a top-level `capabilities.interceptor`—earlier drafts used that key, but it is not the SEP shape and the stock Client strips it during `initialize` parsing.

### 5.4 Client extension requests

```ts
await client.request(
  { method: 'interceptors/list', params: {} },
  InterceptorsListResultSchema,
);
```

Implement in **`src/client/client-extensions.ts`**. Deserialize into types from **`src/protocol`**.

### 5.5 v2 migration notes (completed)

The package originally targeted the v1 monolith (`@modelcontextprotocol/sdk` ^1.x) and was migrated to the v2 packages. Decisions worth knowing when reviewing the code:

1. Custom method registration uses the v2 3-arg `setRequestHandler(method, { params }, handler)` form; result schemas are deliberately **not** passed so the server does not re-validate (or transform) handler output — matching the v1 behavior.
2. Client-side custom requests keep an **explicit result schema** (`client.request(req, schema, options)`), which v2 still supports for non-spec methods.
3. The gateway forwards paginated list methods via `client.request(...)` instead of the typed list verbs, because the v2 verbs auto-aggregate every page and the proxy must forward the caller's page verbatim.
4. `InterceptingMcpClient.callTool` uses `client.request(..., CompatibilityCallToolResultSchema, ...)` since v2 `callTool()` no longer accepts a result schema and this client keeps v1's tolerant legacy-result parsing.
5. `McpError` / `ErrorCode` are now `ProtocolError` / `ProtocolErrorCode`. Cross-package error matching (client vs server bundles) uses `code`-field checks, not `instanceof` (see `isInvalidParamsError`).

**Unchanged across the migration:** `src/protocol/*` DTOs, `src/client/chain-orchestrator.ts`, gateway orchestration design, SEP ordering semantics.

---

## 6. Package layout

Single `package.json`, single published `"."` export (optional subpath exports only with deliberate `package.json` change):

```text
src/
  index.ts                      # public barrel (named exports only)
  protocol/
    constants.ts, types.ts, results.ts, zod-schemas.ts, errors.ts, llm-payload.ts
  client/
    client-extensions.ts        # list / invoke / executeChainOnClient
    chain-orchestrator.ts       # SEP chain ordering (no MCP SDK imports)
    interceptor-chain-runner.ts           # multi-host chain runner (client + gateway)
    execute-interceptor-chain-on-clients.ts
    merge-interceptor-chain-entries.ts
    intercepting-client.ts                # InterceptingMcpClient
  server/
    register-interceptors.ts
    capabilities.ts
    interceptor-definition.ts
    reflection.ts               # defineInterceptor
  gateway/
    mcp-interceptor-gateway.ts
    gateway-proxy-configurator.ts
    gateway-protocol-bridge.ts  # optional exposeInterceptorProtocol
    proxy-request.ts
  __tests__/
    fixtures/hosts.ts           # connectInterceptorHost, connectEchoBackend
    integration/                # client-extensions, intercepting-client, mcp-interceptor-gateway
    server-wiring.test.ts
```

Cross-language naming:

| C# (`ExecuteChainAsync`) | TypeScript |
|--------------------------|------------|
| Orchestrator only | `executeInterceptorChain(interceptors, invoker, params, signal?)` |
| `McpClient` + list + invoke | `executeInterceptorChainOnClient(client, params, signal?)` |

Also: `listInterceptors`, `invokeInterceptor`.

**Tests** (Vitest; co-located with sources where practical):

- Unit: `src/protocol/protocol.test.ts`, `src/client/chain-orchestrator.test.ts`, `src/server/reflection.test.ts`, `src/server/register-interceptors.test.ts`
- Integration / E2E: `src/__tests__/integration/*.test.ts`, `src/__tests__/server-wiring.test.ts`
- Shared fixtures: `src/__tests__/fixtures/hosts.ts` (`connectInterceptorHost`, `connectEchoBackend`)

Optional future layout: `__tests__/protocol/` golden JSON; rename fixtures to `buildInterceptorHost` / `buildTestBackend` aliases.

**Examples** (under `examples/`, not published in npm `"files"`; see §10):

```text
examples/
  interceptor-server/     # package.json; ↔ InterceptorServerSample
  interceptor-client/     # package.json; ↔ InterceptorClientSample
  gateway/src/            # ↔ GatewaySample (InterceptingMcpClient)
  transparent-proxy/src/  # ↔ TransparentProxySample (McpInterceptorGateway stdio)
  gateway-chain/src/      # ↔ GatewayChainSample (simplified; see §4.3)
```

All runnable examples import `../../../dist/index.js` after `npm run build`. Root `package.json` scripts: `example:interceptor-server`, `example:interceptor-client`, `example:gateway`, `example:transparent-proxy`, `example:gateway-chain` (uses `tsx`).

---

## 7. Public API

### 7.1 Protocol

- Types and constants aligned with SEP and C# `Protocol/`.
- **`InterceptorResult`:** discriminated union on `type: "validation" | "mutation" | "sink"` with safe parsing from JSON.

### 7.2 Client

- **`listInterceptors(client, params?)`**, **`invokeInterceptor(client, params)`** — wire calls on MCP `Client`.
- **`executeInterceptorChain(interceptors, invoker, params, signal?)`** — pure orchestrator; `invoker` typically calls `interceptor/invoke`.
- **`executeInterceptorChainOnClient(client, params, signal?)`** — discovers via `interceptors/list`, then orchestrates invokes (C# `ExecuteChainAsync`).
- **`executeInterceptorChainOnClients(clients, params, signal?)`** — multi-host list, merge, and chain (SEP merge semantics).
- **`InterceptingMcpClient`** wrapping backend + interceptor clients; same operation set as C# where applicable: `callTool`, `listTools`, `listPrompts`, `getPrompt`, `listResources`, `readResource`, `subscribeResource`, `listInterceptors`.

### 7.3 Interceptor host (server-side)

- Build an **interceptor host** using the MCP SDK’s `Server` / `McpServer`—a real MCP protocol endpoint, typically **not** the same process or role as the application backend that serves tools/resources.
- In-process interceptor registry (name → handler).
- **`registerInterceptorsOnServer(server, interceptors, options?)`**: installs wire handlers on that host, merges the interceptors extensions capability from registered hooks’ events.

### 7.4 Gateway

- **`McpInterceptorGateway`** — transparent MCP proxy: MCP **server** toward clients, MCP **client(s)** toward the **application backend** and **interceptor host(s)**.
- **`configureServer(server)`** — mirror backend capabilities; register proxy handlers (tools, prompts, resources, completions/logging passthrough). Call **before** `server.connect()`.
- **`registerNotificationForwarding(proxyServer)`** — forward backend `list_changed` notifications when advertised.
- **`exposeInterceptorProtocol`** — optional aggregated `interceptors/list` / `interceptor/invoke` on the proxy via `GatewayInterceptorProtocolBridge`.
- Reuses **`InterceptorChainRunner`** (`executeInterceptorChainOnClients` with merged chain).

Callers supply **already-connected** `Client` instances (stdio spawn is sample responsibility; see §10).

---

## 8. Parity with C# SDK

| Capability | C# | TypeScript | Notes |
|------------|-----|------------|-------|
| `interceptors/list` | Yes | Yes | |
| `interceptor/invoke` | Yes | Yes | |
| Polymorphic results + JSON round-trip | Yes | Yes | Golden JSON vs C# deferred |
| Chain semantics (order, audit, failOpen, timeout) | Yes | Yes | |
| Multi-host chain merge | Yes (see §11) | Yes | `executeInterceptorChainOnClients` |
| Client list / invoke / executeChain | Yes | Yes | |
| Extensions capability on initialize (SEP) | Yes (`extensions["io.modelcontextprotocol/interceptors"]`) | Yes | Same wire shape in both SDKs |
| `InterceptingMcpClient` operations | Yes | Yes | API parity; E2E tests mainly `tools/call` |
| Server registration ergonomics | Yes (`IMcpServerBuilder`) | Yes | `setRequestHandler`, not message filter |
| Reflection-style interceptors | Yes | Yes | `defineInterceptor` |
| LLM completion payload types | Yes (protocol) | Yes | Types only; no live `llm/completion` wiring |
| Transparent gateway + optional SEP exposure | Yes | Yes | Subset of C# gateway tests (§4.3) |
| Runnable examples (core set, §10) | Yes (`samples/`) | Yes | `gateway-chain` simplified vs C# |

---

## 9. Testing

Testing mirrors the C# Interceptor SDK test project: every shipped module has targeted tests, shared fixtures avoid copy-paste host setup, and **integration** tests use **`InMemoryTransport`** (or equivalent) for real JSON-RPC sessions—not only isolated pure functions.

### 9.1 Layers

| Layer | Transport / MCP session | Purpose |
|-------|-------------------------|---------|
| **Unit** | None | Pure logic: protocol parsing, chain ordering, capability merge helpers, registry mapping, result discrimination. Fast; no `Client`/`Server` lifecycle unless a one-line stub is unavoidable. |
| **Integration** | `InMemoryTransport` (paired client + server) | Wire handlers, `initialize` + capabilities on the wire, `listInterceptors` / `invokeInterceptor` against a real interceptor host built with this SDK. |
| **End-to-end (within Vitest)** | Multiple transports or gateway wiring | Full paths the product cares about: e.g. `InterceptingMcpClient` → interceptor host(s) → stub **backend**; gateway proxy forwarding and chain injection. Still in-process; not a separate test runner. |

**End-to-end** = multi-role flows (client + host + backend). **Integration** = client ↔ single host.

### 9.2 Shared fixtures (`src/__tests__/fixtures/hosts.ts`)

- **`connectInterceptorHost(interceptors)`** — in-memory interceptor host via `registerInterceptorsOnServer`; returns `{ client, server, close }`.
- **`connectEchoBackend()`** — minimal backend with `tools/list` + `tools/call` echo; exposes `lastCall` for assertions.
- **Sample interceptors** — defined inline in tests (validator / mutator / sink) with predictable names and return shapes.
- **Golden JSON** — not implemented; deferred (optional alignment with C# `ProtocolTypesSerializationTests.cs`).

Integration and gateway tests use these helpers so registration and capability setup stay consistent.

### 9.3 Coverage by module

| Module / API | Unit | Integration / E2E | C# reference |
|--------------|------|---------------------|--------------|
| `protocol/` (types, zod, `InterceptorResult` parsers) | Round-trip and omit-null JSON; enum/string wire shapes | — | `ProtocolTypesSerializationTests.cs` |
| `client/chain-orchestrator.ts` | Ordering, parallel validation, audit, failOpen, timeout, abort; fake invoker (no MCP) | Chain invoked via real `invokeInterceptor` callbacks in integration tests | `InterceptorChainOrchestratorTests.cs` |
| `client/client-extensions.ts` | — | `listInterceptors` / `invokeInterceptor` / `executeInterceptorChainOnClient` against fixture host | (orchestrator + client extensions) |
| `client/intercepting-client.ts` | — | E2E: `tools/call` request mutation reaches backend (expand to other operations optional) | `McpInterceptorGatewayTests.cs` (overlapping scenarios) |
| `server/register-interceptors.ts` | Registry → handler dispatch, error paths | `interceptors/list` filter by `event`; `interceptor/invoke` returns correct polymorphic result | — |
| `server/capabilities.ts` | `supportedEvents` derived from registered hooks | `initialize` / `getCapabilities()` includes the extensions capability on wire | — |
| `server/reflection.ts` | Metadata extraction, invalid registration | Invoke reflected handler over transport | `ReflectionMcpServerInterceptorTests.cs` |
| `client/execute-interceptor-chain-on-clients.ts` | Merge, duplicate-name policy | Multi-host priority and routing | — |
| `gateway/` | `proxy-request.ts` chain wrapper | 13 tests in `mcp-interceptor-gateway.test.ts` | Subset of `GatewayComponentsTests.cs`, `McpInterceptorGatewayTests.cs` |

### 9.4 Minimum scenarios (not exhaustive)

**Protocol:** descriptor and invoke/chain param round-trips; validation / mutation / sink result unions; list result shape.

**Client:** list returns registered interceptors; invoke returns each result type; executeChain applies order and aggregates failures; extensions send correct JSON-RPC method names.

**Server:** host advertises the interceptors extensions capability; list respects optional `event` filter; invoke dispatches to the right handler; unknown name / bad phase errors.

**Integration (client ↔ host):** connect with `InMemoryTransport`; full list + invoke for at least one validator and one mutator.

**End-to-end:** `InterceptingMcpClient` with fixture backend + host—covered for `tools/call`; other wrapped operations are API-complete but not all covered by dedicated E2E tests yet.

**Gateway:** `tools/list` and `tools/call` forwarding, chain mutation, validation abort (as `ProtocolError` over JSON-RPC), `exposeInterceptorProtocol` list aggregation, multi-host merge scenarios.

**Current total:** 73 Vitest tests. CI runs all tests on every change.

---

## 10. Examples (samples)

Runnable examples mirror the **C# Interceptor SDK** layout in [`csharp/sdk/samples`](../../csharp/sdk/samples) and the walkthroughs in [`csharp/sdk/README.md`](../../csharp/sdk/README.md). They teach the same deployment patterns (interceptor host, direct client API, gateway, transparent proxy, chained gateways). **Scenario parity** with C# is the goal—not line-by-line ports.

### 10.1 Principles

- **Not published:** Examples live under `typescript/sdk/examples/` and are not included in the npm package `"files"` / `"exports"` for `mcp-ext-interceptors`. `interceptor-server` and `interceptor-client` include `"private": true` `package.json` files; `gateway`, `transparent-proxy`, and `gateway-chain` are single-entry scripts run from the SDK root.
- **Complement tests:** Vitest fixtures and integration tests (§9) remain the regression source of truth. Examples are copy-paste-friendly docs for humans; reuse the same interceptor names and behaviors as `src/__tests__/fixtures/` where practical.
- **Complement README:** Package `README.md` keeps short snippets; examples show **stdio spawn** wiring like the C# samples (parent process launches child; JSON-RPC over the child’s stdin/stdout).
- **C# reference column:** When implementing an example, read the matching C# `Program.cs` and treat it as the behavioral spec.

### 10.2 Implemented examples

| TypeScript example | C# sample | Script | What it demonstrates |
|--------------------|-----------|--------|----------------------|
| `examples/interceptor-server/` | `InterceptorServerSample` | `example:interceptor-server` | Stdio **interceptor host** (PII validator, email redactor, request-logger sink) |
| `examples/interceptor-client/` | `InterceptorClientSample` | `example:interceptor-client` | **Client** API; spawns interceptor-server via `StdioClientTransport` |
| `examples/gateway/` | `GatewaySample` | `example:gateway` | `InterceptingMcpClient` → interceptor host → `@modelcontextprotocol/server-everything` |
| `examples/transparent-proxy/` | `TransparentProxySample` | `example:transparent-proxy` | Stdio **`McpInterceptorGateway`**; parent spawns backend + interceptor host |
| `examples/gateway-chain/` | `GatewayChainSample` | `example:gateway-chain` | **Simplified:** documents multi-host ordering via `interceptorClients: [first, second]` on `McpInterceptorGateway` (C# runs two stdio interceptor processes + nested `InterceptingMcpClient`) |

### 10.3 Out of scope

| C# sample | Decision |
|-----------|----------|
| `AvatarMoodInterceptorSample` | **Not planned.** Pedagogy for `llm/completion` **sink** interceptors with live Anthropic calls and console UI—not MCP wiring. A TS port would add API keys, network, and non-CI dependencies without teaching the SDK’s core client/host/gateway paths. Sink behavior is covered in tests; optional tiny in-process sink demo only if needed later. |
| `ConfigDrivenGatewaySample` | **Optional.** C# treats `mcp-interceptors.json` as **sample-only** config, not a library format. Add a TS example only if we want the same “compose gateway from JSON” illustration; not required for parity with the five core samples above. |

### 10.4 Stdio transport (match C#)

| Role | Who spawns whom | Transport |
|------|-----------------|-----------|
| **Interceptor host** | Spawned as child | Stdio server (`WithStdioServerTransport` / equivalent). |
| **Client / gateway sample** | Parent process | `StdioClientTransport` with `command` + `args` (e.g. `node` / `tsx` + path to `interceptor-server`), same as C# `dotnet run --project …`. |
| **Transparent proxy** | Host app spawns proxy; proxy spawns peers | Stdio server toward host; stdio clients toward backend and interceptor host(s). |

**ConfigDrivenGateway** (C#, optional for TS): outbound legs may use Streamable HTTP; gateway exposes stdio to the connecting host.

### 10.5 Implementation notes

- **Dependencies:** Examples depend on `mcp-ext-interceptors` (workspace/`file:`), the v2 MCP packages (`@modelcontextprotocol/client` / `@modelcontextprotocol/server`), and Node stdio transports—no example-only dependency on the C# SDK.
- **Scripts:** Root `package.json` exposes `npm run example:*` for all five samples; spawning examples embed child `command`/`args` like C#.
- **Shared interceptors:** Prefer importing or duplicating minimal handler definitions from test fixtures so examples and tests do not diverge.
- **Gateway examples:** Spawn `interceptor-server` and stub backend via stdio client transport inside the gateway/proxy process—the same as C# `StdioClientTransport` + `dotnet run --project …`.

---

## 11. Multi-host chain merge (C# port notes)

This section documents multi-host chain behavior in the TypeScript SDK so the **C# Interceptor SDK** team can implement the same pattern if desired. It is not a breaking change to the wire protocol; it aligns client-side chain utilities with **SEP-2624 chain execution** ([`docs/sep.md`](../../docs/sep.md) § Chain Execution).

### 11.1 Problem

The SEP defines chain orchestration across **N MCP servers**:

1. **Discover** — `interceptors/list` on one or more servers  
2. **Merge & sort** — one combined chain (`priorityHint` ascending, name tie-break for mutations)  
3. **Order by trust boundary** — request: mutations → validations → sinks; response: validations → sinks → mutations  
4. **Execute** — `interceptor/invoke` on the **host that owns** each interceptor  

Both reference SDKs already implement step 3–4 for a **single flat interceptor list** (`InterceptorChainOrchestrator` / `executeInterceptorChain`).  

Previously, **multi-host** callers used `InterceptorChainRunner`, which ran a **full** `ExecuteChainAsync` **per host in series**. That preserves tiered pipelines but does **not** merge mutations globally by `priorityHint` across hosts (e.g. a mutator at `-1000` on host B must run before a mutator at `0` on host A per the SEP).

### 11.2 TypeScript implementation

| Piece | Role |
|-------|------|
| `executeInterceptorChain` | Unchanged — SEP execution model for one descriptor list + `invoker` |
| `executeInterceptorChainOnClient` | Unchanged surface — delegates to multi-host helper with one client |
| **`executeInterceptorChainOnClients`** | **New** — list each host → merge entries → `executeInterceptorChain` with routed `invoke` |
| `listInterceptorChainEntries` / `mergeInterceptorChainEntries` | **New** — discover + duplicate-name policy |
| `InterceptorChainRunner` | **Updated** — uses `executeInterceptorChainOnClients` instead of per-host full chains |

**Duplicate interceptor names across hosts**

- Default: **`duplicateNamePolicy: 'error'`** — throw `DuplicateInterceptorNameError` listing name and host labels (invoke routing is ambiguous because `interceptor/invoke` only carries `name`).  
- Optional: **`'first-wins'`** — keep first entry in host array order (documented for tests / explicit tiering only).  
- Deployment guidance: use **globally unique** interceptor names when using merged chains.
- Gateway SEP passthrough (`exposeInterceptorProtocol`): `interceptors/list` aggregates every host verbatim; `interceptor/invoke` rejects a name held by more than one host with `InvalidParams`, since the request carries only the name. Names unique across hosts route normally.

### 11.3 SEP compliance

- Normative chain steps 1–5 are implemented in the **multi-host entry point**; the orchestrator still enforces type/phase semantics (mutations sequential by `priorityHint`, validations parallel, sinks non-blocking).  
- No new JSON-RPC methods; still `interceptors/list` + `interceptor/invoke` per owning server.  
- `ChainEntry.server` in the SEP maps to `InterceptorChainEntry.client` (implementation-specific connection type).

**Not changed:** chain `config` map forwarding, or wildcard event matching — same gaps as before (§4.3). Per-phase `priorityHint` is implemented in TypeScript (§4.3.1); C# still scalar-only.

### 11.4 Suggested C# port

1. Add `InterceptorChainEntry` (descriptor + `McpClient` + host label) and `ExecuteChainOnClientsAsync(IReadOnlyList<InterceptorChainHost> hosts, ExecuteChainRequestParams request, …)`.  
2. Implement merge + `DuplicateInterceptorNameException` (default throw on duplicate `Name`).  
3. Call existing `InterceptorChainOrchestrator.ExecuteAsync` with an invoker that dispatches `InvokeInterceptorAsync` to the correct client.  
4. Switch `InterceptorChainRunner` to use the new API (or document runner as “sequential per-host” if retaining old behavior behind a flag).  
5. Add tests: global `PriorityHint` across two hosts; duplicate name error; optional `first-wins`.

### 11.5 When to keep sequential per-host chains

If product intent is **tiered hosts** (“always run security host chain, then logging host chain”) rather than **one global mutation order**, expose that as an explicit option (e.g. `ChainMergeMode.Merged` vs `PerHostSequential`) rather than overloading merge semantics. The SEP default for the chain **utility** is merge; tiered ordering is a deployment pattern clients can opt into.
