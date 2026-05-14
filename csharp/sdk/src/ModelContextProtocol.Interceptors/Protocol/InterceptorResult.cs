using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Base class for interceptor invocation results. The concrete type is determined by <see cref="InterceptorType"/>.
/// Uses STJ polymorphic dispatch on the <c>type</c> discriminator property.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ValidationInterceptorResult), "validation")]
[JsonDerivedType(typeof(MutationInterceptorResult), "mutation")]
[JsonDerivedType(typeof(SinkInterceptorResult), "sink")]
public abstract class InterceptorResult
{
    /// <summary>Gets or sets the name of the interceptor that produced this result.</summary>
    [JsonPropertyName("interceptor")]
    public string? InterceptorName { get; set; }

    /// <summary>Gets or sets the phase during which this result was produced.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }

    /// <summary>Gets or sets the execution duration in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    /// <summary>Gets or sets additional metadata about the result.</summary>
    [JsonPropertyName("info")]
    public IDictionary<string, object>? Info { get; set; }
}
