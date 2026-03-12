using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Result of the <c>interceptors/list</c> request.
/// </summary>
public sealed class ListInterceptorsResult
{
    /// <summary>Gets or sets the list of available interceptors.</summary>
    [JsonPropertyName("interceptors")]
    public IList<Interceptor> Interceptors { get; set; } = [];

    /// <summary>Gets or sets the cursor for the next page, if more results are available.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}
