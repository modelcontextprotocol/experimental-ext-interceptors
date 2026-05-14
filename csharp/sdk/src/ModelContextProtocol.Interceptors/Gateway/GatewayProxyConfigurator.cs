using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

internal sealed class GatewayProxyConfigurator
{
    private readonly McpClient _backend;
    private readonly GatewayInterceptorClientProvider _interceptorClientProvider;
    private readonly IList<string>? _events;
    private readonly int? _timeoutMs;
    private readonly InvokeInterceptorContext? _defaultContext;
    private readonly JsonSerializerOptions _jsonOptions;

    internal GatewayProxyConfigurator(
        McpClient backend,
        GatewayInterceptorClientProvider interceptorClientProvider,
        IList<string>? events,
        int? timeoutMs,
        InvokeInterceptorContext? defaultContext,
        JsonSerializerOptions jsonOptions)
    {
        _backend = backend;
        _interceptorClientProvider = interceptorClientProvider;
        _events = events;
        _timeoutMs = timeoutMs;
        _defaultContext = defaultContext;
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

        // Task passthrough is not implemented yet, so do not advertise backend task support.
#pragma warning disable MCPEXP001
        serverOptions.Capabilities.Tasks = null;
#pragma warning restore MCPEXP001

        ConfigureTools(serverOptions, backendCaps);
        ConfigurePrompts(serverOptions, backendCaps);
        ConfigureResources(serverOptions, backendCaps);
        ConfigureCompletions(serverOptions, backendCaps);
        ConfigureLogging(serverOptions, backendCaps);
    }

