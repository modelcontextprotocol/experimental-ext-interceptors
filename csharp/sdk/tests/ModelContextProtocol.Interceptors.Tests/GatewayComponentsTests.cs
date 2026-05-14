using System.Text.Json;
using System.IO.Pipes;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

public class GatewayComponentsTests
{
    [Fact]
    public async Task GatewayInterceptorProtocolBridge_UsesCentralizedExtensionKey()
    {
        await using var fixture = await GatewayComponentFixture.CreateAsync();

        var serverOptions = new McpServerOptions
        {
            Capabilities = new ServerCapabilities(),
        };

        var bridge = new GatewayInterceptorProtocolBridge(
            [fixture.InterceptorClient],
            InterceptorJsonUtilities.DefaultOptions);

        bridge.Configure(serverOptions);

#pragma warning disable MCPEXP001
        Assert.NotNull(serverOptions.Capabilities.Extensions);
        Assert.True(serverOptions.Capabilities.Extensions!.ContainsKey(InterceptorProtocolConstants.ExtensionCapabilityKey));
#pragma warning restore MCPEXP001
    }

    [Fact]
    public async Task GatewayProxyConfigurator_ClonesBackendCapabilities()
    {
        await using var fixture = await GatewayComponentFixture.CreateAsync(backendConfigure: options =>
        {
            options.Capabilities ??= new();
            options.Capabilities.Tools ??= new() { ListChanged = true };
            options.Capabilities.Experimental = new Dictionary<string, object>
            {
                ["com.example/test"] = JsonSerializer.SerializeToElement(new { enabled = true }),
            };
            options.Handlers.ListToolsHandler = (request, ct) =>
                new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
            options.Handlers.CallToolHandler = (request, ct) =>
                new ValueTask<CallToolResult>(new CallToolResult());
        });

        var provider = new GatewayInterceptorClientProvider([fixture.InterceptorClient], connectionResolver: null);
        var configurator = new GatewayProxyConfigurator(
            fixture.BackendClient,
            provider,
            events: null,
            timeoutMs: null,
            defaultContext: null,
            InterceptorJsonUtilities.DefaultOptions);

        var serverOptions = new McpServerOptions();
        configurator.Configure(serverOptions, serverInfoOverride: null);

        Assert.NotSame(fixture.BackendClient.ServerCapabilities, serverOptions.Capabilities);
        Assert.NotNull(serverOptions.Capabilities?.Experimental);
        Assert.True(serverOptions.Capabilities!.Experimental!.ContainsKey("com.example/test"));

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task McpInterceptorGateway_CreateAsync_ConnectsExternalInterceptorTransport()
    {
        await using var fixture = await GatewayComponentFixture.CreateAsync();

        await using var gateway = await McpInterceptorGateway.CreateAsync(new McpInterceptorGatewayOptions
        {
            BackendClient = fixture.BackendClient,
            InterceptorServerConnections =
            [
                new McpInterceptorServerConnectionOptions
                {
                    Transport = fixture.CreateInterceptorTransport(),
                },
            ],
            ExposeInterceptorProtocol = true,
        });

        var serverOptions = new McpServerOptions();
        gateway.ConfigureServerOptions(serverOptions);

#pragma warning disable MCPEXP001
        Assert.True(serverOptions.Capabilities?.Extensions?.ContainsKey(InterceptorProtocolConstants.ExtensionCapabilityKey) ?? false);
#pragma warning restore MCPEXP001
    }

    [Fact]
    public void WithInterceptorGateway_RejectsExternalConnectionsInBuilderPath()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddMcpServer().WithInterceptorGateway(new McpInterceptorGatewayOptions
            {
                BackendClient = NullMcpClient.Instance,
                InterceptorServerConnections =
                [
                    new McpInterceptorServerConnectionOptions
                    {
                        Transport = new ThrowingClientTransport(),
                    },
                ],
            }));

