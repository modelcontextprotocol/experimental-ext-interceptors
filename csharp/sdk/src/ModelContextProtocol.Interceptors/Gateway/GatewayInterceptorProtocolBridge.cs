using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Gateway;

internal sealed class GatewayInterceptorProtocolBridge
{
    private readonly IReadOnlyList<McpClient> _interceptorClients;
    private readonly JsonSerializerOptions _jsonOptions;

    internal GatewayInterceptorProtocolBridge(
        IReadOnlyList<McpClient> interceptorClients,
        JsonSerializerOptions jsonOptions)
    {
        _interceptorClients = interceptorClients;
        _jsonOptions = jsonOptions;
    }

    internal void Configure(McpServerOptions serverOptions)
    {
        ConfigureCapability(serverOptions);
        ConfigurePassthroughFilter(serverOptions);
    }

    private void ConfigureCapability(McpServerOptions serverOptions)
    {
        var allEvents = new HashSet<string>();
        var anyInterceptorCapabilityFound = false;

        foreach (var client in _interceptorClients)
        {
#pragma warning disable MCPEXP001
            if (client.ServerCapabilities?.Extensions is { } extensions &&
                extensions.TryGetValue(InterceptorProtocolConstants.ExtensionCapabilityKey, out var capObj))
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
                }
            }
#pragma warning restore MCPEXP001
        }

        if (!anyInterceptorCapabilityFound)
        {
            return;
        }

#pragma warning disable MCPEXP001
        serverOptions.Capabilities ??= new ServerCapabilities();
        serverOptions.Capabilities.Extensions ??= new Dictionary<string, object>();
        serverOptions.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
            new InterceptorsCapability { SupportedEvents = allEvents.ToList() },
            InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
    }

    private void ConfigurePassthroughFilter(McpServerOptions serverOptions)
    {
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
                    }
                }

                await next(context, ct);
            };
        });
    }

    private async Task HandleInterceptorsListPassthrough(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        ListInterceptorsRequestParams? requestParams = null;
        if (request.Params is not null)
        {
            requestParams = JsonSerializer.Deserialize<ListInterceptorsRequestParams>(request.Params, _jsonOptions);
        }

        var allInterceptors = new List<Interceptor>();
        foreach (var client in _interceptorClients)
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

    private async Task HandleInvokePassthrough(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        try
        {
            var invokeParams = JsonSerializer.Deserialize<InvokeInterceptorRequestParams>(request.Params!, _jsonOptions)!;

            InterceptorResult? invokeResult = null;

            foreach (var client in _interceptorClients)
            {
                try
                {
                    invokeResult = await client.InvokeInterceptorAsync(invokeParams, ct);
                    break;
                }
                catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
                {
                    continue;
                }
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

    private static async Task SendErrorFromException(MessageContext context, RequestId requestId, Exception ex, CancellationToken ct)
    {
        var code = ex is McpProtocolException mpe ? (int)mpe.ErrorCode : -32603;
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
}
