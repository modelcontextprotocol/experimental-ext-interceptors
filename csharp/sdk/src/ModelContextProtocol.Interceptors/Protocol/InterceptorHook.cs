using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// A single hook entry on an <see cref="Interceptor"/>, pairing a set of lifecycle events
/// with a single phase. An interceptor that needs to run on both request and response phases
/// uses two hook entries — one per phase.
/// </summary>
public sealed class InterceptorHook
{
    /// <summary>Gets or sets the lifecycle events this hook subscribes to.</summary>
    [JsonPropertyName("events")]
    public IList<string> Events { get; set; } = [];

    /// <summary>Gets or sets the phase (request or response) on which this hook fires.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }
}
