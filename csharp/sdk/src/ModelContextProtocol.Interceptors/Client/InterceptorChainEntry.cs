using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// An entry in an interceptor chain: an interceptor descriptor paired with the MCP server that
/// hosts it. The server reference is used to route <c>interceptor/invoke</c> calls to the correct
/// server when a chain spans multiple servers.
/// </summary>
public sealed class InterceptorChainEntry
{
    /// <summary>Gets the interceptor descriptor, as returned by <c>interceptors/list</c>.</summary>
    public required Interceptor Interceptor { get; init; }

    /// <summary>Gets the client connected to the MCP server hosting this interceptor.</summary>
    public required McpClient Server { get; init; }
}
