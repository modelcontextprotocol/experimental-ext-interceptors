using System.Text.Json.Serialization;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors;

/// <summary>
/// Source-generated JSON serialization context for all interceptor protocol types.
/// </summary>
[JsonSerializable(typeof(Interceptor))]
[JsonSerializable(typeof(InterceptorType))]
[JsonSerializable(typeof(InterceptorPhase))]
[JsonSerializable(typeof(InterceptorCompatibility))]
[JsonSerializable(typeof(InterceptorsCapability))]
[JsonSerializable(typeof(InterceptorResult))]
[JsonSerializable(typeof(ValidationInterceptorResult))]
[JsonSerializable(typeof(MutationInterceptorResult))]
[JsonSerializable(typeof(ObservabilityInterceptorResult))]
[JsonSerializable(typeof(ValidationMessage))]
[JsonSerializable(typeof(ValidationSeverity))]
[JsonSerializable(typeof(ValidationSuggestion))]
[JsonSerializable(typeof(InvokeInterceptorRequestParams))]
[JsonSerializable(typeof(InvokeInterceptorContext))]
[JsonSerializable(typeof(InterceptorPrincipal))]
[JsonSerializable(typeof(ListInterceptorsRequestParams))]
[JsonSerializable(typeof(ListInterceptorsResult))]
[JsonSerializable(typeof(ExecuteChainRequestParams))]
[JsonSerializable(typeof(InterceptorChainResult))]
[JsonSerializable(typeof(InterceptorChainStatus))]
[JsonSerializable(typeof(ChainAbortInfo))]
[JsonSerializable(typeof(ChainValidationSummary))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class InterceptorJsonContext : JsonSerializerContext;
