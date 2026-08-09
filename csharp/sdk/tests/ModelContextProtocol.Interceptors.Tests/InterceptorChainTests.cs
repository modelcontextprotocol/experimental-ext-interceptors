using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using ModelContextProtocol.Server;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

/// <summary>
/// Tests for the public <see cref="InterceptorChain"/> API over real in-memory interceptor servers.
/// </summary>
public class InterceptorChainTests
{
    [Fact]
    public async Task DiscoverAsync_MergesEntriesFromAllServersWithServerAttribution()
    {
        await using var fixture = await TwoServerFixture.CreateAsync(
            serverAInterceptors: [MutationDescriptor("a-mutator", priorityHint: 10)],
            serverBInterceptors: [MutationDescriptor("b-mutator", priorityHint: -10)]);

        var chain = await InterceptorChain.DiscoverAsync([fixture.ClientA, fixture.ClientB]);

        Assert.Equal(2, chain.Entries.Count);
        var entryA = Assert.Single(chain.Entries, e => e.Interceptor.Name == "a-mutator");
        var entryB = Assert.Single(chain.Entries, e => e.Interceptor.Name == "b-mutator");
        Assert.Same(fixture.ClientA, entryA.Server);
        Assert.Same(fixture.ClientB, entryB.Server);
    }

    [Fact]
    public async Task ExecuteAsync_RunsMergedChainInGlobalPriorityOrderAcrossServers()
    {
        // Server A's mutation has the higher priority value, so server B's must run first
        // and server A's must observe B's payload — the opposite of per-server sequencing.
        await using var fixture = await TwoServerFixture.CreateAsync(
            serverAInterceptors: [MutationDescriptor("a-late", priorityHint: 10)],
            serverBInterceptors: [MutationDescriptor("b-early", priorityHint: -10)],
            handler: (req, _) =>
            {
                var obj = req.Payload!.AsObject();
                var order = obj["order"]?.AsArray() ?? [];
                order.Add(req.Name);
                obj["order"] = order.DeepClone();
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = obj });
            });

        var result = await InterceptorChain.ExecuteAsync(
            [fixture.ClientA, fixture.ClientB],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            });

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(2, result.Results.Count);
        var order = result.FinalPayload!["order"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["b-early", "a-late"], order);
    }

    [Fact]
    public async Task ExecuteAsync_FailsClosedWhenAnyServerListFails()
    {
        await using var fixture = await TwoServerFixture.CreateAsync(
            serverAInterceptors: [MutationDescriptor("a-mutator", priorityHint: 0)],
            // Server B does not speak the interceptor protocol at all, so interceptors/list fails.
            serverBInterceptors: null);

        await Assert.ThrowsAsync<McpProtocolException>(async () =>
        {
            await InterceptorChain.ExecuteAsync(
                [fixture.ClientA, fixture.ClientB],
                new ExecuteChainRequestParams
                {
                    Event = InterceptionEvents.ToolsCall,
                    Phase = InterceptorPhase.Request,
                    Payload = JsonNode.Parse("""{}""")!,
                });
        });
    }

    [Fact]
    public async Task SingleClientExecuteChainAsync_StillWorksThroughMergedPath()
    {
        await using var fixture = await TwoServerFixture.CreateAsync(
            serverAInterceptors: [MutationDescriptor("solo-mutator", priorityHint: 0)],
            serverBInterceptors: null,
            handler: (req, _) =>
            {
                var obj = req.Payload!.AsObject();
                obj["mutated"] = true;
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = obj });
            });

        var result = await fixture.ClientA.ExecuteChainAsync(
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{"original":true}""")!,
            });

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.True(result.FinalPayload!["mutated"]!.GetValue<bool>());
        Assert.True(result.FinalPayload["original"]!.GetValue<bool>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Interceptor MutationDescriptor(string name, PriorityHint priorityHint) => new()
    {
        Name = name,
        Type = InterceptorType.Mutation,
        Hooks =
        [
            new InterceptorHook { Events = [InterceptionEvents.All], Phase = InterceptorPhase.Request },
            new InterceptorHook { Events = [InterceptionEvents.All], Phase = InterceptorPhase.Response },
        ],
        PriorityHint = priorityHint,
    };

    private sealed class TwoServerFixture : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _disposables;

        public McpClient ClientA { get; }
        public McpClient ClientB { get; }

        private TwoServerFixture(McpClient clientA, McpClient clientB, List<IAsyncDisposable> disposables)
        {
            ClientA = clientA;
            ClientB = clientB;
            _disposables = disposables;
        }

        /// <summary>
        /// Spins up two in-memory servers. A <see langword="null"/> interceptor list creates a
        /// plain MCP server without the interceptor protocol (its <c>interceptors/list</c> fails).
        /// </summary>
        public static async Task<TwoServerFixture> CreateAsync(
            Interceptor[]? serverAInterceptors,
            Interceptor[]? serverBInterceptors,
            Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>>? handler = null)
        {
            handler ??= (req, _) => new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
            var disposables = new List<IAsyncDisposable>();

            try
            {
                var (serverA, clientA) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
                    "chain-server-a", options => Configure(options, serverAInterceptors, handler));
                disposables.Add(serverA);
                disposables.Add(clientA);

                var (serverB, clientB) = await McpInterceptorGatewayTests.GatewayTestFixture.CreateServerClientPairForTesting(
                    "chain-server-b", options => Configure(options, serverBInterceptors, handler));
                disposables.Add(serverB);
                disposables.Add(clientB);

                return new TwoServerFixture(clientA, clientB, disposables);
            }
            catch
            {
                foreach (var d in disposables)
                {
                    await d.DisposeAsync();
                }

                throw;
            }
        }

        private static void Configure(
            McpServerOptions options,
            Interceptor[]? interceptors,
            Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler)
        {
            if (interceptors is null)
            {
                return;
            }

            var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
            foreach (var descriptor in interceptors)
            {
                collection.Add(new DelegatingInterceptor(descriptor, handler));
            }

            var filter = new InterceptorMessageFilter(collection);
            options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);

            options.Capabilities ??= new();
#pragma warning disable MCPEXP001
            options.Capabilities.Extensions ??= new Dictionary<string, object>();
            options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                new InterceptorsCapability { SupportedEvents = [InterceptionEvents.All] },
                InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
        }

        public async ValueTask DisposeAsync()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    await _disposables[i].DisposeAsync();
                }
                catch
                {
                    // Swallow disposal errors in tests
                }
            }
        }
    }

    private sealed class DelegatingInterceptor : McpServerInterceptor
    {
        private readonly Interceptor _interceptor;
        private readonly Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> _handler;

        public DelegatingInterceptor(
            Interceptor interceptor,
            Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler)
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
            _handler(request, cancellationToken);
    }
}
