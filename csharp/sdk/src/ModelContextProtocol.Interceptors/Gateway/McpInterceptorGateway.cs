using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

/// <summary>
/// A transparent MCP gateway that proxies requests through interceptor chains
/// before forwarding to a backend MCP server.
/// </summary>
/// <remarks>
/// <para>
/// This class configures an <see cref="McpServer"/> to act as a transparent proxy. To connecting
/// clients, it appears to be the backend server itself, but all requests are routed through
/// configured interceptor servers for validation, mutation, and observability.
/// </para>
/// <para>
/// Usage:
/// <list type="number">
/// <item>Connect to the backend and interceptor servers as <see cref="McpClient"/> instances.</item>
/// <item>Create an <see cref="McpInterceptorGateway"/> with the clients.</item>
/// <item>Call <see cref="ConfigureServerOptions"/> to wire up proxy handlers on your server options.</item>
/// <item>Create your <see cref="McpServer"/> with those options.</item>
/// <item>Call <see cref="RegisterNotificationForwarding"/> to forward backend notifications.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class McpInterceptorGateway : IAsyncDisposable
{
    private readonly McpInterceptorGatewayOptions _options;
    private readonly InterceptorChainRunner _chainRunner;
    private readonly JsonSerializerOptions _jsonOptions = InterceptorJsonUtilities.DefaultOptions;
    private readonly List<IAsyncDisposable> _notificationRegistrations = new();

    /// <summary>Creates a new <see cref="McpInterceptorGateway"/>.</summary>
    /// <param name="options">Configuration for the gateway.</param>
    public McpInterceptorGateway(McpInterceptorGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BackendClient);
        ArgumentNullException.ThrowIfNull(options.InterceptorClients);

        if (options.InterceptorClients.Count == 0)
        {
            throw new ArgumentException("At least one interceptor client is required.", nameof(options));
        }

        _options = options;
        _chainRunner = new InterceptorChainRunner(
            options.InterceptorClients,
            options.Events,
            options.TimeoutMs,
            options.DefaultContext);
    }

    /// <summary>Gets the backend MCP client.</summary>
    public McpClient BackendClient => _options.BackendClient;

    /// <summary>
    /// Configures the given <see cref="McpServerOptions"/> with proxy handlers that forward
    /// requests through interceptor chains to the backend server.
    /// </summary>
    /// <remarks>
    /// Capabilities are mirrored from the backend server. Only capabilities advertised by the
    /// backend will have handlers registered. The interceptors extension capability is also advertised.
    /// </remarks>
    public void ConfigureServerOptions(McpServerOptions serverOptions)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);

        var backend = _options.BackendClient;
        var backendCaps = backend.ServerCapabilities;

        // Mirror server info if not explicitly overridden
        if (_options.ServerInfo is not null)
        {
            serverOptions.ServerInfo = _options.ServerInfo;
        }
        else if (backend.ServerInfo is { } info)
        {
            serverOptions.ServerInfo = info;
        }

        serverOptions.Capabilities ??= new();

        // Tools
        if (backendCaps?.Tools is not null)
        {
            serverOptions.Capabilities.Tools ??= new()
            {
                ListChanged = backendCaps.Tools.ListChanged,
            };

            serverOptions.Handlers.ListToolsHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsList))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ToolsList, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("tools/list", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<ListToolsRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                var result = await backend.ListToolsAsync(mutatedParams, ct);

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsList))
                {
                    var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                    var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ToolsList, InterceptorPhase.Response, responsePayload, ct);
                    if (responseStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("tools/list", InterceptorPhase.Response, responseStatus);
                    result = JsonSerializer.Deserialize<ListToolsResult>(processed, _jsonOptions) ?? result;
                }

                return result;
            };

            serverOptions.Handlers.CallToolHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsCall))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ToolsCall, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("tools/call", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<CallToolRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                var result = await backend.CallToolAsync(mutatedParams, ct);

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsCall))
                {
                    var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                    var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ToolsCall, InterceptorPhase.Response, responsePayload, ct);
                    if (responseStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("tools/call", InterceptorPhase.Response, responseStatus);
                    result = JsonSerializer.Deserialize<CallToolResult>(processed, _jsonOptions) ?? result;
                }

                return result;
            };
        }

        // Prompts
        if (backendCaps?.Prompts is not null)
        {
            serverOptions.Capabilities.Prompts ??= new()
            {
                ListChanged = backendCaps.Prompts.ListChanged,
            };

            serverOptions.Handlers.ListPromptsHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsList))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.PromptsList, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("prompts/list", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<ListPromptsRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                var result = await backend.ListPromptsAsync(mutatedParams, ct);

                if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsList))
                {
                    var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                    var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.PromptsList, InterceptorPhase.Response, responsePayload, ct);
                    if (responseStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("prompts/list", InterceptorPhase.Response, responseStatus);
                    result = JsonSerializer.Deserialize<ListPromptsResult>(processed, _jsonOptions) ?? result;
                }

                return result;
            };

            serverOptions.Handlers.GetPromptHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsGet))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.PromptsGet, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("prompts/get", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<GetPromptRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                var result = await backend.GetPromptAsync(mutatedParams, ct);

                if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsGet))
                {
                    var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                    var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.PromptsGet, InterceptorPhase.Response, responsePayload, ct);
                    if (responseStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("prompts/get", InterceptorPhase.Response, responseStatus);
                    result = JsonSerializer.Deserialize<GetPromptResult>(processed, _jsonOptions) ?? result;
                }

                return result;
            };
        }

        // Resources
        if (backendCaps?.Resources is not null)
        {
            serverOptions.Capabilities.Resources ??= new()
            {
                ListChanged = backendCaps.Resources.ListChanged,
                Subscribe = backendCaps.Resources.Subscribe,
            };

            serverOptions.Handlers.ListResourcesHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesList))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ResourcesList, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("resources/list", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<ListResourcesRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                var result = await backend.ListResourcesAsync(mutatedParams, ct);

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesList))
                {
                    var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                    var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ResourcesList, InterceptorPhase.Response, responsePayload, ct);
                    if (responseStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("resources/list", InterceptorPhase.Response, responseStatus);
                    result = JsonSerializer.Deserialize<ListResourcesResult>(processed, _jsonOptions) ?? result;
                }

                return result;
            };

            serverOptions.Handlers.ReadResourceHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesRead))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ResourcesRead, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("resources/read", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<ReadResourceRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                var result = await backend.ReadResourceAsync(mutatedParams, ct);

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesRead))
                {
                    var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                    var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ResourcesRead, InterceptorPhase.Response, responsePayload, ct);
                    if (responseStatus != InterceptorChainStatus.Success)
                        ThrowChainFailure("resources/read", InterceptorPhase.Response, responseStatus);
                    result = JsonSerializer.Deserialize<ReadResourceResult>(processed, _jsonOptions) ?? result;
                }

                return result;
            };

            serverOptions.Handlers.ListResourceTemplatesHandler = async (request, ct) =>
            {
                return await backend.ListResourceTemplatesAsync(request.Params!, ct);
            };

            if (backendCaps.Resources.Subscribe == true)
            {
                serverOptions.Handlers.SubscribeToResourcesHandler = async (request, ct) =>
                {
                    var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;

                    if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesSubscribe))
                    {
                        var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                            InterceptorEvents.ResourcesSubscribe, InterceptorPhase.Request, requestPayload, ct);
                        if (requestStatus != InterceptorChainStatus.Success)
                            ThrowChainFailure("resources/subscribe", InterceptorPhase.Request, requestStatus);
                        requestPayload = processed;
                    }

                    var mutatedParams = JsonSerializer.Deserialize<SubscribeRequestParams>(requestPayload, _jsonOptions)
                        ?? request.Params!;
                    await backend.SubscribeToResourceAsync(mutatedParams, ct);
                    return new EmptyResult();
                };

                serverOptions.Handlers.UnsubscribeFromResourcesHandler = async (request, ct) =>
                {
                    await backend.UnsubscribeFromResourceAsync(request.Params!, ct);
                    return new EmptyResult();
                };
            }
        }

        // Completions
        if (backendCaps?.Completions is not null)
        {
            serverOptions.Capabilities.Completions ??= new();

            serverOptions.Handlers.CompleteHandler = async (request, ct) =>
            {
                return await backend.CompleteAsync(request.Params!, ct);
            };
        }

        // Logging
        if (backendCaps?.Logging is not null)
        {
            serverOptions.Capabilities.Logging ??= new();

            serverOptions.Handlers.SetLoggingLevelHandler = async (request, ct) =>
            {
                await backend.SetLoggingLevelAsync(request.Params!, ct);
                return new EmptyResult();
            };
        }

        // Advertise interceptor extension capability and add passthrough filter
        ConfigureInterceptorPassthrough(serverOptions);
    }

    /// <summary>
    /// Registers notification forwarding from the backend server to the proxy server.
    /// Backend notifications (tools/list_changed, prompts/list_changed, resources/list_changed)
    /// are re-sent to connecting clients through the proxy server.
    /// </summary>
    /// <param name="proxyServer">The proxy <see cref="McpServer"/> to forward notifications through.</param>
    public void RegisterNotificationForwarding(McpServer proxyServer)
    {
        ArgumentNullException.ThrowIfNull(proxyServer);

        var backend = _options.BackendClient;
        var backendCaps = backend.ServerCapabilities;

        if (backendCaps?.Tools?.ListChanged == true)
        {
            _notificationRegistrations.Add(
                backend.RegisterNotificationHandler(
                    NotificationMethods.ToolListChangedNotification,
                    (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(
                        NotificationMethods.ToolListChangedNotification, ct))));
        }

        if (backendCaps?.Prompts?.ListChanged == true)
        {
            _notificationRegistrations.Add(
                backend.RegisterNotificationHandler(
                    NotificationMethods.PromptListChangedNotification,
                    (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(
                        NotificationMethods.PromptListChangedNotification, ct))));
        }

        if (backendCaps?.Resources?.ListChanged == true)
        {
            _notificationRegistrations.Add(
                backend.RegisterNotificationHandler(
                    NotificationMethods.ResourceListChangedNotification,
                    (_, ct) => new ValueTask(proxyServer.SendNotificationAsync(
                        NotificationMethods.ResourceListChangedNotification, ct))));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _notificationRegistrations)
        {
            await registration.DisposeAsync();
        }

        _notificationRegistrations.Clear();
    }

    /// <summary>
    /// Adds an incoming message filter that passes through <c>interceptors/list</c>,
    /// <c>interceptor/invoke</c>, and <c>interceptor/executeChain</c> requests
    /// to the configured interceptor clients. Also advertises the interceptors capability.
    /// </summary>
    private void ConfigureInterceptorPassthrough(McpServerOptions serverOptions)
    {
        // Aggregate supported events from each interceptor server's advertised capabilities.
        // Track whether any interceptor server actually advertises the interceptors extension,
        // even if it has no events (an empty-event server is still a valid interceptor server).
        var allEvents = new HashSet<string>();
        bool anyInterceptorCapabilityFound = false;
        foreach (var client in _options.InterceptorClients)
        {
#pragma warning disable MCPEXP001
            if (client.ServerCapabilities?.Extensions is { } extensions &&
                extensions.TryGetValue("interceptors", out var capObj))
            {
                anyInterceptorCapabilityFound = true;
                try
                {
                    InterceptorsCapability? cap = capObj switch
                    {
                        JsonElement je => JsonSerializer.Deserialize<InterceptorsCapability>(je, _jsonOptions),
                        _ => null,
                    };

                    if (cap?.SupportedEvents is { } events)
                    {
                        foreach (var ev in events)
                        {
                            allEvents.Add(ev);
                        }
                    }
                }
                catch
                {
                    // If deserialization fails, skip this client's capability
                }
            }
#pragma warning restore MCPEXP001
        }

        // If capability discovery didn't find events, try querying interceptors/list
        if (allEvents.Count == 0)
        {
            foreach (var client in _options.InterceptorClients)
            {
                try
                {
                    var listResult = client.ListInterceptorsAsync().AsTask().GetAwaiter().GetResult();
                    if (listResult.Interceptors.Count > 0)
                    {
                        anyInterceptorCapabilityFound = true;
                    }

                    foreach (var interceptor in listResult.Interceptors)
                    {
                        foreach (var ev in interceptor.Events)
                        {
                            allEvents.Add(ev);
                        }
                    }
                }
                catch
                {
                    // If listing fails, skip this client
                }
            }
        }

        // Advertise interceptors capability if any interceptor server was found.
        // Use discovered events, or empty list if none were discovered (avoids over-advertising).
        if (anyInterceptorCapabilityFound)
        {
#pragma warning disable MCPEXP001
            serverOptions.Capabilities!.Extensions ??= new Dictionary<string, object>();
            var capability = new InterceptorsCapability
            {
                SupportedEvents = allEvents.ToList(),
            };
            serverOptions.Capabilities.Extensions["interceptors"] = JsonSerializer.SerializeToElement(
                capability, InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
        }

        // Add a message filter that forwards interceptor protocol requests
        serverOptions.Filters.Message.IncomingFilters.Add(next =>
        {
            return async (context, ct) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest request)
                {
                    switch (request.Method)
                    {
                        case InterceptorRequestMethods.InterceptorsList:
                            await HandleInterceptorsListPassthrough(context, request, ct);
                            return;
                        case InterceptorRequestMethods.InterceptorInvoke:
                            await HandleInvokePassthrough(context, request, ct);
                            return;
                        case InterceptorRequestMethods.InterceptorExecuteChain:
                            await HandleExecuteChainPassthrough(context, request, ct);
                            return;
                    }
                }

                await next(context, ct);
            };
        });
    }

    /// <summary>
    /// Handles <c>interceptors/list</c> by aggregating results from all interceptor clients.
    /// </summary>
    private async Task HandleInterceptorsListPassthrough(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        ListInterceptorsRequestParams? requestParams = null;
        if (request.Params is not null)
        {
            requestParams = JsonSerializer.Deserialize<ListInterceptorsRequestParams>(request.Params, _jsonOptions);
        }

        var allInterceptors = new List<Interceptor>();
        foreach (var client in _options.InterceptorClients)
        {
            var result = await client.ListInterceptorsAsync(requestParams, ct);
            allInterceptors.AddRange(result.Interceptors);
        }

        var aggregatedResult = new ListInterceptorsResult { Interceptors = allInterceptors };
        var resultNode = JsonSerializer.SerializeToNode(aggregatedResult, _jsonOptions);

        await context.Server.SendMessageAsync(
            new JsonRpcResponse { Id = request.Id, Result = resultNode },
            ct);
    }

    /// <summary>
    /// Handles <c>interceptor/invoke</c> by finding the named interceptor across all clients.
    /// Only continues to the next client when the interceptor is not found;
    /// real execution failures are propagated immediately with their original error codes.
    /// </summary>
    private async Task HandleInvokePassthrough(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        try
        {
            var invokeParams = JsonSerializer.Deserialize<InvokeInterceptorRequestParams>(request.Params!, _jsonOptions)!;

            InterceptorResult? invokeResult = null;

            foreach (var client in _options.InterceptorClients)
            {
                // Check if this client has the interceptor via a lightweight list query
                var listResult = await client.ListInterceptorsAsync(
                    new ListInterceptorsRequestParams(), ct);
                var found = false;
                foreach (var interceptor in listResult.Interceptors)
                {
                    if (interceptor.Name == invokeParams.Name)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    continue;
                }

                // Interceptor exists on this client — invoke it; failures propagate
                invokeResult = await client.InvokeInterceptorAsync(invokeParams, ct);
                break;
            }

            if (invokeResult is null)
            {
                await SendError(context, request.Id, -32602,
                    $"Interceptor '{invokeParams.Name}' not found on any interceptor server", ct);
                return;
            }

            var resultNode = JsonSerializer.SerializeToNode<InterceptorResult>(invokeResult, _jsonOptions);
            await context.Server.SendMessageAsync(
                new JsonRpcResponse { Id = request.Id, Result = resultNode },
                ct);
        }
        catch (Exception ex)
        {
            await SendErrorFromException(context, request.Id, ex, ct);
        }
    }

    /// <summary>
    /// Handles <c>interceptor/executeChain</c> by chaining through all interceptor clients
    /// sequentially, matching the behavior of <see cref="InterceptorChainRunner"/>.
    /// Each client's chain receives the (potentially mutated) payload from the previous one.
    /// Results from all clients are aggregated into a single <see cref="InterceptorChainResult"/>.
    /// </summary>
    private async Task HandleExecuteChainPassthrough(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        try
        {
            var chainParams = JsonSerializer.Deserialize<ExecuteChainRequestParams>(request.Params!, _jsonOptions)!;

            var allResults = new List<InterceptorResult>();
            long totalDurationMs = 0;
            int validationErrors = 0, validationWarnings = 0, validationInfos = 0;
            InterceptorChainStatus aggregateStatus = InterceptorChainStatus.Success;
            ChainAbortInfo? abortedAt = null;
            var currentPayload = chainParams.Payload;

            foreach (var client in _options.InterceptorClients)
            {
                var clientParams = new ExecuteChainRequestParams
                {
                    Event = chainParams.Event,
                    Phase = chainParams.Phase,
                    Payload = currentPayload,
                    InterceptorNames = chainParams.InterceptorNames,
                    Config = chainParams.Config,
                    TimeoutMs = chainParams.TimeoutMs,
                    Context = chainParams.Context,
                };

                var clientResult = await client.ExecuteChainAsync(clientParams, ct);

                // Aggregate results
                allResults.AddRange(clientResult.Results);
                totalDurationMs += clientResult.TotalDurationMs;

                if (clientResult.ValidationSummary is { } vs)
                {
                    validationErrors += vs.Errors;
                    validationWarnings += vs.Warnings;
                    validationInfos += vs.Infos;
                }

                // If this client's chain failed, capture the abort and stop
                if (clientResult.Status != InterceptorChainStatus.Success)
                {
                    aggregateStatus = clientResult.Status;
                    abortedAt = clientResult.AbortedAt;
                    currentPayload = clientResult.FinalPayload ?? currentPayload;
                    break;
                }

                currentPayload = clientResult.FinalPayload ?? currentPayload;
            }

            var aggregatedResult = new InterceptorChainResult
            {
                Status = aggregateStatus,
                Event = chainParams.Event,
                Phase = chainParams.Phase,
                Results = allResults,
                FinalPayload = currentPayload,
                TotalDurationMs = totalDurationMs,
                AbortedAt = abortedAt,
                ValidationSummary = new ChainValidationSummary
                {
                    Errors = validationErrors,
                    Warnings = validationWarnings,
                    Infos = validationInfos,
                },
            };

            var resultNode = JsonSerializer.SerializeToNode(aggregatedResult, _jsonOptions);
            await context.Server.SendMessageAsync(
                new JsonRpcResponse { Id = request.Id, Result = resultNode },
                ct);
        }
        catch (Exception ex)
        {
            await SendErrorFromException(context, request.Id, ex, ct);
        }
    }

    /// <summary>
    /// Sends a JSON-RPC error, preserving the original error code from <see cref="McpProtocolException"/>
    /// when available.
    /// </summary>
    private static async Task SendErrorFromException(MessageContext context, RequestId requestId, Exception ex, CancellationToken ct)
    {
        int code = ex is McpProtocolException mpe ? (int)mpe.ErrorCode : -32603;
        await SendError(context, requestId, code, ex.Message, ct);
    }

    private static async Task SendError(MessageContext context, RequestId requestId, int code, string message, CancellationToken ct)
    {
        await context.Server.SendMessageAsync(
            new JsonRpcError
            {
                Id = requestId,
                Error = new JsonRpcErrorDetail { Code = code, Message = message },
            },
            ct);
    }

    private static void ThrowChainFailure(string operation, InterceptorPhase phase, InterceptorChainStatus status)
    {
        var phaseText = phase == InterceptorPhase.Request ? "Request" : "Response";
        if (status == InterceptorChainStatus.ValidationFailed)
        {
            throw new McpInterceptorValidationException($"{phaseText}-phase interceptor validation failed for {operation}.");
        }

        throw new InvalidOperationException($"{phaseText}-phase interceptor chain failed for {operation} with status '{status}'.");
    }
}
