using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// A transparent MCP gateway that proxies requests through interceptor chains
/// before forwarding to a backend MCP server.
/// </summary>
/// <remarks>
/// <para>
/// This class configures an <see cref="McpServer"/> to act as a transparent proxy. To connecting
/// clients, it appears to be the backend server itself, but all requests are routed through
/// configured interceptor servers for validation, mutation, and sink interceptors.
/// </para>
/// <para>
/// Usage:
/// <list type="number">
/// <item>Connect to the backend and interceptor servers as <see cref="McpClient"/> instances.</item>
/// <item>Create an <see cref="McpInterceptorGateway"/> with the clients.</item>
/// <item>Call <see cref="ConfigureServerOptions"/> to wire up proxy handlers on your server options.</item>
/// <item>Create your <see cref="McpServer"/> with those options.</item>
/// <item>Call <see cref="RegisterNotificationForwarding"/> to forward backend notifications.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class McpInterceptorGateway : IAsyncDisposable
{
    private readonly McpInterceptorGatewayOptions _options;
    private readonly IReadOnlyList<McpClient> _interceptorClients;
    private readonly List<IAsyncDisposable> _ownedClients = new();
    private readonly GatewayInterceptorClientProvider _interceptorClientProvider;
    private readonly GatewayProxyConfigurator _proxyConfigurator;
    private readonly GatewayInterceptorProtocolBridge _protocolBridge;
    private readonly List<IAsyncDisposable> _notificationRegistrations = new();
    private readonly Lock _notificationRegistrationsLock = new();

    /// <summary>Creates a new <see cref="McpInterceptorGateway"/>.</summary>
    /// <param name="options">Configuration for the gateway.</param>
    public McpInterceptorGateway(McpInterceptorGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BackendClient);

        _interceptorClients = GetConfiguredInterceptorClients(options);
        if (options.ExposeInterceptorProtocol && options.InterceptorServerConnectionResolver is not null)
        {
            throw new ArgumentException(
                $"{nameof(McpInterceptorGatewayOptions.InterceptorServerConnectionResolver)} is only supported for the transparent proxy path. " +
                $"Disable {nameof(McpInterceptorGatewayOptions.ExposeInterceptorProtocol)} or provide static interceptor clients for SEP passthrough.",
                nameof(options));
        }

        if (_interceptorClients.Count == 0 && options.InterceptorServerConnectionResolver is null)
        {
            throw new ArgumentException(
                $"At least one of {nameof(McpInterceptorGatewayOptions.InterceptorClients)}, {nameof(McpInterceptorGatewayOptions.InterceptorServerConnections)}, " +
                $"or {nameof(McpInterceptorGatewayOptions.InterceptorServerConnectionResolver)} is required.",
                nameof(options));
        }

        _options = options;
        var jsonOptions = InterceptorJsonUtilities.DefaultOptions;
        _interceptorClientProvider = new GatewayInterceptorClientProvider(
            _interceptorClients,
            options.InterceptorServerConnectionResolver);

        _proxyConfigurator = new GatewayProxyConfigurator(
            options.BackendClient,
            _interceptorClientProvider,
            options.Events,
            options.TimeoutMs,
            options.DefaultContext,
            jsonOptions);
        _protocolBridge = new GatewayInterceptorProtocolBridge(_interceptorClients, jsonOptions);
    }

    private McpInterceptorGateway(McpInterceptorGatewayOptions options, IReadOnlyList<McpClient> interceptorClients)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BackendClient);

        if (options.ExposeInterceptorProtocol && options.InterceptorServerConnectionResolver is not null)
        {
            throw new ArgumentException(
                $"{nameof(McpInterceptorGatewayOptions.InterceptorServerConnectionResolver)} is only supported for the transparent proxy path. " +
                $"Disable {nameof(McpInterceptorGatewayOptions.ExposeInterceptorProtocol)} or provide static interceptor clients for SEP passthrough.",
                nameof(options));
        }

        if (interceptorClients.Count == 0 && options.InterceptorServerConnectionResolver is null)
        {
            throw new ArgumentException(
                $"At least one of {nameof(McpInterceptorGatewayOptions.InterceptorClients)}, {nameof(McpInterceptorGatewayOptions.InterceptorServerConnections)}, " +
                $"or {nameof(McpInterceptorGatewayOptions.InterceptorServerConnectionResolver)} is required.",
                nameof(options));
        }

        _options = options;
        _interceptorClients = interceptorClients;
        var jsonOptions = InterceptorJsonUtilities.DefaultOptions;
        _interceptorClientProvider = new GatewayInterceptorClientProvider(
            interceptorClients,
            options.InterceptorServerConnectionResolver);

        _proxyConfigurator = new GatewayProxyConfigurator(
            options.BackendClient,
            _interceptorClientProvider,
            options.Events,
            options.TimeoutMs,
            options.DefaultContext,
            jsonOptions);
        _protocolBridge = new GatewayInterceptorProtocolBridge(interceptorClients, jsonOptions);
    }

    /// <summary>
    /// Creates a gateway and connects any configured external interceptor servers using the standard MCP client transport pattern.
    /// </summary>
    public static async Task<McpInterceptorGateway> CreateAsync(
        McpInterceptorGatewayOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var ownedClients = new List<McpClient>();
        try
        {
            var interceptorClients = new List<McpClient>();
            if (options.InterceptorClients is { Count: > 0 })
            {
                interceptorClients.AddRange(options.InterceptorClients);
            }

            if (options.InterceptorServerConnections is { Count: > 0 })
            {
                foreach (var connection in options.InterceptorServerConnections)
                {
                    ArgumentNullException.ThrowIfNull(connection);
                    ArgumentNullException.ThrowIfNull(connection.Transport);

                    var client = await McpClient.CreateAsync(
                        connection.Transport,
                        connection.ClientOptions,
                        connection.LoggerFactory,
                        cancellationToken);
                    interceptorClients.Add(client);
                    ownedClients.Add(client);
                }
            }

            var gateway = new McpInterceptorGateway(options, interceptorClients);
            gateway._ownedClients.AddRange(ownedClients);
            return gateway;
        }
        catch
        {
            foreach (var client in ownedClients)
            {
                await client.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>Gets the backend MCP client.</summary>
    public McpClient BackendClient => _options.BackendClient;

    /// <summary>
    /// Configures the given <see cref="McpServerOptions"/> with proxy handlers that forward
    /// requests through interceptor chains to the backend server.
    /// </summary>
    /// <remarks>
    /// Capabilities are mirrored from the backend server. Only capabilities advertised by the
    /// backend will have handlers registered. The interceptors extension capability is also advertised.
    /// </remarks>
    public void ConfigureServerOptions(McpServerOptions serverOptions)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        _proxyConfigurator.Configure(serverOptions, _options.ServerInfo);

        if (_options.ExposeInterceptorProtocol)
        {
            _protocolBridge.Configure(serverOptions);
        }
    }

    /// <summary>
    /// Registers notification forwarding from the backend server to the proxy server.
    /// Backend notifications (tools/list_changed, prompts/list_changed, resources/list_changed)
    /// are re-sent to connecting clients through the proxy server.
    /// </summary>
    /// <param name="proxyServer">The proxy <see cref="McpServer"/> to forward notifications through.</param>
    public void RegisterNotificationForwarding(McpServer proxyServer)
    {
        ArgumentNullException.ThrowIfNull(proxyServer);

        var backend = _options.BackendClient;
        var backendCaps = backend.ServerCapabilities;

        if (backendCaps?.Tools?.ListChanged == true)
        {
            AddNotificationRegistration(
                backend.RegisterNotificationHandler(
                    NotificationMethods.ToolListChangedNotification,
                    (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(
                        NotificationMethods.ToolListChangedNotification, ct))));
        }

        if (backendCaps?.Prompts?.ListChanged == true)
        {
            AddNotificationRegistration(
                backend.RegisterNotificationHandler(
                    NotificationMethods.PromptListChangedNotification,
                    (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(
                        NotificationMethods.PromptListChangedNotification, ct))));
        }

        if (backendCaps?.Resources?.ListChanged == true)
        {
            AddNotificationRegistration(
                backend.RegisterNotificationHandler(
                    NotificationMethods.ResourceListChangedNotification,
                    (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(
                        NotificationMethods.ResourceListChangedNotification, ct))));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<IAsyncDisposable> registrations;
        lock (_notificationRegistrationsLock)
        {
            registrations = [.. _notificationRegistrations];
            _notificationRegistrations.Clear();
        }

        foreach (var registration in registrations)
        {
            await registration.DisposeAsync();
        }

        foreach (var ownedClient in _ownedClients)
        {
            await ownedClient.DisposeAsync();
        }

        _ownedClients.Clear();
        await _interceptorClientProvider.DisposeAsync();
    }

    private void AddNotificationRegistration(IAsyncDisposable registration)
    {
        lock (_notificationRegistrationsLock)
        {
            _notificationRegistrations.Add(registration);
        }
    }

    private static IReadOnlyList<McpClient> GetConfiguredInterceptorClients(McpInterceptorGatewayOptions options)
    {
        if (options.InterceptorClients is { Count: > 0 } interceptorClients)
        {
            return interceptorClients;
        }

        if (options.InterceptorServerConnections is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"{nameof(McpInterceptorGateway)}.{nameof(CreateAsync)} must be used when {nameof(McpInterceptorGatewayOptions.InterceptorServerConnections)} is configured.");
        }

        return [];
    }

}
