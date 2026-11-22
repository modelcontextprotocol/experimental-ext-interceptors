using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

/// <summary>
/// SEP-2624 execution semantics that depend on invoker policy: overrides, direction, timeouts,
/// fail-open routing, atomic mutations, the validation verdict, and background sinks.
/// </summary>
public class ExecutionPolicyTests
{
    private static readonly JsonNode Payload = JsonNode.Parse("""{"name":"fetch","arguments":{"url":"https://evil.example"}}""")!;

    // ── Overrides: capability vs policy ─────────────────────────────────────

    [Fact]
    public async Task AuditOverride_TurnsBlockingValidatorIntoWouldBlock()
    {
        var guard = Validator("guard", Fail("not allowed"));

        var result = await RunAsync([guard with { Overrides = new() { Mode = InterceptorMode.Audit } }]);

        Assert.Equal(InterceptorExecutionStatus.Success, result.Status);
        var invocation = Assert.Single(result.Invocations);
        Assert.Equal(InterceptorEffect.WouldBlock, invocation.Effect);
        Assert.Equal(InterceptorMode.Audit, invocation.Mode);
        Assert.Equal(InterceptorPolicySource.Override, invocation.ModeSource);
        Assert.Equal(InterceptorPolicySource.Default, invocation.FailOpenSource);
    }

