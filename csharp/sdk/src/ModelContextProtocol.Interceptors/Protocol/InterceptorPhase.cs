using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Defines when an interceptor executes relative to the request/response lifecycle.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorPhase>))]
public enum InterceptorPhase
{
    /// <summary>The interceptor runs during the request phase (before the operation executes).</summary>
    [JsonStringEnumMemberName("request")]
    Request,

    /// <summary>The interceptor runs during the response phase (after the operation executes).</summary>
    [JsonStringEnumMemberName("response")]
    Response,

    /// <summary>The interceptor runs during both request and response phases.</summary>
    [JsonStringEnumMemberName("both")]
    Both,
}
