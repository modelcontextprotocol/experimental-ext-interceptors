using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// One lifecycle event to run interceptors for: the event, phase, direction and payload.
/// </summary>
/// <remarks>
/// This is SDK orchestration input, not a wire message. SEP-2624 defines no aggregate invocation
/// method: each applicable interceptor is invoked with <c>interceptor/invoke</c> on the server that
/// advertised it. Which interceptors apply, and with what overrides, is set by an
/// <see cref="Client.InterceptorPolicy"/>.
/// </remarks>
public sealed class InterceptorExecutionRequest
{
    /// <summary>Gets or sets the lifecycle event, for example <c>tools/call</c>.</summary>
    [JsonPropertyName("event")]
    public required string Event { get; set; }

    /// <summary>Gets or sets the phase: <see cref="InterceptorPhase.Request"/> or <see cref="InterceptorPhase.Response"/>.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }

    /// <summary>
    /// Gets or sets whether the invoker is sending or receiving this message. Decides the execution
    /// order. When <see langword="null"/>, the invoker is assumed to act for the client
    /// (<see cref="InterceptorInvokerRole.Client"/>): requests are sent and responses received.
    /// </summary>
    [JsonPropertyName("direction")]
    public InterceptorDirection? Direction { get; set; }

    /// <summary>Gets or sets the message payload.</summary>
    [JsonPropertyName("payload")]
    public required JsonNode Payload { get; set; }

    /// <summary>
    /// Gets or sets an aggregate timeout in milliseconds covering every interceptor applied to this
    /// event. When it elapses, the remaining invocations are cancelled and the operation is treated
    /// as timed out, regardless of fail-open. Per-interceptor timeouts are set with
    /// <see cref="Client.InterceptorOverrides.TimeoutMs"/>.
    /// </summary>
    [JsonPropertyName("timeoutMs")]
    [JsonConverter(typeof(LenientInt32MillisecondsConverter))]
    public int? TimeoutMs { get; set; }

    /// <summary>Gets or sets the context (principal, trace, session) sent with each invocation.</summary>
    [JsonPropertyName("context")]
    public InvokeInterceptorContext? Context { get; set; }
}
