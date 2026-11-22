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
/// Concurrent callers on a cold or expired entry share one listing.
/// </remarks>
internal sealed class InterceptorDiscoveryCache
{
    private readonly ConditionalWeakTable<McpClient, Entry> _entries = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _duration;
    private readonly TimeSpan _discoveryTimeout;
    private readonly TimeProvider _timeProvider;

    internal InterceptorDiscoveryCache(TimeSpan? duration, TimeProvider? timeProvider = null, TimeSpan? discoveryTimeout = null)
    {
        _duration = duration ?? DefaultDuration;
        _discoveryTimeout = discoveryTimeout ?? DefaultDiscoveryTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The default cache duration.</summary>
    internal static TimeSpan DefaultDuration { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Upper bound for one discovery. A shared discovery is not tied to any caller's token, and a caller may
    /// set no deadline at all, so without a bound a stalled <c>interceptors/list</c> would stay pending forever.
    /// </summary>
    internal static TimeSpan DefaultDiscoveryTimeout { get; } = TimeSpan.FromSeconds(30);

    internal async ValueTask<IList<Interceptor>> GetAsync(McpClient client, CancellationToken cancellationToken)
    {
        if (_duration <= TimeSpan.Zero)
        {
            // Caching is off, but the discovery bound still applies.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_discoveryTimeout);
            return await client.ListAllInterceptorsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
        }

        Entry entry;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (!_entries.TryGetValue(client, out entry!) || !entry.IsUsable(now))
            {
                entry = new Entry(client, now + _duration, _discoveryTimeout);
                _entries.AddOrUpdate(client, entry);
            }
        }

        // The listing starts outside the lock, exactly once per entry. It is not tied to the caller's token:
        // concurrent callers share it, and one caller cancelling must not fail the others.
        var discovery = entry.Discovery;
        try
        {
            return await discovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (discovery.IsFaulted || discovery.IsCanceled)
        {
            Remove(client, entry);
            throw;
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>Removes <paramref name="entry"/> unless a newer entry has already replaced it.</summary>
    private void Remove(McpClient client, Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(client, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(client);
            }
        }
    }

    private sealed class Entry(McpClient client, DateTimeOffset expiresAt, TimeSpan timeout)
    {
        private readonly Lazy<Task<IList<Interceptor>>> _discovery = new(() => DiscoverAsync(client, timeout));

        public Task<IList<Interceptor>> Discovery => _discovery.Value;

        public bool IsUsable(DateTimeOffset now) =>
            expiresAt > now && !(_discovery.IsValueCreated && (_discovery.Value.IsFaulted || _discovery.Value.IsCanceled));

        private static async Task<IList<Interceptor>> DiscoverAsync(McpClient client, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await client.ListAllInterceptorsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
        }
    }
}
