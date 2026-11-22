using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>Settings shared by every surface that applies interceptors to proxied MCP traffic.</summary>
internal sealed record InterceptorRuntimeSettings
{
    public IList<string>? Events { get; init; }
    public InterceptorInvokerRole Role { get; init; } = InterceptorInvokerRole.Client;
    public int? AggregateTimeoutMs { get; init; }
    public InvokeInterceptorContext? DefaultContext { get; init; }
    public InterceptorPolicy? Policy { get; init; }
    public Action<InterceptorExecutionResult>? Observer { get; init; }
    public TimeSpan? DiscoveryCacheDuration { get; init; }
    public ILogger? Logger { get; init; }
}

/// <summary>
/// Applies interceptors to one phase of a proxied MCP operation. Shared by
/// <see cref="Client.InterceptingMcpClient"/> and <see cref="McpInterceptorGateway"/>.
/// </summary>
/// <remarks>
/// Interceptors discovered on every interceptor client are merged and ordered globally (see
/// <see cref="InterceptorOrchestrator"/>). Discovery results are cached; a failed run throws an
/// <see cref="McpInterceptorException"/>.
/// </remarks>
internal sealed class InterceptorPhaseRunner
{
    private readonly InterceptorRuntimeSettings _settings;
    private readonly InterceptorPolicy _policy;
    private readonly InterceptorDiscoveryCache _discovery;
    private readonly ILogger _logger;

    internal InterceptorPhaseRunner(InterceptorRuntimeSettings settings)
    {
        _settings = settings;
        _policy = settings.Policy ?? new InterceptorPolicy();
        _discovery = new InterceptorDiscoveryCache(settings.DiscoveryCacheDuration);
        _logger = settings.Logger ?? NullLogger.Instance;
    }

    internal InterceptorInvokerRole Role => _settings.Role;

    /// <summary>Returns whether <paramref name="eventName"/> passes the configured event filter.</summary>
    internal bool ShouldIntercept(string eventName) =>
        _settings.Events is not { Count: > 0 } events || events.Contains(eventName);

    /// <summary>Drops cached discovery results, so the next operation re-lists interceptors.</summary>
    internal void InvalidateDiscovery() => _discovery.Invalidate();

    /// <summary>
    /// Runs the interceptors that apply to <paramref name="eventName"/> in <paramref name="phase"/>.
    /// </summary>
    /// <returns>The payload after all applied mutations.</returns>
    /// <exception cref="McpInterceptorException">The interceptors rejected the operation.</exception>
    internal async ValueTask<JsonNode> RunPhaseAsync(
        IReadOnlyList<McpClient> interceptorClients,
        string eventName,
        InterceptorPhase phase,
        JsonNode payload,
        InvokeInterceptorContext? context,
        CancellationToken cancellationToken)
    {
        var bindings = await BindAsync(interceptorClients, cancellationToken).ConfigureAwait(false);

        var result = await InterceptorExecutionEngine.ExecuteAsync(
            InterceptorOrchestrator.CreateEntries(bindings),
            new InterceptorExecutionRequest
            {
                Event = eventName,
                Phase = phase,
                Direction = _settings.Role.GetDirection(phase),
                Payload = payload,
                TimeoutMs = _settings.AggregateTimeoutMs,
                Context = context ?? _settings.DefaultContext,
            },
            cancellationToken,
            _logger).ConfigureAwait(false);

        Notify(result);

        if (result.Status != InterceptorExecutionStatus.Success)
        {
            throw McpInterceptorException.FromResult(result);
        }

        return result.FinalPayload ?? payload;
    }

    /// <summary>
    /// Builds the context sent with each invocation: the configured defaults, plus a fresh timestamp,
    /// the current trace, and the session and user when known.
    /// </summary>
    internal InvokeInterceptorContext CreateContext(string? sessionId, ClaimsPrincipal? user)
    {
        var defaults = _settings.DefaultContext;
        var activity = Activity.Current;
        return new InvokeInterceptorContext
        {
            Principal = defaults?.Principal ?? CreatePrincipal(user),
            TraceId = defaults?.TraceId ?? activity?.TraceId.ToString(),
            SpanId = defaults?.SpanId ?? activity?.SpanId.ToString(),
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            SessionId = defaults?.SessionId ?? sessionId,
        };
    }

    private async ValueTask<IReadOnlyList<InterceptorBinding>> BindAsync(
        IReadOnlyList<McpClient> interceptorClients,
        CancellationToken cancellationToken)
    {
        // Discovery is fail-closed: an unreachable interceptor server fails the operation.
        var listed = await Task.WhenAll(interceptorClients.Select(
            client => _discovery.GetAsync(client, cancellationToken).AsTask())).ConfigureAwait(false);

        try
        {
            return _policy.Bind(interceptorClients.SelectMany((client, i) => listed[i].Select(interceptor => (interceptor, client))));
        }
        catch (InvalidOperationException ex)
        {
            throw new McpInterceptorException(ex.Message, McpErrorCode.InternalError);
        }
    }

    private void Notify(InterceptorExecutionResult result)
    {
        if (_settings.Observer is not { } observer)
        {
            return;
        }

        try
        {
            observer(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interceptor execution observer threw; ignoring.");
        }
    }

    private static InterceptorPrincipal? CreatePrincipal(ClaimsPrincipal? user) =>
        user?.Identity is { IsAuthenticated: true } identity
            ? new InterceptorPrincipal { Type = "user", Id = identity.Name }
            : null;
}
