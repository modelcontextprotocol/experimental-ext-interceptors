using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// A discovered interceptor paired with the MCP server that advertised it and the invoker's policy
/// for it. <c>interceptor/invoke</c> for this interceptor is always sent to <see cref="Server"/>.
/// </summary>
public sealed class InterceptorBinding
{
    /// <summary>Gets the interceptor descriptor, as returned by <c>interceptors/list</c>.</summary>
    public required Interceptor Interceptor { get; init; }

    /// <summary>Gets the client connected to the MCP server hosting this interceptor.</summary>
    public required McpClient Server { get; init; }

    /// <summary>Gets the invoker's overrides for this interceptor, if any.</summary>
    public InterceptorOverrides? Overrides { get; init; }

    /// <summary>Gets the configuration sent as <c>config</c> on each invocation, if any.</summary>
    public JsonNode? Config { get; init; }
}
