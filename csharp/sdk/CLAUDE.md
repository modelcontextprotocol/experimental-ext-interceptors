# MCP Interceptors - C# SDK

## What this is
C# implementation of gateway-level interceptors from [SEP-1763](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763). NuGet package additive to the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) (v1.1.0). Focus is on the protocol-level extension (client → interceptor server → server), NOT in-process middleware.

## Build & test
```
dotnet build   # from csharp/sdk/
dotnet test    # 42 tests across 4 files
```

## Key architectural constraints

**Why message filter, not handlers**: `McpServerHandlers` and `McpServerImpl` are `internal` in the SDK. We can't register handlers for new JSON-RPC methods from outside. Instead we use `McpServerOptions.Filters.Message.IncomingFilters` to intercept `interceptors/list`, `interceptor/invoke`, `interceptor/executeChain`, handle them, send `JsonRpcResponse` via `context.Server.SendMessageAsync()`, and skip calling `next`. See `InterceptorMessageFilter.cs`.

**Why `ServerCapabilities.Extensions`**: The SDK's intended mechanism for protocol extensions. Requires `#pragma warning disable MCPEXP001`. We advertise `InterceptorsCapability { SupportedEvents }` under `Extensions["interceptors"]`.

**Client `SendRequestAsync`**: The public overload (`McpSession.Methods.cs:24`) takes `JsonSerializerOptions`. We pass `InterceptorJsonUtilities.DefaultOptions` which chains `McpJsonUtilities.DefaultOptions` + our `InterceptorJsonContext`. The internal overload takes `JsonTypeInfo<T>` — we can't use it.

**`InterceptingMcpClient` is composition**: `McpClient` has an internal constructor; subclassing is `[Experimental]`. We wrap it as a concrete class exposing `.Inner` for direct access.

## Chain execution order (SEP-1763)
- **Request phase (sending)**: Mutations (sequential by priority ↑) → Validations (parallel) → Observability (fire-and-forget)
- **Response phase (receiving)**: Validations (parallel) → Observability (fire-and-forget) → Mutations (sequential by priority ↑)
- Lower `PriorityHint` executes first; ties broken alphabetically by name

## JSON-RPC methods
| Method | Params → Result |
|--------|----------------|
| `interceptors/list` | `ListInterceptorsRequestParams` → `ListInterceptorsResult` |
| `interceptor/invoke` | `InvokeInterceptorRequestParams` → `InterceptorResult` (polymorphic) |
| `interceptor/executeChain` | `ExecuteChainRequestParams` → `InterceptorChainResult` |

## `InterceptorResult` polymorphism
Uses `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` with `"validation"`, `"mutation"`, `"observability"` discriminators. Serialization/deserialization handles this automatically via STJ source-gen in `InterceptorJsonContext`.

## Parameter binding (ReflectionMcpServerInterceptor)
Interceptor methods auto-bind from `InvokeInterceptorRequestParams`:
- `JsonNode payload` → `.Payload`
- `JsonNode config` → `.Config`
- `string event` / `string eventName` → `.Event`
- `InterceptorPhase phase` → `.Phase`
- `InvokeInterceptorContext` → `.Context`
- `CancellationToken`, `McpServer`, `IServiceProvider` → framework
- Return `bool` → wrapped as `ValidationInterceptorResult { Valid = result }`

## SDK reference paths (local at /mnt/d/code/ai/mcp/csharp-sdk)
- `src/ModelContextProtocol.Core/Server/McpServerTool.cs` — pattern we follow
- `src/ModelContextProtocol.Core/Server/McpMessageFilter.cs` — our hook point
- `src/ModelContextProtocol.Core/Protocol/ServerCapabilities.cs` — Extensions dict
- `src/ModelContextProtocol.Core/McpSession.Methods.cs` — public SendRequestAsync
- `src/ModelContextProtocol/McpServerBuilderExtensions.cs` — builder pattern
- `src/ModelContextProtocol.Core/McpJsonUtilities.cs` — JSON context chaining pattern

## Transparent gateway/proxy (`Gateway/`)

**`McpInterceptorGateway`**: Configures an `McpServer` as a transparent proxy. Reads backend `ServerCapabilities`, registers handler delegates (`CallToolHandler`, `ListToolsHandler`, etc.) that route through `InterceptorChainRunner` before forwarding to the backend. To connecting clients, the proxy appears to be the backend server.

**`InterceptorChainRunner`** (internal): Shared interception logic used by both `InterceptingMcpClient` and `McpInterceptorGateway`. Supports multiple interceptor clients executed sequentially — each client's `ExecuteChainAsync` receives the mutated payload from the previous one.

**`McpInterceptorGatewayOptions`**: Configuration — `BackendClient`, `InterceptorClients` (ordered), `Events` filter, `TimeoutMs`, `DefaultContext`, optional `ServerInfo` override.

**Builder extension**: `IMcpServerBuilder.WithInterceptorGateway(options)` for DI/builder scenarios.

**Notification forwarding**: `gateway.RegisterNotificationForwarding(proxyServer)` subscribes to backend `tools/list_changed`, `prompts/list_changed`, `resources/list_changed` and re-sends through the proxy.

**Why handler delegates (not message filters) for the proxy**: The SDK's `With*Handler` methods automatically set `ServerCapabilities`, are type-safe, and are the intended extension point. Message filters are still used for interceptor protocol passthrough (`interceptors/list`, `interceptor/invoke`, `interceptor/executeChain`).

**Tool call error handling note**: When an interceptor validation aborts a `tools/call`, the gateway throws `McpInterceptorValidationException`. The SDK catches this and returns `CallToolResult { IsError = true }` (not a JSON-RPC error), since tool execution errors are returned as results by design.

## `InterceptingMcpClient` wrapped methods
- `CallToolAsync` — `tools/call`
- `ListToolsAsync` — `tools/list`
- `ListPromptsAsync` — `prompts/list`
- `GetPromptAsync` — `prompts/get`
- `ListResourcesAsync` — `resources/list`
- `ReadResourceAsync` — `resources/read`
- `SubscribeToResourceAsync` — `resources/subscribe`
- `ListInterceptorsAsync` — direct passthrough to interceptor client

## LLM completion payloads (`Protocol/LlmCompletionPayload.cs`)
- `LlmCompletionRequestPayload` — model, messages, maxTokens, temperature, metadata
- `LlmCompletionResponsePayload` — model, message, stopReason, usage, metadata
- `LlmMessage` — role + content
- `LlmUsage` — inputTokens + outputTokens
- Registered in `InterceptorJsonContext` for source-gen serialization
- Not wired into `InterceptingMcpClient` — these are for custom gateway use

## Samples
- `InterceptorServerSample` — stdio server hosting 3 interceptors
- `GatewaySample` — single gateway: client → interceptor → everything server
- `InterceptorClientSample` — client API: list, invoke, execute chain directly
- `GatewayChainSample` — chained gateways: security layer → logging layer → server
- `TransparentProxySample` — stdio proxy server: clients connect to it as if it were the backend, all requests routed through interceptors
