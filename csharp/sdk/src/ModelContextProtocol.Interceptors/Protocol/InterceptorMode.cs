using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Controls whether an interceptor's effects are applied or just recorded.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorMode>))]
public enum InterceptorMode
{
    /// <summary>
    /// Normal blocking / transforming behavior. Validators block on error severity;
    /// mutators apply their payload changes.
    /// </summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>
    /// Non-blocking operation. Validators log violations without blocking execution;
    /// mutators compute transformations without applying them (shadow mutations).
    /// </summary>
    [JsonStringEnumMemberName("audit")]
    Audit,
}
