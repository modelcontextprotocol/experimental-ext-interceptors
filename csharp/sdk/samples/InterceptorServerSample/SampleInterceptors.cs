using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;

/// <summary>
/// Sample interceptors demonstrating validation, mutation, and sink types.
/// </summary>
[McpServerInterceptorType]
public class SampleInterceptors
{
    /// <summary>
    /// Validates that tool call arguments don't contain PII patterns.
    /// </summary>
    [McpServerInterceptor(
        Name = "pii-validator",
        Description = "Checks tool call arguments for PII patterns",
        Type = InterceptorType.Validation,
        Events = [InterceptionEvents.ToolsCall],
        Phase = InterceptorPhase.Request)]
    public static ValidationInterceptorResult ValidatePii(JsonNode payload)
    {
        var json = payload.ToJsonString();

        // Simple pattern check for demonstration
        if (json.Contains("ssn", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("social security", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationInterceptorResult.Failure(
                new ValidationMessage
                {
                    Path = "$.arguments",
                    Message = "Payload may contain Social Security Number data",
                    Severity = ValidationSeverity.Error,
                });
        }

        return ValidationInterceptorResult.Success();
    }

    /// <summary>
    /// Redacts email addresses from tool call payloads.
    /// </summary>
    [McpServerInterceptor(
        Name = "email-redactor",
        Description = "Redacts email addresses from payloads",
        Type = InterceptorType.Mutation,
        Events = [InterceptionEvents.ToolsCall],
        Phase = InterceptorPhase.Request,
        PriorityHint = -1000)]
    public static MutationInterceptorResult RedactEmails(JsonNode payload)
    {
        var json = payload.ToJsonString();
        var redacted = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
            "[EMAIL_REDACTED]");

        if (redacted != json)
        {
            return new MutationInterceptorResult
            {
                Modified = true,
                Payload = JsonNode.Parse(redacted),
            };
        }

        return new MutationInterceptorResult { Modified = false, Payload = payload };
    }

    /// <summary>
    /// Logs all intercepted events.
    /// </summary>
    [McpServerInterceptor(
        Name = "request-logger",
        Description = "Logs all requests",
        Type = InterceptorType.Sink,
        Events = [InterceptionEvents.All])]
    public static SinkInterceptorResult LogRequest(
        JsonNode payload,
        string @event,
        InterceptorPhase phase,
        InvokeInterceptorContext? context)
    {
        Console.Error.WriteLine($"[interceptor] event={@event} phase={phase} traceId={context?.TraceId ?? "none"} payloadSize={payload.ToJsonString().Length}");

        return new SinkInterceptorResult
        {
            Recorded = true,
            Metrics = new Dictionary<string, double>
            {
                ["payloadBytes"] = payload.ToJsonString().Length,
            },
        };
    }
}