        Assert.Contains(nameof(McpInterceptorGateway.CreateAsync), exception.Message);
    }

    [Fact]
    public async Task GatewayInterceptorClientProvider_ResolvesConnectionsFromMessageContext()
    {
        await using var fixture = await GatewayComponentFixture.CreateAsync();

        var provider = new GatewayInterceptorClientProvider(
            staticClients: [],
            connectionResolver: (context, @event, ct) =>
            {
                var userName = context.User?.Identity?.Name;
                return ValueTask.FromResult<IReadOnlyList<McpInterceptorServerConnectionOptions>>(
                    @event == InterceptionEvents.ToolsCall && userName == "alice"
                        ?
                        [
                            new McpInterceptorServerConnectionOptions
                            {
                                ConnectionId = "alice",
                                Transport = fixture.CreateInterceptorTransport(),
                            },
                        ]
                        : []);
            });

        var message = new JsonRpcRequest
        {
            Method = RequestMethods.ToolsCall,
            Id = new RequestId(1),
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams { Name = "echo" }),
            Context = new JsonRpcMessageContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], authenticationType: "test")),
            },
        };
        var context = new MessageContext(NullMcpServer.Instance, message);

        await using var resolved = await provider.ResolveAsync(context, InterceptionEvents.ToolsCall, CancellationToken.None);

        Assert.Single(resolved.Clients);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task McpInterceptorGateway_SupportsResolverOnlyTransparentMode()
    {
        await using var fixture = await GatewayComponentFixture.CreateAsync();

        await using var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
        {
            BackendClient = fixture.BackendClient,
            InterceptorServerConnectionResolver = (context, @event, ct) =>
                ValueTask.FromResult<IReadOnlyList<McpInterceptorServerConnectionOptions>>(
                    @event == InterceptionEvents.ToolsCall
                        ?
                        [
                            new McpInterceptorServerConnectionOptions
                            {
                                Transport = fixture.CreateInterceptorTransport(),
                            },
                        ]
                        : []),
        });

        var serverOptions = new McpServerOptions();
        gateway.ConfigureServerOptions(serverOptions);

        Assert.NotNull(serverOptions.Handlers.CallToolHandler);
    }

    [Fact]
    public void McpInterceptorGateway_RejectsDynamicResolverWhenSepPassthroughEnabled()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new McpInterceptorGateway(new McpInterceptorGatewayOptions
            {
                BackendClient = NullMcpClient.Instance,
                InterceptorClients = [NullMcpClient.Instance],
                ExposeInterceptorProtocol = true,
                InterceptorServerConnectionResolver = (context, @event, ct) =>
                    ValueTask.FromResult<IReadOnlyList<McpInterceptorServerConnectionOptions>>([]),
            }));

        Assert.Contains(nameof(McpInterceptorGatewayOptions.InterceptorServerConnectionResolver), exception.Message);
    }

    private sealed class GatewayComponentFixture : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _disposables;

        public McpClient BackendClient { get; }
        public McpClient InterceptorClient { get; }
        private readonly Func<IClientTransport> _interceptorTransportFactory;

        private GatewayComponentFixture(McpClient backendClient, McpClient interceptorClient, Func<IClientTransport> interceptorTransportFactory, List<IAsyncDisposable> disposables)
        {
            BackendClient = backendClient;
            InterceptorClient = interceptorClient;
            _interceptorTransportFactory = interceptorTransportFactory;
            _disposables = disposables;
        }

        public static async Task<GatewayComponentFixture> CreateAsync(Action<McpServerOptions>? backendConfigure = null)
        {
            var disposables = new List<IAsyncDisposable>();

            try
            {
                var (backendServer, backendClient) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
                    "component-backend",
                    options =>
                    {
                        options.Capabilities ??= new();
                        options.Capabilities.Tools ??= new();
                        options.Handlers.ListToolsHandler = (request, ct) =>
                            new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                        options.Handlers.CallToolHandler = (request, ct) =>
                            new ValueTask<CallToolResult>(new CallToolResult());
                        backendConfigure?.Invoke(options);
                    });
                disposables.Add(backendServer);
                disposables.Add(backendClient);

                Func<IClientTransport>? interceptorTransportFactory = null;
                var (interceptorServer, interceptorClient) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
                    "component-interceptor",
                    options =>
                    {
                        var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
                        collection.Add(new TestInterceptor(
                            new Interceptor
                            {
                                Name = "validator",
                                Type = InterceptorType.Validation,
                                Hooks =
                                [
                                    new InterceptorHook { Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Request },
                                    new InterceptorHook { Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Response },
                                ],
                            },
                            (_, _, _, _) => new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success())));

                        var filter = new InterceptorMessageFilter(collection);
                        options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);
                        options.Capabilities ??= new();
