using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Result from a mutation interceptor invocation.
/// </summary>
public sealed class MutationInterceptorResult : InterceptorResult
{
    /// <summary>Gets or sets whether the payload was modified.</summary>
    [JsonPropertyName("modified")]
    public bool Modified { get; set; }

    /// <summary>Gets or sets the original or mutated payload.</summary>
    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }
}
