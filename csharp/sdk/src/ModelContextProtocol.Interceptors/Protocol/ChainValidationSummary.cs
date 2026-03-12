using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Summary of validation results across all interceptors in a chain execution.
/// </summary>
public sealed class ChainValidationSummary
{
    /// <summary>Gets or sets the number of error-level validation messages.</summary>
    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    /// <summary>Gets or sets the number of warning-level validation messages.</summary>
    [JsonPropertyName("warnings")]
    public int Warnings { get; set; }

    /// <summary>Gets or sets the number of info-level validation messages.</summary>
    [JsonPropertyName("infos")]
    public int Infos { get; set; }
}
