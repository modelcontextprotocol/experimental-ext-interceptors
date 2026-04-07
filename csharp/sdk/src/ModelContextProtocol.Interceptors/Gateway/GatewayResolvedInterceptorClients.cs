using ModelContextProtocol.Client;

namespace ModelContextProtocol.Interceptors.Gateway;

internal sealed class GatewayResolvedInterceptorClients : IAsyncDisposable
{
    private readonly IReadOnlyList<IAsyncDisposable> _ownedClients;

    internal GatewayResolvedInterceptorClients(IReadOnlyList<McpClient> clients, IReadOnlyList<IAsyncDisposable>? ownedClients = null)
    {
        Clients = clients;
        _ownedClients = ownedClients ?? [];
    }

    internal IReadOnlyList<McpClient> Clients { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var ownedClient in _ownedClients)
        {
            await ownedClient.DisposeAsync();
        }
    }
}
