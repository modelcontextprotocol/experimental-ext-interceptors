using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Defines when an interceptor executes relative to the request/response lifecycle.
/// </summary>
/// <remarks>
/// <see cref="Both"/> is only valid at the attribute/SDK convenience layer (see
/// <see cref="ModelContextProtocol.Interceptors.Server.McpServerInterceptorAttribute"/>); it
/// must never appear on the wire. The reflection layer expands <c>Both</c> into two
/// <see cref="InterceptorHook"/> entries — one for <see cref="Request"/> and one for
/// <see cref="Response"/> — when constructing the protocol-level <see cref="Interceptor"/>.
/// Wire-level types (<see cref="InterceptorHook"/>, <see cref="InterceptorResult"/>,
/// <see cref="ExecuteChainRequestParams"/>, <see cref="InvokeInterceptorRequestParams"/>) only
/// ever carry <see cref="Request"/> or <see cref="Response"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorPhase>))]
public enum InterceptorPhase
{
    /// <summary>The interceptor runs during the request phase (before the operation executes).</summary>
    [JsonStringEnumMemberName("request")]
    Request,

    /// <summary>The interceptor runs during the response phase (after the operation executes).</summary>
    [JsonStringEnumMemberName("response")]
    Response,

    /// <summary>
    /// Attribute-only convenience value meaning the interceptor runs on both request and response phases.
    /// The reflection layer expands this into two hook entries; it must not appear on the wire.
    /// </summary>
    [JsonStringEnumMemberName("both")]
    Both,
}
