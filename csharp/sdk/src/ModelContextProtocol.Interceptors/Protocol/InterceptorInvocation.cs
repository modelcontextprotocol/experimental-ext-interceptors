using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// What happened when one interceptor was applied to an operation: the policy it ran under, where
/// each policy value came from, and its effect on the operation.
/// </summary>
/// <remarks>
/// SEP-2624 Security Best Practice 3 asks invokers to log, for each invocation, the resolved
/// <c>mode</c> and <c>failOpen</c> and whether each came from an invoker override or the
/// interceptor's declared default. <see cref="InterceptorExecutionResult.Invocations"/> carries one
/// of these per applied interceptor, in execution order.
/// </remarks>
public sealed class InterceptorInvocation
{
    /// <summary>Gets the interceptor name.</summary>
    [JsonPropertyName("interceptor")]
    public required string Interceptor { get; init; }

    /// <summary>Gets the name of the server hosting the interceptor, when known.</summary>
    [JsonPropertyName("server")]
    public string? Server { get; init; }

    /// <summary>Gets the interceptor type.</summary>
    [JsonPropertyName("type")]
    public InterceptorType Type { get; init; }

    /// <summary>Gets the resolved execution mode.</summary>
    [JsonPropertyName("mode")]
    public InterceptorMode Mode { get; init; }

    /// <summary>Gets where <see cref="Mode"/> came from.</summary>
    [JsonPropertyName("modeSource")]
    public InterceptorPolicySource ModeSource { get; init; }

    /// <summary>Gets the resolved failure routing policy.</summary>
    [JsonPropertyName("failOpen")]
    public bool FailOpen { get; init; }

    /// <summary>Gets where <see cref="FailOpen"/> came from.</summary>
    [JsonPropertyName("failOpenSource")]
    public InterceptorPolicySource FailOpenSource { get; init; }

    /// <summary>Gets the resolved priority for this phase. Only meaningful for mutators.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    /// <summary>Gets the resolved per-interceptor timeout, if any.</summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }

    /// <summary>Gets how the invocation itself ended.</summary>
    [JsonPropertyName("outcome")]
    public InterceptorInvocationOutcome Outcome { get; set; }

    /// <summary>Gets the effect the invocation had on the operation.</summary>
    [JsonPropertyName("effect")]
    public InterceptorEffect Effect { get; set; }

    /// <summary>Gets the interceptor's result, when the invocation completed.</summary>
    [JsonPropertyName("result")]
    public InterceptorResult? Result { get; set; }

    /// <summary>Gets the failure reason, when the invocation failed or timed out.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Gets the time the invoker spent waiting for the interceptor, in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }
}

/// <summary>Where a resolved policy value came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorPolicySource>))]
public enum InterceptorPolicySource
{
    /// <summary>Neither the invoker nor the interceptor set it; the SEP default applies.</summary>
    [JsonStringEnumMemberName("default")]
    Default,

    /// <summary>The interceptor declared it in <c>interceptors/list</c>.</summary>
    [JsonStringEnumMemberName("declared")]
    Declared,

    /// <summary>The invoker set it in <c>InterceptorOverrides</c>.</summary>
    [JsonStringEnumMemberName("override")]
    Override,
}

/// <summary>How an interceptor invocation ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorInvocationOutcome>))]
public enum InterceptorInvocationOutcome
{
    /// <summary>The interceptor returned a result.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>The interceptor returned an error, returned an unusable result, or could not be reached.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The per-interceptor timeout elapsed.</summary>
    [JsonStringEnumMemberName("timed_out")]
    TimedOut,

    /// <summary>A sink was dispatched in the background; the operation did not wait for it.</summary>
    [JsonStringEnumMemberName("dispatched")]
    Dispatched,
}

/// <summary>The effect an interceptor invocation had on the operation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorEffect>))]
public enum InterceptorEffect
{
    /// <summary>No effect: the validator passed, the mutator left the payload unchanged, or the sink was dispatched.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>The mutator changed the payload and the change was applied.</summary>
    [JsonStringEnumMemberName("modified")]
    Modified,

    /// <summary>The interceptor blocked the operation (a failing validator, or a fail-closed crash or timeout).</summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    /// <summary>An audit-mode mutator changed the payload, but the change was not applied.</summary>
    [JsonStringEnumMemberName("shadow_modified")]
    ShadowModified,

    /// <summary>An audit-mode validator would have blocked the operation in active mode.</summary>
    [JsonStringEnumMemberName("would_block")]
    WouldBlock,

    /// <summary>The interceptor crashed or timed out, and fail-open let the operation proceed.</summary>
    [JsonStringEnumMemberName("failed_open")]
    FailedOpen,
}
