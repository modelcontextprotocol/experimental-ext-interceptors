# MCP Interceptors TypeScript SDK

TypeScript implementation of [SEP-2624](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624) — gateway-level interceptors for the [Model Context Protocol](https://modelcontextprotocol.io/).

Requires the **MCP TypeScript SDK v2 packages** (`@modelcontextprotocol/client`, `@modelcontextprotocol/server`) as peer dependencies.

## Installation

```bash
npm install mcp-ext-interceptors @modelcontextprotocol/client @modelcontextprotocol/server
```

## Overview

```
Client  ──▶  Interceptor host  ──▶  Application MCP server
        ◀──  (validate/mutate)  ◀──  (tools, resources, …)
```

An **interceptor host** is a normal MCP server that exposes `interceptors/list` and `interceptor/invoke`. It is not the same role as your tools/resources **backend** server.

## Quick start — interceptor host (stdio)

```typescript
import { Server } from '@modelcontextprotocol/server';
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';
import {
  registerInterceptorsOnServer,
  InterceptionEvents,
  validationSuccess,
  type RegisteredInterceptor,
} from 'mcp-ext-interceptors';

const interceptors: RegisteredInterceptor[] = [
  {
    descriptor: {
      name: 'pii-validator',
      type: 'validation',
      hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
    },
    handler: () => validationSuccess('request'),
  },
];

const server = new Server(
  { name: 'my-interceptor-host', version: '1.0.0' },
  { capabilities: {} },
);
registerInterceptorsOnServer(server, interceptors);

await server.connect(new StdioServerTransport());
```

Or use **`defineInterceptor`** for a C#-style handler definition:

```typescript
import { defineInterceptor, InterceptionEvents } from 'mcp-ext-interceptors';

const entry = defineInterceptor(
  {
    name: 'email-redactor',
    type: 'mutation',
    events: [InterceptionEvents.ToolsCall],
    phase: 'request',
    priorityHint: -1000,
  },
  (payload) => ({
    type: 'mutation',
    phase: 'request',
    modified: true,
    payload: redactEmails(payload),
  }),
);
```

`defineInterceptor` binds handler parameters using `Function.prototype.toString` heuristics (not full reflection like C#). Prefer:

- `(params) => { … }` — `params` is the full invoke request (`payload`, `event`, `phase`, `context`, …)
- `({ payload, phase }) => { … }` — destructuring from the same object
- `(payload, event, phase, context, signal) =>` — positional by arity

Avoid `(...rest) =>` and relying on default parameters alone; use `(params) =>` instead.

## Quick start — client API

```typescript
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import {
  listInterceptors,
  invokeInterceptor,
  executeInterceptorChainOnClient,
} from 'mcp-ext-interceptors';

const client = new Client({ name: 'app', version: '1.0.0' }, { capabilities: {} });
await client.connect(
  new StdioClientTransport({
    command: 'npx',
    args: ['tsx', 'path/to/interceptor-server/src/index.ts'],
  }),
);

const listed = await listInterceptors(client);
const result = await invokeInterceptor(client, {
  name: 'pii-validator',
  event: 'tools/call',
  phase: 'request',
  payload: { name: 'my-tool', arguments: {} },
});

const chain = await executeInterceptorChainOnClient(client, {
  event: 'tools/call',
  phase: 'request',
  payload: { name: 'my-tool', arguments: { message: 'hello' } },
});
```

Chain execution is orchestrated in the SDK (`list` + ordered `invoke`); there is no `interceptor/executeChain` wire method.

## Quick start — InterceptingMcpClient

```typescript
import { InterceptingMcpClient } from 'mcp-ext-interceptors';

const gateway = new InterceptingMcpClient(backendClient, {
  interceptorClient: interceptorHostClient,
  events: ['tools/call'],
});

const result = await gateway.callTool('echo', { message: 'hello' });
```

## Quick start — transparent proxy (`McpInterceptorGateway`)

```typescript
import { Client } from '@modelcontextprotocol/client';
import { Server } from '@modelcontextprotocol/server';
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';
import { McpInterceptorGateway } from 'mcp-ext-interceptors';

const gateway = new McpInterceptorGateway({
  backendClient,
  interceptorClients: [interceptorHostClient],
  events: ['tools/call'],
});

const server = new Server({ name: 'interceptor-proxy', version: '1.0.0' }, { capabilities: {} });
gateway.configureServer(server); // before connect
gateway.registerNotificationForwarding(server);

await server.connect(new StdioServerTransport());
```

Connecting clients use the proxy as the backend; the parent process spawns interceptor and backend servers over stdio (same pattern as the C# `TransparentProxySample`).

## Capabilities

Interceptor hosts advertise SEP **`capabilities.extensions["io.modelcontextprotocol/interceptors"]`** with `supportedEvents` (merged automatically by `registerInterceptorsOnServer`; key exported as `InterceptorExtensionCapabilityKey`).

The C# SDK in this repository advertises the same key, so mixed TS/C# deployments interoperate directly.

## Examples

From `typescript/sdk` after `npm run build` (examples import `dist/` from the local build):

| Script | C# sample | Description |
|--------|-----------|-------------|
| `npm run example:interceptor-server` | `InterceptorServerSample` | Stdio interceptor host (PII validator, email redactor, logger sink) |
| `npm run example:interceptor-client` | `InterceptorClientSample` | Spawns the server via `StdioClientTransport`; list, invoke, chain |
| `npm run example:gateway` | `GatewaySample` | `InterceptingMcpClient` → interceptor host → everything server |
| `npm run example:transparent-proxy` | `TransparentProxySample` | Stdio transparent proxy (`McpInterceptorGateway`) |
| `npm run example:gateway-chain` | `GatewayChainSample` | Notes on multi-host ordering with `interceptorClients` |

The client sample spawns the server process and talks over its stdio pipes (same pattern as the C# `StdioClientTransport` + `dotnet run --project …`).

## Development

```bash
npm install
npm run build
npm test
npm run lint
```

## License

Apache-2.0 — see the repository [LICENSE](https://github.com/modelcontextprotocol/ext-interceptors/blob/main/LICENSE).
