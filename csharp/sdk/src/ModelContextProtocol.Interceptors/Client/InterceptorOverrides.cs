using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Invoker-side policy for one discovered interceptor (SEP-2624 <c>InterceptorOverrides</c>).
/// Each value, when set, takes precedence over the default the interceptor declares in
/// <c>interceptors/list</c>.
/// </summary>
/// <remarks>
/// <para>
/// The SEP separates <em>capability</em> (what the interceptor server declares: type, hooks,
/// <c>configSchema</c>) from <em>policy</em> (how this deployment runs it). Overrides are
/// invoker-local and never sent to the interceptor server; only the resolved <see cref="TimeoutMs"/>
/// travels, as <c>interceptor/invoke</c>'s <c>timeoutMs</c>.
/// </para>
/// <para>
/// Typical uses: run an interceptor in <see cref="InterceptorMode.Audit"/> in staging and active in
/// production, make a fragile interceptor fail-open with a short timeout, or route only some of an
/// interceptor's events to it.
/// </para>
/// </remarks>
public sealed class InterceptorOverrides
{
    /// <summary>
    /// Gets or sets the failure routing policy. <see langword="true"/> lets the message proceed if the
    /// interceptor crashes or times out; <see langword="false"/> blocks it.
    /// </summary>
    public bool? FailOpen { get; set; }

    /// <summary>Gets or sets the mutation ordering priority. Ignored for validators and sinks.</summary>
    public PriorityHint? PriorityHint { get; set; }

    /// <summary>
    /// Gets or sets the execution mode. <see cref="InterceptorMode.Audit"/> records results without
    /// blocking (validators) or applying changes (mutators).
    /// </summary>
    public InterceptorMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the per-interceptor timeout in milliseconds. When it elapses the invocation is
    /// cancelled and the resolved <see cref="FailOpen"/> decides whether the message proceeds.
    /// </summary>
    public int? TimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets hooks that narrow the interceptor's declared hooks. Every event in every entry
    /// must be covered by a declared hook for the same phase; overrides that widen are rejected.
    /// </summary>
    public IList<InterceptorHook>? Hooks { get; set; }

    /// <summary>
    /// Throws when <paramref name="overrides"/> would widen the hooks <paramref name="interceptor"/> declares.
    /// </summary>
    /// <exception cref="InvalidOperationException">The overrides reference an event or phase the interceptor does not declare.</exception>
    public static void ValidateFor(Interceptor interceptor, InterceptorOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        if (overrides?.Hooks is not { } hooks)
        {
            return;
        }

        foreach (var hook in hooks)
        {
            if (hook.Phase is not (InterceptorPhase.Request or InterceptorPhase.Response))
            {
                throw new InvalidOperationException(
                    $"Override hooks for interceptor '{interceptor.Name}' must use the 'request' or 'response' phase, not '{hook.Phase}'.");
            }

            foreach (var ev in hook.Events)
            {
                var declared = interceptor.Hooks.Any(d =>
                    d.Phase == hook.Phase &&
                    (d.Events.Contains(InterceptionEvents.All) || d.Events.Contains(ev)));

                if (!declared)
                {
                    throw new InvalidOperationException(
                        $"Override hooks for interceptor '{interceptor.Name}' widen its declared hooks: " +
                        $"'{ev}' in the '{hook.Phase.ToString().ToLowerInvariant()}' phase is not declared. Overrides may only narrow hooks.");
                }
            }
        }
    }
}
