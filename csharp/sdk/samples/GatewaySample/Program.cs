using System.Runtime.CompilerServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;

// ──────────────────────────────────────────────────────────────────────
// Gateway Sample
//
// Demonstrates a full gateway chain:
//   Client ──▶ Interceptor Server (PII validator + email redactor) ──▶ Everything Server
//
// The interceptor server hosts three interceptors:
//   1. pii-validator    (validation)  – blocks payloads containing SSN-like data
//   2. email-redactor   (mutation)    – replaces email addresses with [EMAIL_REDACTED]
//   3. request-logger   (sink) – logs all events to stderr
// ──────────────────────────────────────────────────────────────────────

Console.WriteLine("=== MCP Interceptors Gateway Sample ===");
Console.WriteLine();

// 1. Connect to the interceptor server (our InterceptorServerSample, launched via dotnet run)
Console.WriteLine("[setup] Starting interceptor server...");
var interceptorServerPath = Path.Combine(GetSourceDir(), "..", "InterceptorServerSample");
await using var interceptorClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "InterceptorServer",
        Command = "dotnet",
        Arguments = ["run", "--project", interceptorServerPath],
    }));

// 2. Connect to the everything server (MCP reference server via npx)
Console.WriteLine("[setup] Starting everything server...");
await using var everythingClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "EverythingServer",
        Command = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-everything"],
    }));

// 3. Create the gateway wrapper
var gateway = new InterceptingMcpClient(everythingClient, new InterceptingMcpClientOptions
{
    InterceptorClient = interceptorClient,
    Events = [InterceptionEvents.ToolsCall],
});

// 4. List available interceptors
Console.WriteLine("[setup] Connected! Listing interceptors...");
var interceptors = await gateway.ListInterceptorsAsync();
Console.WriteLine();
foreach (var i in interceptors.Interceptors)
{
    var events = string.Join(", ", i.Hooks.SelectMany(h => h.Events).Distinct());
    Console.WriteLine($"  interceptor: {i.Name,-20} type={i.Type,-15} events=[{events}]");
}

// 5. List tools from the everything server (passes through gateway)
Console.WriteLine();
var tools = await gateway.ListToolsAsync();
Console.WriteLine($"[tools] {tools.Count} tools available: {string.Join(", ", tools.Select(t => t.Name))}");

// ── Demo 1: Normal tool call (passes through cleanly) ──────────────
Console.WriteLine();
Console.WriteLine("── Demo 1: Normal echo (should pass through) ──");
try
{
    var result = await gateway.CallToolAsync("echo", new Dictionary<string, object?>
    {
        ["message"] = "Hello from the gateway!"
    });
    Console.WriteLine($"  Result: {result.Content.FirstOrDefault()}");
}
catch (McpInterceptorValidationException ex)
{
    Console.WriteLine($"  BLOCKED: {ex.Message}");
}

// ── Demo 2: Tool call with email (mutation - email gets redacted) ──
Console.WriteLine();
Console.WriteLine("── Demo 2: Echo with email (should be redacted by email-redactor) ──");
try
{
    var result = await gateway.CallToolAsync("echo", new Dictionary<string, object?>
    {
        ["message"] = "Contact me at john.doe@example.com for details"
    });
    Console.WriteLine($"  Result: {result.Content.FirstOrDefault()}");
}
catch (McpInterceptorValidationException ex)
{
    Console.WriteLine($"  BLOCKED: {ex.Message}");
}

// ── Demo 3: Tool call with PII (validation - gets blocked) ─────────
Console.WriteLine();
Console.WriteLine("── Demo 3: Echo with SSN reference (should be blocked by pii-validator) ──");
try
{
    var result = await gateway.CallToolAsync("echo", new Dictionary<string, object?>
    {
        ["message"] = "My SSN is 123-45-6789"
    });
    Console.WriteLine($"  Result: {result.Content.FirstOrDefault()}");
}
catch (McpInterceptorValidationException ex)
{
    Console.WriteLine($"  BLOCKED: {ex.Message}");
}

// ── Demo 4: Normal add (should pass through) ───────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 4: Add 17 + 25 (should pass through) ──");
try
{
    var result = await gateway.CallToolAsync("get-sum", new Dictionary<string, object?>
    {
        ["a"] = 17,
        ["b"] = 25,
    });
    Console.WriteLine($"  Result: {result.Content.FirstOrDefault()}");
}
catch (McpInterceptorValidationException ex)
{
    Console.WriteLine($"  BLOCKED: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

static string GetSourceDir([CallerFilePath] string? path = null) =>
    Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
