# ModelContextProtocol.Interceptors

C# implementation of the [MCP Interceptors Extension (SEP-1763)](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763) — gateway-level interceptors for the Model Context Protocol.

Architecture work is tracked in [`docs/ARCHITECTURE_PHASES.md`](docs/ARCHITECTURE_PHASES.md).

## Overview

This package enables creating interceptor servers that sit between MCP clients and servers, providing validation, mutation, and observability capabilities without modifying either the client or server.

```
Client  ──▶  Interceptor Server  ──▶  Server
        ◀──  (validates/mutates)  ◀──  (tools)
```

## Quick Start

### Creating an Interceptor Server

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithInterceptors<MyInterceptors>();

var app = builder.Build();
await app.RunAsync();

[McpServerInterceptorType]
public class MyInterceptors
{
    [McpServerInterceptor(Name = "pii-validator", Type = InterceptorType.Validation,
        Events = [InterceptorEvents.ToolsCall], Phase = InterceptorPhase.Request)]
    public static ValidationInterceptorResult ValidatePii(JsonNode payload)
    {
        // Check for PII patterns
        return ValidationInterceptorResult.Success();
    }

    [McpServerInterceptor(Name = "email-redactor", Type = InterceptorType.Mutation,
        Events = [InterceptorEvents.ToolsCall], PriorityHint = -1000)]
    public static MutationInterceptorResult RedactEmails(JsonNode payload)
    {
        // Modify the payload
        return new MutationInterceptorResult { Modified = true, Payload = modifiedPayload };
    }
}
```

### Consuming Interceptors from a Client

```csharp
// Connect to the interceptor server
var interceptorClient = await McpClient.CreateAsync(interceptorTransport);

// List available interceptors
var interceptors = await interceptorClient.ListInterceptorsAsync();

// Invoke a single interceptor
var result = await interceptorClient.InvokeInterceptorAsync(new InvokeInterceptorRequestParams
{
    Name = "pii-validator",
    Event = InterceptorEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = JsonNode.Parse("""{"name":"call-tool","arguments":{"query":"test"}}""")!,
});

// Execute a full chain
var chainResult = await interceptorClient.ExecuteChainAsync(new ExecuteChainRequestParams
{
    Event = InterceptorEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = myPayload,
});
```

### Gateway Pattern (Client-Side)

Use `InterceptingMcpClient` when your code is the caller and you want to route operations through interceptors before they reach the server:

```csharp
// Connect to both the interceptor server and the actual MCP server
var interceptorClient = await McpClient.CreateAsync(interceptorTransport);
var mcpClient = await McpClient.CreateAsync(mcpTransport);

// Create the gateway wrapper
var gateway = new InterceptingMcpClient(mcpClient, new InterceptingMcpClientOptions
{
    InterceptorClient = interceptorClient,
    Events = [InterceptorEvents.ToolsCall],
});

// All tool calls now flow through interceptors automatically
var result = await gateway.CallToolAsync("my-tool", new Dictionary<string, object?> { ["query"] = "test" });
```

### Transparent Proxy (Server-Side)

Use `McpInterceptorGateway` to create an MCP server that transparently proxies requests through interceptors to a backend server. Connecting clients see the proxy as the backend itself — no client-side changes needed.

By default, the gateway is transparent-only: it does not advertise or expose the SEP interceptor protocol to connecting clients. If you want the gateway to also expose `interceptors/list`, `interceptor/invoke`, and `interceptor/executeChain`, enable that explicitly with `ExposeInterceptorProtocol = true`.

```
Client  ──▶  Proxy Server  ──▶  Interceptor Server  ──▶  Backend Server
        ◀──  (transparent)  ◀──  (validates/mutates)  ◀──  (tools, etc.)
```

```csharp
// Connect to the backend and interceptor servers
await using var backend = await McpClient.CreateAsync(backendTransport);
await using var interceptors = await McpClient.CreateAsync(interceptorTransport);

// Create the gateway
await using var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
{
    BackendClient = backend,
    InterceptorClients = [interceptors],
    Events = [InterceptorEvents.ToolsCall], // null = intercept all events
    ExposeInterceptorProtocol = false,
});

// Configure and start the proxy server on stdio
var serverOptions = new McpServerOptions();
gateway.ConfigureServerOptions(serverOptions);

await using var server = McpServer.Create(
    new StdioServerTransport("my-proxy"), serverOptions);
gateway.RegisterNotificationForwarding(server);
await server.RunAsync();
```

The proxy automatically mirrors the backend's capabilities (tools, prompts, resources, completions, logging) and forwards `*_list_changed` notifications. Multiple interceptor clients can be chained — they execute in order, each receiving the previous client's mutated payload.

**With DI / builder pattern:**

```csharp
builder.Services.AddMcpServer()
    .WithInterceptorGateway(new McpInterceptorGatewayOptions
    {
        BackendClient = backend,
        InterceptorClients = [interceptors],
    });
```

The builder extension handles notification forwarding automatically, registering once per session for multi-connection transports (HTTP) and once for single-connection transports (stdio).

To expose the SEP interceptor protocol through the gateway as an advanced mode, set:

```csharp
ExposeInterceptorProtocol = true
```

**Claude Desktop integration** — point it at a proxy binary:

```json
{
  "mcpServers": {
    "my-server-with-interceptors": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/TransparentProxySample"]
    }
  }
}
```

## Interceptor Types

| Type | Execution | Purpose |
|------|-----------|---------|
| **Validation** | Parallel | Validates payloads. Error severity aborts the chain. |
| **Mutation** | Sequential (by priority) | Transforms payloads. Output chains to next mutation. |
| **Observability** | Parallel (fire-and-forget) | Logging/metrics. Failures are swallowed. |

## Chain Execution Order

**Request phase (sending):** Mutations → Validations → Observability → send
**Response phase (receiving):** Validations → Observability → Mutations → process

## Parameter Binding

Interceptor methods support automatic parameter binding:

| Parameter Type | Bound From |
|---------------|------------|
| `JsonNode payload` | `InvokeInterceptorRequestParams.Payload` |
| `JsonNode config` | `InvokeInterceptorRequestParams.Config` |
| `string event` | `InvokeInterceptorRequestParams.Event` |
| `InterceptorPhase phase` | `InvokeInterceptorRequestParams.Phase` |
| `InvokeInterceptorContext` | `InvokeInterceptorRequestParams.Context` |
| `CancellationToken` | Framework cancellation token |
| `McpServer` | Current server instance |
| `IServiceProvider` | Request-scoped DI container |

Methods can return `InterceptorResult` (or any subclass), `bool` (wrapped as `ValidationInterceptorResult`), or `Task<T>`/`ValueTask<T>` variants of these.
