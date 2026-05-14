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
internal sealed class InterceptorChainRunner
{
    private readonly IReadOnlyList<McpClient> _interceptorClients;
    private readonly IList<string>? _events;
    private readonly int? _timeoutMs;
    private readonly InvokeInterceptorContext? _defaultContext;

    internal InterceptorChainRunner(
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
    /// Runs the interceptor chain across all configured interceptor clients sequentially.
    /// Each client's <see cref="McpClientInterceptorExtensions.ExecuteChainAsync"/> (SDK-level chain
    /// orchestration via list + invoke) receives the original or last successful payload from the
    /// previous one. Any non-success result stops the chain immediately.
    /// </summary>
    /// <returns>The payload after the last successful client and the final chain status.</returns>
    internal async ValueTask<(JsonNode payload, InterceptorChainStatus status)> RunChainPhaseAsync(
        string eventName, InterceptorPhase phase, JsonNode payload, CancellationToken ct)
    {
        var currentPayload = payload;

        foreach (var client in _interceptorClients)
        {
            var chainResult = await client.ExecuteChainAsync(
                new ExecuteChainRequestParams
                {
                    Event = eventName,
                    Phase = phase,
                    Payload = currentPayload,
                    TimeoutMs = _timeoutMs,
                    Context = _defaultContext,
                },
                ct);

            if (chainResult.Status != InterceptorChainStatus.Success)
            {
                return (currentPayload, chainResult.Status);
            }

            currentPayload = chainResult.FinalPayload ?? currentPayload;
        }

        return (currentPayload, InterceptorChainStatus.Success);
    }

    /// <summary>
    /// Throws an appropriate exception for a non-success chain status.
    /// </summary>
    internal static void ThrowChainFailure(string operation, InterceptorPhase phase, InterceptorChainStatus status)
    {
        var phaseText = phase == InterceptorPhase.Request ? "Request" : "Response";
        if (status == InterceptorChainStatus.ValidationFailed)
        {
            throw new McpInterceptorValidationException($"{phaseText}-phase interceptor validation failed for {operation}.");
        }

        throw new InvalidOperationException($"{phaseText}-phase interceptor chain failed for {operation} with status '{status}'.");
    }
}
