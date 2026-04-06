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
            // In multi-session transports (HTTP), each session has its own McpServer,
            // so we track which servers we've already registered for.
            var registeredServers = new HashSet<McpServer>();
            options.Filters.Message.IncomingFilters.Add(next =>
            {
                return async (context, ct) =>
                {
                    lock (registeredServers)
                    {
                        if (registeredServers.Add(context.Server))
                        {
                            _gateway.RegisterNotificationForwarding(context.Server);
                        }
                    }

                    await next(context, ct);
                };
            });
        }
    }
}
