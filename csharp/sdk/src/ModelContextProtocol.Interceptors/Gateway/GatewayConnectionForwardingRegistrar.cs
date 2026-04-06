using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

internal sealed class GatewayConnectionForwardingRegistrar
{
    private readonly McpInterceptorGateway _gateway;
    private readonly HashSet<string> _registeredConnectionIds = new(StringComparer.Ordinal);
    private int _registeredFallback;

    internal GatewayConnectionForwardingRegistrar(McpInterceptorGateway gateway)
    {
        _gateway = gateway;
    }

    internal void Configure(McpServerOptions options)
    {
        options.Filters.Message.IncomingFilters.Add(next =>
        {
            return async (context, ct) =>
            {
                var shouldRegister = false;

                lock (_registeredConnectionIds)
                {
                    if (context.Server.SessionId is { Length: > 0 } connectionId)
                    {
                        shouldRegister = _registeredConnectionIds.Add(connectionId);
                    }
                    else if (Interlocked.CompareExchange(ref _registeredFallback, 1, 0) == 0)
                    {
                        shouldRegister = true;
                    }
                }

                if (shouldRegister)
                {
                    _gateway.RegisterNotificationForwarding(context.Server);
                }

                await next(context, ct);
            };
        });
    }
}
