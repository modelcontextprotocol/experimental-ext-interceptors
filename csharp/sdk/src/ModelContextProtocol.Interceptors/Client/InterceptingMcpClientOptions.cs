using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Configuration options for <see cref="InterceptingMcpClient"/>.
/// </summary>
public sealed class InterceptingMcpClientOptions
{
    /// <summary>Gets or sets the client connected to the interceptor server. Required.</summary>
    public required McpClient InterceptorClient { get; set; }

    /// <summary>
    /// Gets or sets the event types to intercept. When null or empty, all events are intercepted.
    /// </summary>
    public IList<string>? Events { get; set; }

    /// <summary>Gets or sets the default timeout in milliseconds for interceptor invocations.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Gets or sets the default context to attach to interceptor invocations.</summary>
    public InvokeInterceptorContext? DefaultContext { get; set; }
}
