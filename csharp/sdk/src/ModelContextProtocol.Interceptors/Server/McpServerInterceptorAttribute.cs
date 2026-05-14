using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// Marks a method as an MCP server interceptor. Methods with this attribute are discovered
/// by <see cref="McpServerInterceptorBuilderExtensions.WithInterceptors{T}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpServerInterceptorAttribute : Attribute
{
    /// <summary>Gets or sets the interceptor name. Defaults to the method name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a description of this interceptor.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the event types this interceptor handles.</summary>
    public string[] Events { get; set; } = [InterceptionEvents.All];

    /// <summary>Gets or sets the interceptor type.</summary>
    public InterceptorType Type { get; set; }

    /// <summary>
    /// Gets or sets the phase(s) in which this interceptor executes.
    /// Defaults to <see cref="InterceptorPhase.Both"/>, which expands to two hook entries
    /// (one for request, one for response) in the protocol-level <see cref="Interceptor"/>.
    /// </summary>
    public InterceptorPhase Phase { get; set; } = InterceptorPhase.Both;

    /// <summary>Gets or sets the priority hint for mutation ordering. Lower values execute first.</summary>
    public int PriorityHint { get; set; }

    /// <summary>
    /// Gets or sets the execution mode. Defaults to <see cref="InterceptorMode.Active"/>.
    /// <see cref="InterceptorMode.Audit"/> records results without blocking or applying changes.
    /// </summary>
    public InterceptorMode Mode { get; set; } = InterceptorMode.Active;

    /// <summary>
    /// Gets or sets the failure-routing policy. <c>false</c> (default) is fail-closed —
    /// crashes/timeouts block the message. <c>true</c> is fail-open — they allow it to proceed.
    /// </summary>
    public bool FailOpen { get; set; }
}
