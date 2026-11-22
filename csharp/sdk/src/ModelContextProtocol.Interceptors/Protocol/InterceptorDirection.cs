using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// The direction in which a message crosses the trust boundary, from the invoker's point of view.
/// Direction, not phase, decides the SEP's execution order.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Sending"/>: mutate (sequential) → validate (parallel) → send. Validators see the final bytes.</item>
/// <item><see cref="Receiving"/>: validate (parallel) → mutate (sequential) → process. Validators act as a security barrier.</item>
/// </list>
/// Phase alone cannot determine direction: a client sends its request and receives the response,
/// while a server receives the request and sends the response. Use <see cref="InterceptorInvokerRole"/>
/// to derive the direction for a phase.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorDirection>))]
public enum InterceptorDirection
{
    /// <summary>The invoker is about to send the message across the trust boundary.</summary>
    [JsonStringEnumMemberName("sending")]
    Sending,

    /// <summary>The invoker has just received the message from across the trust boundary.</summary>
    [JsonStringEnumMemberName("receiving")]
    Receiving,
}

/// <summary>
/// Which side of the trust boundary an invoker acts for. Determines the <see cref="InterceptorDirection"/>
/// of each phase.
/// </summary>
public enum InterceptorInvokerRole
{
    /// <summary>
    /// The invoker acts for the client: it sends requests and receives responses. Use this for
    /// agents, client-side wrappers, and egress gateways that protect an agent from the servers it calls.
    /// </summary>
    Client,

    /// <summary>
    /// The invoker acts for the server: it receives requests and sends responses. Use this for
    /// ingress gateways that protect a server from the clients calling it.
    /// </summary>
    Server,
}

/// <summary>Helpers for mapping phases to directions.</summary>
public static class InterceptorDirectionExtensions
{
    /// <summary>Gets the direction of <paramref name="phase"/> for an invoker acting in <paramref name="role"/>.</summary>
    public static InterceptorDirection GetDirection(this InterceptorInvokerRole role, InterceptorPhase phase)
    {
        var isRequest = phase switch
        {
            InterceptorPhase.Request => true,
            InterceptorPhase.Response => false,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Only the request and response phases have a direction."),
        };

        return (role, isRequest) switch
        {
            (InterceptorInvokerRole.Client, true) => InterceptorDirection.Sending,
            (InterceptorInvokerRole.Client, false) => InterceptorDirection.Receiving,
            (InterceptorInvokerRole.Server, true) => InterceptorDirection.Receiving,
            _ => InterceptorDirection.Sending,
        };
    }
}
