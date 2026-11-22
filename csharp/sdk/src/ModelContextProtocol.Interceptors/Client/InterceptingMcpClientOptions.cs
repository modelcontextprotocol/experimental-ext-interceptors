using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Configuration for <see cref="InterceptingMcpClient"/>.
/// </summary>
public sealed class InterceptingMcpClientOptions
{
    /// <summary>
    /// Gets or sets a single interceptor server client. Shorthand for a one-element
    /// <see cref="InterceptorClients"/>; set one or both.
    /// </summary>
    public McpClient? InterceptorClient { get; set; }

    /// <summary>
    /// Gets or sets the interceptor server clients. Their interceptors are merged into one global
    /// order by priority hint (alphabetical tie-break), and each is invoked on its own server.
    /// </summary>
    public IReadOnlyList<McpClient>? InterceptorClients { get; set; }

    /// <summary>
    /// Gets or sets the policy for applying discovered interceptors: which run, their overrides, and
    /// their configuration. When <see langword="null"/>, every discovered interceptor runs with its
    /// declared defaults.
    /// </summary>
    public InterceptorPolicy? Policy { get; set; }

    /// <summary>Gets or sets the event types to intercept. When null or empty, all events are intercepted.</summary>
    public IList<string>? Events { get; set; }

    /// <summary>
    /// Gets or sets an aggregate timeout in milliseconds for all interceptors applied to one phase of
    /// one operation. Per-interceptor timeouts are set through <see cref="Policy"/>.
    /// </summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Gets or sets context values attached to every invocation.</summary>
    public InvokeInterceptorContext? DefaultContext { get; set; }

    /// <summary>
    /// Gets or sets a callback invoked after interceptors run for each phase of each operation.
    /// Exceptions thrown by the callback are logged and ignored.
    /// </summary>
    public Action<InterceptorExecutionResult>? OnInterceptorsExecuted { get; set; }

    /// <summary>
    /// Gets or sets how long each interceptor server's <c>interceptors/list</c> result is reused.
    /// Defaults to one minute; <see cref="TimeSpan.Zero"/> re-discovers on every phase.
    /// </summary>
    public TimeSpan? DiscoveryCacheDuration { get; set; }

    /// <summary>Gets or sets the logger factory used to record interceptor invocations.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}
