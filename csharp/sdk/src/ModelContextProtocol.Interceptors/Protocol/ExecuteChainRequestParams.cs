using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Parameters for SDK-level chain execution (see
/// <see cref="Client.McpClientInterceptorExtensions.ExecuteChainAsync"/>). Per SEP-1763 the chain
/// is orchestrated client-side via <c>interceptors/list</c> + <c>interceptor/invoke</c>; there is
/// no <c>interceptor/executeChain</c> wire method.
/// </summary>
public sealed class ExecuteChainRequestParams
{
    /// <summary>Gets or sets the event that triggered this chain execution.</summary>
    [JsonPropertyName("event")]
    public required string Event { get; set; }

    /// <summary>Gets or sets the phase of this chain execution.</summary>
    [JsonPropertyName("phase")]
    public InterceptorPhase Phase { get; set; }

    /// <summary>Gets or sets the message payload to process through the chain.</summary>
    [JsonPropertyName("payload")]
    public required JsonNode Payload { get; set; }

    /// <summary>Gets or sets an optional list of specific interceptor names to include in the chain.</summary>
    [JsonPropertyName("interceptors")]
    public IList<string>? InterceptorNames { get; set; }

    /// <summary>Gets or sets optional per-interceptor configuration, keyed by interceptor name.</summary>
    [JsonPropertyName("config")]
    public JsonNode? Config { get; set; }

    /// <summary>Gets or sets the chain-wide execution timeout in milliseconds.</summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; set; }

    /// <summary>Gets or sets the request context (principal, trace, session).</summary>
    [JsonPropertyName("context")]
    public InvokeInterceptorContext? Context { get; set; }
}
