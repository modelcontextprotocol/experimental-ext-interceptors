# MCP Interceptors - C# SDK

## What this is
C# implementation of gateway-level interceptors from [SEP-1763](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763). NuGet package additive to the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) (v1.1.0). Focus is on the protocol-level extension (client → interceptor server → server), NOT in-process middleware.

## Build & test
```
dotnet build   # from csharp/sdk/
dotnet test    # 66 tests across the interceptor test project
```

## Key architectural constraints

**Why message filter, not handlers**: `McpServerHandlers` and `McpServerImpl` are `internal` in the SDK. We can't register handlers for new JSON-RPC methods from outside. Instead we use `McpServerOptions.Filters.Message.IncomingFilters` to intercept `interceptors/list` and `interceptor/invoke`, handle them, send `JsonRpcResponse` via `context.Server.SendMessageAsync()`, and skip calling `next`. See `InterceptorMessageFilter.cs`. Chain execution is no longer a wire method — it's SDK orchestration in `Client/InterceptorChainOrchestrator.cs`.

**Why `ServerCapabilities.Extensions`**: The SDK's intended mechanism for protocol extensions. Requires `#pragma warning disable MCPEXP001`. We advertise `InterceptorsCapability { SupportedEvents }` under `Extensions["interceptors"]`.

**Client `SendRequestAsync`**: The public overload (`McpSession.Methods.cs:24`) takes `JsonSerializerOptions`. We pass `InterceptorJsonUtilities.DefaultOptions` which chains `McpJsonUtilities.DefaultOptions` + our `InterceptorJsonContext`. The internal overload takes `JsonTypeInfo<T>` — we can't use it.

**`InterceptingMcpClient` is composition**: `McpClient` has an internal constructor; subclassing is `[Experimental]`. We wrap it as a concrete class exposing `.Inner` for direct access.

## Chain execution order (SEP-1763)
- **Request phase (sending)**: Mutations (sequential by priority ↑) → Validations (parallel) → Sinks (fire-and-forget)
- **Response phase (receiving)**: Validations (parallel) → Sinks (fire-and-forget) → Mutations (sequential by priority ↑)
- Lower `PriorityHint` executes first; ties broken alphabetically by name

## JSON-RPC methods
| Method | Params → Result |
|--------|----------------|
| `interceptors/list` | `ListInterceptorsRequestParams` → `ListInterceptorsResult` |
| `interceptor/invoke` | `InvokeInterceptorRequestParams` → `InterceptorResult` (polymorphic) |

## `InterceptorResult` polymorphism
Uses `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` with `"validation"`, `"mutation"`, `"sink"` discriminators. Serialization/deserialization handles this automatically via STJ source-gen in `InterceptorJsonContext`.

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

**`McpInterceptorGateway`**: Configures an `McpServer` as a transparent proxy. Reads backend `ServerCapabilities`, registers handler delegates (`CallToolHandler`, `ListToolsHandler`, etc.) that route through interceptor chains before forwarding to the backend. By default the gateway is transparent-only; SEP passthrough is opt-in via `ExposeInterceptorProtocol = true`. To connecting clients, the proxy appears to be the backend server.

**`InterceptorChainRunner`** (internal): Shared interception logic used by both `InterceptingMcpClient` and `McpInterceptorGateway`. Supports multiple interceptor clients executed sequentially — each client's `ExecuteChainAsync` (SDK-level orchestration via list + invoke) receives the mutated payload from the previous one.

**Gateway split**: `GatewayProxyConfigurator` owns transparent MCP proxy wiring, `GatewayInterceptorProtocolBridge` owns optional SEP passthrough wiring, and `GatewayConnectionForwardingRegistrar` owns connection-bound notification forwarding registration.

**`McpInterceptorGatewayOptions`**: Configuration — `BackendClient`, optional preconnected `InterceptorClients`, optional external `InterceptorServerConnections`, optional dynamic `InterceptorServerConnectionResolver`, `Events` filter, `TimeoutMs`, `DefaultContext`, optional `ServerInfo` override, and `ExposeInterceptorProtocol`.

**External interceptor connections**: `McpInterceptorGateway.CreateAsync(...)` can create interceptor clients from standard SDK transports (`IClientTransport`) via `McpInterceptorServerConnectionOptions`. Dynamic per-request transparent resolution is supported via `InterceptorServerConnectionResolver` using the SDK's `MessageContext` plus SEP event name.

**Builder extension**: `IMcpServerBuilder.WithInterceptorGateway(options)` and `WithInterceptorGateway(Func<IServiceProvider, McpInterceptorGatewayOptions>)` for DI/builder scenarios. Builder-based wiring still expects already-connected interceptor clients; async external transport setup should use `McpInterceptorGateway.CreateAsync(...)`.

**Notification forwarding**: `gateway.RegisterNotificationForwarding(proxyServer)` subscribes to backend `tools/list_changed`, `prompts/list_changed`, `resources/list_changed` and re-sends through the proxy.

**Why handler delegates (not message filters) for the proxy**: The SDK's `With*Handler` methods automatically set `ServerCapabilities`, are type-safe, and are the intended extension point. Message filters are still used for interceptor protocol passthrough (`interceptors/list`, `interceptor/invoke`).

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
- `ConfigDrivenGatewaySample` — sample-only config-driven host composed from the gateway primitives; demonstrates static and dynamic external interceptor resolution with transport-configured backend/interceptor clients
