using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors;

/// <summary>
/// Extension methods for <see cref="IMcpServerBuilder"/> to configure a transparent interceptor gateway.
/// </summary>
public static class McpInterceptorGatewayBuilderExtensions
{
    /// <summary>
    /// Configures the MCP server as a transparent interceptor gateway that proxies
    /// requests through interceptor chains to a backend server.
    /// </summary>
    /// <param name="builder">The server builder.</param>
    /// <param name="options">Configuration for the gateway including backend and interceptor clients.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method configures request proxying and interceptor passthrough. It also registers
    /// notification forwarding automatically via an incoming message filter that captures the
    /// <see cref="McpServer"/> reference on first request. If you need earlier control over
    /// notification forwarding, use <see cref="McpInterceptorGateway.ConfigureServerOptions"/>
    /// and <see cref="McpInterceptorGateway.RegisterNotificationForwarding"/> manually.
    /// </para>
    /// </remarks>
    public static IMcpServerBuilder WithInterceptorGateway(
        this IMcpServerBuilder builder,
        McpInterceptorGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var gateway = new McpInterceptorGateway(options);

        builder.Services.AddSingleton(gateway);
        builder.Services.AddSingleton<IConfigureOptions<McpServerOptions>>(
            new GatewayServerOptionsSetup(gateway));

        return builder;
    }

    private sealed class GatewayServerOptionsSetup : IConfigureOptions<McpServerOptions>
    {
        private readonly McpInterceptorGateway _gateway;

        internal GatewayServerOptionsSetup(McpInterceptorGateway gateway)
        {
            _gateway = gateway;
        }

        public void Configure(McpServerOptions options)
        {
            _gateway.ConfigureServerOptions(options);

            // Wire notification forwarding lazily via an incoming message filter.
            // Deduplicate by stable session identity when available. `context.Server`
            // is a destination-bound wrapper created per message, so reference identity
            // would re-register on every request.
            var registeredSessionIds = new HashSet<string>(StringComparer.Ordinal);
            var registeredFallback = 0;
            options.Filters.Message.IncomingFilters.Add(next =>
            {
                return async (context, ct) =>
                {
                    var registered = false;

                    lock (registeredSessionIds)
                    {
                        if (context.Server.SessionId is { Length: > 0 } sessionId)
                        {
                            registered = registeredSessionIds.Add(sessionId);
                        }
                        else if (Interlocked.CompareExchange(ref registeredFallback, 1, 0) == 0)
                        {
                            // Non-session transports (for example stdio/stream) have a single
                            // logical connection, so register only once.
                            registered = true;
                        }
                    }

                    if (registered)
                    {
                        _gateway.RegisterNotificationForwarding(context.Server);
                    }

                    await next(context, ct);
                };
            });
        }
    }
}
