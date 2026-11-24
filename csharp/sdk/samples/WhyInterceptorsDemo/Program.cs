// ──────────────────────────────────────────────────────────────────────
// Why Interceptors? — a scripted, three-minute demo
//
// An agent asks a third-party MCP server for help with its inbox. One email
// carries a prompt injection, the CRM returns data the model never needed,
// and the agent leaks both. Then the same agent and the same server run
// again behind an interceptor proxy, and the leak is stopped.
//
// The agent's reasoning is scripted so the demo is deterministic; every MCP
// message in it is real. This one binary plays three parts:
//
//   (no arguments)        the story: agent, interceptor proxy, narration
//   --workspace-server    "Acme Workspace", the third-party MCP server (stdio)
//   --guard-server        "Security Guard", the interceptor server (stdio)
//
// Story options:
//   --auto     advance on a timer instead of waiting for Enter
//   --fast     no pauses or typing animation (smoke tests)
//   --act N    start at act N (1-4)
//   --export-html FILE   also save the whole run as a colored HTML page
// ──────────────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Interceptors;
using WhyInterceptorsDemo;

switch (args)
{
    case ["--workspace-server", ..]:
        await RunStdioServerAsync("acme-workspace", builder => builder.WithTools<AcmeWorkspaceTools>());
        return 0;

    case ["--guard-server", .. var rest]:
        SecurityGuardInterceptors.AuditLogPath = OptionValue(rest, "--audit-log") is { } path ? Path.GetFullPath(path) : null;
        await RunStdioServerAsync("security-guard", builder => builder.WithInterceptors<SecurityGuardInterceptors>());
        return 0;

    default:
        return await Story.RunAsync(args);
}

static async Task RunStdioServerAsync(string name, Action<IMcpServerBuilder> configure)
{
    var builder = Host.CreateApplicationBuilder();

    // stdout carries MCP messages; keep logs on stderr and quiet.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    var mcp = builder.Services
        .AddMcpServer(options => options.ServerInfo = new() { Name = name, Version = "1.0.0" })
        .WithStdioServerTransport();
    configure(mcp);
    await builder.Build().RunAsync();
}

static string? OptionValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
