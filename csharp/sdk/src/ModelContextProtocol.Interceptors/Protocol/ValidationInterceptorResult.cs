using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Result from a validation interceptor invocation.
/// </summary>
public sealed class ValidationInterceptorResult : InterceptorResult
{
    /// <summary>Gets or sets whether the validation passed.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary>Gets or sets the overall severity of the validation result.</summary>
    [JsonPropertyName("severity")]
    public ValidationSeverity? Severity { get; set; }

    /// <summary>Gets or sets detailed validation messages.</summary>
    [JsonPropertyName("messages")]
    public IList<ValidationMessage>? Messages { get; set; }

    /// <summary>Gets or sets suggested fixes for validation failures.</summary>
    [JsonPropertyName("suggestions")]
    public IList<ValidationSuggestion>? Suggestions { get; set; }

    /// <summary>Creates a successful validation result.</summary>
    public static ValidationInterceptorResult Success() => new() { Valid = true };

    /// <summary>Creates a failed validation result with the given messages.</summary>
    public static ValidationInterceptorResult Failure(params ValidationMessage[] messages) => new()
    {
        Valid = false,
        Severity = ValidationSeverity.Error,
        Messages = messages,
    };
}
