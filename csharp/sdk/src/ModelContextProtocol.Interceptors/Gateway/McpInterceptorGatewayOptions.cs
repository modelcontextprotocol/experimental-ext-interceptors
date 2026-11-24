using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// Configuration options for <see cref="McpInterceptorGateway"/>.
/// </summary>
public sealed class McpInterceptorGatewayOptions
{
    /// <summary>Gets or sets the client connected to the backend MCP server. Required.</summary>
    public required McpClient BackendClient { get; set; }

    /// <summary>
    /// Gets or sets the clients connected to interceptor servers. Their interceptors are merged
    /// into one global order by priority hint (alphabetical tie-break).
    /// Use this when the interceptor clients are already connected.
    /// </summary>
    public IReadOnlyList<McpClient>? InterceptorClients { get; set; }

    /// <summary>
    /// Gets or sets external interceptor server connections that should be created by the gateway
    /// using <see cref="McpClient.CreateAsync(IClientTransport, McpClientOptions?, ILoggerFactory?, CancellationToken)"/>.
    /// Use <see cref="McpInterceptorGateway.CreateAsync(McpInterceptorGatewayOptions, CancellationToken)"/>
    /// when this property is populated.
    /// </summary>
    public IReadOnlyList<McpInterceptorServerConnectionOptions>? InterceptorServerConnections { get; set; }

    /// <summary>
    /// Gets or sets an optional dynamic resolver for external interceptor server connections used by the
    /// transparent proxy path. This enables per-request interceptor selection based on the existing SDK
    /// <see cref="MessageContext"/> and SEP event name.
    /// Resolved connections are appended after any statically configured <see cref="InterceptorClients"/>.
    /// SEP passthrough exposure is not supported with this resolver.
    /// </summary>
    public Func<MessageContext, string, CancellationToken, ValueTask<IReadOnlyList<McpInterceptorServerConnectionOptions>>>? InterceptorServerConnectionResolver { get; set; }

    /// <summary>
    /// Gets or sets the policy for applying discovered interceptors: which run, their
    /// <see cref="InterceptorOverrides"/> (mode, fail-open, priority, timeout, hook narrowing), and
    /// their configuration. When <see langword="null"/>, every discovered interceptor runs with the
    /// defaults it declares.
    /// </summary>
    public InterceptorPolicy? Policy { get; set; }

    /// <summary>
    /// Gets or sets which side of the trust boundary the gateway protects. This decides the
    /// execution order in each phase.
    /// </summary>
    /// <remarks>
    /// <see cref="InterceptorInvokerRole.Client"/> (default) treats the backend as the far side of the
    /// trust boundary: requests are <em>sent</em> to it (mutate, then validate the final bytes) and
    /// responses are <em>received</em> from it (validate first, then mutate). Use this to protect an
    /// agent from the servers it calls. <see cref="InterceptorInvokerRole.Server"/> treats connecting
    /// clients as the far side: use it to protect a backend you own.
    /// </remarks>
    public InterceptorInvokerRole InvokerRole { get; set; } = InterceptorInvokerRole.Client;

    /// <summary>
    /// Gets or sets the event types to intercept. When null or empty, all events are intercepted.
    /// </summary>
    public IList<string>? Events { get; set; }

    /// <summary>
    /// Gets or sets an aggregate timeout in milliseconds for all interceptors applied to one phase of
    /// one operation. When it elapses, the operation fails regardless of fail-open. Per-interceptor
    /// timeouts are set through <see cref="Policy"/>.
    /// </summary>
    public int? TimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets context values attached to every invocation. The gateway adds a fresh timestamp,
    /// the current trace, the downstream session id and the authenticated user when these are not set.
    /// </summary>
    public InvokeInterceptorContext? DefaultContext { get; set; }

    /// <summary>
    /// Gets or sets how a <c>tools/call</c> rejected by interceptors is reported to the connecting client.
    /// </summary>
    public ToolCallRejectionMode ToolCallRejection { get; set; } = ToolCallRejectionMode.ErrorResult;

    /// <summary>
    /// Gets or sets a callback invoked after interceptors run for each phase of each operation, with
    /// every invocation and its resolved policy. Use it for audit trails, metrics or live displays.
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

    /// <summary>
    /// Gets or sets whether the gateway should expose the SEP interceptor protocol
    /// (`interceptors/list`, `interceptor/invoke`) to connecting clients.
    /// When <see langword="false"/>, the gateway is transparent by default and only proxies the backend surface.
    /// </summary>
    public bool ExposeInterceptorProtocol { get; set; }

    /// <summary>
    /// Gets or sets the server info to advertise to connecting clients.
    /// When null, the backend server's info is proxied.
    /// </summary>
    public Implementation? ServerInfo { get; set; }
}

/// <summary>How a gateway reports a <c>tools/call</c> that interceptors rejected.</summary>
public enum ToolCallRejectionMode
{
    /// <summary>
    /// Return a tool result with <c>isError: true</c> whose text gives the reason, and the SEP's
    /// <c>validationErrors</c> under <c>_meta["io.modelcontextprotocol/interceptors"]</c>. Clients pass
    /// tool error results to the model, so the agent can explain the block or try something else.
    /// </summary>
    ErrorResult,

    /// <summary>
    /// Return the SEP's JSON-RPC error: <c>-32602 Interceptor validation failed</c> with
    /// <c>data.validationErrors</c> (or <c>-32603</c>/<c>-32000</c> for mutation failures and timeouts).
    /// </summary>
    ProtocolError,
}
