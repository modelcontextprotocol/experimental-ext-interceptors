using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;

// ──────────────────────────────────────────────────────────────────────
// Gateway Chain Sample
//
// Demonstrates chaining two interceptor servers in front of an MCP server:
//
//   Client ──▶ Security Interceptors ──▶ Logging Interceptors ──▶ Everything Server
//
// This shows how multiple independent interceptor layers compose — the
// security layer (PII + email redaction) runs first, then the logging
// layer observes the already-sanitized payloads.
//
// Both interceptor layers use the same InterceptorServerSample binary;
// in production each layer would be a different server with its own
// interceptor set.
// ──────────────────────────────────────────────────────────────────────

Console.WriteLine("=== MCP Interceptors Gateway Chain Sample ===");
Console.WriteLine();

var interceptorServerPath = Path.Combine(GetSourceDir(), "..", "InterceptorServerSample");

// 1. Launch two interceptor servers — same binary here, but logically distinct layers
Console.WriteLine("[setup] Starting security interceptor server...");
await using var securityInterceptorClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "SecurityInterceptors",
        Command = "dotnet",
        Arguments = ["run", "--project", interceptorServerPath],
    }));

Console.WriteLine("[setup] Starting logging interceptor server...");
await using var loggingInterceptorClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "LoggingInterceptors",
        Command = "dotnet",
        Arguments = ["run", "--project", interceptorServerPath],
    }));

// 2. Connect to the MCP everything server
Console.WriteLine("[setup] Starting everything server...");
await using var everythingClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "EverythingServer",
        Command = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-everything"],
    }));

// 3. Chain: wrap everything server with logging, then wrap that with security
//    Security layer intercepts tools/call (validation + mutation)
//    Logging layer intercepts all events (sink only, since that's
//    what request-logger is configured for — it won't block anything)

// Inner layer: logging interceptors → everything server
var loggingGateway = new InterceptingMcpClient(everythingClient, new InterceptingMcpClientOptions
{
    InterceptorClient = loggingInterceptorClient,
    Events = [InterceptionEvents.ToolsCall, InterceptionEvents.ToolsList],
});

// Outer layer: security interceptors → logging gateway
// Note: InterceptingMcpClient wraps McpClient, so to chain we use the
// security interceptors directly via ExecuteChainAsync before the inner gateway.

Console.WriteLine("[setup] Connected! Chain: Security → Logging → Everything Server");
Console.WriteLine();

// 4. List interceptors from both layers
var securityInterceptors = await securityInterceptorClient.ListInterceptorsAsync();
var loggingInterceptors = await loggingInterceptorClient.ListInterceptorsAsync();

Console.WriteLine("[security layer] interceptors:");
foreach (var i in securityInterceptors.Interceptors)
{
    var events = string.Join(", ", i.Hooks.SelectMany(h => h.Events).Distinct());
    Console.WriteLine($"  {i.Name,-20} type={i.Type,-15} events=[{events}]");
}
Console.WriteLine("[logging layer] interceptors:");
foreach (var i in loggingInterceptors.Interceptors)
{
    var events = string.Join(", ", i.Hooks.SelectMany(h => h.Events).Distinct());
    Console.WriteLine($"  {i.Name,-20} type={i.Type,-15} events=[{events}]");
}

// 5. List tools (flows through logging gateway)
Console.WriteLine();
var tools = await loggingGateway.ListToolsAsync();
Console.WriteLine($"[tools] {tools.Count} tools available");

// ── Demo 1: Clean call through both layers ───────────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 1: Clean echo through both layers ──");
{
    var payload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"Hello from the chain!"}}""")!;

    // Security layer (request phase)
    var securityResult = await securityInterceptorClient.ExecuteChainAsync(new ExecuteChainRequestParams
    {
        Event = InterceptionEvents.ToolsCall,
        Phase = InterceptorPhase.Request,
        Payload = payload,
    });
    Console.WriteLine($"  Security layer: {securityResult.Status}");

    // Logging layer + actual call (uses the sanitized payload)
    var result = await loggingGateway.CallToolAsync("echo", new Dictionary<string, object?>
    {
        ["message"] = "Hello from the chain!",
    });
    Console.WriteLine($"  Result: {result.Content.FirstOrDefault()}");
}

// ── Demo 2: Email in payload — security redacts, logging sees redacted ─
Console.WriteLine();
Console.WriteLine("── Demo 2: Email payload through both layers ──");
{
    var payload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"Contact alice@secret.com"}}""")!;

    // Security layer redacts the email
    var securityResult = await securityInterceptorClient.ExecuteChainAsync(new ExecuteChainRequestParams
    {
        Event = InterceptionEvents.ToolsCall,
        Phase = InterceptorPhase.Request,
        Payload = payload,
    });
    Console.WriteLine($"  Security layer: {securityResult.Status}");
    Console.WriteLine($"  Sanitized payload: {securityResult.FinalPayload}");

    // Extract the redacted message for the actual call
    var sanitizedArgs = securityResult.FinalPayload?["arguments"];
    var sanitizedMessage = sanitizedArgs?["message"]?.GetValue<string>() ?? "[redacted]";

    // Logging layer + actual call with sanitized payload
    var result = await loggingGateway.CallToolAsync("echo", new Dictionary<string, object?>
    {
        ["message"] = sanitizedMessage,
    });
    Console.WriteLine($"  Result: {result.Content.FirstOrDefault()}");
}

// ── Demo 3: PII in payload — security blocks, never reaches logging ──
Console.WriteLine();
Console.WriteLine("── Demo 3: PII payload — blocked by security layer ──");
{
    var payload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"SSN: 123-45-6789"}}""")!;

    var securityResult = await securityInterceptorClient.ExecuteChainAsync(new ExecuteChainRequestParams
    {
        Event = InterceptionEvents.ToolsCall,
        Phase = InterceptorPhase.Request,
        Payload = payload,
    });
    Console.WriteLine($"  Security layer: {securityResult.Status}");

    if (securityResult.Status == InterceptorChainStatus.ValidationFailed)
    {
        Console.WriteLine($"  BLOCKED — request never reached logging layer or server");
        if (securityResult.AbortedAt is { } abort)
        {
            Console.WriteLine($"  Aborted by: {abort.Interceptor} ({abort.Reason})");
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

static string GetSourceDir([CallerFilePath] string? path = null) =>
    Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
