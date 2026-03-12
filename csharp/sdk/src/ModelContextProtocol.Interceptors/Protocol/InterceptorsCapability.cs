using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Represents the interceptors capability advertised by a server during initialization.
/// This is placed in <c>ServerCapabilities.Extensions["interceptors"]</c>.
/// </summary>
public sealed class InterceptorsCapability
{
    /// <summary>Gets or sets the list of event types this server's interceptors support.</summary>
    [JsonPropertyName("supportedEvents")]
    public IList<string> SupportedEvents { get; set; } = [];
}
