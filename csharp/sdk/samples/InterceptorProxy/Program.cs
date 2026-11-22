// ──────────────────────────────────────────────────────────────────────
// InterceptorProxy — put interceptors in front of any MCP server
//
// A stdio MCP server that looks exactly like the backend it wraps, but runs
// every request and response through the interceptors on one or more
// interceptor servers. Register it in Claude Code, Claude Desktop or VS Code
// in place of the server itself: no client or server changes needed.
//
// This is the "common interceptor sidecar" from SEP-2624's reference
// implementation section, built from the library's McpInterceptorGateway.
//
//   InterceptorProxy proxy.json           serve on stdio
//   InterceptorProxy proxy.json --check   connect, validate the policy, describe, exit
//
// The config path may also be given as --config <path>.
//
// The config format is this sample's own; see README.md.
// ──────────────────────────────────────────────────────────────────────

using System.Text.Json.Nodes;
using InterceptorProxy;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var configPath = Path.GetFullPath(
    OptionValue(args, "--config") ?? args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "interceptor-proxy.json");
var check = args.Contains("--check");
var log = (string line) => Console.Error.WriteLine(line); // stdout is the MCP channel

ProxyConfig config;
try
{
    config = ProxyConfig.Load(configPath);
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
{
    log($"interceptor-proxy: cannot load {configPath}: {ex.Message}");
    return 1;
}

var baseDirectory = Path.GetDirectoryName(configPath)!;
var auditLogPath = config.AuditLog is { } auditLog ? Path.GetFullPath(auditLog, baseDirectory) : null;
var auditLock = new Lock();

await using var backend = await McpClient.CreateAsync(config.Backend.CreateTransport("backend", baseDirectory, log));

var interceptorClients = new List<McpClient>();
try
{
    foreach (var (serverName, connection) in config.InterceptorServers)
    {
        interceptorClients.Add(await McpClient.CreateAsync(connection.CreateTransport(serverName, baseDirectory, log)));
    }

    if (check)
    {
        await DescribeAsync(backend, interceptorClients, config);
        return 0;
    }

    await using var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
    {
        BackendClient = backend,
        InterceptorClients = interceptorClients,
        Policy = config.ToPolicy(),
        InvokerRole = config.InvokerRole,
        Events = config.Events,
        TimeoutMs = config.TimeoutMs,
        ToolCallRejection = config.ToolCallRejection,
        ServerInfo = config.Name is { } name ? new Implementation { Name = name, Version = backend.ServerInfo?.Version ?? "1.0.0" } : null,
        OnInterceptorsExecuted = result =>
        {
            if (result.Invocations.Count == 0)
            {
                return; // no interceptor hooks this event and phase
            }

            log(Summarize(result));
            if (auditLogPath is not null)
            {
                lock (auditLock)
                {
                    File.AppendAllText(auditLogPath, AuditRecord(result).ToJsonString() + Environment.NewLine);
                }
            }
        },
    });

    var serverOptions = new McpServerOptions();
    gateway.ConfigureServerOptions(serverOptions);

    await using var server = McpServer.Create(new StdioServerTransport(serverOptions.ServerInfo?.Name ?? "interceptor-proxy"), serverOptions);
    gateway.RegisterNotificationForwarding(server);

    log($"interceptor-proxy: serving '{serverOptions.ServerInfo?.Name}' with {interceptorClients.Count} interceptor server(s); invoker role {config.InvokerRole}.");
    await server.RunAsync();
    return 0;
}
finally
{
    foreach (var client in interceptorClients)
    {
        await client.DisposeAsync();
    }
}

