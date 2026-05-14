using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Defines the type of an interceptor, which determines its behavior and result type.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorType>))]
public enum InterceptorType
{
    /// <summary>
    /// Validates requests/responses, returning pass/fail with severity levels.
    /// Validation interceptors run in parallel and can block the chain on error severity.
    /// </summary>
    [JsonStringEnumMemberName("validation")]
    Validation,

    /// <summary>
    /// Transforms payloads before continuing through the pipeline.
    /// Mutation interceptors run sequentially ordered by priority hint.
    /// </summary>
    [JsonStringEnumMemberName("mutation")]
    Mutation,

    /// <summary>
    /// Fire-and-forget, non-blocking, non-mutating interceptor type for reacting to context
    /// (logging, telemetry, avatar animation, voice-mode triggers, etc.) without affecting
    /// the interaction. Sink interceptors run in parallel and failures are swallowed.
    /// </summary>
    [JsonStringEnumMemberName("sink")]
    Sink,
}
