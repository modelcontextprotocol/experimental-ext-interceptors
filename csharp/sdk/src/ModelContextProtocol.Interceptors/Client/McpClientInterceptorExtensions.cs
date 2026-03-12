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
    /// Executes a chain of interceptors on the remote server.
    /// </summary>
    public static ValueTask<InterceptorChainResult> ExecuteChainAsync(
        this McpClient client,
        ExecuteChainRequestParams requestParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestParams);

        return client.SendRequestAsync<ExecuteChainRequestParams, InterceptorChainResult>(
            InterceptorRequestMethods.InterceptorExecuteChain,
            requestParams,
            InterceptorJsonUtilities.DefaultOptions,
            cancellationToken: cancellationToken);
    }
}
