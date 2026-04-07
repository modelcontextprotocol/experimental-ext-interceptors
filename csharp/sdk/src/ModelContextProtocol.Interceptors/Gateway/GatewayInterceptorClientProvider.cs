using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

internal sealed class GatewayInterceptorClientProvider : IAsyncDisposable
{
    private readonly IReadOnlyList<McpClient> _staticClients;
    private readonly Func<MessageContext, string, CancellationToken, ValueTask<IReadOnlyList<McpInterceptorServerConnectionOptions>>>? _connectionResolver;
    private readonly GatewayInterceptorClientPool? _clientPool;

    internal GatewayInterceptorClientProvider(
        IReadOnlyList<McpClient> staticClients,
        Func<MessageContext, string, CancellationToken, ValueTask<IReadOnlyList<McpInterceptorServerConnectionOptions>>>? connectionResolver)
    {
        _staticClients = staticClients;
        _connectionResolver = connectionResolver;
        _clientPool = connectionResolver is null ? null : new GatewayInterceptorClientPool();
    }

    internal IReadOnlyList<McpClient> StaticClients => _staticClients;

    internal async ValueTask<GatewayResolvedInterceptorClients> ResolveAsync(
        MessageContext messageContext,
        string @event,
        CancellationToken cancellationToken)
    {
        if (_connectionResolver is null)
        {
            return new GatewayResolvedInterceptorClients(_staticClients);
        }

        var resolvedConnections = await _connectionResolver(messageContext, @event, cancellationToken) ?? [];
        var resolvedDynamicClients = await _clientPool!.ResolveClientsAsync(resolvedConnections, cancellationToken);

        if (_staticClients.Count == 0)
        {
            return resolvedDynamicClients;
        }

        if (resolvedDynamicClients.Clients.Count == 0)
        {
            await resolvedDynamicClients.DisposeAsync();
            return new GatewayResolvedInterceptorClients(_staticClients);
        }

        var combined = new List<McpClient>(_staticClients.Count + resolvedDynamicClients.Clients.Count);
        combined.AddRange(_staticClients);
        combined.AddRange(resolvedDynamicClients.Clients);
        return new GatewayResolvedInterceptorClients(combined, [resolvedDynamicClients]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientPool is not null)
        {
            await _clientPool.DisposeAsync();
        }
    }
}
