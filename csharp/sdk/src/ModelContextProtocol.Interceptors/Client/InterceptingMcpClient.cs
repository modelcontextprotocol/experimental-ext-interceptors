using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// A gateway wrapper that intercepts MCP operations by routing them through an interceptor server
/// before/after forwarding to the actual MCP server.
/// </summary>
/// <remarks>
/// <para>
/// This class orchestrates the gateway pattern: for each intercepted operation, it first sends the
/// request payload to the interceptor server for request-phase processing (validation, mutation,
/// observability), then forwards the (possibly mutated) payload to the actual server, then sends
/// the response payload to the interceptor server for response-phase processing.
/// </para>
/// <para>
/// This is a concrete class (not inheriting from <see cref="McpClient"/>). Use <see cref="Inner"/>
/// to access the underlying <see cref="McpClient"/> for operations that aren't intercepted.
/// </para>
/// </remarks>
public sealed class InterceptingMcpClient : IAsyncDisposable
{
    private readonly McpClient _inner;
    private readonly McpClient _interceptorClient;
    private readonly InterceptingMcpClientOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = InterceptorJsonUtilities.DefaultOptions;

    /// <summary>Creates a new <see cref="InterceptingMcpClient"/>.</summary>
    /// <param name="inner">The actual MCP server client.</param>
    /// <param name="options">Configuration including the interceptor server client.</param>
    public InterceptingMcpClient(McpClient inner, InterceptingMcpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.InterceptorClient);

        _inner = inner;
        _interceptorClient = options.InterceptorClient;
        _options = options;
    }

    /// <summary>Gets the underlying MCP server client for direct access.</summary>
    public McpClient Inner => _inner;

    /// <summary>Gets the interceptor server client.</summary>
    public McpClient InterceptorClient => _interceptorClient;

    /// <summary>
    /// Calls a tool on the MCP server, routing through interceptors for the <c>tools/call</c> event.
    /// </summary>
    public async ValueTask<CallToolResult> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldIntercept(InterceptorEvents.ToolsCall))
        {
            return await _inner.CallToolAsync(name, arguments, cancellationToken: cancellationToken);
        }

        // Build request payload
        var callParams = new CallToolRequestParams { Name = name };
        if (arguments is not null)
        {
            callParams.Arguments = arguments.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is JsonElement je ? je : JsonSerializer.SerializeToElement(kvp.Value, _jsonOptions));
        }

        var requestPayload = JsonSerializer.SerializeToNode(callParams, _jsonOptions)!;

        // Request phase
        var (processedPayload, aborted) = await RunChainPhaseAsync(
            InterceptorEvents.ToolsCall, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (aborted)
        {
            throw new McpInterceptorValidationException("Request-phase interceptor validation failed for tools/call.");
        }

        // Forward to actual server using the raw CallToolRequestParams overload
        var mutatedParams = JsonSerializer.Deserialize<CallToolRequestParams>(processedPayload, _jsonOptions)
            ?? callParams;
        var result = await _inner.CallToolAsync(mutatedParams, cancellationToken);

        // Response phase
        if (!ShouldIntercept(InterceptorEvents.ToolsCall))
        {
            return result;
        }

        var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
        var (processedResponse, responseAborted) = await RunChainPhaseAsync(
            InterceptorEvents.ToolsCall, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseAborted)
        {
            throw new McpInterceptorValidationException("Response-phase interceptor validation failed for tools/call.");
        }

        return JsonSerializer.Deserialize<CallToolResult>(processedResponse, _jsonOptions) ?? result;
    }

    /// <summary>
    /// Lists tools from the MCP server, routing through interceptors for the <c>tools/list</c> event.
    /// </summary>
    public async ValueTask<IList<McpClientTool>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!ShouldIntercept(InterceptorEvents.ToolsList))
        {
            return await _inner.ListToolsAsync(cancellationToken: cancellationToken);
        }

        // Request phase
        var requestPayload = JsonSerializer.SerializeToNode(
            new ListToolsRequestParams(), _jsonOptions)!;
        var (_, aborted) = await RunChainPhaseAsync(
            InterceptorEvents.ToolsList, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (aborted)
        {
            throw new McpInterceptorValidationException("Request-phase interceptor validation failed for tools/list.");
        }

        var tools = await _inner.ListToolsAsync(cancellationToken: cancellationToken);

        // Response phase
        var responsePayload = JsonSerializer.SerializeToNode(
            new ListToolsResult { Tools = tools.Select(t => t.ProtocolTool).ToList() }, _jsonOptions)!;
        await RunChainPhaseAsync(
            InterceptorEvents.ToolsList, InterceptorPhase.Response, responsePayload, cancellationToken);

        return tools;
    }

    /// <summary>
    /// Lists interceptors available on the interceptor server.
    /// </summary>
    public ValueTask<ListInterceptorsResult> ListInterceptorsAsync(
        ListInterceptorsRequestParams? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        return _interceptorClient.ListInterceptorsAsync(requestParams, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await _interceptorClient.DisposeAsync();
    }

    private bool ShouldIntercept(string eventName)
    {
        if (_options.Events is not { Count: > 0 } events)
        {
            return true;
        }

        return events.Contains(eventName);
    }

    private async ValueTask<(JsonNode payload, bool aborted)> RunChainPhaseAsync(
        string eventName, InterceptorPhase phase, JsonNode payload, CancellationToken ct)
    {
        var chainResult = await _interceptorClient.ExecuteChainAsync(
            new ExecuteChainRequestParams
            {
                Event = eventName,
                Phase = phase,
                Payload = payload,
                TimeoutMs = _options.TimeoutMs,
                Context = _options.DefaultContext,
            },
            ct);

        if (chainResult.Status == InterceptorChainStatus.ValidationFailed)
        {
            return (payload, true);
        }

        return (chainResult.FinalPayload ?? payload, false);
    }
}
