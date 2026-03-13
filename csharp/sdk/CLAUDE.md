# MCP Interceptors - C# SDK

## What this is
C# implementation of gateway-level interceptors from [SEP-1763](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763). NuGet package additive to the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) (v1.1.0). Focus is on the protocol-level extension (client → interceptor server → server), NOT in-process middleware.

## Build & test
```
dotnet build   # from csharp/sdk/
dotnet test    # 33 tests across 3 files
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

## Not yet implemented
- LLM event types (`llm/completion`) — constant defined, no payload types or interception
- Additional `InterceptingMcpClient` methods beyond `CallToolAsync`/`ListToolsAsync`
- Samples: `InterceptorClientSample`, `GatewayChainSample`
