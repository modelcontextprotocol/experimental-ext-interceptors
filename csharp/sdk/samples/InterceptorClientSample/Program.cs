using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;

// ──────────────────────────────────────────────────────────────────────
// Interceptor Client Sample
//
// Demonstrates the client-side interceptor API:
//   1. Listing available interceptors
//   2. Invoking a single interceptor directly
//   3. Executing a full interceptor chain
//
// Uses the InterceptorServerSample as the backend.
// ──────────────────────────────────────────────────────────────────────

Console.WriteLine("=== MCP Interceptors Client Sample ===");
Console.WriteLine();

// 1. Connect to the interceptor server
Console.WriteLine("[setup] Starting interceptor server...");
var interceptorServerPath = Path.Combine(GetSourceDir(), "..", "InterceptorServerSample");
await using var client = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "InterceptorServer",
        Command = "dotnet",
        Arguments = ["run", "--project", interceptorServerPath],
    }));

Console.WriteLine("[setup] Connected!");
Console.WriteLine();

// ── Demo 1: List all interceptors ────────────────────────────────────
Console.WriteLine("── Demo 1: List interceptors ──");
var listResult = await client.ListInterceptorsAsync();
foreach (var interceptor in listResult.Interceptors)
{
    var hookSummary = string.Join("; ", interceptor.Hooks.Select(h => $"{h.Phase}:[{string.Join(",", h.Events)}]"));
    Console.WriteLine($"  {interceptor.Name,-20} type={interceptor.Type,-15} hooks={hookSummary}");
    if (interceptor.Description is not null)
    {
        Console.WriteLine($"  {"",20} {interceptor.Description}");
    }
}

// ── Demo 2: Invoke a single interceptor ──────────────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 2: Invoke email-redactor directly ──");

var cleanPayload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"Contact alice@example.com"}}""")!;
var invokeResult = await client.InvokeInterceptorAsync(new InvokeInterceptorRequestParams
{
    Name = "email-redactor",
    Event = InterceptionEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = cleanPayload,
});

if (invokeResult is MutationInterceptorResult mutation)
{
    Console.WriteLine($"  Modified: {mutation.Modified}");
    Console.WriteLine($"  Payload:  {mutation.Payload}");
}

// ── Demo 3: Invoke pii-validator with clean data ─────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 3: Invoke pii-validator (clean data) ──");

var safePayload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"Hello world"}}""")!;
var validationResult = await client.InvokeInterceptorAsync(new InvokeInterceptorRequestParams
{
    Name = "pii-validator",
    Event = InterceptionEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = safePayload,
});

if (validationResult is ValidationInterceptorResult validation)
{
    Console.WriteLine($"  Valid: {validation.Valid}");
}

// ── Demo 4: Invoke pii-validator with PII ────────────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 4: Invoke pii-validator (with PII) ──");

var piiPayload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"My SSN is 123-45-6789"}}""")!;
var piiResult = await client.InvokeInterceptorAsync(new InvokeInterceptorRequestParams
{
    Name = "pii-validator",
    Event = InterceptionEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = piiPayload,
});

if (piiResult is ValidationInterceptorResult piiValidation)
{
    Console.WriteLine($"  Valid: {piiValidation.Valid}");
    Console.WriteLine($"  Severity: {piiValidation.Severity}");
    foreach (var msg in piiValidation.Messages ?? [])
    {
        Console.WriteLine($"  Message: [{msg.Severity}] {msg.Message} (path: {msg.Path})");
    }
}

// ── Demo 5: Execute full chain ───────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 5: Execute chain (email + PII check) ──");

var chainPayload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"Email bob@corp.com about SSN"}}""")!;
var chainResult = await client.ExecuteChainAsync(new ExecuteChainRequestParams
{
    Event = InterceptionEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = chainPayload,
    Context = new InvokeInterceptorContext
    {
        TraceId = Guid.NewGuid().ToString("N"),
    },
});

Console.WriteLine($"  Status: {chainResult.Status}");
Console.WriteLine($"  Duration: {chainResult.TotalDurationMs:F1}ms");
Console.WriteLine($"  Results: {chainResult.Results?.Count ?? 0} interceptor(s) ran");

if (chainResult.AbortedAt is { } abort)
{
    Console.WriteLine($"  Aborted at: {abort.Interceptor} ({abort.Reason})");
}

foreach (var r in chainResult.Results ?? [])
{
    var typeName = r switch
    {
        ValidationInterceptorResult => "validation",
        MutationInterceptorResult => "mutation",
        SinkInterceptorResult => "sink",
        _ => "unknown",
    };
    Console.WriteLine($"    {r.InterceptorName,-20} type={typeName,-15} duration={r.DurationMs:F1}ms");
}

// ── Demo 6: Execute chain with clean data ────────────────────────────
Console.WriteLine();
Console.WriteLine("── Demo 6: Execute chain (clean data, should pass) ──");

var cleanChainPayload = JsonNode.Parse("""{"name":"echo","arguments":{"message":"Contact bob@corp.com please"}}""")!;
var cleanChainResult = await client.ExecuteChainAsync(new ExecuteChainRequestParams
{
    Event = InterceptionEvents.ToolsCall,
    Phase = InterceptorPhase.Request,
    Payload = cleanChainPayload,
});

Console.WriteLine($"  Status: {cleanChainResult.Status}");
Console.WriteLine($"  Final payload: {cleanChainResult.FinalPayload}");

Console.WriteLine();
Console.WriteLine("=== Done ===");

static string GetSourceDir([CallerFilePath] string? path = null) =>
    Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
