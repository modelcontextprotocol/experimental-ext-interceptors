using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// The outcome of running interceptors for one lifecycle event: the final payload, every
/// invocation with its resolved policy, and why the operation was rejected, if it was.
/// </summary>
public sealed class InterceptorExecutionResult
{
    /// <summary>Gets or sets the overall status.</summary>
    [JsonPropertyName("status")]
    public InterceptorExecutionStatus Status { get; set; }

    /// <summary>Gets or sets the event that was processed.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>Gets or sets the phase that was processed.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }

    /// <summary>Gets or sets the direction the message was travelling, which decided the execution order.</summary>
    [JsonPropertyName("direction")]
    public InterceptorDirection Direction { get; set; }

    /// <summary>
    /// Gets or sets the results returned by validators and mutators, including those running in
    /// audit mode. Sinks run in the background and are not included.
    /// </summary>
    [JsonPropertyName("results")]
    public IList<InterceptorResult> Results { get; set; } = [];

    /// <summary>Gets or sets one record per applied interceptor, in execution order.</summary>
    [JsonPropertyName("invocations")]
    public IList<InterceptorInvocation> Invocations { get; set; } = [];

    /// <summary>
    /// Gets or sets the payload as it was before any interceptor ran, for observers that want to show
    /// what mutations changed.
    /// </summary>
    /// <remarks>
    /// Not serialized: an audit log written from this result must not capture pre-redaction content.
    /// </remarks>
    [JsonIgnore]
    public JsonNode? OriginalPayload { get; set; }

    /// <summary>
    /// Gets or sets the payload after all applied mutations. When the operation was rejected, this is
    /// the original payload: mutations are atomic, so either all apply or none do.
    /// </summary>
    [JsonPropertyName("finalPayload")]
    public JsonNode? FinalPayload { get; set; }

    /// <summary>Gets or sets message counts by severity across all validator results.</summary>
    [JsonPropertyName("validationSummary")]
    public ValidationSummary? ValidationSummary { get; set; }

    /// <summary>
    /// Gets or sets every reason validation rejected the operation. Empty unless
    /// <see cref="Status"/> is <see cref="InterceptorExecutionStatus.ValidationFailed"/>.
    /// </summary>
    [JsonPropertyName("validationErrors")]
    public IList<InterceptorValidationError> ValidationErrors { get; set; } = [];

    /// <summary>Gets or sets the total time spent running interceptors, in milliseconds.</summary>
    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    /// <summary>Gets or sets which interceptor rejected the operation and why, if it was rejected.</summary>
    [JsonPropertyName("abortedAt")]
    public InterceptorAbortInfo? AbortedAt { get; set; }
}
