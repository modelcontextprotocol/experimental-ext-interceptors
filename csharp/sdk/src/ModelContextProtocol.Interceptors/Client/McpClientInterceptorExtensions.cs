using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Extension methods on <see cref="McpClient"/> for consuming the interceptors extension.
/// </summary>
public static class McpClientInterceptorExtensions
{
    /// <summary>
    /// Sends one <c>interceptors/list</c> request to the remote server.
    /// </summary>
    public static ValueTask<ListInterceptorsResult> ListInterceptorsAsync(
        this McpClient client,
        ListInterceptorsRequestParams? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.SendRequestAsync<ListInterceptorsRequestParams, ListInterceptorsResult>(
            InterceptorRequestMethods.InterceptorsList,
            requestParams ?? new ListInterceptorsRequestParams(),
            InterceptorJsonUtilities.DefaultOptions,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Lists every interceptor on the remote server, following <c>nextCursor</c> across pages.
    /// </summary>
    public static async ValueTask<IList<Interceptor>> ListAllInterceptorsAsync(
        this McpClient client,
        string? eventFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var interceptors = new List<Interceptor>();
        string? cursor = null;
        do
        {
            var page = await client.ListInterceptorsAsync(
                new ListInterceptorsRequestParams { Event = eventFilter, Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
            interceptors.AddRange(page.Interceptors);
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return interceptors;
    }

    /// <summary>
    /// Invokes a single interceptor on the remote server.
    /// </summary>
    public static ValueTask<InterceptorResult> InvokeInterceptorAsync(
        this McpClient client,
        InvokeInterceptorRequestParams requestParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestParams);

        return client.SendRequestAsync<InvokeInterceptorRequestParams, InterceptorResult>(
            InterceptorRequestMethods.InterceptorInvoke,
            requestParams,
            InterceptorJsonUtilities.DefaultOptions,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Invokes a single interceptor whose type is already known from <c>interceptors/list</c>.
    /// A result that omits <c>type</c> is read as <paramref name="expectedType"/>.
    /// </summary>
    /// <remarks>
    /// The SEP's response examples omit <c>type</c>, <c>interceptor</c> and <c>phase</c>
    /// (experimental-ext-interceptors#50), so servers written from them interoperate only if the
    /// invoker falls back to the declared type.
    /// </remarks>
    public static async ValueTask<InterceptorResult> InvokeInterceptorAsync(
        this McpClient client,
        InvokeInterceptorRequestParams requestParams,
        InterceptorType expectedType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestParams);

        var node = await client.SendRequestAsync<InvokeInterceptorRequestParams, JsonNode>(
            InterceptorRequestMethods.InterceptorInvoke,
            requestParams,
            InterceptorJsonUtilities.DefaultOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (node is JsonObject obj && !obj.ContainsKey("type"))
        {
            obj["type"] = expectedType switch
            {
                InterceptorType.Validation => "validation",
                InterceptorType.Mutation => "mutation",
                InterceptorType.Sink => "sink",
                _ => throw new ArgumentOutOfRangeException(nameof(expectedType)),
            };
        }

        return node.Deserialize<InterceptorResult>(InterceptorJsonUtilities.DefaultOptions)
            ?? throw new JsonException($"Interceptor '{requestParams.Name}' returned a null result.");
    }

    /// <summary>
    /// Runs the interceptors on the remote server that apply to one lifecycle event, using the SEP
    /// execution model.
    /// </summary>
    /// <remarks>
    /// SEP-2624 defines no aggregate invocation method. This helper discovers the server's
    /// interceptors via <c>interceptors/list</c> and invokes each applicable one via
    /// <c>interceptor/invoke</c>, applying ordering, parallelism, audit mode and fail-open locally.
    /// To span several servers, use <see cref="InterceptorOrchestrator"/>.
    /// </remarks>
    public static ValueTask<InterceptorExecutionResult> ExecuteInterceptorsAsync(
        this McpClient client,
        InterceptorExecutionRequest request,
        InterceptorPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        return InterceptorOrchestrator.ExecuteAsync([client], request, policy, cancellationToken);
    }
}
