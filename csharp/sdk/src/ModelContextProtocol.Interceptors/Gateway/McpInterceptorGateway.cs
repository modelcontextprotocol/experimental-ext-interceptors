using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// A transparent MCP gateway that runs interceptors on every request and response before
/// forwarding to a backend MCP server.
/// </summary>
/// <remarks>
/// <para>
/// This class configures an <see cref="McpServer"/> to act as a transparent proxy. To connecting
/// clients it looks like the backend server itself, while every request and response is passed
/// through the interceptors hosted on the configured interceptor servers. Neither the client nor the
/// backend needs to know interceptors exist.
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
    private readonly List<IAsyncDisposable> _ownedClients = new();
    private readonly GatewayInterceptorClientProvider _interceptorClientProvider;
    private readonly InterceptorPhaseRunner _runner;
    private readonly GatewayProxyConfigurator _proxyConfigurator;
    private readonly GatewayInterceptorProtocolBridge _protocolBridge;
    private readonly List<IAsyncDisposable> _notificationRegistrations = new();
    private readonly Lock _notificationRegistrationsLock = new();

    /// <summary>Creates a new <see cref="McpInterceptorGateway"/>.</summary>
    /// <param name="options">Configuration for the gateway.</param>
    public McpInterceptorGateway(McpInterceptorGatewayOptions options)
        : this(options, GetConfiguredInterceptorClients(options))
    {
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
        var jsonOptions = InterceptorJsonUtilities.DefaultOptions;
        _interceptorClientProvider = new GatewayInterceptorClientProvider(
            interceptorClients,
            options.InterceptorServerConnectionResolver);

        _runner = new InterceptorPhaseRunner(new InterceptorRuntimeSettings
        {
            Events = options.Events,
            Role = options.InvokerRole,
            AggregateTimeoutMs = options.TimeoutMs,
            DefaultContext = options.DefaultContext,
            Policy = options.Policy,
            Observer = options.OnInterceptorsExecuted,
            DiscoveryCacheDuration = options.DiscoveryCacheDuration,
            Logger = options.LoggerFactory?.CreateLogger<McpInterceptorGateway>(),
        });

        _proxyConfigurator = new GatewayProxyConfigurator(
            options.BackendClient,
            _interceptorClientProvider,
            _runner,
            options.ToolCallRejection,
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
    /// requests through interceptors to the backend server.
    /// </summary>
    /// <remarks>
    /// Capabilities are mirrored from the backend server. Only capabilities advertised by the
    /// backend have handlers registered. The interceptors extension capability is advertised only when
    /// <see cref="McpInterceptorGatewayOptions.ExposeInterceptorProtocol"/> is enabled.
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
    /// Drops cached <c>interceptors/list</c> results, so the next operation re-discovers interceptors.
    /// </summary>
    public void RefreshInterceptors() => _runner.InvalidateDiscovery();

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
            Forward(NotificationMethods.ToolListChangedNotification);
        }

        if (backendCaps?.Prompts?.ListChanged == true)
        {
            Forward(NotificationMethods.PromptListChangedNotification);
        }

        if (backendCaps?.Resources?.ListChanged == true)
        {
            Forward(NotificationMethods.ResourceListChangedNotification);
        }

        void Forward(string method) => AddNotificationRegistration(
            backend.RegisterNotificationHandler(
                method,
                (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(method, ct))));
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
        ArgumentNullException.ThrowIfNull(options);

        if (options.InterceptorServerConnections is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"{nameof(McpInterceptorGateway)}.{nameof(CreateAsync)} must be used when {nameof(McpInterceptorGatewayOptions.InterceptorServerConnections)} is configured.");
        }

        return options.InterceptorClients is { Count: > 0 } interceptorClients ? interceptorClients : [];
    }
}
