using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Defines the severity level of a validation message.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ValidationSeverity>))]
public enum ValidationSeverity
{
    /// <summary>Informational message that does not block execution.</summary>
    [JsonStringEnumMemberName("info")]
    Info,

    /// <summary>Warning that does not block execution but should be reviewed.</summary>
    [JsonStringEnumMemberName("warn")]
    Warn,

    /// <summary>Error that blocks execution and aborts the interceptor chain.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}
