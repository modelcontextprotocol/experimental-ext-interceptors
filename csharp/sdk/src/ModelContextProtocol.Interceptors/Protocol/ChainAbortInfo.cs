using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Information about which interceptor caused a chain execution to abort.
/// </summary>
public sealed class ChainAbortInfo
{
    /// <summary>Gets or sets the name of the interceptor that caused the abort.</summary>
    [JsonPropertyName("interceptor")]
    public required string Interceptor { get; set; }

    /// <summary>Gets or sets the reason for the abort.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    /// <summary>Gets or sets the type of abort (validation, mutation, or timeout).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}
