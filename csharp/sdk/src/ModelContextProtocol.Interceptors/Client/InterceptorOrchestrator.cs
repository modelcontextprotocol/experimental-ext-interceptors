using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Applies interceptors hosted on one or more MCP servers to lifecycle events, following the
/// SEP-2624 orchestration pattern.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Discover</b>: call <c>interceptors/list</c> on every server.</item>
/// <item><b>Select and resolve policy</b>: apply an <see cref="InterceptorPolicy"/> (which interceptors,
/// their overrides and configuration) and keep those whose hooks match the event and phase.</item>
/// <item><b>Sort</b>: mutations by resolved priority, then name, across all servers.</item>
/// <item><b>Order by trust boundary</b>: mutate then validate when sending; validate then mutate when receiving.</item>
/// <item><b>Execute</b>: invoke each interceptor with <c>interceptor/invoke</c> on the server that advertised it.</item>
/// <item><b>Aggregate</b>: return the final payload, every invocation, and any validation errors.</item>
/// </list>
/// This is an SDK convenience, not wire protocol: the SEP defines no chain resource or aggregate method.
/// Interceptors with the same name on different servers are not deduplicated.
/// </remarks>
public sealed class InterceptorOrchestrator
{
    /// <summary>Creates an orchestrator from already-discovered bindings.</summary>
    /// <exception cref="InvalidOperationException">A binding's overrides widen the interceptor's declared hooks.</exception>
    public InterceptorOrchestrator(IReadOnlyList<InterceptorBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var binding in bindings)
        {
            InterceptorOverrides.ValidateFor(binding.Interceptor, binding.Overrides);
        }

        Bindings = bindings;
    }

    /// <summary>Gets the interceptors this orchestrator applies, collected from one or more servers.</summary>
    public IReadOnlyList<InterceptorBinding> Bindings { get; }

    /// <summary>Gets or sets the logger that records each invocation's resolved policy.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Discovers interceptors on every server in parallel and binds them according to <paramref name="policy"/>.
    /// </summary>
    /// <remarks>
    /// Discovery is fail-closed: if any server's <c>interceptors/list</c> fails, this throws rather
    /// than silently dropping that server's interceptors.
    /// </remarks>
    public static async ValueTask<InterceptorOrchestrator> DiscoverAsync(
        IEnumerable<McpClient> servers,
        InterceptorPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var serverList = servers.ToList();
        var listed = await Task.WhenAll(serverList.Select(
            server => server.ListAllInterceptorsAsync(cancellationToken: cancellationToken).AsTask())).ConfigureAwait(false);

        var discovered = serverList.SelectMany((server, i) => listed[i].Select(interceptor => (interceptor, server)));
        return new InterceptorOrchestrator((policy ?? new InterceptorPolicy()).Bind(discovered));
    }

    /// <summary>Runs the interceptors that apply to one lifecycle event.</summary>
    public ValueTask<InterceptorExecutionResult> ExecuteAsync(
        InterceptorExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InterceptorExecutionEngine.ExecuteAsync(CreateEntries(Bindings), request, cancellationToken, Logger);
    }

    /// <summary>
    /// Discovers interceptors on the given servers and runs those that apply to one lifecycle event.
    /// </summary>
    /// <remarks>
    /// The request's aggregate <see cref="InterceptorExecutionRequest.TimeoutMs"/> covers discovery too:
    /// the invocations get what is left of it, and if it elapses during discovery the result has
    /// <see cref="InterceptorExecutionStatus.Timeout"/> status.
    /// </remarks>
    public static async ValueTask<InterceptorExecutionResult> ExecuteAsync(
        IEnumerable<McpClient> servers,
        InterceptorExecutionRequest request,
        InterceptorPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        InterceptorExecutionEngine.ValidateRequest(request);

        if (request.TimeoutMs is not int timeoutMs)
        {
            var orchestrator = await DiscoverAsync(servers, policy, cancellationToken).ConfigureAwait(false);
            return await orchestrator.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        InterceptorOrchestrator discovered;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(timeoutMs);
            try
            {
                discovered = await DiscoverAsync(servers, policy, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return InterceptorExecutionEngine.TimedOut(
                    request,
                    $"Interceptor discovery for '{request.Event}' did not complete within the aggregate timeout.",
                    stopwatch.ElapsedMilliseconds);
            }
        }

        var remaining = new InterceptorExecutionRequest
        {
            Event = request.Event,
            Phase = request.Phase,
            Direction = request.Direction,
            Payload = request.Payload,
            TimeoutMs = Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds),
            Context = request.Context,
        };
        return await discovered.ExecuteAsync(remaining, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<InterceptorExecutionEngine.ExecutionEntry> CreateEntries(IEnumerable<InterceptorBinding> bindings) =>
        bindings.Select(binding => new InterceptorExecutionEngine.ExecutionEntry(
            binding.Interceptor,
            (invokeParams, ct) => binding.Server.InvokeInterceptorAsync(invokeParams, binding.Interceptor.Type, ct),
            binding.Overrides,
            binding.Config,
            binding.Server.ServerInfo?.Name)).ToList();
}
