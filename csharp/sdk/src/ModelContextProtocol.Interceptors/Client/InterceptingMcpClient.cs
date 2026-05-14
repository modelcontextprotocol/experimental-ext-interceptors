using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Gateway;
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
/// sinks), then forwards the (possibly mutated) payload to the actual server, then sends
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
    private readonly InterceptorChainRunner _chainRunner;
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
        _chainRunner = new InterceptorChainRunner(
            [options.InterceptorClient],
            options.Events,
            options.TimeoutMs,
            options.DefaultContext);
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
        if (!_chainRunner.ShouldIntercept(InterceptionEvents.ToolsCall))
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
        var (processedPayload, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ToolsCall, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("tools/call", InterceptorPhase.Request, requestStatus);
        }

        // Forward to actual server using the raw CallToolRequestParams overload
        var mutatedParams = JsonSerializer.Deserialize<CallToolRequestParams>(processedPayload, _jsonOptions)
            ?? callParams;
        var result = await _inner.CallToolAsync(mutatedParams, cancellationToken);

        // Response phase
        if (!_chainRunner.ShouldIntercept(InterceptionEvents.ToolsCall))
        {
            return result;
        }

        var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
        var (processedResponse, responseStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ToolsCall, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("tools/call", InterceptorPhase.Response, responseStatus);
        }

        return JsonSerializer.Deserialize<CallToolResult>(processedResponse, _jsonOptions) ?? result;
    }

    /// <summary>
    /// Lists tools from the MCP server, routing through interceptors for the <c>tools/list</c> event.
    /// </summary>
    public async ValueTask<IList<McpClientTool>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_chainRunner.ShouldIntercept(InterceptionEvents.ToolsList))
        {
            return await _inner.ListToolsAsync(cancellationToken: cancellationToken);
        }

        // Request phase
        var requestPayload = JsonSerializer.SerializeToNode(
            new ListToolsRequestParams(), _jsonOptions)!;
        var (_, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ToolsList, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("tools/list", InterceptorPhase.Request, requestStatus);
        }

        var tools = await _inner.ListToolsAsync(cancellationToken: cancellationToken);

        // Response phase
        var responsePayload = JsonSerializer.SerializeToNode(
            new ListToolsResult { Tools = tools.Select(t => t.ProtocolTool).ToList() }, _jsonOptions)!;
        var (_, responseStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ToolsList, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("tools/list", InterceptorPhase.Response, responseStatus);
        }

        return tools;
    }

    /// <summary>
    /// Lists prompts from the MCP server, routing through interceptors for the <c>prompts/list</c> event.
    /// </summary>
    public async ValueTask<IList<McpClientPrompt>> ListPromptsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_chainRunner.ShouldIntercept(InterceptionEvents.PromptsList))
        {
            return await _inner.ListPromptsAsync(cancellationToken: cancellationToken);
        }

        // Request phase
        var requestPayload = JsonSerializer.SerializeToNode(
            new ListPromptsRequestParams(), _jsonOptions)!;
        var (_, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.PromptsList, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("prompts/list", InterceptorPhase.Request, requestStatus);
        }

        var prompts = await _inner.ListPromptsAsync(cancellationToken: cancellationToken);

        // Response phase
        var responsePayload = JsonSerializer.SerializeToNode(
            new ListPromptsResult { Prompts = prompts.Select(p => p.ProtocolPrompt).ToList() }, _jsonOptions)!;
        var (_, responseStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.PromptsList, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("prompts/list", InterceptorPhase.Response, responseStatus);
        }

        return prompts;
    }

    /// <summary>
    /// Gets a prompt from the MCP server, routing through interceptors for the <c>prompts/get</c> event.
    /// </summary>
    public async ValueTask<GetPromptResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_chainRunner.ShouldIntercept(InterceptionEvents.PromptsGet))
        {
            return await _inner.GetPromptAsync(name, arguments, cancellationToken: cancellationToken);
        }

        // Build request payload
        var getParams = new GetPromptRequestParams { Name = name };
        if (arguments is not null)
        {
            getParams.Arguments = arguments.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is JsonElement je ? je : JsonSerializer.SerializeToElement(kvp.Value, _jsonOptions));
        }

        var requestPayload = JsonSerializer.SerializeToNode(getParams, _jsonOptions)!;

        // Request phase
        var (processedPayload, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.PromptsGet, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("prompts/get", InterceptorPhase.Request, requestStatus);
        }

        var mutatedParams = JsonSerializer.Deserialize<GetPromptRequestParams>(processedPayload, _jsonOptions)
            ?? getParams;
        var result = await _inner.GetPromptAsync(mutatedParams, cancellationToken);

        // Response phase
        var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
        var (processedResponse, responseStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.PromptsGet, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("prompts/get", InterceptorPhase.Response, responseStatus);
        }

        return JsonSerializer.Deserialize<GetPromptResult>(processedResponse, _jsonOptions) ?? result;
    }

    /// <summary>
    /// Lists resources from the MCP server, routing through interceptors for the <c>resources/list</c> event.
    /// </summary>
    public async ValueTask<IList<McpClientResource>> ListResourcesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_chainRunner.ShouldIntercept(InterceptionEvents.ResourcesList))
        {
            return await _inner.ListResourcesAsync(cancellationToken: cancellationToken);
        }

        // Request phase
        var requestPayload = JsonSerializer.SerializeToNode(
            new ListResourcesRequestParams(), _jsonOptions)!;
        var (_, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ResourcesList, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("resources/list", InterceptorPhase.Request, requestStatus);
        }

        var resources = await _inner.ListResourcesAsync(cancellationToken: cancellationToken);

        // Response phase
        var responsePayload = JsonSerializer.SerializeToNode(
            new ListResourcesResult { Resources = resources.Select(r => r.ProtocolResource).ToList() }, _jsonOptions)!;
        var (_, responseStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ResourcesList, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("resources/list", InterceptorPhase.Response, responseStatus);
        }

        return resources;
    }

    /// <summary>
    /// Reads a resource from the MCP server, routing through interceptors for the <c>resources/read</c> event.
    /// </summary>
    public async ValueTask<ReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!_chainRunner.ShouldIntercept(InterceptionEvents.ResourcesRead))
        {
            return await _inner.ReadResourceAsync(uri, cancellationToken: cancellationToken);
        }

        // Build request payload
        var readParams = new ReadResourceRequestParams { Uri = uri };
        var requestPayload = JsonSerializer.SerializeToNode(readParams, _jsonOptions)!;

        // Request phase
        var (processedPayload, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ResourcesRead, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("resources/read", InterceptorPhase.Request, requestStatus);
        }

        var mutatedParams = JsonSerializer.Deserialize<ReadResourceRequestParams>(processedPayload, _jsonOptions)
            ?? readParams;
        var result = await _inner.ReadResourceAsync(mutatedParams, cancellationToken);

        // Response phase
        var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
        var (processedResponse, responseStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ResourcesRead, InterceptorPhase.Response, responsePayload, cancellationToken);

        if (responseStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("resources/read", InterceptorPhase.Response, responseStatus);
        }

        return JsonSerializer.Deserialize<ReadResourceResult>(processedResponse, _jsonOptions) ?? result;
    }

    /// <summary>
    /// Subscribes to a resource on the MCP server, routing through request-phase interceptors
    /// for the <c>resources/subscribe</c> event.
    /// </summary>
    public async Task SubscribeToResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!_chainRunner.ShouldIntercept(InterceptionEvents.ResourcesSubscribe))
        {
            await _inner.SubscribeToResourceAsync(uri, cancellationToken: cancellationToken);
            return;
        }

        // Build request payload
        var subscribeParams = new SubscribeRequestParams { Uri = uri };
        var requestPayload = JsonSerializer.SerializeToNode(subscribeParams, _jsonOptions)!;

        // Request phase
        var (processedPayload, requestStatus) = await _chainRunner.RunChainPhaseAsync(
            InterceptionEvents.ResourcesSubscribe, InterceptorPhase.Request, requestPayload, cancellationToken);

        if (requestStatus != InterceptorChainStatus.Success)
        {
            InterceptorChainRunner.ThrowChainFailure("resources/subscribe", InterceptorPhase.Request, requestStatus);
        }

        var mutatedParams = JsonSerializer.Deserialize<SubscribeRequestParams>(processedPayload, _jsonOptions)
            ?? subscribeParams;
        await _inner.SubscribeToResourceAsync(mutatedParams, cancellationToken);
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

}
