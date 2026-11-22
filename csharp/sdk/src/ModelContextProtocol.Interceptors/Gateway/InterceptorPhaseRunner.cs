using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// Encapsulates the logic for running interceptor chain phases (request/response)
/// against one or more interceptor server clients. Used by both <see cref="Client.InterceptingMcpClient"/>
/// and <see cref="McpInterceptorGateway"/>.
/// </summary>
internal sealed class InterceptorPhaseRunner
{
    private readonly IReadOnlyList<McpClient> _interceptorClients;
    private readonly IList<string>? _events;
    private readonly int? _timeoutMs;
    private readonly InvokeInterceptorContext? _defaultContext;

    internal InterceptorPhaseRunner(
        IReadOnlyList<McpClient> interceptorClients,
        IList<string>? events = null,
        int? timeoutMs = null,
        InvokeInterceptorContext? defaultContext = null)
    {
        _interceptorClients = interceptorClients;
        _events = events;
        _timeoutMs = timeoutMs;
        _defaultContext = defaultContext;
    }

    /// <summary>
    /// Returns whether the given event should be intercepted based on the configured event filter.
    /// </summary>
    internal bool ShouldIntercept(string eventName)
    {
        if (_events is not { Count: > 0 } events)
        {
            return true;
        }

        return events.Contains(eventName);
    }

    /// <summary>
    /// Runs the interceptor chain phase across all configured interceptor clients as a single
    /// merged chain per the SEP: interceptors discovered from every client are combined, sorted
    /// globally by priority hint (alphabetical tie-break), and each is invoked on the client that
    /// hosts it via <see cref="InterceptorOrchestrator.ExecuteAsync(IEnumerable{McpClient}, InterceptorExecutionRequest, CancellationToken)"/>.
    /// </summary>
    /// <returns>The final payload (the original on failure) and the chain status.</returns>
    internal async ValueTask<(JsonNode payload, InterceptorExecutionStatus status)> RunPhaseAsync(
        string eventName, InterceptorPhase phase, JsonNode payload, CancellationToken ct)
    {
        var chainResult = await InterceptorOrchestrator.ExecuteAsync(
            _interceptorClients,
            new InterceptorExecutionRequest
            {
                Event = eventName,
                Phase = phase,
                Payload = payload,
                TimeoutMs = _timeoutMs,
                Context = _defaultContext,
            },
            ct);

        return chainResult.Status == InterceptorExecutionStatus.Success
            ? (chainResult.FinalPayload ?? payload, InterceptorExecutionStatus.Success)
            : (payload, chainResult.Status);
    }

    /// <summary>
    /// Throws an appropriate exception for a non-success chain status.
    /// </summary>
    internal static void ThrowFailure(string operation, InterceptorPhase phase, InterceptorExecutionStatus status)
    {
        var phaseText = phase == InterceptorPhase.Request ? "Request" : "Response";
        if (status == InterceptorExecutionStatus.ValidationFailed)
        {
            throw new McpInterceptorValidationException($"{phaseText}-phase interceptor validation failed for {operation}.");
        }

        throw new InvalidOperationException($"{phaseText}-phase interceptor chain failed for {operation} with status '{status}'.");
    }
}
