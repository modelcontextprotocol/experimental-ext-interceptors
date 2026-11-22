# ModelContextProtocol.Interceptors

C# implementation of the [MCP Interceptors extension (SEP-2624)](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2624),
built on the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) (2.1, MCP `2026-07-28`).

## Why interceptors?

An agent talks to MCP servers that someone else wrote. Everything a server returns goes straight
into the model's context, and everything the model decides to do goes straight back out as a tool
call. MCP has no standard place to check either direction, so every client, gateway and server
vendor builds its own guardrails, in its own language, with its own configuration.

An **interceptor** is that place. It is hosted on an ordinary MCP server (an *interceptor server*)
and called over MCP with two methods, `interceptors/list` and `interceptor/invoke`. **Validators**
gate a message; **mutators** rewrite it. The invoker (a client, gateway or server) runs them at
fixed points, in an order that respects the trust boundary. Guardrails are written once, in any
language, and plugged in anywhere.

**See it:** `dotnet run --project samples/WhyInterceptorsDemo` is a three-minute story. An agent is
tricked into leaking data through a third-party server, then the same agent and server run behind
an interceptor proxy. To try it with your own agent, see
[InterceptorProxy](samples/InterceptorProxy). All samples are listed in [samples/README.md](samples/README.md).

## Writing an interceptor server

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithInterceptors<SecurityGuard>();
await builder.Build().RunAsync();

[McpServerInterceptorType]
public sealed class SecurityGuard
{
    [McpServerInterceptor(Name = "exfiltration-guard", Type = InterceptorType.Validation,
        Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Request,
        ConfigSchema = """{"type":"object","properties":{"allowedDomains":{"type":"array"}}}""")]
    public static ValidationInterceptorResult BlockUnapprovedDomains(JsonNode payload, JsonNode? config)
    {
        // ...inspect payload["arguments"]...
        return ValidationInterceptorResult.Failure(new ValidationMessage
        {
            Path = "$.arguments.url",
            Message = "evil.example is not an approved destination",
            Severity = ValidationSeverity.Error,
        });
    }

    [McpServerInterceptor(Name = "pii-redactor", Type = InterceptorType.Mutation,
        Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Response, PriorityHint = -50_000)]
    public static MutationInterceptorResult Redact(JsonNode payload) =>
        new() { Modified = true, Payload = /* rewritten payload */ payload };
}
```

Interceptor methods bind parameters by type and name:

| Parameter | Bound from |
| --- | --- |
| `JsonNode payload` | the intercepted message |
| `JsonNode config` | invoker-supplied configuration (`config`) |
| `string event` / `string eventName` | the lifecycle event, e.g. `tools/call` |
| `InterceptorPhase phase` | `Request` or `Response` |
| `InvokeInterceptorContext` | principal, trace, timestamp, session |
| `InvokeInterceptorRequestParams` | the whole invocation |
| `CancellationToken`, `McpServer`, `IServiceProvider` | the framework |

Methods return an `InterceptorResult` subclass or `bool` (a validation verdict), synchronously or
as `Task`/`ValueTask`. `PriorityHint` applies to both phases; use `RequestPriorityHint` /
`ResponsePriorityHint` to order a mutator differently per phase. `Version` and `ConfigSchema` are
advertised in `interceptors/list`.

## Invoking interceptors

There are three ways to apply interceptors to MCP traffic. All three share one execution engine.

### A transparent gateway (no client or server changes)

`McpInterceptorGateway` configures an `McpServer` that looks exactly like a backend server and runs
interceptors on every forwarded request and response. Clients connect to it instead of the backend.

```csharp
await using var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
{
    BackendClient = backend,                 // McpClient connected to the real server
    InterceptorClients = [securityGuard],    // McpClients connected to interceptor servers
    Policy = policy,                         // see "Policy" below
    InvokerRole = InterceptorInvokerRole.Client,
    OnInterceptorsExecuted = result => auditLog.Write(result),
});

var serverOptions = new McpServerOptions();
gateway.ConfigureServerOptions(serverOptions);
await using var server = McpServer.Create(new StdioServerTransport("proxy"), serverOptions);
gateway.RegisterNotificationForwarding(server);
await server.RunAsync();
```

[InterceptorProxy](samples/InterceptorProxy) wraps this in a JSON-configured executable. For
dependency injection, use `IMcpServerBuilder.WithInterceptorGateway(...)`. To connect interceptor
servers from transports, use `McpInterceptorGateway.CreateAsync` with `InterceptorServerConnections`.
To choose interceptor servers per request (for example by user), set
`InterceptorServerConnectionResolver`.

By default the gateway does not expose the interceptor protocol itself. Set
`ExposeInterceptorProtocol = true` to pass `interceptors/list` and `interceptor/invoke` through.

### An agent-side wrapper

```csharp
await using var client = new InterceptingMcpClient(mcpClient, new InterceptingMcpClientOptions
{
    InterceptorClients = [securityGuard],
    Policy = policy,
});