#pragma warning disable MCPEXP001
                        options.Capabilities.Extensions ??= new Dictionary<string, object>();
                        options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                            new InterceptorsCapability { SupportedEvents = [InterceptionEvents.ToolsCall] },
                            InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
                    });
                disposables.Add(interceptorServer);
                disposables.Add(interceptorClient);

                interceptorTransportFactory = () =>
                {
                    var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
                    var serverFromClient = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.GetClientHandleAsString());

                    var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
                    var clientFromServer = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.GetClientHandleAsString());

                    var serverOptions = new McpServerOptions
                    {
                        ServerInfo = new() { Name = "component-interceptor-external", Version = "1.0.0" },
                    };
                    var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
                    collection.Add(new TestInterceptor(
                        new Interceptor
                        {
                            Name = "validator",
                            Type = InterceptorType.Validation,
                            Hooks =
                            [
                                new InterceptorHook { Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Request },
                                new InterceptorHook { Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Response },
                            ],
                        },
                        (_, _, _, _) => new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success())));

                    var filter = new InterceptorMessageFilter(collection);
                    serverOptions.Filters.Message.IncomingFilters.Add(filter.CreateFilter);
                    serverOptions.Capabilities ??= new();
#pragma warning disable MCPEXP001
                    serverOptions.Capabilities.Extensions ??= new Dictionary<string, object>();
                    serverOptions.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                        new InterceptorsCapability { SupportedEvents = [InterceptionEvents.ToolsCall] },
                        InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001

                    var serverTransport = new StreamServerTransport(serverFromClient, serverToClient);
                    var server = McpServer.Create(serverTransport, serverOptions);
                    _ = server.RunAsync();
                    disposables.Add(server);

                    return new StreamClientTransport(clientToServer, clientFromServer);
                };

                return new GatewayComponentFixture(backendClient, interceptorClient, interceptorTransportFactory, disposables);
            }
            catch
            {
                foreach (var disposable in disposables)
                {
                    await disposable.DisposeAsync();
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                await _disposables[i].DisposeAsync();
            }
        }

        public IClientTransport CreateInterceptorTransport() => _interceptorTransportFactory();
    }

    private sealed class ThrowingClientTransport : IClientTransport
    {
        public string Name => "throwing";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

#pragma warning disable MCPEXP002
    private sealed class NullMcpServer : McpServer
#pragma warning restore MCPEXP002
    {
        internal static NullMcpServer Instance { get; } = new();

        public override string? SessionId => null;
        public override string? NegotiatedProtocolVersion => null;
        public override ClientCapabilities? ClientCapabilities => null;
        public override Implementation? ClientInfo => null;
        public override McpServerOptions ServerOptions => new();
        public override IServiceProvider? Services => null;
        public override LoggingLevel? LoggingLevel => null;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler) => throw new NotSupportedException();
        public override Task RunAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

#pragma warning disable MCPEXP002
    private sealed class NullMcpClient : McpClient
#pragma warning restore MCPEXP002
    {
        internal static NullMcpClient Instance { get; } = new();

        public override string? SessionId => null;
        public override string? NegotiatedProtocolVersion => null;
        public override ServerCapabilities ServerCapabilities => new();
        public override Implementation ServerInfo => new() { Name = "null", Version = "1.0.0" };
        public override string? ServerInstructions => null;
        public override Task<ClientCompletionDetails> Completion => Task.FromResult(new ClientCompletionDetails());
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler) => throw new NotSupportedException();
        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestInterceptor : McpServerInterceptor
    {
        private readonly Interceptor _interceptor;
        private readonly Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> _handler;

        public TestInterceptor(
            Interceptor interceptor,
            Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> handler)
        {
            _interceptor = interceptor;
            _handler = handler;
        }

        public override Interceptor ProtocolInterceptor => _interceptor;
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<InterceptorResult> InvokeAsync(
            InvokeInterceptorRequestParams request,
            McpServer server,
            IServiceProvider? services,
            CancellationToken cancellationToken = default) =>
            _handler(request, server, services, cancellationToken);
    }
}
