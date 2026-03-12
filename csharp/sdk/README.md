# ModelContextProtocol.Interceptors

C# implementation of the [MCP Interceptors Extension (SEP-1763)](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763) — gateway-level interceptors for the Model Context Protocol.

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

### Gateway Pattern (Full Chain)

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
