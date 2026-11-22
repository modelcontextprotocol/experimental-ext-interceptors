using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// One reason an operation was rejected by validation, as reported in the SEP's
/// <c>Interceptor validation failed</c> error (<c>data.validationErrors</c>).
/// </summary>
public sealed class InterceptorValidationError
{
    /// <summary>Gets the name of the interceptor that reported the error.</summary>
    [JsonPropertyName("interceptor")]
    public required string Interceptor { get; init; }

    /// <summary>Gets the severity of the message.</summary>
    [JsonPropertyName("severity")]
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;

    /// <summary>Gets the human-readable explanation.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Gets the JSON path to the offending field, if the interceptor gave one.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}
