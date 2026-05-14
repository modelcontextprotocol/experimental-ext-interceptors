using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// An incoming message filter that handles the interceptor extension's JSON-RPC methods:
/// <c>interceptors/list</c> and <c>interceptor/invoke</c>. Chain execution is performed
/// client-side via <see cref="McpClientInterceptorExtensions.ExecuteChainAsync"/>; per the SEP
/// it is no longer a wire method.
/// </summary>
internal sealed class InterceptorMessageFilter
{
    private readonly McpServerPrimitiveCollection<McpServerInterceptor> _interceptors;

    internal InterceptorMessageFilter(McpServerPrimitiveCollection<McpServerInterceptor> interceptors)
    {
        _interceptors = interceptors;
    }

    internal McpMessageHandler CreateFilter(McpMessageHandler next)
    {
        return async (context, ct) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest request)
            {
                switch (request.Method)
                {
                    case InterceptorRequestMethods.InterceptorsList:
                        await HandleListInterceptors(context, request, ct);
                        return;
                    case InterceptorRequestMethods.InterceptorInvoke:
                        await HandleInvokeInterceptor(context, request, ct);
                        return;
                }
            }

            await next(context, ct);
        };
    }

    private async Task HandleListInterceptors(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        var options = InterceptorJsonUtilities.DefaultOptions;

        ListInterceptorsRequestParams? requestParams = null;
        if (request.Params is not null)
        {
            requestParams = JsonSerializer.Deserialize<ListInterceptorsRequestParams>(request.Params, options);
        }

        var interceptors = new List<Interceptor>();
        foreach (var serverInterceptor in _interceptors)
        {
            // Apply event filter if specified: include the interceptor if any hook matches the event
            if (requestParams?.Event is string eventFilter)
            {
                var matchesAnyHook = false;
                foreach (var hook in serverInterceptor.ProtocolInterceptor.Hooks)
                {
                    if (InterceptorChainOrchestrator.MatchesEvent(hook.Events, eventFilter))
                    {
                        matchesAnyHook = true;
                        break;
                    }
                }

                if (!matchesAnyHook)
                {
                    continue;
                }
            }

            interceptors.Add(serverInterceptor.ProtocolInterceptor);
        }

        var result = new ListInterceptorsResult { Interceptors = interceptors };
        var resultNode = JsonSerializer.SerializeToNode(result, options);

        await context.Server.SendMessageAsync(
            new JsonRpcResponse { Id = request.Id, Result = resultNode },
            ct);
    }

    private async Task HandleInvokeInterceptor(MessageContext context, JsonRpcRequest request, CancellationToken ct)
    {
        var options = InterceptorJsonUtilities.DefaultOptions;

        if (request.Params is null)
        {
            await SendError(context, request.Id, -32602, "Missing params", ct);
            return;
        }

        var invokeParams = JsonSerializer.Deserialize<InvokeInterceptorRequestParams>(request.Params, options);
        if (invokeParams is null)
        {
            await SendError(context, request.Id, -32602, "Invalid params", ct);
            return;
        }

        // Find the interceptor by name
        McpServerInterceptor? interceptor = null;
        foreach (var i in _interceptors)
        {
            if (i.ProtocolInterceptor.Name == invokeParams.Name)
            {
                interceptor = i;
                break;
            }
        }

        if (interceptor is null)
        {
            await SendError(context, request.Id, -32602, $"Interceptor '{invokeParams.Name}' not found", ct);
            return;
        }

        // Apply timeout if specified
        using var timeoutCts = invokeParams.TimeoutMs.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(invokeParams.TimeoutMs!.Value);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            var result = await interceptor.InvokeAsync(invokeParams, context.Server, context.Services, effectiveCt);
            result.InterceptorName = interceptor.ProtocolInterceptor.Name;
            result.Phase = invokeParams.Phase;

            var resultNode = JsonSerializer.SerializeToNode<InterceptorResult>(result, options);

            await context.Server.SendMessageAsync(
                new JsonRpcResponse { Id = request.Id, Result = resultNode },
                ct);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            await SendError(context, request.Id, -32000, $"Interceptor '{invokeParams.Name}' timed out after {invokeParams.TimeoutMs}ms", ct);
        }
        catch (Exception ex)
        {
            await SendError(context, request.Id, -32603, $"Interceptor invocation failed: {ex.Message}", ct);
        }
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
