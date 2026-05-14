using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Result from a sink interceptor invocation. Sink interceptors are fire-and-forget,
/// non-blocking, and non-mutating — they react to context (logging, telemetry, avatar
/// animation, voice mode triggers) without affecting the interaction itself.
/// </summary>
public sealed class SinkInterceptorResult : InterceptorResult
{
    /// <summary>Gets or sets whether the sink successfully recorded/reacted to the event.</summary>
    [JsonPropertyName("recorded")]
    public bool Recorded { get; set; }

    /// <summary>Gets or sets collected metrics as name-value pairs.</summary>
    [JsonPropertyName("metrics")]
    public IDictionary<string, double>? Metrics { get; set; }
}
