using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// Configuration options for <see cref="McpInterceptorGateway"/>.
/// </summary>
public sealed class McpInterceptorGatewayOptions
{
    /// <summary>Gets or sets the client connected to the backend MCP server. Required.</summary>
    public required McpClient BackendClient { get; set; }

    /// <summary>
    /// Gets or sets the clients connected to interceptor servers, executed in order.
    /// Required; must contain at least one client.
    /// </summary>
    public required IReadOnlyList<McpClient> InterceptorClients { get; set; }

    /// <summary>
    /// Gets or sets the event types to intercept. When null or empty, all events are intercepted.
    /// </summary>
    public IList<string>? Events { get; set; }

    /// <summary>Gets or sets the default timeout in milliseconds for interceptor chain execution.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Gets or sets the default context to attach to interceptor invocations.</summary>
    public InvokeInterceptorContext? DefaultContext { get; set; }

    /// <summary>
    /// Gets or sets whether the gateway should expose the SEP interceptor protocol
    /// (`interceptors/list`, `interceptor/invoke`, `interceptor/executeChain`) to connecting clients.
    /// When <see langword="false"/>, the gateway is transparent by default and only proxies the backend surface.
    /// </summary>
    public bool ExposeInterceptorProtocol { get; set; }

    /// <summary>
    /// Gets or sets the server info to advertise to connecting clients.
    /// When null, the backend server's info is proxied.
    /// </summary>
    public Implementation? ServerInfo { get; set; }
}
