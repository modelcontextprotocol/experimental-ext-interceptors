# MCP Interceptors - C# SDK

## What this is
C# implementation of [SEP-2624](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2624) interceptors. NuGet package additive to the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) (v2.1.0, MCP spec 2026-07-28). Focus is the protocol-level extension (invoker → interceptor server over MCP), NOT in-process middleware. `docs/sep.md` at the repo root is the WG's working copy of the SEP and is authoritative; SDK wording follows it.

## Build & test
```
dotnet build   # from csharp/sdk/
dotnet test    # ~150 tests
dotnet run --project samples/WhyInterceptorsDemo -- --fast   # end-to-end smoke test over real stdio processes
```

## Key architectural constraints

**Why message filter, not handlers**: `McpServerHandlers` and `McpServerImpl` are `internal` in the SDK. We can't register handlers for new JSON-RPC methods from outside. Instead we use `McpServerOptions.Filters.Message.IncomingFilters` to intercept `interceptors/list` and `interceptor/invoke`, handle them, send `JsonRpcResponse` via `context.Server.SendMessageAsync()`, and skip calling `next`. See `InterceptorMessageFilter.cs`.

**Why `ServerCapabilities.Extensions`**: The SDK's intended mechanism for protocol extensions. We advertise `InterceptorsCapability { SupportedEvents }` under `Extensions["io.modelcontextprotocol/interceptors"]`.

**Client `SendRequestAsync`**: The public `SendRequestAsync<TParameters, TResult>` endpoint extension takes `JsonSerializerOptions` (call it with named `cancellationToken:` — 2.x inserted an optional `RequestId` parameter before it). We pass `InterceptorJsonUtilities.DefaultOptions` (`McpJsonUtilities.DefaultOptions` + `InterceptorJsonContext`, with `AllowOutOfOrderMetadataProperties = true` so a result's `type` discriminator needn't come first).

**SDK 2.x / spec 2026-07-28 notes**:
- Logging (`logging/setLevel`) and `resources/subscribe` are deprecated (SEP-2577 / SEP-2575) and rejected on `2026-07-28` connections. The gateway keeps proxying them for down-level peers (`#pragma warning disable MCP9005`). The two hops negotiate versions independently: when the backend hop is `2026-07-28`+, the gateway answers `logging/setLevel` itself (found with the MCP Inspector CLI).
- Per-request protocol `_meta` (`protocolVersion`, `clientInfo`, `clientCapabilities`, `logLevel`) is hop-by-hop: the gateway strips it before forwarding (`GatewayProxyConfigurator.WithoutHopByHopMeta`). The SDK client overwrites version/client info but *merges* capabilities.
- `Tool.inputSchema` is required and validated on deserialization; the gateway coerces missing/invalid schemas to `{"type":"object"}` (`EnsureToolInputSchemas`).
- MRTR and server→client requests (sampling, elicitation, roots) are not forwarded.

**`InterceptingMcpClient` is composition**: `McpClient` has an internal constructor; subclassing is `[Experimental]`. We wrap it and expose `.Inner`. The SDK's `McpClientTool`/`McpClientPrompt`/`McpClientResource` are sealed and bound to the client that listed them, so the wrapper returns `InterceptedMcpClientTool` (an `AIFunction` routed through the wrapper) and protocol `Prompt`/`Resource` definitions.

