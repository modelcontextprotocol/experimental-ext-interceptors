using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Extension methods on <see cref="McpClient"/> for consuming the interceptors extension.
/// </summary>
public static class McpClientInterceptorExtensions
{
    /// <summary>
    /// Lists all interceptors available on the remote server.
    /// </summary>
    public static ValueTask<ListInterceptorsResult> ListInterceptorsAsync(
        this McpClient client,
        ListInterceptorsRequestParams? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        return client.SendRequestAsync<ListInterceptorsRequestParams, ListInterceptorsResult>(
            InterceptorRequestMethods.InterceptorsList,
            requestParams ?? new ListInterceptorsRequestParams(),
            InterceptorJsonUtilities.DefaultOptions,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Invokes a single interceptor on the remote server.
    /// </summary>
    public static ValueTask<InterceptorResult> InvokeInterceptorAsync(
        this McpClient client,
        InvokeInterceptorRequestParams requestParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestParams);

        return client.SendRequestAsync<InvokeInterceptorRequestParams, InterceptorResult>(
            InterceptorRequestMethods.InterceptorInvoke,
            requestParams,
            InterceptorJsonUtilities.DefaultOptions,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Executes a chain of interceptors against the remote server using the SEP execution model.
    /// </summary>
    /// <remarks>
    /// Per the SEP, chain execution is a convenience utility provided by SDKs — not a wire JSON-RPC
    /// method. This call discovers applicable interceptors via <c>interceptors/list</c> and then
    /// dispatches each one via <c>interceptor/invoke</c>, orchestrating ordering, parallelism,
    /// audit-mode, and fail-open semantics locally.
    /// </remarks>
    public static async ValueTask<InterceptorChainResult> ExecuteChainAsync(
        this McpClient client,
        ExecuteChainRequestParams requestParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestParams);

        var listed = await client.ListInterceptorsAsync(
            new ListInterceptorsRequestParams { Event = requestParams.Event },
            cancellationToken).ConfigureAwait(false);

        return await InterceptorChainOrchestrator.ExecuteAsync(
            listed.Interceptors,
            (invokeParams, ct) => client.InvokeInterceptorAsync(invokeParams, ct),
            requestParams,
            cancellationToken).ConfigureAwait(false);
    }
}
