using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Defines the overall status of an interceptor chain execution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterceptorChainStatus>))]
public enum InterceptorChainStatus
{
    /// <summary>All interceptors executed successfully.</summary>
    [JsonStringEnumMemberName("success")]
    Success,

    /// <summary>A validation interceptor failed with error severity.</summary>
    [JsonStringEnumMemberName("validation_failed")]
    ValidationFailed,

    /// <summary>A mutation interceptor failed during execution.</summary>
    [JsonStringEnumMemberName("mutation_failed")]
    MutationFailed,

    /// <summary>The chain execution timed out.</summary>
    [JsonStringEnumMemberName("timeout")]
    Timeout,
}
