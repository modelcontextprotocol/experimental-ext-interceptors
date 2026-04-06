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
    private readonly InterceptorChainRunner _chainRunner;
    private readonly JsonSerializerOptions _jsonOptions;

    internal GatewayProxyConfigurator(
        McpClient backend,
        InterceptorChainRunner chainRunner,
        JsonSerializerOptions jsonOptions)
    {
        _backend = backend;
        _chainRunner = chainRunner;
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

            if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsList))
            {
                var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.ToolsList, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("tools/list", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ListToolsRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ListToolsAsync(mutatedParams, ct);

            if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsList))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.ToolsList, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("tools/list", InterceptorPhase.Response, responseStatus);
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
                    InterceptorChainRunner.ThrowChainFailure("tools/call", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<CallToolRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.CallToolAsync(mutatedParams, ct);

            if (_chainRunner.ShouldIntercept(InterceptorEvents.ToolsCall))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.ToolsCall, InterceptorPhase.Response, responsePayload, ct);
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

            if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsList))
            {
                var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.PromptsList, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("prompts/list", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ListPromptsRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ListPromptsAsync(mutatedParams, ct);

            if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsList))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.PromptsList, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("prompts/list", InterceptorPhase.Response, responseStatus);
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
                    InterceptorChainRunner.ThrowChainFailure("prompts/get", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<GetPromptRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.GetPromptAsync(mutatedParams, ct);

            if (_chainRunner.ShouldIntercept(InterceptorEvents.PromptsGet))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.PromptsGet, InterceptorPhase.Response, responsePayload, ct);
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

            if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesList))
            {
                var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.ResourcesList, InterceptorPhase.Request, requestPayload, ct);
                if (requestStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("resources/list", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ListResourcesRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ListResourcesAsync(mutatedParams, ct);

            if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesList))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.ResourcesList, InterceptorPhase.Response, responsePayload, ct);
                if (responseStatus != InterceptorChainStatus.Success)
                    InterceptorChainRunner.ThrowChainFailure("resources/list", InterceptorPhase.Response, responseStatus);
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
                    InterceptorChainRunner.ThrowChainFailure("resources/read", InterceptorPhase.Request, requestStatus);
                requestPayload = processed;
            }

            var mutatedParams = JsonSerializer.Deserialize<ReadResourceRequestParams>(requestPayload, _jsonOptions)
                ?? request.Params!;
            var result = await _backend.ReadResourceAsync(mutatedParams, ct);

            if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesRead))
            {
                var responsePayload = JsonSerializer.SerializeToNode(result, _jsonOptions)!;
                var (processed, responseStatus) = await _chainRunner.RunChainPhaseAsync(
                    InterceptorEvents.ResourcesRead, InterceptorPhase.Response, responsePayload, ct);
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

                if (_chainRunner.ShouldIntercept(InterceptorEvents.ResourcesSubscribe))
                {
                    var (processed, requestStatus) = await _chainRunner.RunChainPhaseAsync(
                        InterceptorEvents.ResourcesSubscribe, InterceptorPhase.Request, requestPayload, ct);
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
}
