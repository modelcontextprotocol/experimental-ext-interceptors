using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// The invoker's policy for applying discovered interceptors: which ones run, with which
/// <see cref="InterceptorOverrides"/>, and with which configuration.
/// </summary>
/// <remarks>
/// Per SEP-2624, the set of interceptors applied to an operation, their order, and their overrides
/// are invoker-owned state. A policy is keyed by interceptor name; interceptors with the same name on
/// different servers share an entry.
/// </remarks>
public sealed class InterceptorPolicy
{
    /// <summary>
    /// Gets or sets the names of the interceptors to apply. When <see langword="null"/>, every
    /// discovered interceptor is applied.
    /// </summary>
    /// <remarks>
    /// Selection is fail-closed: if a listed interceptor is not discovered on any interceptor server,
    /// execution fails instead of silently running without it.
    /// </remarks>
    public ISet<string>? Include { get; set; }

    /// <summary>Gets or sets per-interceptor overrides, keyed by interceptor name.</summary>
    public IDictionary<string, InterceptorOverrides> Overrides { get; set; } =
        new Dictionary<string, InterceptorOverrides>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets per-interceptor configuration, keyed by interceptor name. The value is sent as
    /// <c>config</c> on every <c>interceptor/invoke</c>; when absent, the interceptor's own defaults apply.
    /// </summary>
    public IDictionary<string, JsonNode> Config { get; set; } =
        new Dictionary<string, JsonNode>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the timeout in milliseconds applied to interceptors whose overrides set no
    /// <see cref="InterceptorOverrides.TimeoutMs"/>.
    /// </summary>
    public int? DefaultTimeoutMs { get; set; }

    /// <summary>
    /// Binds discovered interceptors to their servers, applying this policy.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An interceptor in <see cref="Include"/> was not discovered, or an override widens declared hooks.
    /// </exception>
    public IReadOnlyList<InterceptorBinding> Bind(IEnumerable<(Interceptor Interceptor, McpClient Server)> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (DefaultTimeoutMs is <= 0)
        {
            throw new InvalidOperationException($"DefaultTimeoutMs must be a positive number of milliseconds, not {DefaultTimeoutMs}.");
        }

        var bindings = new List<InterceptorBinding>();
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (interceptor, server) in discovered)
        {
            if (Include is not null && !Include.Contains(interceptor.Name))
            {
                continue;
            }

            found.Add(interceptor.Name);
            bindings.Add(new InterceptorBinding
            {
                Interceptor = interceptor,
                Server = server,
                Overrides = ResolveOverrides(interceptor.Name),
                Config = Config.TryGetValue(interceptor.Name, out var config) ? config : null,
            });
        }

        if (Include is not null)
        {
            var missing = Include.Where(name => !found.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Required interceptor(s) {string.Join(", ", missing.Select(m => $"'{m}'"))} were not discovered on any interceptor server.");
            }
        }

        foreach (var binding in bindings)
        {
            InterceptorOverrides.ValidateFor(binding.Interceptor, binding.Overrides);
        }

        return bindings;
    }

    private InterceptorOverrides? ResolveOverrides(string name)
    {
        Overrides.TryGetValue(name, out var overrides);
        if (DefaultTimeoutMs is not int defaultTimeout || overrides?.TimeoutMs is not null)
        {
            return overrides;
        }

        return new InterceptorOverrides
        {
            FailOpen = overrides?.FailOpen,
            PriorityHint = overrides?.PriorityHint,
            Mode = overrides?.Mode,
            Hooks = overrides?.Hooks,
            TimeoutMs = defaultTimeout,
        };
    }
}
