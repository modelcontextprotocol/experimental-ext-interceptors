using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// Wires the transparent proxy: mirrors the backend's capabilities and registers a handler for each
/// backend request that runs interceptors around a raw JSON forward.
/// </summary>
/// <remarks>
/// Each forwarded method is intercepted under an event named after the method (for example
/// <c>tools/call</c>), as the working group agreed that any MCP JSON-RPC message is interceptable.
/// Requests and results are forwarded as JSON, so fields this SDK does not model survive the hop
/// and interceptors see exactly what crosses the wire.
/// </remarks>
internal sealed class GatewayProxyConfigurator
{
    /// <summary>The <c>_meta</c> key under which rejected tool calls carry structured details.</summary>
    internal const string RejectionMetaKey = "io.modelcontextprotocol/interceptors";

    private readonly McpClient _backend;
    private readonly GatewayInterceptorClientProvider _interceptorClientProvider;
    private readonly InterceptorPhaseRunner _runner;
    private readonly ToolCallRejectionMode _toolCallRejection;
    private readonly JsonSerializerOptions _jsonOptions;

    internal GatewayProxyConfigurator(
        McpClient backend,
        GatewayInterceptorClientProvider interceptorClientProvider,
        InterceptorPhaseRunner runner,
        ToolCallRejectionMode toolCallRejection,
        JsonSerializerOptions jsonOptions)
    {
        _backend = backend;
        _interceptorClientProvider = interceptorClientProvider;
        _runner = runner;
        _toolCallRejection = toolCallRejection;
        _jsonOptions = jsonOptions;
    }

    internal void Configure(McpServerOptions serverOptions, Implementation? serverInfoOverride)
    {
        var backendCaps = _backend.ServerCapabilities;

        if (serverInfoOverride is not null)
        {
            serverOptions.ServerInfo = serverInfoOverride;
        }
        else if (_backend.ServerInfo is { } info)
        {
            serverOptions.ServerInfo = info;
        }

        serverOptions.Capabilities = backendCaps is not null
            ? CloneCapabilities(backendCaps)
            : serverOptions.Capabilities ?? new ServerCapabilities();

        var handlers = serverOptions.Handlers;

        if (backendCaps?.Tools is not null)
        {
            handlers.ListToolsHandler = (request, ct) =>
                ForwardAsync<ListToolsRequestParams, ListToolsResult>(request, RequestMethods.ToolsList, ct, EnsureToolInputSchemas);
            handlers.CallToolHandler = CallToolAsync;
        }

        if (backendCaps?.Prompts is not null)
        {
            handlers.ListPromptsHandler = (request, ct) =>
                ForwardAsync<ListPromptsRequestParams, ListPromptsResult>(request, RequestMethods.PromptsList, ct);
            handlers.GetPromptHandler = (request, ct) =>
                ForwardAsync<GetPromptRequestParams, GetPromptResult>(request, RequestMethods.PromptsGet, ct);
        }

        if (backendCaps?.Resources is not null)
        {
            handlers.ListResourcesHandler = (request, ct) =>
                ForwardAsync<ListResourcesRequestParams, ListResourcesResult>(request, RequestMethods.ResourcesList, ct);
            handlers.ReadResourceHandler = (request, ct) =>
                ForwardAsync<ReadResourceRequestParams, ReadResourceResult>(request, RequestMethods.ResourcesRead, ct);
            handlers.ListResourceTemplatesHandler = (request, ct) =>
                ForwardAsync<ListResourceTemplatesRequestParams, ListResourceTemplatesResult>(request, RequestMethods.ResourcesTemplatesList, ct);

            // resources/subscribe is deprecated upstream (SEP-2575) but still proxied for down-level peers.
#pragma warning disable MCP9005
            if (backendCaps.Resources.Subscribe == true)
            {
                handlers.SubscribeToResourcesHandler = (request, ct) =>
                    ForwardAsync<SubscribeRequestParams, EmptyResult>(request, RequestMethods.ResourcesSubscribe, ct);
                handlers.UnsubscribeFromResourcesHandler = (request, ct) =>
                    ForwardAsync<UnsubscribeRequestParams, EmptyResult>(request, RequestMethods.ResourcesUnsubscribe, ct);
            }
#pragma warning restore MCP9005
        }

        if (backendCaps?.Completions is not null)
        {
            handlers.CompleteHandler = (request, ct) =>
                ForwardAsync<CompleteRequestParams, CompleteResult>(request, RequestMethods.CompletionComplete, ct);
        }

        // Logging is deprecated upstream (SEP-2577) but the gateway keeps proxying it for down-level peers.
#pragma warning disable MCP9005
        if (backendCaps?.Logging is not null)
        {
            handlers.SetLoggingLevelHandler = (request, ct) =>
                ForwardAsync<SetLevelRequestParams, EmptyResult>(request, RequestMethods.LoggingSetLevel, ct);
        }
#pragma warning restore MCP9005
    }

