using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Specifies protocol version compatibility constraints for an interceptor.
/// </summary>
public sealed class InterceptorCompatibility
{
    /// <summary>Gets or sets the minimum protocol version required.</summary>
    [JsonPropertyName("minProtocol")]
    public required string MinProtocol { get; set; }

    /// <summary>Gets or sets the optional maximum protocol version supported.</summary>
    [JsonPropertyName("maxProtocol")]
    public string? MaxProtocol { get; set; }
}
