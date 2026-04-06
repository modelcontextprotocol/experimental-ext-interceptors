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
    /// Each client's <c>interceptor/executeChain</c> receives the (potentially mutated) payload
    /// from the previous one. If any client's chain returns <see cref="InterceptorChainStatus.ValidationFailed"/>,
    /// the chain is aborted immediately.
    /// </summary>
    /// <returns>The (potentially mutated) payload and whether the chain was aborted.</returns>
    internal async ValueTask<(JsonNode payload, bool aborted)> RunChainPhaseAsync(
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

            if (chainResult.Status == InterceptorChainStatus.ValidationFailed)
            {
                return (currentPayload, true);
            }

            currentPayload = chainResult.FinalPayload ?? currentPayload;
        }

        return (currentPayload, false);
    }
}
