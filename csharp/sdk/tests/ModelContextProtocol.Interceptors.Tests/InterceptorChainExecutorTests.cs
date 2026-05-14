using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using ModelContextProtocol.Server;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

public class InterceptorChainExecutorTests
{
    [Fact]
    public async Task RequestPhase_ExecutesMutationsBeforeValidations()
    {
        // Track execution order
        var executionOrder = new List<string>();

        var mutation = CreateInterceptor("mut-1", InterceptorType.Mutation, (req, _, _, _) =>
        {
            executionOrder.Add("mutation");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult
            {
                Modified = true,
                Payload = JsonNode.Parse("""{"mutated":true}"""),
            });
        });

        var validation = CreateInterceptor("val-1", InterceptorType.Validation, (req, _, _, _) =>
        {
            executionOrder.Add("validation");
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        });

        var sink = CreateInterceptor("sink-1", InterceptorType.Sink, (req, _, _, _) =>
        {
            executionOrder.Add("sink");
            return new ValueTask<InterceptorResult>(new SinkInterceptorResult { Recorded = true });
        });

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"original":true}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [mutation, validation, sink], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(["mutation", "validation", "sink"], executionOrder);
        Assert.True(result.FinalPayload!["mutated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ResponsePhase_ExecutesValidationsBeforeMutations()
    {
        var executionOrder = new List<string>();

        var mutation = CreateInterceptor("mut-1", InterceptorType.Mutation, (req, _, _, _) =>
        {
            executionOrder.Add("mutation");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        });

        var validation = CreateInterceptor("val-1", InterceptorType.Validation, (req, _, _, _) =>
        {
            executionOrder.Add("validation");
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        });

        var sink = CreateInterceptor("sink-1", InterceptorType.Sink, (req, _, _, _) =>
        {
            executionOrder.Add("sink");
            return new ValueTask<InterceptorResult>(new SinkInterceptorResult { Recorded = true });
        });

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Response,
            Payload = JsonNode.Parse("""{"test":true}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [mutation, validation, sink], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(["validation", "sink", "mutation"], executionOrder);
    }

    [Fact]
    public async Task MutationsExecuteSequentiallyByPriority()
    {
        var executionOrder = new List<string>();

        var mutHigh = CreateInterceptor("mut-high", InterceptorType.Mutation, (req, _, _, _) =>
        {
            executionOrder.Add("high-priority");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        }, priorityHint: 100);

        var mutLow = CreateInterceptor("mut-low", InterceptorType.Mutation, (req, _, _, _) =>
        {
            executionOrder.Add("low-priority");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        }, priorityHint: -100);

        var mutDefault = CreateInterceptor("mut-default", InterceptorType.Mutation, (req, _, _, _) =>
        {
            executionOrder.Add("default-priority");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        }, priorityHint: 0);

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [mutHigh, mutLow, mutDefault], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(["low-priority", "default-priority", "high-priority"], executionOrder);
    }

    [Fact]
    public async Task MutationsChainPayloads()
    {
        var mut1 = CreateInterceptor("mut-1", InterceptorType.Mutation, (req, _, _, _) =>
        {
            var payload = req.Payload;
            var obj = payload.AsObject();
            obj["step1"] = true;
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = obj });
        }, priorityHint: 0);

        var mut2 = CreateInterceptor("mut-2", InterceptorType.Mutation, (req, _, _, _) =>
        {
            var payload = req.Payload;
            Assert.True(payload["step1"]!.GetValue<bool>()); // Verify we got mut1's output
            var obj = payload.AsObject();
            obj["step2"] = true;
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = obj });
        }, priorityHint: 1);

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{"original":true}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [mut1, mut2], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.True(result.FinalPayload!["original"]!.GetValue<bool>());
        Assert.True(result.FinalPayload!["step1"]!.GetValue<bool>());
        Assert.True(result.FinalPayload!["step2"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ValidationErrorAbortsChain()
    {
        var validation = CreateInterceptor("strict-val", InterceptorType.Validation, (req, _, _, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Failure(
                new ValidationMessage { Message = "Required field missing", Severity = ValidationSeverity.Error }));
        });

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [validation], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.ValidationFailed, result.Status);
        Assert.NotNull(result.AbortedAt);
        Assert.Equal("strict-val", result.AbortedAt!.Interceptor);
        Assert.Equal("validation", result.AbortedAt.Type);
    }

    [Fact]
    public async Task SinkFailuresAreSwallowed()
    {
        var sink = CreateInterceptor("failing-sink", InterceptorType.Sink, (req, _, _, _) =>
        {
            throw new InvalidOperationException("Sink failure");
        });

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [sink], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results);
        var sinkResult = Assert.IsType<SinkInterceptorResult>(result.Results[0]);
        Assert.False(sinkResult.Recorded);
    }

    [Fact]
    public async Task FiltersInterceptorsByEvent()
    {
        var toolsInterceptor = CreateInterceptor("tools-only", InterceptorType.Validation, (req, _, _, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, events: [InterceptorEvents.ToolsCall]);

        var promptsInterceptor = CreateInterceptor("prompts-only", InterceptorType.Validation, (req, _, _, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, events: [InterceptorEvents.PromptsGet]);

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [toolsInterceptor, promptsInterceptor], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results); // Only the tools interceptor ran
    }

    [Fact]
    public async Task FiltersInterceptorsByPhase()
    {
        var requestOnly = CreateInterceptor("request-only", InterceptorType.Validation, (req, _, _, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, phase: InterceptorPhase.Request);

        var responseOnly = CreateInterceptor("response-only", InterceptorType.Validation, (req, _, _, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, phase: InterceptorPhase.Response);

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [requestOnly, responseOnly], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results);
        Assert.Equal("request-only", result.Results[0].InterceptorName);
    }

    [Fact]
    public async Task ValidationSummaryCountsCorrectly()
    {
        var val = CreateInterceptor("val", InterceptorType.Validation, (req, _, _, _) =>
        {
            return new ValueTask<InterceptorResult>(new ValidationInterceptorResult
            {
                Valid = true, // Still valid overall
                Messages =
                [
                    new ValidationMessage { Message = "Info", Severity = ValidationSeverity.Info },
                    new ValidationMessage { Message = "Warn 1", Severity = ValidationSeverity.Warn },
                    new ValidationMessage { Message = "Warn 2", Severity = ValidationSeverity.Warn },
                ],
            });
        });

        var chainParams = new ExecuteChainRequestParams
        {
            Event = InterceptorEvents.ToolsCall,
            Phase = InterceptorPhase.Request,
            Payload = JsonNode.Parse("""{}""")!,
        };

        var result = await InterceptorChainExecutor.ExecuteAsync(
            [val], chainParams, null!, null, CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(0, result.ValidationSummary!.Errors);
        Assert.Equal(2, result.ValidationSummary.Warnings);
        Assert.Equal(1, result.ValidationSummary.Infos);
    }

    private static TestInterceptor CreateInterceptor(
        string name,
        InterceptorType type,
        Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> handler,
        int priorityHint = 0,
        string[]? events = null,
        InterceptorPhase phase = InterceptorPhase.Both)
    {
        return new TestInterceptor(
            new Interceptor
            {
                Name = name,
                Type = type,
                Phase = phase,
                Events = events ?? [InterceptorEvents.All],
                PriorityHint = priorityHint,
            },
            handler);
    }

    private sealed class TestInterceptor : McpServerInterceptor
    {
        private readonly Interceptor _interceptor;
        private readonly Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> _handler;

        public TestInterceptor(
            Interceptor interceptor,
            Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> handler)
        {
            _interceptor = interceptor;
            _handler = handler;
        }

        public override Interceptor ProtocolInterceptor => _interceptor;
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<InterceptorResult> InvokeAsync(
            InvokeInterceptorRequestParams request,
            McpServer server,
            IServiceProvider? services,
            CancellationToken cancellationToken = default) =>
            _handler(request, server, services, cancellationToken);
    }
}
