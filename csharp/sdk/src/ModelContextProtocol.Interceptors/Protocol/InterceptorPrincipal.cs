using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Represents the principal (identity) associated with an interceptor invocation.
/// </summary>
public sealed class InterceptorPrincipal
{
    /// <summary>Gets or sets the type of principal: "user", "service", or "anonymous".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>Gets or sets the optional identifier of the principal.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets optional claims associated with the principal.</summary>
    [JsonPropertyName("claims")]
    public IDictionary<string, object>? Claims { get; set; }
}