    private void ConfigureTools(McpServerOptions serverOptions, ServerCapabilities? backendCaps)
    {
        if (backendCaps?.Tools is null)
        {
            return;
        }

        serverOptions.Handlers.ListToolsHandler = async (request, ct) =>
        {
            var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
            await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.ToolsList, ct);
            var chainRunner = CreateChainRunner(resolvedClients.Clients);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ToolsList))
            {
                var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ToolsList, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("tools/list", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ListToolsRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ListToolsAsync(mutatedParams, ct);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ToolsList))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ToolsList, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("tools/list", InterceptorPhase.Response, responseStatus);
                result = JsonSerializer.Deserialize<ListToolsResult>(processed, _jsonOptions) ?? result;
            }

            return result;
        };

        serverOptions.Handlers.CallToolHandler = async (request, ct) =>
        {
            var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
            await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.ToolsCall, ct);
            var chainRunner = CreateChainRunner(resolvedClients.Clients);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ToolsCall))
            {
                var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ToolsCall, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("tools/call", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<CallToolRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.CallToolAsync(mutatedParams, ct);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ToolsCall))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ToolsCall, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("tools/call", InterceptorPhase.Response, responseStatus);
                result = JsonSerializer.Deserialize<CallToolResult>(processed, _jsonOptions) ?? result;
            }

            return result;
        };
    }

    private void ConfigurePrompts(McpServerOptions serverOptions, ServerCapabilities? backendCaps)
    {
        if (backendCaps?.Prompts is null)
        {
            return;
        }

        serverOptions.Handlers.ListPromptsHandler = async (request, ct) =>
        {
            var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
            await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.PromptsList, ct);
            var chainRunner = CreateChainRunner(resolvedClients.Clients);

            if (chainRunner.ShouldIntercept(InterceptionEvents.PromptsList))
            {
                var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.PromptsList, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("prompts/list", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ListPromptsRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ListPromptsAsync(mutatedParams, ct);

            if (chainRunner.ShouldIntercept(InterceptionEvents.PromptsList))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.PromptsList, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("prompts/list", InterceptorPhase.Response, responseStatus);
                result = JsonSerializer.Deserialize<ListPromptsResult>(processed, _jsonOptions) ?? result;
            }

            return result;
        };

        serverOptions.Handlers.GetPromptHandler = async (request, ct) =>
        {
            var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
            await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.PromptsGet, ct);
            var chainRunner = CreateChainRunner(resolvedClients.Clients);

            if (chainRunner.ShouldIntercept(InterceptionEvents.PromptsGet))
            {
                var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.PromptsGet, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("prompts/get", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<GetPromptRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.GetPromptAsync(mutatedParams, ct);

            if (chainRunner.ShouldIntercept(InterceptionEvents.PromptsGet))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.PromptsGet, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("prompts/get", InterceptorPhase.Response, responseStatus);
                result = JsonSerializer.Deserialize<GetPromptResult>(processed, _jsonOptions) ?? result;
            }

            return result;
        };
    }

    private void ConfigureResources(McpServerOptions serverOptions, ServerCapabilities? backendCaps)
    {
        if (backendCaps?.Resources is null)
        {
            return;
        }

        serverOptions.Handlers.ListResourcesHandler = async (request, ct) =>
        {
            var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
            await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.ResourcesList, ct);
            var chainRunner = CreateChainRunner(resolvedClients.Clients);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ResourcesList))
            {
                var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ResourcesList, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("resources/list", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ListResourcesRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ListResourcesAsync(mutatedParams, ct);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ResourcesList))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ResourcesList, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("resources/list", InterceptorPhase.Response, responseStatus);
                result = JsonSerializer.Deserialize<ListResourcesResult>(processed, _jsonOptions) ?? result;
            }

            return result;
        };

        serverOptions.Handlers.ReadResourceHandler = async (request, ct) =>
        {
            var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
            await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.ResourcesRead, ct);
            var chainRunner = CreateChainRunner(resolvedClients.Clients);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ResourcesRead))
            {
                var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ResourcesRead, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("resources/read", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ReadResourceRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ReadResourceAsync(mutatedParams, ct);

            if (chainRunner.ShouldIntercept(InterceptionEvents.ResourcesRead))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await chainRunner.RunChainPhaseAsync(
                    InterceptionEvents.ResourcesRead, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("resources/read", InterceptorPhase.Response, responseStatus);
                result = JsonSerializer.Deserialize<ReadResourceResult>(processed, _jsonOptions) ?? result;
            }

            return result;
        };

        serverOptions.Handlers.ListResourceTemplatesHandler = async (request, ct) =>
            await _backend.ListResourceTemplatesAsync(request.Params!, ct);

        if (backendCaps.Resources.Subscribe == true)
        {
            serverOptions.Handlers.SubscribeToResourcesHandler = async (request, ct) =>
            {
                var requestPayload = JsonSerializer.SerializeToNode(request.Params, _jsonOptions)!;
                await using var resolvedClients = await _interceptorClientProvider.ResolveAsync(request, InterceptionEvents.ResourcesSubscribe, ct);
                var chainRunner = CreateChainRunner(resolvedClients.Clients);

                if (chainRunner.ShouldIntercept(InterceptionEvents.ResourcesSubscribe))
                {
                    var (processed, requestStatus) = await chainRunner.RunChainPhaseAsync(
                        InterceptionEvents.ResourcesSubscribe, InterceptorPhase.Request, requestPayload, ct);
                    if (requestStatus != InterceptorChainStatus.Success)
                        InterceptorChainRunner.ThrowChainFailure("resources/subscribe", InterceptorPhase.Request, requestStatus);
                    requestPayload = processed;
                }

                var mutatedParams = JsonSerializer.Deserialize<SubscribeRequestParams>(requestPayload, _jsonOptions)
                    ?? request.Params!;
                await _backend.SubscribeToResourceAsync(mutatedParams, ct);
                return new EmptyResult();
            };

            serverOptions.Handlers.UnsubscribeFromResourcesHandler = async (request, ct) =>
            {
                await _backend.UnsubscribeFromResourceAsync(request.Params!, ct);
                return new EmptyResult();
            };
        }
    }

    private void ConfigureCompletions(McpServerOptions serverOptions, ServerCapabilities? backendCaps)
    {
        if (backendCaps?.Completions is null)
        {
            return;
        }

        serverOptions.Handlers.CompleteHandler = async (request, ct) =>
            await _backend.CompleteAsync(request.Params!, ct);
    }

    private void ConfigureLogging(McpServerOptions serverOptions, ServerCapabilities? backendCaps)
    {
        if (backendCaps?.Logging is null)
        {
            return;
        }

        serverOptions.Handlers.SetLoggingLevelHandler = async (request, ct) =>
        {
            await _backend.SetLoggingLevelAsync(request.Params!, ct);
            return new EmptyResult();
        };
    }

    private ServerCapabilities CloneCapabilities(ServerCapabilities capabilities)
    {
        var node = JsonSerializer.SerializeToNode(capabilities, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to serialize backend server capabilities.");
        return JsonSerializer.Deserialize<ServerCapabilities>(node, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone backend server capabilities.");
    }

    private InterceptorChainRunner CreateChainRunner(IReadOnlyList<McpClient> interceptorClients) =>
        new(interceptorClients, _events, _timeoutMs, _defaultContext);
}
