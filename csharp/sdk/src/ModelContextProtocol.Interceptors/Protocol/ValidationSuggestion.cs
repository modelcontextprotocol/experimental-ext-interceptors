using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Represents a suggested fix from a validation interceptor.
/// </summary>
public sealed class ValidationSuggestion
{
    /// <summary>Gets or sets the JSON path to the field to modify.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    /// <summary>Gets or sets the suggested value for the field.</summary>
    [JsonPropertyName("value")]
    public JsonNode? Value { get; set; }
}
