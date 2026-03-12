using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// Represents an invocable interceptor hosted by an MCP server. Analogous to <see cref="McpServerTool"/>
/// but for the interceptors extension.
/// </summary>
public abstract class McpServerInterceptor : IMcpServerPrimitive
{
    /// <summary>Gets the protocol-level interceptor definition.</summary>
    public abstract Interceptor ProtocolInterceptor { get; }

    /// <summary>Gets the metadata for this interceptor instance.</summary>
    public abstract IReadOnlyList<object> Metadata { get; }

    /// <summary>Invokes this interceptor with the given parameters.</summary>
    /// <param name="request">The invocation parameters.</param>
    /// <param name="server">The MCP server hosting this interceptor.</param>
    /// <param name="services">The scoped service provider for this request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The interceptor result.</returns>
    public abstract ValueTask<InterceptorResult> InvokeAsync(
        InvokeInterceptorRequestParams request,
        McpServer server,
        IServiceProvider? services,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an <see cref="McpServerInterceptor"/> from a delegate.
    /// </summary>
    public static McpServerInterceptor Create(
        Delegate method,
        McpServerInterceptorCreateOptions? options = null) =>
        ReflectionMcpServerInterceptor.Create(method, options);

    /// <inheritdoc />
    public override string ToString() => ProtocolInterceptor.Name;

    /// <inheritdoc />
    string IMcpServerPrimitive.Id => ProtocolInterceptor.Name;

    /// <inheritdoc />
    IReadOnlyList<object> IMcpServerPrimitive.Metadata => Metadata;
}
