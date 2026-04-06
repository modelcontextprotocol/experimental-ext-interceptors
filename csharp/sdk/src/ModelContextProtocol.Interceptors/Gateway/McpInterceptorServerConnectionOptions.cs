using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// Configuration for connecting the gateway to an external SEP-exposing interceptor server
/// using the standard MCP client transport patterns.
/// </summary>
public sealed class McpInterceptorServerConnectionOptions
{
    /// <summary>
    /// Gets or sets the client transport used to connect to the external interceptor server.
    /// </summary>
    public required IClientTransport Transport { get; set; }

    /// <summary>
    /// Gets or sets optional MCP client configuration used when creating the interceptor client.
    /// </summary>
    public McpClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Gets or sets an optional logger factory used when creating the interceptor client.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}
