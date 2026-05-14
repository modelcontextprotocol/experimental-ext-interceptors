using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// ──────────────────────────────────────────────────────────────────────
// Config-Driven Gateway Sample
//
// This sample shows one way to build a transparent MCP gateway host from the
// interceptor SDK primitives. The JSON file format used here is sample-only.
// The core library does not prescribe a config schema.
//
// Features demonstrated:
//   1. Backend client created from transport config (stdio or streamable-http)
//   2. Static external interceptor servers via CreateAsync(...)
//   3. Dynamic per-request interceptor resolution via MessageContext
//   4. Transparent proxy surface for downstream MCP clients (stdio in this sample)
//
// To host the gateway itself over Streamable HTTP, compose the same gateway
// primitives with the core SDK's ASP.NET transport support
// (AddMcpServer().WithHttpTransport(), then MapMcp()).
// ──────────────────────────────────────────────────────────────────────

var configPath = Path.Combine(GetSourceDir(), "mcp-interceptors.json");
var config = JsonSerializer.Deserialize<SampleGatewayConfig>(await File.ReadAllTextAsync(configPath))
    ?? throw new InvalidOperationException("Failed to load gateway config.");

await using var backendClient = await McpClient.CreateAsync(CreateTransport(config.Backend));

var staticConnections = config.StaticInterceptors.Select(CreateConnectionOptions).ToArray();
var dynamicConnections = config.DynamicInterceptors.ToDictionary(
    kvp => kvp.Key,
    kvp => (IReadOnlyList<McpInterceptorServerConnectionOptions>)kvp.Value.Select(CreateConnectionOptions).ToArray(),
    StringComparer.OrdinalIgnoreCase);

var gatewayOptions = new McpInterceptorGatewayOptions
{
    BackendClient = backendClient,
    InterceptorServerConnections = staticConnections,
    Events = config.InterceptedEvents,
    ServerInfo = config.ServerInfo,
    InterceptorServerConnectionResolver = (context, @event, ct) =>
    {
        if (@event != InterceptionEvents.ToolsCall)
        {
            return ValueTask.FromResult<IReadOnlyList<McpInterceptorServerConnectionOptions>>([]);
        }

        var userName = context.User?.Identity?.Name;
        if (userName is not null && dynamicConnections.TryGetValue(userName, out var connections))
        {
            return ValueTask.FromResult(connections);
        }

        return ValueTask.FromResult<IReadOnlyList<McpInterceptorServerConnectionOptions>>([]);
    },
};

await using var gateway = await McpInterceptorGateway.CreateAsync(gatewayOptions);

var serverOptions = new McpServerOptions
{
    ServerInfo = config.ServerInfo,
};
gateway.ConfigureServerOptions(serverOptions);

await using var server = McpServer.Create(new StdioServerTransport(config.ServerInfo.Name), serverOptions);
gateway.RegisterNotificationForwarding(server);

await server.RunAsync();

static IClientTransport CreateTransport(TransportConfig config)
{
    return config.Transport switch
    {
        "stdio" => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command ?? throw new InvalidOperationException("Command is required for stdio transport."),
            Arguments = config.Arguments,
        }),
        "http" => new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = config.Name,
            Endpoint = config.Endpoint ?? throw new InvalidOperationException("HTTP endpoint is required for streamable-http transport."),
            TransportMode = HttpTransportMode.StreamableHttp,
        }),
        _ => throw new NotSupportedException($"Unsupported transport '{config.Transport}' in sample."),
    };
}

static McpInterceptorServerConnectionOptions CreateConnectionOptions(TransportConfig config)
{
    return new McpInterceptorServerConnectionOptions
    {
        ConnectionId = config.ConnectionId,
        Transport = CreateTransport(config),
    };
}

static string GetSourceDir([CallerFilePath] string? path = null) =>
    Path.GetDirectoryName(path) ?? throw new InvalidOperationException();

internal sealed class SampleGatewayConfig
{
    public required Implementation ServerInfo { get; set; }
    public required TransportConfig Backend { get; set; }
    public IList<TransportConfig> StaticInterceptors { get; set; } = [];
    public Dictionary<string, IList<TransportConfig>> DynamicInterceptors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public IList<string>? InterceptedEvents { get; set; }
}

internal sealed class TransportConfig
{
    public required string Transport { get; set; }
    public required string Name { get; set; }
    public string? Command { get; set; }
    public IList<string> Arguments { get; set; } = [];
    public Uri? Endpoint { get; set; }
    public string? ConnectionId { get; set; }
}