static async Task DescribeAsync(McpClient backend, List<McpClient> interceptorClients, ProxyConfig config)
{
    Console.WriteLine($"Backend: {backend.ServerInfo?.Name} {backend.ServerInfo?.Version}");
    if (backend.ServerCapabilities?.Tools is not null)
    {
        var tools = await backend.ListToolsAsync();
        Console.WriteLine($"  tools: {string.Join(", ", tools.Select(t => t.Name))}");
    }

    var discovered = new List<(Interceptor, McpClient)>();
    foreach (var client in interceptorClients)
    {
        Console.WriteLine($"Interceptor server: {client.ServerInfo?.Name} {client.ServerInfo?.Version}");
        foreach (var interceptor in await client.ListAllInterceptorsAsync())
        {
            discovered.Add((interceptor, client));
            var hooks = string.Join("; ", interceptor.Hooks.Select(h => $"{string.Join(",", h.Events)} {h.Phase.ToString().ToLowerInvariant()}"));
            Console.WriteLine($"  {interceptor.Name,-22} {interceptor.Type.ToString().ToLowerInvariant(),-10} {hooks}");
        }
    }

    // Binding validates the policy against what was discovered: missing required interceptors and
    // hook overrides that widen declared hooks are reported here instead of on the first request.
    var bindings = config.ToPolicy().Bind(discovered);
    Console.WriteLine($"Policy OK: {bindings.Count} interceptor(s) will run; invoker role {config.InvokerRole}.");
    foreach (var binding in bindings.Where(b => b.Overrides is not null || b.Config is not null))
    {
        var o = binding.Overrides;
        var parts = new List<string>();
        if (o?.Mode is { } mode) parts.Add($"mode={mode.ToString().ToLowerInvariant()}");
        if (o?.FailOpen is { } failOpen) parts.Add($"failOpen={failOpen.ToString().ToLowerInvariant()}");
        if (o?.TimeoutMs is { } timeout) parts.Add($"timeoutMs={timeout}");
        if (o?.Hooks is { } hooks) parts.Add($"hooks narrowed to {hooks.Count} entr{(hooks.Count == 1 ? "y" : "ies")}");
        if (binding.Config is not null) parts.Add("config set");
        Console.WriteLine($"  {binding.Interceptor.Name}: {string.Join(", ", parts)}");
    }
}

static string Summarize(InterceptorExecutionResult result)
{
    var phase = result.Phase == InterceptorPhase.Request ? "request " : "response";
    var parts = result.Invocations.Select(i => i.Effect switch
    {
        InterceptorEffect.Blocked => $"{i.Interceptor}=BLOCKED({First(i)})",
        InterceptorEffect.WouldBlock => $"{i.Interceptor}=would-block({First(i)})",
        InterceptorEffect.Modified => $"{i.Interceptor}=modified",
        InterceptorEffect.ShadowModified => $"{i.Interceptor}=would-modify",
        InterceptorEffect.FailedOpen => $"{i.Interceptor}=failed-open({i.Error})",
        _ => $"{i.Interceptor}=ok",
    });
    return $"[interceptors] {result.Event} {phase} {result.Status.ToString().ToLowerInvariant(),-16} {string.Join(" ", parts)} ({result.TotalDurationMs} ms)";

    static string First(InterceptorInvocation i) =>
        (i.Result as ValidationInterceptorResult)?.Messages?.FirstOrDefault()?.Message ?? i.Error ?? "";
}

/// <summary>
/// One audit record per interceptor run: the resolved policy of every invocation and where each
/// value came from (SEP Security Best Practice 3), without payloads.
/// </summary>
static JsonObject AuditRecord(InterceptorExecutionResult result) => new()
{
    ["time"] = DateTimeOffset.UtcNow.ToString("O"),
    ["event"] = result.Event,
    ["phase"] = result.Phase == InterceptorPhase.Request ? "request" : "response",
    ["direction"] = result.Direction == InterceptorDirection.Sending ? "sending" : "receiving",
    ["status"] = result.Status.ToString(),
    ["blockedBy"] = result.AbortedAt?.Interceptor,
    ["reasons"] = new JsonArray([.. result.ValidationErrors.Select(e => (JsonNode)JsonValue.Create($"{e.Interceptor}: {e.Message}"))]),
    ["invocations"] = new JsonArray([.. result.Invocations.Select(i => (JsonNode)new JsonObject
    {
        ["interceptor"] = i.Interceptor,
        ["server"] = i.Server,
        ["type"] = i.Type.ToString().ToLowerInvariant(),
        ["mode"] = i.Mode.ToString().ToLowerInvariant(),
        ["modeSource"] = i.ModeSource.ToString().ToLowerInvariant(),
        ["failOpen"] = i.FailOpen,
        ["failOpenSource"] = i.FailOpenSource.ToString().ToLowerInvariant(),
        ["outcome"] = i.Outcome.ToString(),
        ["effect"] = i.Effect.ToString(),
        ["durationMs"] = i.DurationMs,
    })]),
};

static string? OptionValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
