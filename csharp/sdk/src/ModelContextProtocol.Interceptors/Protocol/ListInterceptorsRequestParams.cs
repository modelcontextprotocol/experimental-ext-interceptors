using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Parameters for the <c>interceptors/list</c> request.
/// </summary>
public sealed class ListInterceptorsRequestParams
{
    /// <summary>Gets or sets an optional cursor for pagination.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    /// <summary>Gets or sets an optional event filter to list only interceptors matching the given event.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>Gets or sets optional metadata.</summary>
    [JsonPropertyName("_meta")]
    public JsonObject? Meta { get; set; }
}