var result = await client.CallToolAsync("fetch_url", new Dictionary<string, object?> { ["url"] = url });
```

`CallToolAsync`, `ListToolsAsync`, `ListPromptsAsync`, `GetPromptAsync`, `ListResourcesAsync`,
`ReadResourceAsync` and `SubscribeToResourceAsync` run interceptors in both phases. Mutations to
list results apply.

### Directly

```csharp
var interceptors = await securityGuard.ListAllInterceptorsAsync();
var verdict = await securityGuard.InvokeInterceptorAsync(new InvokeInterceptorRequestParams { ... });

// Run every applicable interceptor across several servers, per the SEP execution model:
var orchestrator = await InterceptorOrchestrator.DiscoverAsync([guardA, guardB], policy);
var result = await orchestrator.ExecuteAsync(new InterceptorExecutionRequest
{
    Event = InterceptionEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = payload,
});
```

## Execution model

For each event and phase, the invoker applies the interceptors whose hooks match, from every
interceptor server, in one global order.

| | Sending a message | Receiving a message |
| --- | --- | --- |
| Order | mutate (sequential) → validate (parallel) → send | validate (parallel) → mutate (sequential) → process |
| Why | validators see the final bytes that cross the boundary | validators are a barrier before anything acts on untrusted data |

- **Direction is not phase.** A client *sends* requests and *receives* responses; a server does the
  opposite. Set `InvokerRole` on a gateway (`Client` by default: it protects an agent from the server
  it calls), or `Direction` on an `InterceptorExecutionRequest`.
- **Mutators** run by resolved priority (lower first, then name). They are atomic: if one fails
  closed, none apply.
- **Validators** all complete before the verdict, and every blocking error is reported. A result
  blocks when `valid` is false and its severity is `error`. Top-level `severity` wins, then the
  most severe message; an invalid result with no severity blocks (fail-secure). This tracks the
  open question in experimental-ext-interceptors#51; see `ValidationVerdict`.
- **Failures** (errors, unusable results, per-interceptor timeouts) block unless the interceptor is
  fail-open. Audit-mode interceptors never block: validators report *would block*, and mutators are
  computed but not applied.
- An **aggregate timeout** (`TimeoutMs`) cancels the remaining invocations and fails the operation.
- **Sinks** (the proposed third type in experimental-ext-interceptors#28) are dispatched in the
  background on a snapshot of the payload. The operation never waits for them.

## Policy: capability vs. policy

An interceptor server declares what each interceptor *can* do (type, hooks, `configSchema`) and its
defaults. The invoker decides how it *runs*, with `InterceptorPolicy`:

```csharp
var policy = new InterceptorPolicy
{
    Include = new HashSet<string> { "exfiltration-guard", "pii-redactor" }, // fail closed if missing
    DefaultTimeoutMs = 5_000,
    Overrides =
    {
        // SEP-2624 InterceptorOverrides: mode, failOpen, priorityHint, timeoutMs, hooks (narrow only)
        ["exfiltration-guard"] = new() { Mode = InterceptorMode.Audit },
    },
    Config =
    {
        ["exfiltration-guard"] = JsonNode.Parse("""{"allowedDomains":["acme.example"]}""")!,
    },
};
```

Every run returns an `InterceptorExecutionResult` whose `Invocations` record, for each interceptor,
the resolved `Mode` and `FailOpen`, whether each came from an override, the declaration or the SEP
default (SEP Security Best Practice 3), and its `Effect` (`Blocked`, `Modified`, `WouldBlock`, …).
Gateways and wrappers pass each result to `OnInterceptorsExecuted`; `ILoggerFactory` logging records
the same.

## When interceptors reject an operation

- **Agent-side wrapper:** throws `McpInterceptorValidationException` (all `ValidationErrors`) or
  `McpInterceptorException` (mutation failure, timeout). Both carry the `ExecutionResult`.
- **Gateway:** a rejected `tools/call` becomes a tool result with `isError: true`, a readable reason
  for the model, and the SEP's details under `_meta["io.modelcontextprotocol/interceptors"]`.
  Clients pass tool errors to the model, so the agent can explain the block. Set
  `ToolCallRejection = ToolCallRejectionMode.ProtocolError` for the SEP's JSON-RPC error instead
  (`-32602 Interceptor validation failed` with `data.validationErrors`; `-32603` for mutation
  failures, `-32000` for timeouts). Other methods always return the JSON-RPC error.

## Interoperability notes

- Result `type` need not be the first JSON property.
- `timeoutMs` and `durationMs` accept fractional numbers and are written as integers (#49).
- A result without `type` is read as the type declared in `interceptors/list` (#50).
- The gateway strips per-request protocol metadata (`protocolVersion`, `clientInfo`,
  `clientCapabilities`, `logLevel`) before forwarding: it describes the client's hop, not the
  gateway's.
- `interceptors/list` results are cached per interceptor server (`DiscoveryCacheDuration`, one
  minute by default; `RefreshInterceptors()` clears the cache), so proxying an operation costs one
  `interceptor/invoke` per applicable interceptor.

## Build and test

```bash
dotnet build
dotnet test
```

Architecture notes are in [`docs/ARCHITECTURE_PHASES.md`](docs/ARCHITECTURE_PHASES.md).
