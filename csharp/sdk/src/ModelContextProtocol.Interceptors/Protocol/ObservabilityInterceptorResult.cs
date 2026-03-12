using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Result from an observability interceptor invocation.
/// </summary>
public sealed class ObservabilityInterceptorResult : InterceptorResult
{
    /// <summary>Gets or sets whether the observation was recorded.</summary>
    [JsonPropertyName("observed")]
    public bool Observed { get; set; }

    /// <summary>Gets or sets collected metrics as name-value pairs.</summary>
    [JsonPropertyName("metrics")]
    public IDictionary<string, double>? Metrics { get; set; }
}
