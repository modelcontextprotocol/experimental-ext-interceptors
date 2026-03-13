using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Request payload for the <c>llm/completion</c> event, representing an LLM completion
/// request being intercepted by the gateway.
/// </summary>
public sealed class LlmCompletionRequestPayload
{
    /// <summary>Gets or sets the model identifier.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Gets or sets the messages to send to the model.</summary>
    [JsonPropertyName("messages")]
    public IList<LlmMessage>? Messages { get; set; }

    /// <summary>Gets or sets the maximum number of tokens to generate.</summary>
    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; set; }

    /// <summary>Gets or sets the sampling temperature.</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>Gets or sets arbitrary additional parameters for the completion request.</summary>
    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

/// <summary>
/// Response payload for the <c>llm/completion</c> event, representing an LLM completion
/// response being intercepted by the gateway.
/// </summary>
public sealed class LlmCompletionResponsePayload
{
    /// <summary>Gets or sets the model identifier that generated the response.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Gets or sets the generated message.</summary>
    [JsonPropertyName("message")]
    public LlmMessage? Message { get; set; }

    /// <summary>Gets or sets the reason the model stopped generating.</summary>
    [JsonPropertyName("stopReason")]
    public string? StopReason { get; set; }

    /// <summary>Gets or sets the usage information.</summary>
    [JsonPropertyName("usage")]
    public LlmUsage? Usage { get; set; }

    /// <summary>Gets or sets arbitrary additional metadata from the provider.</summary>
    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; set; }
}

/// <summary>
/// A message in an LLM conversation.
/// </summary>
public sealed class LlmMessage
{
    /// <summary>Gets or sets the role (e.g., "user", "assistant", "system").</summary>
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    /// <summary>Gets or sets the text content of the message.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

/// <summary>
/// Token usage information for an LLM completion.
/// </summary>
public sealed class LlmUsage
{
    /// <summary>Gets or sets the number of input tokens.</summary>
    [JsonPropertyName("inputTokens")]
    public int? InputTokens { get; set; }

    /// <summary>Gets or sets the number of output tokens.</summary>
    [JsonPropertyName("outputTokens")]
    public int? OutputTokens { get; set; }
}
