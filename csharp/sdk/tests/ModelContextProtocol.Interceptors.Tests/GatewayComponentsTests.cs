using System.Text.Json;
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

        var chainRunner = new InterceptorChainRunner([fixture.InterceptorClient]);
        var configurator = new GatewayProxyConfigurator(
            fixture.BackendClient,
            chainRunner,
            InterceptorJsonUtilities.DefaultOptions);

        var serverOptions = new McpServerOptions();
        configurator.Configure(serverOptions, serverInfoOverride: null);

        Assert.NotSame(fixture.BackendClient.ServerCapabilities, serverOptions.Capabilities);
        Assert.NotNull(serverOptions.Capabilities?.Experimental);
        Assert.True(serverOptions.Capabilities!.Experimental!.ContainsKey("com.example/test"));
    }

    private sealed class GatewayComponentFixture : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _disposables;

        public McpClient BackendClient { get; }
        public McpClient InterceptorClient { get; }

        private GatewayComponentFixture(McpClient backendClient, McpClient interceptorClient, List<IAsyncDisposable> disposables)
        {
            BackendClient = backendClient;
            InterceptorClient = interceptorClient;
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
                                Phase = InterceptorPhase.Both,
                                Events = [InterceptorEvents.ToolsCall],
                            },
                            (_, _, _, _) => new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success())));

                        var filter = new InterceptorMessageFilter(collection);
                        options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);
                        options.Capabilities ??= new();
#pragma warning disable MCPEXP001
                        options.Capabilities.Extensions ??= new Dictionary<string, object>();
                        options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                            new InterceptorsCapability { SupportedEvents = [InterceptorEvents.ToolsCall] },
                            InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
                    });
                disposables.Add(interceptorServer);
                disposables.Add(interceptorClient);

                return new GatewayComponentFixture(backendClient, interceptorClient, disposables);
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