    [Fact]
    public async Task ActiveOverride_PromotesDeclaredAuditValidator()
    {
        var guard = Validator("guard", Fail("not allowed"), mode: InterceptorMode.Audit);

        var declared = await RunAsync([guard]);
        var promoted = await RunAsync([guard with { Overrides = new() { Mode = InterceptorMode.Active } }]);

        Assert.Equal(InterceptorExecutionStatus.Success, declared.Status);
        Assert.Equal(InterceptorPolicySource.Declared, declared.Invocations[0].ModeSource);
        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, promoted.Status);
        Assert.Equal(InterceptorEffect.Blocked, promoted.Invocations[0].Effect);
    }

    [Fact]
    public async Task FailOpenOverride_LetsCrashingValidatorPass()
    {
        var fragile = Validator("fragile", (_, _) => throw new InvalidOperationException("boom"));

        var closed = await RunAsync([fragile]);
        var open = await RunAsync([fragile with { Overrides = new() { FailOpen = true } }]);

        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, closed.Status);
        Assert.Contains("boom", closed.ValidationErrors[0].Message);
        Assert.Equal(InterceptorExecutionStatus.Success, open.Status);
        Assert.Equal(InterceptorEffect.FailedOpen, open.Invocations[0].Effect);
        Assert.Equal(InterceptorPolicySource.Override, open.Invocations[0].FailOpenSource);
    }

    [Fact]
    public async Task PriorityOverride_ReordersMutations()
    {
        var order = new List<string>();
        var a = Mutator("a", Record(order, "a"), priority: -10);
        var b = Mutator("b", Record(order, "b"), priority: 10);

        await RunAsync([a with { Overrides = new() { PriorityHint = 100 } }, b]);

        Assert.Equal(["b", "a"], order);
    }

    [Fact]
    public async Task HookOverride_NarrowsEvents()
    {
        var calls = 0;
        var redactor = Mutator("redactor", (_, _) => { calls++; return Unchanged(); }, events: [InterceptionEvents.ToolsCall, InterceptionEvents.PromptsGet]);
        var narrowed = redactor with
        {
            Overrides = new() { Hooks = [new InterceptorHook { Events = [InterceptionEvents.PromptsGet], Phase = InterceptorPhase.Request }] },
        };

        await RunAsync([narrowed], eventName: InterceptionEvents.ToolsCall);
        await RunAsync([narrowed], eventName: InterceptionEvents.PromptsGet);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HookOverride_ThatWidens_IsRejected()
    {
        var redactor = Mutator("redactor", (_, _) => Unchanged(), events: [InterceptionEvents.ToolsCall]);
        var widened = redactor with
        {
            Overrides = new() { Hooks = [new InterceptorHook { Events = [InterceptionEvents.All], Phase = InterceptorPhase.Request }] },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync([widened]).AsTask());
        Assert.Contains("widen", ex.Message);
    }

    // ── Direction ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(InterceptorDirection.Sending, new[] { "mutation", "validation" })]
    [InlineData(InterceptorDirection.Receiving, new[] { "validation", "mutation" })]
    public async Task Direction_NotPhase_DecidesOrder(InterceptorDirection direction, string[] expected)
    {
        var order = new List<string>();
        var entries = new[]
        {
            Mutator("m", Record(order, "mutation")),
            Validator("v", (_, _) => { order.Add("validation"); return Pass(); }),
        };

        // Same phase, opposite directions: a server-side invoker receives requests.
        var result = await RunAsync(entries, direction: direction);

        Assert.Equal(direction, result.Direction);
        Assert.Equal(expected, order);
    }

    [Fact]
    public void InvokerRole_MapsPhaseToDirection()
    {
        Assert.Equal(InterceptorDirection.Sending, InterceptorInvokerRole.Client.GetDirection(InterceptorPhase.Request));
        Assert.Equal(InterceptorDirection.Receiving, InterceptorInvokerRole.Client.GetDirection(InterceptorPhase.Response));
        Assert.Equal(InterceptorDirection.Receiving, InterceptorInvokerRole.Server.GetDirection(InterceptorPhase.Request));
        Assert.Equal(InterceptorDirection.Sending, InterceptorInvokerRole.Server.GetDirection(InterceptorPhase.Response));
    }

    // ── Timeouts ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PerInterceptorTimeout_IsRoutedByFailOpen()
    {
        var slow = Validator("slow", async (_, ct) => { await Task.Delay(5_000, ct); return ValidationInterceptorResult.Success(); });

        var closed = await RunAsync([slow with { Overrides = new() { TimeoutMs = 30 } }]);
        var open = await RunAsync([slow with { Overrides = new() { TimeoutMs = 30, FailOpen = true } }]);

        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, closed.Status);
        Assert.Equal(InterceptorInvocationOutcome.TimedOut, closed.Invocations[0].Outcome);
        Assert.Equal(InterceptorExecutionStatus.Success, open.Status);
        Assert.Equal(InterceptorInvocationOutcome.TimedOut, open.Invocations[0].Outcome);
        Assert.Equal(InterceptorEffect.FailedOpen, open.Invocations[0].Effect);
    }

    [Fact]
    public async Task AggregateTimeout_FailsOperationEvenWhenFailOpen()
    {
        var slow = Mutator("slow", async (_, ct) => { await Task.Delay(5_000, ct); return new MutationInterceptorResult(); }, failOpen: true);

        var result = await RunAsync([slow], aggregateTimeoutMs: 30);

        Assert.Equal(InterceptorExecutionStatus.Timeout, result.Status);
        Assert.Equal("timeout", result.AbortedAt!.Type);
    }

    [Fact]
    public async Task InvokeParams_CarryPerInterceptorTimeoutAndConfig_NotAggregateTimeout()
    {
        InvokeInterceptorRequestParams? seen = null;
        var guard = Validator("guard", (req, _) => { seen = req; return Pass(); }) with
        {
            Overrides = new() { TimeoutMs = 1234 },
            Config = JsonNode.Parse("""{"allowedDomains":["acme.com"]}"""),
        };

        await RunAsync([guard], aggregateTimeoutMs: 9999);

        Assert.Equal(1234, seen!.TimeoutMs);
        Assert.Equal("acme.com", seen.Config!["allowedDomains"]![0]!.GetValue<string>());
    }

    // ── Mutations ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mutations_AreAtomic_WhenALaterOneFails()
    {
        var first = Mutator("first", (req, _) => Modified(new JsonObject { ["step"] = "first" }), priority: 1);
        var second = Mutator("second", (_, _) => throw new InvalidOperationException("redaction failed"), priority: 2);

        var result = await RunAsync([first, second]);

        Assert.Equal(InterceptorExecutionStatus.MutationFailed, result.Status);
        Assert.Null(result.FinalPayload); // nothing is forwarded, and nothing pre-redaction is serialized
        Assert.True(JsonNode.DeepEquals(Payload, result.OriginalPayload));
        Assert.DoesNotContain("finalPayload", JsonSerializer.Serialize(result, InterceptorJsonUtilities.DefaultOptions));
        Assert.Equal("second", result.AbortedAt!.Interceptor);
    }

    [Fact]
    public async Task Mutator_ReturningWrongResultType_FailsClosed()
    {
        var confused = Mutator("confused", (_, _) => Pass());

        var result = await RunAsync([confused]);

        Assert.Equal(InterceptorExecutionStatus.MutationFailed, result.Status);
        Assert.Contains("declared as a mutation", result.AbortedAt!.Reason);
    }

    [Fact]
    public async Task AuditMutator_IsComputedButNotApplied()
    {
        var shadow = Mutator("shadow", (_, _) => Modified(new JsonObject { ["rewritten"] = true }), mode: InterceptorMode.Audit);

        var result = await RunAsync([shadow]);

        Assert.Equal(InterceptorExecutionStatus.Success, result.Status);
        Assert.True(JsonNode.DeepEquals(Payload, result.FinalPayload));
        Assert.Equal(InterceptorEffect.ShadowModified, result.Invocations[0].Effect);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_ReportsEveryBlockingError()
    {
        var a = Validator("a", Fail("first reason"));
        var b = Validator("b", Fail("second reason"));
        var c = Validator("c", (_, _) => Pass());

        var result = await RunAsync([a, b, c]);

        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, result.Status);
        Assert.Equal(["a", "b"], result.ValidationErrors.Select(e => e.Interceptor));
        Assert.Equal(2, result.ValidationSummary!.Errors);
        Assert.Equal(3, result.Invocations.Count);
    }

    public static TheoryData<string, bool> VerdictCases => new()
    {
        // experimental-ext-interceptors#51: the fail-secure reading.
        { """{"type":"validation","valid":false,"severity":"error"}""", true },
        { """{"type":"validation","valid":false,"messages":[{"message":"m","severity":"error"}]}""", true },
        { """{"type":"validation","valid":false,"severity":"warn","messages":[{"message":"m","severity":"error"}]}""", false },
        { """{"type":"validation","valid":false}""", true },
        { """{"type":"validation","valid":true,"severity":"error"}""", false },
        { """{"type":"validation","valid":false,"messages":[{"message":"m","severity":"info"}]}""", false },
        // A message without a severity counts as error in an invalid result (review finding: it read as info).
        { """{"type":"validation","valid":false,"messages":[{"message":"SQL injection detected"}]}""", true },
        { """{"type":"validation","valid":true,"messages":[{"message":"looks fine"}]}""", false },
    };

    [Fact]
    public async Task ValidationVerdict_CSharpMessageWithoutSeverity_Blocks()
    {
        var validator = Validator("pii", (_, _) => new ValueTask<InterceptorResult>(
            new ValidationInterceptorResult { Valid = false, Messages = [new ValidationMessage { Message = "PII found" }] }));

        var result = await RunAsync([validator]);

        Assert.Equal(InterceptorExecutionStatus.ValidationFailed, result.Status);
        Assert.Equal(ValidationSeverity.Error, Assert.Single(result.ValidationErrors).Severity);
    }

    [Fact]
    public async Task AggregateTimeout_DoesNotFailOperation_ForAuditModeInterceptor()
    {
        var slowAudit = Validator("audit-logger", async (_, ct) =>
        {
            await Task.Delay(2_000, ct);
            return ValidationInterceptorResult.Success();
        }, mode: InterceptorMode.Audit);

        var result = await RunAsync([slowAudit], aggregateTimeoutMs: 100);

        Assert.Equal(InterceptorExecutionStatus.Success, result.Status);
        var invocation = Assert.Single(result.Invocations);
        Assert.Equal(InterceptorInvocationOutcome.TimedOut, invocation.Outcome);
        Assert.Equal(InterceptorEffect.None, invocation.Effect);
    }

    [Fact]
    public async Task NonPositiveTimeouts_AreRejected()
    {
        var guard = Validator("guard", (_, _) => Pass());

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync([guard with { Overrides = new() { TimeoutMs = -5 } }]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync([guard], aggregateTimeoutMs: 0).AsTask());
        Assert.Throws<InvalidOperationException>(() => new InterceptorPolicy { DefaultTimeoutMs = -1 }.Bind([]));
    }

    [Theory]
    [MemberData(nameof(VerdictCases))]
    public async Task ValidationVerdict_FollowsFailSecureRule(string resultJson, bool blocks)
    {
        var result = JsonSerializer.Deserialize<InterceptorResult>(resultJson, InterceptorJsonUtilities.DefaultOptions)!;
        var validator = Validator("v", (_, _) => new ValueTask<InterceptorResult>(result));

        var execution = await RunAsync([validator]);

        Assert.Equal(blocks, execution.Status == InterceptorExecutionStatus.ValidationFailed);
    }

    // ── Sinks ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sinks_NeverDelayTheOperation()
    {
        var sink = Entry("slow-sink", InterceptorType.Sink, async (_, ct) =>
        {
            await Task.Delay(3_000, ct);
            return new SinkInterceptorResult { Recorded = true };
        });

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync([sink]);
        stopwatch.Stop();

        Assert.Equal(InterceptorExecutionStatus.Success, result.Status);
        Assert.Equal(InterceptorInvocationOutcome.Dispatched, result.Invocations[0].Outcome);
        Assert.True(stopwatch.ElapsedMilliseconds < 1_000, $"Execution waited {stopwatch.ElapsedMilliseconds}ms for a sink.");
    }

    [Fact]
    public async Task Sinks_ObserveASnapshot_UnaffectedByLaterMutations()
    {
        var observed = new TaskCompletionSource<string>();
        var sink = Entry("sink", InterceptorType.Sink, (req, _) =>
        {
            observed.TrySetResult(req.Payload.ToJsonString());
            return new ValueTask<InterceptorResult>(new SinkInterceptorResult { Recorded = true });
        });
        var mutator = Mutator("m", (req, _) =>
        {
            req.Payload["arguments"]!["url"] = "rewritten"; // in-place edit by an in-process mutator
            return Modified(req.Payload);
        });

        // Receiving: validate → sinks → mutate.
        await RunAsync([sink, mutator], direction: InterceptorDirection.Receiving);

        Assert.Contains("evil.example", await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ── Policy binding ──────────────────────────────────────────────────────

    [Fact]
    public void Policy_Include_IsFailClosed()
    {
        var policy = new InterceptorPolicy { Include = new HashSet<string> { "exfiltration-guard" } };

        var ex = Assert.Throws<InvalidOperationException>(() => policy.Bind([]));
        Assert.Contains("exfiltration-guard", ex.Message);
    }

    [Fact]
    public void Policy_DefaultTimeout_FillsUnsetOverrides()
    {
        var policy = new InterceptorPolicy
        {
            DefaultTimeoutMs = 500,
            Overrides = { ["explicit"] = new() { TimeoutMs = 50, Mode = InterceptorMode.Audit } },
        };
        var discovered = new[] { Descriptor("explicit"), Descriptor("implicit") }.Select(d => (d, (ModelContextProtocol.Client.McpClient)null!));

        var bindings = policy.Bind(discovered);

        Assert.Equal(50, bindings.Single(b => b.Interceptor.Name == "explicit").Overrides!.TimeoutMs);
        Assert.Equal(InterceptorMode.Audit, bindings.Single(b => b.Interceptor.Name == "explicit").Overrides!.Mode);
        Assert.Equal(500, bindings.Single(b => b.Interceptor.Name == "implicit").Overrides!.TimeoutMs);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ValueTask<InterceptorExecutionResult> RunAsync(
        IEnumerable<InterceptorExecutionEngine.ExecutionEntry> entries,
        string eventName = InterceptionEvents.ToolsCall,
        InterceptorDirection? direction = null,
        int? aggregateTimeoutMs = null) =>
        InterceptorExecutionEngine.ExecuteAsync(
            entries.ToList(),
            new InterceptorExecutionRequest
            {
                Event = eventName,
                Phase = InterceptorPhase.Request,
                Direction = direction,
                Payload = Payload.DeepClone(),
                TimeoutMs = aggregateTimeoutMs,
            },
            CancellationToken.None);

    private static Interceptor Descriptor(
        string name,
        InterceptorType type = InterceptorType.Validation,
        string[]? events = null,
        InterceptorMode? mode = null,
        bool? failOpen = null,
        int? priority = null) => new()
        {
            Name = name,
            Type = type,
            Hooks =
            [
                new InterceptorHook { Events = [.. events ?? [InterceptionEvents.All]], Phase = InterceptorPhase.Request },
                new InterceptorHook { Events = [.. events ?? [InterceptionEvents.All]], Phase = InterceptorPhase.Response },
            ],
            Mode = mode,
            FailOpen = failOpen,
            PriorityHint = priority is int p ? p : null,
        };

    private static InterceptorExecutionEngine.ExecutionEntry Entry(
        string name,
        InterceptorType type,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler,
        string[]? events = null,
        InterceptorMode? mode = null,
        bool? failOpen = null,
        int? priority = null) =>
        new(Descriptor(name, type, events, mode, failOpen, priority), (req, ct) => handler(req, ct));

    private static InterceptorExecutionEngine.ExecutionEntry Validator(
        string name,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler,
        InterceptorMode? mode = null) =>
        Entry(name, InterceptorType.Validation, handler, mode: mode);

    private static InterceptorExecutionEngine.ExecutionEntry Mutator(
        string name,
        Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> handler,
        int? priority = null,
        string[]? events = null,
        InterceptorMode? mode = null,
        bool? failOpen = null) =>
        Entry(name, InterceptorType.Mutation, handler, events, mode, failOpen, priority);

    private static Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> Fail(string message) =>
        (_, _) => new ValueTask<InterceptorResult>(ValidationInterceptorResult.Failure(
            new ValidationMessage { Message = message, Severity = ValidationSeverity.Error }));

    private static ValueTask<InterceptorResult> Pass() => new(ValidationInterceptorResult.Success());

    private static ValueTask<InterceptorResult> Unchanged() => new(new MutationInterceptorResult { Modified = false });

    private static ValueTask<InterceptorResult> Modified(JsonNode payload) =>
        new(new MutationInterceptorResult { Modified = true, Payload = payload });

    private static Func<InvokeInterceptorRequestParams, CancellationToken, ValueTask<InterceptorResult>> Record(List<string> order, string name) =>
        (_, _) => { order.Add(name); return Unchanged(); };
}
