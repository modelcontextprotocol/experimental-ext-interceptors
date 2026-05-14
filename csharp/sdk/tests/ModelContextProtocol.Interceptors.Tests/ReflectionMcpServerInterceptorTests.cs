using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

[McpServerInterceptorType]
public class TestInterceptors
{
    [McpServerInterceptor(Name = "bool-validator", Type = InterceptorType.Validation, Events = ["tools/call"])]
    public static bool ValidateWithBool(JsonNode payload) => payload["valid"]?.GetValue<bool>() ?? false;

    [McpServerInterceptor(Name = "result-validator", Type = InterceptorType.Validation, Events = ["tools/call"])]
    public static ValidationInterceptorResult ValidateWithResult(JsonNode payload, string @event, InterceptorPhase phase)
    {
        return new ValidationInterceptorResult
        {
            Valid = true,
            Messages = [new ValidationMessage { Message = $"Validated {payload} for {@event} in {phase}", Severity = ValidationSeverity.Info }],
        };
    }

    [McpServerInterceptor(Name = "mutator", Type = InterceptorType.Mutation, Events = ["tools/call"], PriorityHint = -100)]
    public static MutationInterceptorResult Mutate(JsonNode payload)
    {
        var obj = payload.AsObject();
        obj["mutated"] = true;
        return new MutationInterceptorResult { Modified = true, Payload = obj };
    }

    [McpServerInterceptor(Name = "sink", Type = InterceptorType.Sink, Events = ["*"])]
    public static SinkInterceptorResult Sink(JsonNode payload, InvokeInterceptorContext? context)
    {
        return new SinkInterceptorResult
        {
            Recorded = true,
            Metrics = new Dictionary<string, double> { ["payloadSize"] = payload.ToJsonString().Length },
        };
    }

    [McpServerInterceptor(Name = "async-validator", Type = InterceptorType.Validation, Events = ["tools/call"])]
    public static async Task<ValidationInterceptorResult> ValidateAsync(JsonNode payload, CancellationToken ct)
    {
        await Task.Delay(1, ct);
        return ValidationInterceptorResult.Success();
    }
}

public class ReflectionMcpServerInterceptorTests
{
    [Fact]
    public void Create_FromMethodWithAttribute_ExtractsMetadata()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.ValidateWithBool))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        Assert.Equal("bool-validator", interceptor.ProtocolInterceptor.Name);
        Assert.Equal(InterceptorType.Validation, interceptor.ProtocolInterceptor.Type);
        Assert.Contains(interceptor.ProtocolInterceptor.Hooks, h => h.Events.Contains("tools/call"));
    }

    [Fact]
    public async Task Invoke_BoolReturn_WrapsAsValidationResult()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.ValidateWithBool))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        var request = new InvokeInterceptorRequestParams
        {
            Name = "bool-validator",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"valid":true}""")!,
        };

        var result = await interceptor.InvokeAsync(request, null!, null, CancellationToken.None);
        var validation = Assert.IsType<ValidationInterceptorResult>(result);
        Assert.True(validation.Valid);
    }

    [Fact]
    public async Task Invoke_BoolReturn_FalseWrapsAsInvalid()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.ValidateWithBool))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        var request = new InvokeInterceptorRequestParams
        {
            Name = "bool-validator",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"valid":false}""")!,
        };

        var result = await interceptor.InvokeAsync(request, null!, null, CancellationToken.None);
        var validation = Assert.IsType<ValidationInterceptorResult>(result);
        Assert.False(validation.Valid);
    }

    [Fact]
    public async Task Invoke_BindsEventAndPhase()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.ValidateWithResult))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        var request = new InvokeInterceptorRequestParams
        {
            Name = "result-validator",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Response,
            Payload = JsonNode.Parse("""{"test":true}""")!,
        };

        var result = await interceptor.InvokeAsync(request, null!, null, CancellationToken.None);
        var validation = Assert.IsType<ValidationInterceptorResult>(result);
        Assert.True(validation.Valid);
        Assert.Single(validation.Messages!);
        Assert.Contains("tools/call", validation.Messages![0].Message);
        Assert.Contains("Response", validation.Messages[0].Message);
    }

    [Fact]
    public async Task Invoke_MutationReturnsModifiedPayload()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.Mutate))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        Assert.Equal("mutator", interceptor.ProtocolInterceptor.Name);
        Assert.Equal(-100, interceptor.ProtocolInterceptor.PriorityHint);

        var request = new InvokeInterceptorRequestParams
        {
            Name = "mutator",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"original":true}""")!,
        };

        var result = await interceptor.InvokeAsync(request, null!, null, CancellationToken.None);
        var mutation = Assert.IsType<MutationInterceptorResult>(result);
        Assert.True(mutation.Modified);
        Assert.True(mutation.Payload!["mutated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Invoke_SinkBindsContext()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.Sink))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        var request = new InvokeInterceptorRequestParams
        {
            Name = "sink",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"data":"test"}""")!,
            Context = new InvokeInterceptorContext { TraceId = "trace-123" },
        };

        var result = await interceptor.InvokeAsync(request, null!, null, CancellationToken.None);
        var sink = Assert.IsType<SinkInterceptorResult>(result);
        Assert.True(sink.Recorded);
        Assert.True(sink.Metrics!["payloadSize"] > 0);
    }

    [Fact]
    public async Task Invoke_AsyncMethod_ReturnsCorrectResult()
    {
        var method = typeof(TestInterceptors).GetMethod(nameof(TestInterceptors.ValidateAsync))!;
        var interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);

        var request = new InvokeInterceptorRequestParams
        {
            Name = "async-validator",
            Event = InterceptionEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await interceptor.InvokeAsync(request, null!, null, CancellationToken.None);
        var validation = Assert.IsType<ValidationInterceptorResult>(result);
        Assert.True(validation.Valid);
    }

    [Fact]
    public void Create_MethodWithoutAttribute_Throws()
    {
        var method = typeof(string).GetMethod(nameof(string.ToString), Type.EmptyTypes)!;
        Assert.Throws<InvalidOperationException>(() => ReflectionMcpServerInterceptor.Create(method, target: null));
    }
}
