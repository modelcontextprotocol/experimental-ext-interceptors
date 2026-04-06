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

        return builder.WithInterceptorGateway(_ => options);
    }

    /// <summary>
    /// Configures the MCP server as a transparent interceptor gateway using options
    /// resolved from the application's service provider.
    /// </summary>
    /// <param name="builder">The server builder.</param>
    /// <param name="optionsFactory">Creates the gateway options from the service provider.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMcpServerBuilder WithInterceptorGateway(
        this IMcpServerBuilder builder,
        Func<IServiceProvider, McpInterceptorGatewayOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        builder.Services.AddSingleton(optionsFactory);
        builder.Services.AddSingleton(sp => new McpInterceptorGateway(sp.GetRequiredService<McpInterceptorGatewayOptions>()));
        builder.Services.AddSingleton<IConfigureOptions<McpServerOptions>, GatewayServerOptionsSetup>();

        return builder;
    }

    private sealed class GatewayServerOptionsSetup : IConfigureOptions<McpServerOptions>
    {
        private readonly McpInterceptorGateway _gateway;
        private readonly GatewayConnectionForwardingRegistrar _forwardingRegistrar;

        internal GatewayServerOptionsSetup(McpInterceptorGateway gateway)
        {
            _gateway = gateway;
            _forwardingRegistrar = new GatewayConnectionForwardingRegistrar(gateway);
        }

        public void Configure(McpServerOptions options)
        {
            _gateway.ConfigureServerOptions(options);
            _forwardingRegistrar.Configure(options);
        }
    }
}