    /// <summary>
    /// Forwards a <c>tools/call</c>, reporting a rejection as a tool error result unless the gateway is
    /// configured to return the SEP's protocol error.
    /// </summary>
    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        try
        {
            return await ForwardAsync<CallToolRequestParams, CallToolResult>(request, RequestMethods.ToolsCall, ct).ConfigureAwait(false);
        }
        catch (McpInterceptorException ex) when (_toolCallRejection == ToolCallRejectionMode.ErrorResult)
        {
            return CreateRejectedToolResult(ex);
        }
    }

    /// <summary>
    /// Runs request-phase interceptors on the request, forwards it to the backend as JSON, then runs
    /// response-phase interceptors on the result.
    /// </summary>
    private async ValueTask<TResult> ForwardAsync<TParams, TResult>(
        RequestContext<TParams> request,
        string method,
        CancellationToken ct,
        Action<JsonNode?>? normalizeResult = null)
        where TResult : class
    {
        var eventName = method;
        var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions) ?? new JsonObject();

        if (!_runner.ShouldIntercept(eventName))
        {
            var passthrough = await SendToBackendAsync(method, requestPayload, ct).ConfigureAwait(false);
            normalizeResult?.Invoke(passthrough);
            return Deserialize<TResult>(passthrough, method, "backend");
        }

        await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, eventName, ct).ConfigureAwait(false);
        var context = _runner.CreateContext(request.Server.SessionId, request.User);

        var forwardedRequest = await _runner.RunPhaseAsync(
            resolvedClients.Clients, eventName, InterceptorPhase.Request, requestPayload, context, ct).ConfigureAwait(false);

        var backendResult = await SendToBackendAsync(method, forwardedRequest, ct).ConfigureAwait(false);
        normalizeResult?.Invoke(backendResult);

        var finalResult = await _runner.RunPhaseAsync(
            resolvedClients.Clients, eventName, InterceptorPhase.Response, backendResult, context, ct).ConfigureAwait(false);
        normalizeResult?.Invoke(finalResult);

        return Deserialize<TResult>(finalResult, method, "interceptor mutations");
    }

    private async ValueTask<JsonNode> SendToBackendAsync(string method, JsonNode payload, CancellationToken ct)
    {
        var outgoing = WithoutHopByHopMeta(payload);
        return await _backend.SendRequestAsync<JsonNode, JsonNode>(method, outgoing, _jsonOptions, cancellationToken: ct).ConfigureAwait(false)
            ?? new JsonObject();
    }

    /// <summary>
    /// Per-request protocol metadata (protocol version, client info, client capabilities, log level)
    /// describes the downstream client's session with the gateway, not the gateway's session with the
    /// backend. Strip it before forwarding; the backend client injects its own. The SDK would overwrite
    /// version and client info anyway, but it merges capabilities, so a downstream client's
    /// capability opt-ins would otherwise reach the backend as if the gateway supported them.
    /// </summary>
    internal static JsonNode WithoutHopByHopMeta(JsonNode payload)
    {
        if (payload is not JsonObject obj || obj["_meta"] is not JsonObject meta ||
            !HopByHopMetaKeys.Any(meta.ContainsKey))
        {
            return payload;
        }

        var copy = (JsonObject)obj.DeepClone();
        var copiedMeta = (JsonObject)copy["_meta"]!;
        foreach (var key in HopByHopMetaKeys)
        {
            copiedMeta.Remove(key);
        }

        if (copiedMeta.Count == 0)
        {
            copy.Remove("_meta");
        }

        return copy;
    }

    private static readonly string[] HopByHopMetaKeys =
    [
        MetaKeys.ProtocolVersion,
        MetaKeys.ClientInfo,
        MetaKeys.ClientCapabilities,
        MetaKeys.LogLevel,
    ];

    private TResult Deserialize<TResult>(JsonNode node, string method, string source)
        where TResult : class
    {
        try
        {
            return node.Deserialize<TResult>(_jsonOptions)
                ?? throw new JsonException("The result was null.");
        }
        catch (JsonException ex)
        {
            throw new McpProtocolException(
                $"The {source} produced an invalid '{method}' result: {ex.Message}",
                McpErrorCode.InternalError);
        }
    }

    internal static CallToolResult CreateRejectedToolResult(McpInterceptorException exception)
    {
        var details = new JsonObject
        {
            ["code"] = (int)exception.ErrorCode,
            ["message"] = exception.Message,
        };

        if (exception.ExecutionResult is { } result)
        {
            details["event"] = result.Event;
            details["phase"] = result.Phase == InterceptorPhase.Response ? "response" : "request";
            if (result.AbortedAt is { } abort)
            {
                details["failedInterceptor"] = abort.Interceptor;
            }
        }

        if (exception is McpInterceptorValidationException validation)
        {
            details["validationErrors"] = JsonSerializer.SerializeToNode(
                validation.ValidationErrors,
                InterceptorJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IReadOnlyList<InterceptorValidationError>)));
        }

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = DescribeRejection(exception) }],
            Meta = new JsonObject { [RejectionMetaKey] = details },
        };
    }

    private static string DescribeRejection(McpInterceptorException exception)
    {
        var result = exception.ExecutionResult;
        var stage = result?.Phase == InterceptorPhase.Response
            ? "The tool ran, but its result was withheld"
            : "The tool call was blocked before reaching the server";

        if (exception is McpInterceptorValidationException { ValidationErrors.Count: > 0 } validation)
        {
            var reasons = string.Join(Environment.NewLine, validation.ValidationErrors.Select(e => $"- {e.Interceptor}: {e.Message}"));
            return $"{stage} by interceptor policy.{Environment.NewLine}{reasons}";
        }

        return $"{stage} because an interceptor failed: {result?.AbortedAt?.Interceptor ?? "unknown"}: {result?.AbortedAt?.Reason ?? exception.Message}";
    }

    /// <summary>
    /// SDK 2.x requires tools/list entries to carry an inputSchema that is a JSON object with
    /// "type":"object"; missing schemas throw during deserialization and invalid ones are
    /// rejected by the Tool setter. As a transparent proxy we coerce missing or invalid schemas
    /// from down-level backends or interceptor mutations to the spec-default {"type":"object"}.
    /// </summary>
    internal static void EnsureToolInputSchemas(JsonNode? listToolsPayload)
    {
        if (listToolsPayload?["tools"] is not JsonArray tools)
        {
            return;
        }

        foreach (var node in tools)
        {
            if (node is not JsonObject tool)
            {
                continue;
            }

            var valid = tool["inputSchema"] is JsonObject schema
                && schema["type"] is JsonValue typeValue
                && typeValue.TryGetValue<string>(out var type)
                && type == "object";
            if (!valid)
            {
                tool["inputSchema"] = new JsonObject { ["type"] = "object" };
            }
        }
    }

    private ServerCapabilities CloneCapabilities(ServerCapabilities capabilities)
    {
        var node = JsonSerializer.SerializeToNode(capabilities, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to serialize backend server capabilities.");
        return JsonSerializer.Deserialize<ServerCapabilities>(node, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone backend server capabilities.");
    }
}
