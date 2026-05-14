using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors;
using ModelContextProtocol.Interceptors.Protocol;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

public class ProtocolTypesSerializationTests
{
    private static readonly JsonSerializerOptions Options = InterceptorJsonUtilities.DefaultOptions;

    [Fact]
    public void InterceptorType_SerializesAsString()
    {
        Assert.Equal("\"validation\"", JsonSerializer.Serialize(InterceptorType.Validation, Options));
        Assert.Equal("\"mutation\"", JsonSerializer.Serialize(InterceptorType.Mutation, Options));
        Assert.Equal("\"sink\"", JsonSerializer.Serialize(InterceptorType.Sink, Options));
    }

    [Fact]
    public void InterceptorType_DeserializesFromString()
    {
        Assert.Equal(InterceptorType.Validation, JsonSerializer.Deserialize<InterceptorType>("\"validation\"", Options));
        Assert.Equal(InterceptorType.Mutation, JsonSerializer.Deserialize<InterceptorType>("\"mutation\"", Options));
        Assert.Equal(InterceptorType.Sink, JsonSerializer.Deserialize<InterceptorType>("\"sink\"", Options));
    }

    [Fact]
    public void InterceptorPhase_SerializesAsString()
    {
        Assert.Equal("\"request\"", JsonSerializer.Serialize(InterceptorPhase.Request, Options));
        Assert.Equal("\"response\"", JsonSerializer.Serialize(InterceptorPhase.Response, Options));
        Assert.Equal("\"both\"", JsonSerializer.Serialize(InterceptorPhase.Both, Options));
    }

    [Fact]
    public void ValidationSeverity_SerializesAsString()
    {
        Assert.Equal("\"error\"", JsonSerializer.Serialize(ValidationSeverity.Error, Options));
        Assert.Equal("\"warn\"", JsonSerializer.Serialize(ValidationSeverity.Warn, Options));
        Assert.Equal("\"info\"", JsonSerializer.Serialize(ValidationSeverity.Info, Options));
    }

    [Fact]
    public void InterceptorChainStatus_SerializesAsString()
    {
        Assert.Equal("\"success\"", JsonSerializer.Serialize(InterceptorChainStatus.Success, Options));
        Assert.Equal("\"validation_failed\"", JsonSerializer.Serialize(InterceptorChainStatus.ValidationFailed, Options));
        Assert.Equal("\"mutation_failed\"", JsonSerializer.Serialize(InterceptorChainStatus.MutationFailed, Options));
        Assert.Equal("\"timeout\"", JsonSerializer.Serialize(InterceptorChainStatus.Timeout, Options));
    }

    [Fact]
    public void Interceptor_RoundTrips()
    {
        var interceptor = new Interceptor
        {
            Name = "pii-validator",
            Version = "1.0.0",
            Description = "Validates PII in payloads",
            Type = InterceptorType.Validation,
            Hooks =
            [
                new InterceptorHook { Events = [InterceptionEvents.ToolsCall, InterceptionEvents.PromptsGet], Phase = InterceptorPhase.Request },
                new InterceptorHook { Events = [InterceptionEvents.ToolsCall, InterceptionEvents.PromptsGet], Phase = InterceptorPhase.Response },
            ],
            PriorityHint = -1000,
            Compat = new InterceptorCompatibility { MinProtocol = "2024-11-05" },
        };

        var json = JsonSerializer.Serialize(interceptor, Options);
        var deserialized = JsonSerializer.Deserialize<Interceptor>(json, Options)!;

        Assert.Equal("pii-validator", deserialized.Name);
        Assert.Equal("1.0.0", deserialized.Version);
        Assert.Equal("Validates PII in payloads", deserialized.Description);
        Assert.Equal(2, deserialized.Hooks.Count);
        Assert.Equal(2, deserialized.Hooks[0].Events.Count);
        Assert.Equal(InterceptorPhase.Request, deserialized.Hooks[0].Phase);
        Assert.Equal(InterceptorPhase.Response, deserialized.Hooks[1].Phase);
        Assert.Equal(InterceptorType.Validation, deserialized.Type);
        Assert.Equal(-1000, deserialized.PriorityHint);
        Assert.NotNull(deserialized.Compat);
        Assert.Equal("2024-11-05", deserialized.Compat.MinProtocol);
    }