## Execution model (`Client/InterceptorExecutionEngine.cs`)
- **Direction, not phase, decides order** (#29). `Sending`: mutations (sequential by priority ↑, name tie-break) → validations (parallel) → sinks (background). `Receiving`: validations → sinks → mutations. `InterceptorInvokerRole.Client` maps request→sending; `Server` maps request→receiving. Gateways default to `Client`.
- **Policy resolution**: override → declared → SEP default, per field (`InterceptorOverrides`: mode, failOpen, priorityHint, timeoutMs, hooks). Hook overrides may only narrow (`InterceptorOverrides.ValidateFor`). `InterceptorPolicy` adds `Include` (fail-closed selection), per-interceptor `Config`, `DefaultTimeoutMs`.
- **Timeouts**: per-interceptor timeout is routed by failOpen; the aggregate `TimeoutMs` (which also covers discovery in `InterceptorPhaseRunner` and the static `InterceptorOrchestrator.ExecuteAsync`; the invocations get what is left, and a budget used up by discovery fails the operation) cancels the rest and fails the operation regardless of failOpen. Audit-mode invocations cut off by the aggregate are recorded as `TimedOut` and never fail the operation (SEP: audit MUST NOT block). Non-positive timeouts are rejected (overrides, policy, engine, server filter).
- **Validations** all complete; every blocking error is collected into `ValidationErrors`. Verdict rule lives in `ValidationVerdict` (fail-secure reading of #51: top-level severity, else worst message, else block). `ValidationMessage.Severity` is nullable; a missing one counts as error in an invalid result (`ValidationVerdict.SeverityOf`).
- **Mutations** are atomic; a wrong result type fails closed. Nothing pre-redaction is serialized: `FinalPayload` is null on rejection, `OriginalPayload` is `[JsonIgnore]`, and `Results` / `Invocations[].Result` are written without mutation payloads (`PayloadFreeResultConverters.cs`; the `interceptor/invoke` wire result is unaffected).
- **Sinks** are fire-and-forget on a `DeepClone` of the payload; not included in `Results`. `InterceptorExecutionResult.SinkCompletion` (internal) lets the gateway keep per-request interceptor clients open until sinks finish.
- `InterceptorInvocation` per applied interceptor: resolved mode/failOpen + source (SEP Security Best Practice 3), outcome, effect.

## Public API map
| Type | Role |
|------|------|
| `InterceptorOrchestrator` | Discover across servers + execute (SDK orchestration; the SEP has no chain primitive) |
| `InterceptorBinding` | Descriptor + hosting `McpClient` + overrides + config |
| `InterceptorPolicy` / `InterceptorOverrides` | Invoker-side policy |
| `InterceptorExecutionRequest` / `InterceptorExecutionResult` | One event/phase in, aggregate out |
| `McpInterceptorException` / `McpInterceptorValidationException` | `McpProtocolException` subclasses (-32602 + `data.validationErrors`; -32603; -32000) |
| `McpClientInterceptorExtensions` | `ListInterceptorsAsync`, `ListAllInterceptorsAsync` (paginates), `InvokeInterceptorAsync` (with optional expected type, #50), `ExecuteInterceptorsAsync` |

## JSON-RPC methods
| Method | Params → Result |
|--------|----------------|
| `interceptors/list` | `ListInterceptorsRequestParams` → `ListInterceptorsResult` |
| `interceptor/invoke` | `InvokeInterceptorRequestParams` → `InterceptorResult` (polymorphic on `type`: `validation`, `mutation`, `sink`) |

`timeoutMs` / `durationMs` use lenient converters (accept fractional numbers, write integers, never round a positive value to zero; #49).

## Parameter binding (ReflectionMcpServerInterceptor)
`JsonNode payload`, `JsonNode config`, `string event`/`eventName`, `InterceptorPhase`, `InvokeInterceptorContext`, `InvokeInterceptorRequestParams`, `CancellationToken`, `McpServer`, `IServiceProvider`. Return `InterceptorResult` subclasses or `bool`, sync or `Task`/`ValueTask`. Invoked with `BindingFlags.DoNotWrapExceptions` so errors carry the real message. Attribute supports `Version` and `ConfigSchema` (JSON text).

## Transparent gateway (`Gateway/`)
- `McpInterceptorGateway` coordinates; `GatewayProxyConfigurator` mirrors backend capabilities and registers one generic raw-JSON forwarding handler per backend method (event name = method name, so completions/templates/unsubscribe are interceptable too); `GatewayInterceptorProtocolBridge` is the opt-in SEP passthrough (`ExposeInterceptorProtocol`); `GatewayConnectionForwardingRegistrar` handles per-session notification forwarding for the DI path.
- `InterceptorPhaseRunner` (shared with `InterceptingMcpClient`) caches discovery per interceptor client (`InterceptorDiscoveryCache`, default 1 min, weak-keyed, concurrent callers share one listing and always join one in progress, the TTL counts from completion, each discovery capped at 30 s even with caching off; `ListAllInterceptorsAsync` stops on a repeated cursor), wraps discovery failures/timeouts as `McpInterceptorException` (fail-closed), binds the policy, runs the engine, calls `OnInterceptorsExecuted`, throws `McpInterceptorException` on failure, and builds per-request context (timestamp, trace, session, authenticated user).
- **Rejected `tools/call`**: returned as `CallToolResult { IsError = true }` with a readable reason and SEP details under `_meta["io.modelcontextprotocol/interceptors"]` (`ToolCallRejectionMode.ErrorResult`, default), or as the SEP JSON-RPC error (`ProtocolError`). Other methods always get the JSON-RPC error. Background: the SDK hides non-`McpException` messages ("An error occurred invoking 'x'."), and clients reliably show tool error results to the model.
- External connections: `CreateAsync` + `InterceptorServerConnections`; per-request `InterceptorServerConnectionResolver` (transparent path only).

## Samples
See `samples/README.md`. Start with `WhyInterceptorsDemo` (scripted story; real MCP; `--fast`, `--export-html`) and `InterceptorProxy` (JSON-configured sidecar; `--check`; verified with the MCP Inspector CLI and the reference filesystem server).
The test project compiles in the demo's guard and tools and the proxy's `ProxyConfig.cs` (`SampleTests`): the guard parses a whole argument value with `System.Uri`, as `fetch_url` does, checks every place a URL starts in text (property names included), and blocks URL-like text it cannot parse.

## SDK reference
Local checkout: `~/code/mcp/csharp-sdk` (may be ahead of 2.1.0; read with `git show v2.1.0:<path>`).
- `src/ModelContextProtocol.Core/Server/McpServerImpl.cs` — tool error handling, MRTR
- `src/ModelContextProtocol.Core/McpSessionHandler.cs` — exception → JSON-RPC error mapping, per-request `_meta` injection
- `src/ModelContextProtocol.Core/Server/McpMessageFilter.cs` — our hook point
- `src/ModelContextProtocol.Core/McpJsonUtilities.cs` — JSON context chaining pattern
