using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Provides context about the request that triggered an interceptor invocation.
/// </summary>
public sealed class InvokeInterceptorContext
{
    /// <summary>Gets or sets the principal (identity) making the request.</summary>
    [JsonPropertyName("principal")]
    public InterceptorPrincipal? Principal { get; set; }

    /// <summary>Gets or sets the distributed trace ID for correlation.</summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    /// <summary>Gets or sets the span ID within the trace.</summary>
    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    /// <summary>Gets or sets the ISO 8601 timestamp of the request.</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>Gets or sets the session ID for the MCP session.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
}
