using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// An interceptor chain assembled from one or more MCP servers, per the SEP orchestration pattern:
/// discover via <c>interceptors/list</c> on every server, merge all entries into a single chain
/// sorted by priority hint (alphabetical tie-break), and execute each interceptor via
/// <c>interceptor/invoke</c> on the server that hosts it — mutations sequentially (each receiving
/// the previous one's output), validations in parallel.
/// </summary>
/// <remarks>
/// Interceptors with the same name on different servers are not deduplicated; each entry is
/// invoked on its own host, so results may share an <see cref="InterceptorResult.InterceptorName"/>.
/// </remarks>
public sealed class InterceptorChain
{
    /// <summary>Creates a chain from pre-discovered entries.</summary>
    public InterceptorChain(IReadOnlyList<InterceptorChainEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
    }

    /// <summary>Gets all interceptor entries in the chain, collected from one or more servers.</summary>
    public IReadOnlyList<InterceptorChainEntry> Entries { get; }

    /// <summary>
    /// Discovers interceptors by calling <c>interceptors/list</c> on every server in parallel and
    /// assembles them into a chain, preserving server order for stable ordering of exact ties.
    /// </summary>
    /// <remarks>
    /// Discovery is fail-closed: if any server's <c>interceptors/list</c> fails, the whole call
    /// throws rather than silently dropping that server's interceptors.
    /// </remarks>
    public static async ValueTask<InterceptorChain> DiscoverAsync(
        IEnumerable<McpClient> servers,
        ListInterceptorsRequestParams? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var serverList = servers.ToList();
        var listed = await Task.WhenAll(serverList.Select(
            server => server.ListInterceptorsAsync(requestParams, cancellationToken).AsTask())).ConfigureAwait(false);

        var entries = new List<InterceptorChainEntry>();
        for (var i = 0; i < serverList.Count; i++)
        {
            foreach (var interceptor in listed[i].Interceptors)
            {
                entries.Add(new InterceptorChainEntry { Interceptor = interceptor, Server = serverList[i] });
            }
        }

        return new InterceptorChain(entries);
    }

    /// <summary>
    /// Executes the chain for the given event and phase using the SEP execution model.
    /// </summary>
    public ValueTask<InterceptorChainResult> ExecuteAsync(
        ExecuteChainRequestParams requestParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestParams);

        var executionEntries = Entries
            .Select(e => new InterceptorChainOrchestrator.ChainExecutionEntry(
                e.Interceptor,
                (invokeParams, ct) => e.Server.InvokeInterceptorAsync(invokeParams, ct)))
            .ToList();

        return InterceptorChainOrchestrator.ExecuteAsync(executionEntries, requestParams, cancellationToken);
    }

    /// <summary>
    /// Discovers interceptors across the given servers (filtered by the request's event) and
    /// executes the merged chain in one call.
    /// </summary>
    public static async ValueTask<InterceptorChainResult> ExecuteAsync(
        IEnumerable<McpClient> servers,
        ExecuteChainRequestParams requestParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestParams);

        var chain = await DiscoverAsync(
            servers,
            new ListInterceptorsRequestParams { Event = requestParams.Event },
            cancellationToken).ConfigureAwait(false);

        return await chain.ExecuteAsync(requestParams, cancellationToken).ConfigureAwait(false);
    }
}
