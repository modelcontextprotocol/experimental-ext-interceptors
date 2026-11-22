using System.Collections.Concurrent;
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WhyInterceptorsDemo;

/// <summary>
/// The processes and connections behind the story: the third-party workspace server and the
/// security guard run as separate stdio processes; the interceptor proxy runs in this process so
/// the story can show what it did.
/// </summary>
internal sealed class World : IAsyncDisposable
{
    private World(McpClient workspace, McpClient guard, RecordingClientTransport guardTransport, string auditLogPath)
    {
        Workspace = workspace;
        Guard = guard;
        GuardTraffic = guardTransport;
        AuditLogPath = auditLogPath;
    }

    /// <summary>A direct connection to Acme Workspace: what an agent has today.</summary>
    public McpClient Workspace { get; }

    /// <summary>The proxy's connection to the Security Guard interceptor server.</summary>
    public McpClient Guard { get; }

    /// <summary>Every JSON-RPC message exchanged with the Security Guard.</summary>
    public RecordingClientTransport GuardTraffic { get; }

    /// <summary>The file the guard's audit-trail interceptor writes.</summary>
    public string AuditLogPath { get; }

    /// <summary>The user the agent acts for, as the proxy reports it to interceptors.</summary>
    public const string User = "dana@acme.example";

    public static async Task<World> StartAsync()
    {
        var auditLogPath = Path.Combine(Path.GetTempPath(), $"why-interceptors-audit-{Environment.ProcessId}.jsonl");
        File.Delete(auditLogPath);

        var workspace = await McpClient.CreateAsync(new StdioClientTransport(Self("acme-workspace", "--workspace-server")));
        var guardTransport = new RecordingClientTransport(new StdioClientTransport(Self("security-guard", "--guard-server", "--audit-log", auditLogPath)));
        var guard = await McpClient.CreateAsync(guardTransport);
        return new World(workspace, guard, guardTransport, auditLogPath);
    }

    /// <summary>
    /// Starts an interceptor proxy in front of Acme Workspace and returns the agent's connection to
    /// it. To the agent, the proxy is indistinguishable from the workspace server itself.
    /// </summary>
    public async Task<ProxiedConnection> ConnectThroughProxyAsync(InterceptorPolicy policy)
    {
        var traces = new ConcurrentQueue<InterceptorExecutionResult>();
        var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
        {
            BackendClient = Workspace,
            InterceptorClients = [Guard],
            Policy = policy,
            InvokerRole = InterceptorInvokerRole.Client, // protect the agent from the server it calls
            DefaultContext = new InvokeInterceptorContext { Principal = new InterceptorPrincipal { Type = "user", Id = User } },
            OnInterceptorsExecuted = traces.Enqueue,
        });

        var serverOptions = new McpServerOptions();
        gateway.ConfigureServerOptions(serverOptions);

        var toProxy = new Pipe();
        var fromProxy = new Pipe();
        var proxy = McpServer.Create(new StreamServerTransport(toProxy.Reader.AsStream(), fromProxy.Writer.AsStream()), serverOptions);
        var running = proxy.RunAsync();

        var agent = await McpClient.CreateAsync(
            new StreamClientTransport(toProxy.Writer.AsStream(), fromProxy.Reader.AsStream()),
            new McpClientOptions { ClientInfo = new Implementation { Name = "demo-agent", Version = "1.0.0" } });

        return new ProxiedConnection(agent, traces, async () =>
        {
            await agent.DisposeAsync();
            await proxy.DisposeAsync();
            try { await running; } catch { }
            await gateway.DisposeAsync();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Workspace.DisposeAsync();
        await Guard.DisposeAsync();
        File.Delete(AuditLogPath);
    }

    /// <summary>Launches this same program in another role.</summary>
    private static StdioClientTransportOptions Self(string name, params string[] roleArgs)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this program.");
        var viaDotnetHost = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return new StdioClientTransportOptions
        {
            Name = name,
            Command = processPath,
            Arguments = viaDotnetHost ? [typeof(World).Assembly.Location, .. roleArgs] : roleArgs,
        };
    }
}

/// <summary>The agent's connection through the interceptor proxy, plus what the proxy reported.</summary>
internal sealed class ProxiedConnection(
    McpClient agent,
    ConcurrentQueue<InterceptorExecutionResult> traces,
    Func<Task> dispose) : IAsyncDisposable
{
    public McpClient Agent { get; } = agent;

    /// <summary>Takes the interceptor runs recorded since the last call.</summary>
    public List<InterceptorExecutionResult> TakeTraces()
    {
        var taken = new List<InterceptorExecutionResult>();
        while (traces.TryDequeue(out var trace))
        {
            taken.Add(trace);
        }

        return taken;
    }

    public async ValueTask DisposeAsync() => await dispose();
}
