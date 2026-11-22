using System.Runtime.CompilerServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Caches each interceptor server's <c>interceptors/list</c> result for a fixed duration, so that
/// proxying an operation costs N <c>interceptor/invoke</c> calls instead of 2 discoveries plus N invokes.
/// </summary>
/// <remarks>
/// The SEP has no <c>list_changed</c> notification for interceptors, so entries simply expire.
/// Clients are held weakly: a per-request interceptor client that is disposed and collected drops out.
/// </remarks>
internal sealed class InterceptorDiscoveryCache
{
    private readonly ConditionalWeakTable<McpClient, Entry> _entries = new();
    private readonly TimeSpan _duration;
    private readonly TimeProvider _timeProvider;

    internal InterceptorDiscoveryCache(TimeSpan? duration, TimeProvider? timeProvider = null)
    {
        _duration = duration ?? DefaultDuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The default cache duration.</summary>
    internal static TimeSpan DefaultDuration { get; } = TimeSpan.FromMinutes(1);

    internal async ValueTask<IList<Interceptor>> GetAsync(McpClient client, CancellationToken cancellationToken)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return await client.ListAllInterceptorsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        if (!_entries.TryGetValue(client, out var entry) || entry.ExpiresAt <= now || entry.Discovery.IsFaulted || entry.Discovery.IsCanceled)
        {
            // Not tied to the caller's token: concurrent callers share the discovery, and one caller
            // cancelling must not fail the others.
            entry = new Entry(client.ListAllInterceptorsAsync(cancellationToken: CancellationToken.None).AsTask(), now + _duration);
            _entries.AddOrUpdate(client, entry);
        }

        try
        {
            return await entry.Discovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (entry.Discovery.IsFaulted)
        {
            _entries.Remove(client);
            throw;
        }
    }

    internal void Invalidate() => _entries.Clear();

    private sealed record Entry(Task<IList<Interceptor>> Discovery, DateTimeOffset ExpiresAt);
}
