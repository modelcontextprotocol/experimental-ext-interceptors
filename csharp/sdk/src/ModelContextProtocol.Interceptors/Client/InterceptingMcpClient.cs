using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Wraps an <see cref="McpClient"/> so that operations pass through interceptors before they are sent
/// to the server and after its response arrives.
/// </summary>
/// <remarks>
/// <para>
/// This is the client-side counterpart of <see cref="McpInterceptorGateway"/>: use it when your code
/// is the agent. Requests are <em>sent</em> (mutate, then validate the final bytes) and responses are
/// <em>received</em> (validate first, then mutate). A rejected operation throws
/// <see cref="McpInterceptorValidationException"/> or <see cref="McpInterceptorException"/>.
/// </para>
/// <para>
/// This is a concrete class (not inheriting from <see cref="McpClient"/>). Use <see cref="Inner"/>
/// to access the underlying <see cref="McpClient"/> for operations that aren't intercepted.
/// </para>
/// </remarks>
public sealed class InterceptingMcpClient : IAsyncDisposable
{
    private readonly McpClient _inner;
    private readonly IReadOnlyList<McpClient> _interceptorClients;
    private readonly InterceptorPhaseRunner _runner;
    private readonly JsonSerializerOptions _jsonOptions = InterceptorJsonUtilities.DefaultOptions;

    /// <summary>Creates a new <see cref="InterceptingMcpClient"/>.</summary>
    /// <param name="inner">The actual MCP server client.</param>
    /// <param name="options">Configuration including the interceptor server clients.</param>
    public InterceptingMcpClient(McpClient inner, InterceptingMcpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);

        var interceptorClients = new List<McpClient>();
        if (options.InterceptorClient is { } single)
        {
            interceptorClients.Add(single);
        }

        if (options.InterceptorClients is { } many)
        {
            interceptorClients.AddRange(many.Where(c => !interceptorClients.Contains(c)));
        }

        if (interceptorClients.Count == 0)
        {
            throw new ArgumentException(
                $"Set {nameof(InterceptingMcpClientOptions.InterceptorClient)} or {nameof(InterceptingMcpClientOptions.InterceptorClients)}.",
                nameof(options));
        }

        _inner = inner;
        _interceptorClients = interceptorClients;
        _runner = new InterceptorPhaseRunner(new InterceptorRuntimeSettings
        {
            Events = options.Events,
            Role = InterceptorInvokerRole.Client,
            AggregateTimeoutMs = options.TimeoutMs,
            DefaultContext = options.DefaultContext,
            Policy = options.Policy,
            Observer = options.OnInterceptorsExecuted,
            DiscoveryCacheDuration = options.DiscoveryCacheDuration,
            Logger = options.LoggerFactory?.CreateLogger<InterceptingMcpClient>(),
        });
    }

    /// <summary>Gets the underlying MCP server client for direct access.</summary>
    public McpClient Inner => _inner;

    /// <summary>Gets the first interceptor server client.</summary>
    public McpClient InterceptorClient => _interceptorClients[0];

    /// <summary>Gets all interceptor server clients.</summary>
    public IReadOnlyList<McpClient> InterceptorClients => _interceptorClients;

    /// <summary>
    /// Calls a tool on the MCP server, routing through interceptors for the <c>tools/call</c> event.
    /// </summary>
    public ValueTask<CallToolResult> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var callParams = new CallToolRequestParams { Name = name };
        if (arguments is not null)
        {
            callParams.Arguments = arguments.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is JsonElement je ? je : JsonSerializer.SerializeToElement(kvp.Value, _jsonOptions));
        }

        return SendAsync<CallToolRequestParams, CallToolResult>(RequestMethods.ToolsCall, callParams, cancellationToken);
    }

    /// <summary>
    /// Lists tools from the MCP server, routing each page through interceptors for the <c>tools/list</c>
    /// event. Mutations apply: a mutator that removes or rewrites tools changes what is returned.
    /// Calling a returned tool also goes through interceptors.
    /// </summary>
    public async ValueTask<IList<InterceptedMcpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<InterceptedMcpClientTool>();
        string? cursor = null;
        do
        {
            var page = await SendAsync<ListToolsRequestParams, ListToolsResult>(
                RequestMethods.ToolsList, new ListToolsRequestParams { Cursor = cursor }, cancellationToken, GatewayProxyConfigurator.EnsureToolInputSchemas);
            tools.AddRange(page.Tools.Select(tool => new InterceptedMcpClientTool(this, tool)));
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return tools;
    }

    /// <summary>
    /// Lists prompts from the MCP server, routing each page through interceptors for the <c>prompts/list</c> event.
    /// </summary>
    /// <remarks>
    /// Returns protocol definitions rather than the SDK's <see cref="McpClientPrompt"/>, which is bound to
    /// the inner client and would skip interceptors. Use <see cref="GetPromptAsync"/> to get a prompt.
    /// </remarks>
    public async ValueTask<IList<Prompt>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        var prompts = new List<Prompt>();
        string? cursor = null;
        do
        {
            var page = await SendAsync<ListPromptsRequestParams, ListPromptsResult>(
                RequestMethods.PromptsList, new ListPromptsRequestParams { Cursor = cursor }, cancellationToken);
            prompts.AddRange(page.Prompts);
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return prompts;
    }

    /// <summary>
    /// Gets a prompt from the MCP server, routing through interceptors for the <c>prompts/get</c> event.
    /// </summary>
    public ValueTask<GetPromptResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var getParams = new GetPromptRequestParams { Name = name };
        if (arguments is not null)
        {
            getParams.Arguments = arguments.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is JsonElement je ? je : JsonSerializer.SerializeToElement(kvp.Value, _jsonOptions));
        }

        return SendAsync<GetPromptRequestParams, GetPromptResult>(RequestMethods.PromptsGet, getParams, cancellationToken);
    }

    /// <summary>
    /// Lists resources from the MCP server, routing each page through interceptors for the <c>resources/list</c> event.
    /// </summary>
    /// <remarks>
    /// Returns protocol definitions rather than the SDK's <see cref="McpClientResource"/>, which is bound to
    /// the inner client and would skip interceptors. Use <see cref="ReadResourceAsync"/> to read one.
    /// </remarks>
    public async ValueTask<IList<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var resources = new List<Resource>();
        string? cursor = null;
        do
        {
            var page = await SendAsync<ListResourcesRequestParams, ListResourcesResult>(
                RequestMethods.ResourcesList, new ListResourcesRequestParams { Cursor = cursor }, cancellationToken);
            resources.AddRange(page.Resources);
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return resources;
    }

    /// <summary>
    /// Reads a resource from the MCP server, routing through interceptors for the <c>resources/read</c> event.
    /// </summary>
    public ValueTask<ReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return SendAsync<ReadResourceRequestParams, ReadResourceResult>(
            RequestMethods.ResourcesRead, new ReadResourceRequestParams { Uri = uri }, cancellationToken);
    }

    /// <summary>
    /// Subscribes to a resource on the MCP server, routing through interceptors for the
    /// <c>resources/subscribe</c> event.
    /// </summary>
    public async Task SubscribeToResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
