using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Parameters for the <c>interceptor/invoke</c> request.
/// </summary>
public sealed class InvokeInterceptorRequestParams
{
    /// <summary>Gets or sets the name of the interceptor to invoke.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Gets or sets the event that triggered this invocation.</summary>
    [JsonPropertyName("event")]
    public required string Event { get; set; }

    /// <summary>Gets or sets the phase of this invocation.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }

    /// <summary>Gets or sets the message payload to intercept.</summary>
    [JsonPropertyName("payload")]
    public required JsonNode Payload { get; set; }

    /// <summary>Gets or sets optional configuration for this interceptor invocation.</summary>
    [JsonPropertyName("config")]
    public JsonNode? Config { get; set; }

    /// <summary>Gets or sets the execution timeout in milliseconds.</summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; set; }

    /// <summary>Gets or sets the request context (principal, trace, session).</summary>
    [JsonPropertyName("context")]
    public InvokeInterceptorContext? Context { get; set; }

    /// <summary>Gets or sets optional metadata.</summary>
    [JsonPropertyName("_meta")]
    public JsonObject? Meta { get; set; }
}
