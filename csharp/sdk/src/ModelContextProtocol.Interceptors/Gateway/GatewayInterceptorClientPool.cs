using ModelContextProtocol.Client;

namespace ModelContextProtocol.Interceptors.Gateway;

internal sealed class GatewayInterceptorClientPool : IAsyncDisposable
{
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, Lazy<Task<McpClient>>> _cachedClients = new(StringComparer.Ordinal);

    internal async ValueTask<GatewayResolvedInterceptorClients> ResolveClientsAsync(
        IReadOnlyList<McpInterceptorServerConnectionOptions> connections,
        CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
        {
            return new GatewayResolvedInterceptorClients([]);
        }

        var clients = new List<McpClient>(connections.Count);
        var ownedClients = new List<IAsyncDisposable>();

        foreach (var connection in connections)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(connection.Transport);

            if (string.IsNullOrWhiteSpace(connection.ConnectionId))
            {
                var client = await McpClient.CreateAsync(
                    connection.Transport,
                    connection.ClientOptions,
                    connection.LoggerFactory,
                    cancellationToken);
                clients.Add(client);
                ownedClients.Add(client);
                continue;
            }

            Lazy<Task<McpClient>> lazyClient;
            lock (_cacheLock)
            {
                if (!_cachedClients.TryGetValue(connection.ConnectionId, out lazyClient!))
                {
                    lazyClient = new Lazy<Task<McpClient>>(
                        () => McpClient.CreateAsync(
                            connection.Transport,
                            connection.ClientOptions,
                            connection.LoggerFactory,
                            cancellationToken));
                    _cachedClients[connection.ConnectionId] = lazyClient;
                }
            }

            try
            {
                clients.Add(await lazyClient.Value);
            }
            catch
            {
                lock (_cacheLock)
                {
                    _cachedClients.Remove(connection.ConnectionId);
                }

                throw;
            }
        }

        return new GatewayResolvedInterceptorClients(clients, ownedClients);
    }

    public async ValueTask DisposeAsync()
    {
        List<Lazy<Task<McpClient>>> cachedClients;
        lock (_cacheLock)
        {
            cachedClients = [.. _cachedClients.Values];
            _cachedClients.Clear();
        }

        foreach (var lazyClient in cachedClients)
        {
            try
            {
                var client = await lazyClient.Value;
                await client.DisposeAsync();
            }
            catch
            {
            }
        }
    }
}
