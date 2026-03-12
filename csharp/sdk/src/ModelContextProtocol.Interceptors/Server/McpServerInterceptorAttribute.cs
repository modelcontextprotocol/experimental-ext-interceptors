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
    public string[] Events { get; set; } = [InterceptorEvents.All];

    /// <summary>Gets or sets the interceptor type.</summary>
    public InterceptorType Type { get; set; }

    /// <summary>Gets or sets the phase(s) in which this interceptor executes.</summary>
    public InterceptorPhase Phase { get; set; } = InterceptorPhase.Both;

    /// <summary>Gets or sets the priority hint for mutation ordering. Lower values execute first.</summary>
    public int PriorityHint { get; set; }
}