#pragma warning disable MCP9005 // resources/subscribe is deprecated upstream but still supported for down-level servers.
        await SendAsync<SubscribeRequestParams, EmptyResult>(
            RequestMethods.ResourcesSubscribe, new SubscribeRequestParams { Uri = uri }, cancellationToken);
#pragma warning restore MCP9005
    }

    /// <summary>
    /// Lists the interceptors available on every interceptor server.
    /// </summary>
    public async ValueTask<ListInterceptorsResult> ListInterceptorsAsync(
        ListInterceptorsRequestParams? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        var interceptors = new List<Interceptor>();
        foreach (var client in _interceptorClients)
        {
            interceptors.AddRange(await client.ListAllInterceptorsAsync(requestParams?.Event, cancellationToken));
        }

        return new ListInterceptorsResult { Interceptors = interceptors };
    }

    /// <summary>
    /// Drops cached <c>interceptors/list</c> results, so the next operation re-discovers interceptors.
    /// </summary>
    public void RefreshInterceptors() => _runner.InvalidateDiscovery();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        foreach (var client in _interceptorClients)
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs request-phase interceptors, sends the (possibly mutated) request as JSON, then runs
    /// response-phase interceptors on the result.
    /// </summary>
    private async ValueTask<TResult> SendAsync<TParams, TResult>(
        string method,
        TParams requestParams,
        CancellationToken cancellationToken,
        Action<JsonNode?>? normalizeResult = null)
        where TResult : class
    {
        var eventName = method;
        var requestPayload = JsonSerializer.SerializeToNode(requestParams, _jsonOptions) ?? new JsonObject();
        var intercept = _runner.ShouldIntercept(eventName);
        var context = intercept ? _runner.CreateContext(_inner.SessionId, user: null) : null;

        if (intercept)
        {
            requestPayload = await _runner.RunPhaseAsync(
                _interceptorClients, eventName, InterceptorPhase.Request, requestPayload, context, cancellationToken);
        }

        var result = await _inner.SendRequestAsync<JsonNode, JsonNode>(
            method, requestPayload, _jsonOptions, cancellationToken: cancellationToken) ?? new JsonObject();
        normalizeResult?.Invoke(result);

        if (intercept)
        {
            result = await _runner.RunPhaseAsync(
                _interceptorClients, eventName, InterceptorPhase.Response, result, context, cancellationToken);
            normalizeResult?.Invoke(result);
        }

        try
        {
            return result.Deserialize<TResult>(_jsonOptions)
                ?? throw new JsonException("The result was null.");
        }
        catch (JsonException ex)
        {
            throw new McpProtocolException($"Received an invalid '{method}' result: {ex.Message}", McpErrorCode.InternalError);
        }
    }
}