    [Fact]
    public void Interceptor_OmitsNullFields()
    {
        var interceptor = new Interceptor
        {
            Name = "test",
            Type = InterceptorType.Sink,
            Hooks = [new InterceptorHook { Events = [InterceptionEvents.All], Phase = InterceptorPhase.Request }],
        };

        var json = JsonSerializer.Serialize(interceptor, Options);
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("version", out _));
        Assert.False(doc.RootElement.TryGetProperty("description", out _));
        Assert.False(doc.RootElement.TryGetProperty("priorityHint", out _));
        Assert.False(doc.RootElement.TryGetProperty("compat", out _));
        Assert.False(doc.RootElement.TryGetProperty("configSchema", out _));
        Assert.False(doc.RootElement.TryGetProperty("_meta", out _));
        Assert.False(doc.RootElement.TryGetProperty("mode", out _));
        Assert.False(doc.RootElement.TryGetProperty("failOpen", out _));
    }

    [Fact]
    public void InterceptorMode_SerializesAsString()
    {
        Assert.Equal("\"active\"", JsonSerializer.Serialize(InterceptorMode.Active, Options));
        Assert.Equal("\"audit\"", JsonSerializer.Serialize(InterceptorMode.Audit, Options));
    }

    [Fact]
    public void Interceptor_RoundTripsModeAndFailOpen()
    {
        var interceptor = new Interceptor
        {
            Name = "audit-validator",
            Type = InterceptorType.Validation,
            Hooks = [new InterceptorHook { Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Request }],
            Mode = InterceptorMode.Audit,
            FailOpen = true,
        };

        var json = JsonSerializer.Serialize(interceptor, Options);
        Assert.Contains("\"mode\":\"audit\"", json);
        Assert.Contains("\"failOpen\":true", json);

        var deserialized = JsonSerializer.Deserialize<Interceptor>(json, Options)!;
        Assert.Equal(InterceptorMode.Audit, deserialized.Mode);
        Assert.True(deserialized.FailOpen);
    }

    [Fact]
    public void ValidationInterceptorResult_RoundTrips()
    {
        var result = new ValidationInterceptorResult
        {
            InterceptorName = "pii-validator",
            Phase = InterceptorPhase.Request,
            Valid = false,
            Severity = ValidationSeverity.Error,
            DurationMs = 42,
            Messages =
            [
                new ValidationMessage { Path = "$.arguments.email", Message = "Contains PII", Severity = ValidationSeverity.Error },
            ],
            Suggestions =
            [
                new ValidationSuggestion { Path = "$.arguments.email", Value = JsonNode.Parse("\"[REDACTED]\"") },
            ],
        };

        var json = JsonSerializer.Serialize<InterceptorResult>(result, Options);
        Assert.Contains("\"type\":\"validation\"", json);

        var deserialized = JsonSerializer.Deserialize<InterceptorResult>(json, Options);
        var validation = Assert.IsType<ValidationInterceptorResult>(deserialized);

        Assert.Equal("pii-validator", validation.InterceptorName);
        Assert.False(validation.Valid);
        Assert.Equal(ValidationSeverity.Error, validation.Severity);
        Assert.Single(validation.Messages!);
        Assert.Equal("$.arguments.email", validation.Messages![0].Path);
        Assert.Single(validation.Suggestions!);
    }

    [Fact]
    public void MutationInterceptorResult_RoundTrips()
    {
        var result = new MutationInterceptorResult
        {
            InterceptorName = "pii-redactor",
            Phase = InterceptorPhase.Request,
            Modified = true,
            Payload = JsonNode.Parse("""{"email":"[REDACTED]"}"""),
        };

        var json = JsonSerializer.Serialize<InterceptorResult>(result, Options);
        Assert.Contains("\"type\":\"mutation\"", json);

        var deserialized = JsonSerializer.Deserialize<InterceptorResult>(json, Options);
        var mutation = Assert.IsType<MutationInterceptorResult>(deserialized);

        Assert.True(mutation.Modified);
        Assert.NotNull(mutation.Payload);
        Assert.Equal("[REDACTED]", mutation.Payload!["email"]!.GetValue<string>());
    }

    [Fact]
    public void SinkInterceptorResult_RoundTrips()
    {
        var result = new SinkInterceptorResult
        {
            InterceptorName = "logger",
            Phase = InterceptorPhase.Request,
            Recorded = true,
            Metrics = new Dictionary<string, double> { ["latencyMs"] = 12.5, ["payloadBytes"] = 256 },
        };

        var json = JsonSerializer.Serialize<InterceptorResult>(result, Options);
        Assert.Contains("\"type\":\"sink\"", json);

        var deserialized = JsonSerializer.Deserialize<InterceptorResult>(json, Options);
        var sink = Assert.IsType<SinkInterceptorResult>(deserialized);

        Assert.True(sink.Recorded);
        Assert.Equal(2, sink.Metrics!.Count);
        Assert.Equal(12.5, sink.Metrics["latencyMs"]);
    }

    [Fact]
    public void ListInterceptorsResult_RoundTrips()
    {
        var result = new ListInterceptorsResult
        {
            Interceptors =
            [
                new Interceptor
                {
                    Name = "a",
                    Type = InterceptorType.Validation,
                    Hooks = [new InterceptorHook { Events = ["tools/call"], Phase = InterceptorPhase.Request }],
                },
                new Interceptor
                {
                    Name = "b",
                    Type = InterceptorType.Sink,
                    Hooks =
                    [
                        new InterceptorHook { Events = ["*"], Phase = InterceptorPhase.Request },
                        new InterceptorHook { Events = ["*"], Phase = InterceptorPhase.Response },
                    ],
                },
            ],
            NextCursor = "abc123",
        };

        var json = JsonSerializer.Serialize(result, Options);
        var deserialized = JsonSerializer.Deserialize<ListInterceptorsResult>(json, Options)!;

        Assert.Equal(2, deserialized.Interceptors.Count);
        Assert.Equal("abc123", deserialized.NextCursor);
    }

    [Fact]
    public void InvokeInterceptorRequestParams_RoundTrips()
    {
        var request = new InvokeInterceptorRequestParams
        {
            Name = "pii-validator",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"name":"call-tool","arguments":{"query":"test"}}""")!,
            TimeoutMs = 5000,
            Context = new InvokeInterceptorContext
            {
                Principal = new InterceptorPrincipal { Type = "user", Id = "user-123" },
                TraceId = "trace-abc",
                Timestamp = "2025-01-01T00:00:00Z",
                SessionId = "session-xyz",
            },
        };

        var json = JsonSerializer.Serialize(request, Options);
        var deserialized = JsonSerializer.Deserialize<InvokeInterceptorRequestParams>(json, Options)!;

        Assert.Equal("pii-validator", deserialized.Name);
        Assert.Equal(InterceptionEvents.ToolsCall, deserialized.Event);
        Assert.Equal(InterceptorPhase.Request, deserialized.Phase);
        Assert.Equal(5000, deserialized.TimeoutMs);
        Assert.NotNull(deserialized.Context);
        Assert.Equal("user", deserialized.Context!.Principal!.Type);
        Assert.Equal("user-123", deserialized.Context.Principal.Id);
    }

    [Fact]
    public void ExecuteChainRequestParams_RoundTrips()
    {
        var request = new ExecuteChainRequestParams
        {
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"test":true}""")!,
            InterceptorNames = ["pii-validator", "content-filter"],
            TimeoutMs = 10000,
        };

        var json = JsonSerializer.Serialize(request, Options);
        var deserialized = JsonSerializer.Deserialize<ExecuteChainRequestParams>(json, Options)!;

        Assert.Equal(InterceptionEvents.ToolsCall, deserialized.Event);
        Assert.Equal(2, deserialized.InterceptorNames!.Count);
        Assert.Equal(10000, deserialized.TimeoutMs);
    }

    [Fact]
    public void InterceptorChainResult_RoundTrips()
    {
        var chainResult = new InterceptorChainResult
        {
            Status = InterceptorChainStatus.Success,
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Results =
            [
                new MutationInterceptorResult
                {
                    InterceptorName = "redactor",
                    Phase = InterceptorPhase.Request,
                    Modified = true,
                    Payload = JsonNode.Parse("""{"redacted":true}"""),
                },
                new ValidationInterceptorResult
                {
                    InterceptorName = "validator",
                    Phase = InterceptorPhase.Request,
                    Valid = true,
                },
            ],
            FinalPayload = JsonNode.Parse("""{"redacted":true}"""),
            ValidationSummary = new ChainValidationSummary { Errors = 0, Warnings = 1, Infos = 2 },
            TotalDurationMs = 150,
        };

        var json = JsonSerializer.Serialize(chainResult, Options);
        var deserialized = JsonSerializer.Deserialize<InterceptorChainResult>(json, Options)!;

        Assert.Equal(InterceptorChainStatus.Success, deserialized.Status);
        Assert.Equal(2, deserialized.Results.Count);
        Assert.IsType<MutationInterceptorResult>(deserialized.Results[0]);
        Assert.IsType<ValidationInterceptorResult>(deserialized.Results[1]);
        Assert.Equal(150, deserialized.TotalDurationMs);
        Assert.NotNull(deserialized.ValidationSummary);
        Assert.Equal(1, deserialized.ValidationSummary!.Warnings);
    }

    [Fact]
    public void InterceptorChainResult_WithAbort_RoundTrips()
    {
        var chainResult = new InterceptorChainResult
        {
            Status = InterceptorChainStatus.ValidationFailed,
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Results = [],
            TotalDurationMs = 50,
            AbortedAt = new ChainAbortInfo
            {
                Interceptor = "strict-validator",
                Reason = "Required field missing",
                Type = "validation",
            },
        };

        var json = JsonSerializer.Serialize(chainResult, Options);
        var deserialized = JsonSerializer.Deserialize<InterceptorChainResult>(json, Options)!;

        Assert.Equal(InterceptorChainStatus.ValidationFailed, deserialized.Status);
        Assert.NotNull(deserialized.AbortedAt);
        Assert.Equal("strict-validator", deserialized.AbortedAt!.Interceptor);
    }

    [Fact]
    public void InterceptorsCapability_RoundTrips()
    {
        var capability = new InterceptorsCapability
        {
            SupportedEvents = [InterceptionEvents.ToolsCall, InterceptionEvents.ToolsList, InterceptionEvents.PromptsGet],
        };

        var json = JsonSerializer.Serialize(capability, Options);
        var deserialized = JsonSerializer.Deserialize<InterceptorsCapability>(json, Options)!;

        Assert.Equal(3, deserialized.SupportedEvents.Count);
        Assert.Contains(InterceptionEvents.ToolsCall, deserialized.SupportedEvents);
    }
}
