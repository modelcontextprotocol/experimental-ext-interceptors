using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Aggregate result of an SDK-orchestrated interceptor chain (see
/// <see cref="Client.McpClientInterceptorExtensions.ExecuteChainAsync"/>). Carries the aggregated
/// per-interceptor results, the final payload after mutations, a validation summary, and any abort info.
/// </summary>
public sealed class InterceptorChainResult
{
    /// <summary>Gets or sets the overall status of the chain execution.</summary>
    [JsonPropertyName("status")]
    public InterceptorChainStatus Status { get; set; }

    /// <summary>Gets or sets the event that was processed.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>Gets or sets the phase that was processed.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }

    /// <summary>Gets or sets the individual results from each interceptor in the chain.</summary>
    [JsonPropertyName("results")]
    public IList<InterceptorResult> Results { get; set; } = [];

    /// <summary>Gets or sets the final payload after all mutations have been applied.</summary>
    [JsonPropertyName("finalPayload")]
    public JsonNode? FinalPayload { get; set; }

    /// <summary>Gets or sets a summary of validation results across the chain.</summary>
    [JsonPropertyName("validationSummary")]
    public ChainValidationSummary? ValidationSummary { get; set; }

    /// <summary>Gets or sets the total chain execution duration in milliseconds.</summary>
    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    /// <summary>Gets or sets information about which interceptor caused the chain to abort, if applicable.</summary>
    [JsonPropertyName("abortedAt")]
    public ChainAbortInfo? AbortedAt { get; set; }
}
