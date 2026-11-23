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
/// Concurrent callers share one listing, and a listing still in progress is always joined, however long
/// it has run. A result expires the configured duration after its listing completed.
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
                entry = new Entry(client, _duration, _discoveryTimeout, _timeProvider);
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

    /// <summary>One shared listing and, once it succeeds, its result until it expires.</summary>
    private sealed class Entry
    {
        private readonly Lazy<Task<IList<Interceptor>>> _discovery;
        private DateTimeOffset _expiresAt;

        public Entry(McpClient client, TimeSpan duration, TimeSpan timeout, TimeProvider timeProvider)
        {
            _discovery = new(async () =>
            {
                using var cts = new CancellationTokenSource(timeout);
                var interceptors = await client.ListAllInterceptorsAsync(cancellationToken: cts.Token).ConfigureAwait(false);

                // Written before the task completes, and read only after observing that it completed.
                _expiresAt = timeProvider.GetUtcNow() + duration;
                return interceptors;
            });
        }

        public Task<IList<Interceptor>> Discovery => _discovery.Value;

        /// <summary>
        /// A listing in progress is always usable: it is bounded by the discovery timeout, and starting
        /// another would only add load to a slow server. A completed one is usable until it expires.
        /// </summary>
        public bool IsUsable(DateTimeOffset now)
        {
            if (!_discovery.IsValueCreated)
            {
                return true;
            }

            var discovery = _discovery.Value;
            return !discovery.IsCompleted || (discovery.IsCompletedSuccessfully && _expiresAt > now);
        }
    }
}
