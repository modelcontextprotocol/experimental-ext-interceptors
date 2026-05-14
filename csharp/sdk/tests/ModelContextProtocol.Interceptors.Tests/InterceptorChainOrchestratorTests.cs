using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

public class InterceptorChainOrchestratorTests
{
    [Fact]
    public async Task RequestPhase_ExecutesMutationsBeforeValidations()
    {
        var executionOrder = new List<string>();

        var mutation = CreateInterceptor("mut-1", InterceptorType.Mutation, (req, _) =>
        {
            executionOrder.Add("mutation");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult
            {
                Modified = true,
                Payload = JsonNode.Parse("""{"mutated":true}"""),
            });
        });

        var validation = CreateInterceptor("val-1", InterceptorType.Validation, (req, _) =>
        {
            executionOrder.Add("validation");
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        });

        var sink = CreateInterceptor("sink-1", InterceptorType.Sink, (req, _) =>
        {
            executionOrder.Add("sink");
            return new ValueTask<InterceptorResult>(new SinkInterceptorResult { Recorded = true });
        });

        var result = await RunAsync(
            [mutation, validation, sink],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{"original":true}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(["mutation", "validation", "sink"], executionOrder);
        Assert.True(result.FinalPayload!["mutated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ResponsePhase_ExecutesValidationsBeforeMutations()
    {
        var executionOrder = new List<string>();

        var mutation = CreateInterceptor("mut-1", InterceptorType.Mutation, (req, _) =>
        {
            executionOrder.Add("mutation");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        });

        var validation = CreateInterceptor("val-1", InterceptorType.Validation, (req, _) =>
        {
            executionOrder.Add("validation");
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        });

        var sink = CreateInterceptor("sink-1", InterceptorType.Sink, (req, _) =>
        {
            executionOrder.Add("sink");
            return new ValueTask<InterceptorResult>(new SinkInterceptorResult { Recorded = true });
        });

        var result = await RunAsync(
            [mutation, validation, sink],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Response,
                Payload = JsonNode.Parse("""{"test":true}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(["validation", "sink", "mutation"], executionOrder);
    }

    [Fact]
    public async Task MutationsExecuteSequentiallyByPriority()
    {
        var executionOrder = new List<string>();

        var mutHigh = CreateInterceptor("mut-high", InterceptorType.Mutation, (req, _) =>
        {
            executionOrder.Add("high-priority");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        }, priorityHint: 100);

        var mutLow = CreateInterceptor("mut-low", InterceptorType.Mutation, (req, _) =>
        {
            executionOrder.Add("low-priority");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        }, priorityHint: -100);

        var mutDefault = CreateInterceptor("mut-default", InterceptorType.Mutation, (req, _) =>
        {
            executionOrder.Add("default-priority");
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false });
        }, priorityHint: 0);

        var result = await RunAsync(
            [mutHigh, mutLow, mutDefault],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(["low-priority", "default-priority", "high-priority"], executionOrder);
    }

    [Fact]
    public async Task MutationsChainPayloads()
    {
        var mut1 = CreateInterceptor("mut-1", InterceptorType.Mutation, (req, _) =>
        {
            var obj = req.Payload.AsObject();
            obj["step1"] = true;
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = obj });
        }, priorityHint: 0);

        var mut2 = CreateInterceptor("mut-2", InterceptorType.Mutation, (req, _) =>
        {
            Assert.True(req.Payload["step1"]!.GetValue<bool>()); // Verify we got mut1's output
            var obj = req.Payload.AsObject();
            obj["step2"] = true;
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = obj });
        }, priorityHint: 1);

        var result = await RunAsync(
            [mut1, mut2],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{"original":true}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.True(result.FinalPayload!["original"]!.GetValue<bool>());
        Assert.True(result.FinalPayload!["step1"]!.GetValue<bool>());
        Assert.True(result.FinalPayload!["step2"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ValidationErrorAbortsChain()
    {
        var validation = CreateInterceptor("strict-val", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Failure(
                new ValidationMessage { Message = "Required field missing", Severity = ValidationSeverity.Error }));
        });

        var result = await RunAsync(
            [validation],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.ValidationFailed, result.Status);
        Assert.NotNull(result.AbortedAt);
        Assert.Equal("strict-val", result.AbortedAt!.Interceptor);
        Assert.Equal("validation", result.AbortedAt.Type);
    }

    [Fact]
    public async Task AuditMutation_RecordsResultButDoesNotApplyPayload()
    {
        var shadowMutation = CreateInterceptor("shadow", InterceptorType.Mutation, (req, _) =>
        {
            var proposed = JsonNode.Parse("""{"shadowed":true}""")!;
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = proposed });
        }, mode: InterceptorMode.Audit);

        var result = await RunAsync(
            [shadowMutation],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{"original":true}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results);
        Assert.True(result.FinalPayload!["original"]!.GetValue<bool>());
        Assert.Null(result.FinalPayload["shadowed"]);
        var recorded = Assert.IsType<MutationInterceptorResult>(result.Results[0]);
        Assert.True(recorded.Modified);
        Assert.True(recorded.Payload!["shadowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AuditValidation_DoesNotBlockOnError()
    {
        var auditor = CreateInterceptor("auditor", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Failure(
                new ValidationMessage { Message = "Audit-only violation", Severity = ValidationSeverity.Error }));
        }, mode: InterceptorMode.Audit);

        var result = await RunAsync(
            [auditor],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(1, result.ValidationSummary!.Errors);
        Assert.Null(result.AbortedAt);
    }

    [Fact]
    public async Task FailOpenMutation_AllowsChainToContinueOnCrash()
    {
        var crashing = CreateInterceptor("crashing", InterceptorType.Mutation, (req, _) =>
        {
            throw new InvalidOperationException("boom");
        }, failOpen: true);

        var following = CreateInterceptor("following", InterceptorType.Mutation, (req, _) =>
        {
            var payload = req.Payload.AsObject();
            payload["reached"] = true;
            return new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = true, Payload = payload });
        }, priorityHint: 1);

        var result = await RunAsync(
            [crashing, following],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.True(result.FinalPayload!["reached"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FailClosedMutation_HaltsChainOnCrash()
    {
        var crashing = CreateInterceptor("crashing", InterceptorType.Mutation, (req, _) =>
        {
            throw new InvalidOperationException("boom");
        });

        var result = await RunAsync(
            [crashing],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.MutationFailed, result.Status);
        Assert.Equal("crashing", result.AbortedAt!.Interceptor);
    }

    [Fact]
    public async Task FailOpenValidation_AllowsChainToContinueOnCrash()
    {
        var crashing = CreateInterceptor("crashing-validator", InterceptorType.Validation, (req, _) =>
        {
            throw new InvalidOperationException("validator boom");
        }, failOpen: true);

        var result = await RunAsync(
            [crashing],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
    }

    [Fact]
    public async Task FailClosedValidation_HaltsChainOnCrash()
    {
        var crashing = CreateInterceptor("crashing-validator", InterceptorType.Validation, (req, _) =>
        {
            throw new InvalidOperationException("validator boom");
        });

        var result = await RunAsync(
            [crashing],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.ValidationFailed, result.Status);
        Assert.Equal("crashing-validator", result.AbortedAt!.Interceptor);
    }

    [Fact]
    public async Task SinkFailuresAreSwallowed()
    {
        var sink = CreateInterceptor("failing-sink", InterceptorType.Sink, (req, _) =>
        {
            throw new InvalidOperationException("Sink failure");
        });

        var result = await RunAsync(
            [sink],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results);
        var sinkResult = Assert.IsType<SinkInterceptorResult>(result.Results[0]);
        Assert.False(sinkResult.Recorded);
    }

    [Fact]
    public async Task FiltersInterceptorsByEvent()
    {
        var toolsInterceptor = CreateInterceptor("tools-only", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, events: [InterceptionEvents.ToolsCall]);

        var promptsInterceptor = CreateInterceptor("prompts-only", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, events: [InterceptionEvents.PromptsGet]);

        var result = await RunAsync(
            [toolsInterceptor, promptsInterceptor],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results); // Only the tools interceptor ran
    }

    [Fact]
    public async Task FiltersInterceptorsByPhase()
    {
        var requestOnly = CreateInterceptor("request-only", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, phase: InterceptorPhase.Request);

        var responseOnly = CreateInterceptor("response-only", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success());
        }, phase: InterceptorPhase.Response);

        var result = await RunAsync(
            [requestOnly, responseOnly],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Single(result.Results);
        Assert.Equal("request-only", result.Results[0].InterceptorName);
    }

    [Fact]
    public async Task ValidationSummaryCountsCorrectly()
    {
        var val = CreateInterceptor("val", InterceptorType.Validation, (req, _) =>
        {
            return new ValueTask<InterceptorResult>(new ValidationInterceptorResult
            {
                Valid = true,
                Messages =
                [
                    new ValidationMessage { Message = "Info", Severity = ValidationSeverity.Info },
                    new ValidationMessage { Message = "Warn 1", Severity = ValidationSeverity.Warn },
                    new ValidationMessage { Message = "Warn 2", Severity = ValidationSeverity.Warn },
                ],
            });
        });

        var result = await RunAsync(
            [val],
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{}""")!,
            },
            CancellationToken.None);

        Assert.Equal(InterceptorChainStatus.Success, result.Status);
        Assert.Equal(0, result.ValidationSummary!.Errors);
        Assert.Equal(2, result.ValidationSummary.Warnings);
        Assert.Equal(1, result.ValidationSummary.Infos);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ValueTask<InterceptorChainResult> RunAsync(
        IEnumerable<TestEntry> entries,
        ExecuteChainRequestParams chainParams,
        CancellationToken ct)
    {
        var handlerByName = entries.ToDictionary(e => e.Descriptor.Name, e => e.Handler);
        return InterceptorChainOrchestrator.ExecuteAsync(
            entries.Select(e => e.Descriptor),
            (req, c) => handlerByName[req.Name](req, c),
            chainParams,
            ct);
    }

    private static TestEntry CreateInterceptor(
        string name,
        InterceptorType type,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler,
        int priorityHint = 0,
        string[]? events = null,
        InterceptorPhase phase = InterceptorPhase.Both,
        InterceptorMode? mode = null,
        bool? failOpen = null)
    {
        var ev = events ?? [InterceptionEvents.All];
        var hooks = phase switch
        {
            InterceptorPhase.Both =>
            [
                new InterceptorHook { Events = ev.ToList(), Phase = InterceptorPhase.Request },
                new InterceptorHook { Events = ev.ToList(), Phase = InterceptorPhase.Response },
            ],
            _ => new List<InterceptorHook> { new() { Events = ev.ToList(), Phase = phase } },
        };

        var descriptor = new Interceptor
        {
            Name = name,
            Type = type,
            Hooks = hooks,
            Mode = mode,
            FailOpen = failOpen,
            PriorityHint = priorityHint,
        };

        return new TestEntry(descriptor, handler);
    }

    private sealed record TestEntry(
        Interceptor Descriptor,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> Handler);
}
