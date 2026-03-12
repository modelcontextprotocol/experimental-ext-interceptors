using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Represents a validation message returned by a validation interceptor.
/// </summary>
public sealed class ValidationMessage
{
    /// <summary>Gets or sets the JSON path to the field this message relates to.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Gets or sets the human-readable validation message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; set; }

    /// <summary>Gets or sets the severity of this validation message.</summary>
    [JsonPropertyName("severity")]
    public ValidationSeverity Severity { get; set; }
}
