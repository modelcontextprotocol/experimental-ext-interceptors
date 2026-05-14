using System.Runtime.CompilerServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Server;

// ──────────────────────────────────────────────────────────────────────
// Transparent Proxy Sample
//
// Creates a transparent MCP proxy that:
//   1. Connects as a CLIENT to a backend MCP server (everything server)
//   2. Connects as a CLIENT to an interceptor server (PII + email interceptors)
//   3. Exposes itself as a SERVER via stdio
//
// To connecting clients (e.g. Claude Desktop), this proxy appears to be
// the everything server — but all requests flow through the interceptor
// chain first (validation, mutation, sink).
//
// Claude Desktop config example:
//   {
//     "mcpServers": {
//       "everything-with-interceptors": {
//         "command": "dotnet",
//         "args": ["run", "--project", "path/to/TransparentProxySample"]
//       }
//     }
//   }
// ──────────────────────────────────────────────────────────────────────

// 1. Connect to the interceptor server
var interceptorServerPath = Path.Combine(GetSourceDir(), "..", "InterceptorServerSample");
await using var interceptorClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "InterceptorServer",
        Command = "dotnet",
        Arguments = ["run", "--project", interceptorServerPath],
    }));

// 2. Connect to the backend everything server
await using var backendClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "EverythingServer",
        Command = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-everything"],
    }));

// 3. Create the gateway
await using var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
{
    BackendClient = backendClient,
    InterceptorClients = [interceptorClient],
    Events = [InterceptionEvents.ToolsCall], // Only intercept tools/call
});

// 4. Configure server options and create the proxy server on stdio
var serverOptions = new McpServerOptions
{
    ServerInfo = new() { Name = "interceptor-proxy", Version = "1.0.0" },
};
gateway.ConfigureServerOptions(serverOptions);

await using var server = McpServer.Create(new StdioServerTransport("interceptor-proxy"), serverOptions);

// 5. Forward backend notifications to connected clients
gateway.RegisterNotificationForwarding(server);

// 6. Run the proxy — it will serve requests until the client disconnects
await server.RunAsync();

static string GetSourceDir([CallerFilePath] string? path = null) =>
    Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
