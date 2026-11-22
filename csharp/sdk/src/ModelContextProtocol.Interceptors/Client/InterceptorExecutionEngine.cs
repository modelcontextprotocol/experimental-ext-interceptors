using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Runs the interceptors that apply to one lifecycle event according to the SEP-2624 execution model.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Sending: mutations (sequential, by priority) → validations (parallel) → sinks (background).</item>
/// <item>Receiving: validations (parallel) → sinks (background) → mutations (sequential, by priority).</item>
/// <item>Mutations are atomic: if one fails closed, none apply. Audit-mode mutations are computed but not applied.</item>
/// <item>Every validator completes before the verdict; every blocking error is reported.</item>
/// <item>Failures (errors, unusable results, per-interceptor timeouts) are routed by the resolved fail-open policy.
/// Audit-mode interceptors never block.</item>
/// <item>An aggregate timeout cancels the remaining invocations and fails the operation.</item>
/// </list>
/// Each entry is invoked through its own delegate, so a single run can span interceptor servers.
/// </remarks>
internal static class InterceptorExecutionEngine
{
    internal delegate ValueTask<InterceptorResult> InterceptorInvoker(
        InvokeInterceptorRequestParams request,
        CancellationToken cancellationToken);

    /// <summary>
    /// An interceptor descriptor, the delegate that routes <c>interceptor/invoke</c> to the server
    /// hosting it, and the invoker's policy for it.
    /// </summary>
    internal sealed record ExecutionEntry(
        Interceptor Descriptor,
        InterceptorInvoker Invoker,
        InterceptorOverrides? Overrides = null,
        JsonNode? Config = null,
        string? Server = null);

    /// <summary>Upper bound for a background sink invocation that has no timeout of its own.</summary>
    private static readonly TimeSpan DefaultSinkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Convenience overload for a single server: every descriptor shares one invoker.</summary>
    internal static ValueTask<InterceptorExecutionResult> ExecuteAsync(
        IEnumerable<Interceptor> interceptors,
        InterceptorInvoker invoker,
        InterceptorExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            interceptors.Select(i => new ExecutionEntry(i, invoker)).ToList(),
            request,
            cancellationToken);
    }

    internal static async ValueTask<InterceptorExecutionResult> ExecuteAsync(
        IReadOnlyList<ExecutionEntry> entries,
        InterceptorExecutionRequest request,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Phase is not (InterceptorPhase.Request or InterceptorPhase.Response))
        {
            throw new ArgumentException(
                $"Execution phase must be '{nameof(InterceptorPhase.Request)}' or '{nameof(InterceptorPhase.Response)}'; " +
                $"'{request.Phase}' is an attribute-only convenience value that is invalid at execution time.",
                nameof(request));
        }

        var direction = request.Direction ?? InterceptorInvokerRole.Client.GetDirection(request.Phase);
        var state = new ExecutionState(request, direction, logger ?? NullLogger.Instance);
        var stopwatch = Stopwatch.StartNew();

        // One global order across all servers: resolved priority ascending, then name (ordinal).
        // OrderBy is stable, so exact ties keep the caller's server order.
        var applicable = entries
            .Select(entry => ResolvedEntry.Create(entry, request.Phase))
            .Where(entry => entry.Matches(request.Event, request.Phase))
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        var mutations = applicable.Where(e => e.Type == InterceptorType.Mutation).ToList();
        var validations = applicable.Where(e => e.Type == InterceptorType.Validation).ToList();
        var sinks = applicable.Where(e => e.Type == InterceptorType.Sink).ToList();

        using var aggregateCts = request.TimeoutMs is int aggregateTimeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        aggregateCts?.CancelAfter(request.TimeoutMs!.Value);
        var aggregateToken = aggregateCts?.Token ?? cancellationToken;

        try
        {
            if (direction == InterceptorDirection.Sending)
            {
                if (await RunMutationsAsync(mutations, state, aggregateToken).ConfigureAwait(false) &&
                    await RunValidationsAsync(validations, state, aggregateToken).ConfigureAwait(false))
                {
                    DispatchSinks(sinks, state);
                }
            }
            else if (await RunValidationsAsync(validations, state, aggregateToken).ConfigureAwait(false))
            {
                DispatchSinks(sinks, state);
                await RunMutationsAsync(mutations, state, aggregateToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (aggregateCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            state.Fail(InterceptorExecutionStatus.Timeout, new InterceptorAbortInfo
            {
                Interceptor = state.InFlight ?? "*",
                Reason = $"Interceptors for '{request.Event}' did not complete within the aggregate timeout of {request.TimeoutMs}ms.",
                Type = "timeout",
            });
        }

        stopwatch.Stop();
        return state.ToResult(stopwatch.ElapsedMilliseconds);
    }

    /// <returns><see langword="true"/> if execution should continue.</returns>
    private static async ValueTask<bool> RunMutationsAsync(
        List<ResolvedEntry> mutations,
        ExecutionState state,
        CancellationToken aggregateToken)
    {
        foreach (var entry in mutations)
        {
            state.InFlight = entry.Name;
            var outcome = await InvokeAsync(entry, state.Request, state.CurrentPayload, aggregateToken).ConfigureAwait(false);
            var invocation = state.Record(entry, outcome);

            string? failure = outcome.Error;
            if (outcome.Result is { } result)
            {
                if (result is not MutationInterceptorResult mutation)
                {
                    failure = $"returned a '{TypeName(result)}' result, but is declared as a mutation interceptor";
                }
                else if (mutation.Modified && mutation.Payload is null)
                {
                    failure = "reported modified: true without a payload";
                }
                else
                {
                    state.Results.Add(result);
                    if (mutation.Modified)
                    {
                        if (entry.IsAudit)
                        {
                            invocation.Effect = InterceptorEffect.ShadowModified;
                        }
                        else
                        {
                            state.CurrentPayload = mutation.Payload!;
                            invocation.Effect = InterceptorEffect.Modified;
                        }
                    }
                }
            }

            if (failure is null)
            {
                continue;
            }

            invocation.Error ??= failure;
            if (outcome.Result is not null)
            {
                invocation.Outcome = InterceptorInvocationOutcome.Failed;
            }

            if (entry.IsAudit || entry.FailOpen)
            {
                invocation.Effect = entry.FailOpen ? InterceptorEffect.FailedOpen : InterceptorEffect.None;
                state.Logger.LogWarning(
                    "Mutation interceptor '{Interceptor}' failed ({Reason}); continuing because mode={Mode} failOpen={FailOpen}.",
                    entry.Name, failure, entry.Mode, entry.FailOpen);
                continue;
            }

            invocation.Effect = InterceptorEffect.Blocked;
            state.Fail(InterceptorExecutionStatus.MutationFailed, new InterceptorAbortInfo
            {
                Interceptor = entry.Name,
                Reason = failure,
                Type = "mutation",
            });
            return false;
        }

        state.InFlight = null;
        return true;
    }

    /// <returns><see langword="true"/> if no validator blocked the operation.</returns>
    private static async ValueTask<bool> RunValidationsAsync(
        List<ResolvedEntry> validations,
        ExecutionState state,
        CancellationToken aggregateToken)
    {
        if (validations.Count == 0)
        {
            return true;
        }

        state.InFlight = validations.Count == 1 ? validations[0].Name : null;
        var payload = state.CurrentPayload;
        var outcomes = await Task.WhenAll(validations.Select(entry =>
            InvokeAsync(entry, state.Request, payload, aggregateToken).AsTask())).ConfigureAwait(false);
        state.InFlight = null;

        InterceptorAbortInfo? firstAbort = null;
        for (var i = 0; i < validations.Count; i++)
        {
            var entry = validations[i];
            var outcome = outcomes[i];
            var invocation = state.Record(entry, outcome);

            string? failure = outcome.Error;
            if (outcome.Result is { } result && result is not ValidationInterceptorResult)
            {
                failure = $"returned a '{TypeName(result)}' result, but is declared as a validation interceptor";
                invocation.Outcome = InterceptorInvocationOutcome.Failed;
            }

            if (failure is not null)
            {
                invocation.Error ??= failure;
                if (entry.IsAudit || entry.FailOpen)
                {
                    invocation.Effect = entry.FailOpen ? InterceptorEffect.FailedOpen : InterceptorEffect.None;
                    state.Logger.LogWarning(
                        "Validation interceptor '{Interceptor}' failed ({Reason}); continuing because mode={Mode} failOpen={FailOpen}.",
                        entry.Name, failure, entry.Mode, entry.FailOpen);
                    continue;
                }

                invocation.Effect = InterceptorEffect.Blocked;
                state.Summary.Errors++;
                state.ValidationErrors.Add(new InterceptorValidationError
                {
                    Interceptor = entry.Name,
                    Message = $"Interceptor failed: {failure}",
                });
                firstAbort ??= new InterceptorAbortInfo { Interceptor = entry.Name, Reason = failure, Type = "validation" };
                continue;
            }

            var validation = (ValidationInterceptorResult)outcome.Result!;
            state.Results.Add(validation);
            CountMessages(validation, state.Summary);

            if (!ValidationVerdict.Blocks(validation))
            {
                continue;
            }

            if (entry.IsAudit)
            {
                invocation.Effect = InterceptorEffect.WouldBlock;
                state.Logger.LogInformation(
                    "Audit-mode validator '{Interceptor}' would have blocked '{Event}' ({Phase}): {Reason}",
                    entry.Name, state.Request.Event, state.Request.Phase, ValidationVerdict.Describe(validation));
                continue;
            }

            invocation.Effect = InterceptorEffect.Blocked;
            foreach (var error in ValidationVerdict.ErrorsFor(entry.Name, validation))
            {
                state.ValidationErrors.Add(error);
            }

            firstAbort ??= new InterceptorAbortInfo
            {
                Interceptor = entry.Name,
                Reason = ValidationVerdict.Describe(validation),
                Type = "validation",
            };
        }

        if (firstAbort is null)
        {
            return true;
        }

        state.Fail(InterceptorExecutionStatus.ValidationFailed, firstAbort);
        return false;
    }

    /// <summary>
    /// Starts every sink in the background. The operation never waits for a sink, and sink failures
    /// are logged, never surfaced.
    /// </summary>
    private static void DispatchSinks(List<ResolvedEntry> sinks, ExecutionState state)
    {
        foreach (var entry in sinks)
        {
            state.Record(entry, new InvocationOutcome(InterceptorInvocationOutcome.Dispatched, null, null, 0));

            // Snapshot the payload: later mutations must not change what the sink observes.
            var invokeParams = CreateInvokeParams(entry, state.Request, state.CurrentPayload.DeepClone());
            var logger = state.Logger;
            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(
                    entry.TimeoutMs is int timeout ? TimeSpan.FromMilliseconds(timeout) : DefaultSinkTimeout);
                try
                {
                    await entry.Invoker(invokeParams, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Sink interceptor '{Interceptor}' failed.", entry.Name);
                }
            });
        }
    }

    private static async ValueTask<InvocationOutcome> InvokeAsync(
        ResolvedEntry entry,
        InterceptorExecutionRequest request,
        JsonNode payload,
        CancellationToken aggregateToken)
    {
        using var timeoutCts = entry.TimeoutMs is int timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(aggregateToken)
            : null;
        timeoutCts?.CancelAfter(entry.TimeoutMs!.Value);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await entry.Invoker(CreateInvokeParams(entry, request, payload), timeoutCts?.Token ?? aggregateToken).ConfigureAwait(false);
            result.InterceptorName ??= entry.Name;
            result.Phase = request.Phase;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            return new InvocationOutcome(InterceptorInvocationOutcome.Completed, result, null, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!aggregateToken.IsCancellationRequested)
        {
            return timeoutCts?.IsCancellationRequested == true
                ? new InvocationOutcome(InterceptorInvocationOutcome.TimedOut, null, $"timed out after {entry.TimeoutMs}ms", stopwatch.ElapsedMilliseconds)
                : new InvocationOutcome(InterceptorInvocationOutcome.Failed, null, "the invocation was cancelled", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InvocationOutcome(InterceptorInvocationOutcome.Failed, null, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    private static InvokeInterceptorRequestParams CreateInvokeParams(
        ResolvedEntry entry,
        InterceptorExecutionRequest request,
        JsonNode payload) => new()
        {
            Name = entry.Name,
            Event = request.Event,
            Phase = request.Phase,
            Payload = payload,
            Config = entry.Entry.Config,
            TimeoutMs = entry.TimeoutMs,
            Context = request.Context,
        };

    private static void CountMessages(ValidationInterceptorResult validation, ValidationSummary summary)
    {
        if (validation.Messages is { Count: > 0 } messages)
        {
            foreach (var message in messages)
            {
                Count(message.Severity, summary);
            }
        }
        else if (!validation.Valid && ValidationVerdict.EffectiveSeverity(validation) is { } severity)
        {
            Count(severity, summary);
        }

        static void Count(ValidationSeverity severity, ValidationSummary summary)
        {
            switch (severity)
            {
                case ValidationSeverity.Error: summary.Errors++; break;
                case ValidationSeverity.Warn: summary.Warnings++; break;
                case ValidationSeverity.Info: summary.Infos++; break;
            }
        }
    }

    private static string TypeName(InterceptorResult result) => result switch
    {
        ValidationInterceptorResult => "validation",
        MutationInterceptorResult => "mutation",
        SinkInterceptorResult => "sink",
        _ => result.GetType().Name,
    };

    internal static bool MatchesEvent(IList<string> hookEvents, string requestEvent)
    {
        foreach (var ev in hookEvents)
        {
            if (ev == InterceptionEvents.All || ev == requestEvent)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct InvocationOutcome(
        InterceptorInvocationOutcome Kind,
        InterceptorResult? Result,
        string? Error,
        long DurationMs);

    /// <summary>An entry with its policy resolved for one phase (override → declared → SEP default).</summary>
    private sealed class ResolvedEntry
    {
        public required ExecutionEntry Entry { get; init; }
        public required InterceptorMode Mode { get; init; }
        public required InterceptorPolicySource ModeSource { get; init; }
        public required bool FailOpen { get; init; }
        public required InterceptorPolicySource FailOpenSource { get; init; }
        public required int Priority { get; init; }
        public required int? TimeoutMs { get; init; }
        public required IList<InterceptorHook> Hooks { get; init; }

        public string Name => Entry.Descriptor.Name;
        public InterceptorType Type => Entry.Descriptor.Type;
        public InterceptorInvoker Invoker => Entry.Invoker;
        public bool IsAudit => Mode == InterceptorMode.Audit;

        public static ResolvedEntry Create(ExecutionEntry entry, InterceptorPhase phase)
        {
            var descriptor = entry.Descriptor;
            var overrides = entry.Overrides;
            InterceptorOverrides.ValidateFor(descriptor, overrides);

            var (mode, modeSource) = Resolve(overrides?.Mode, descriptor.Mode, InterceptorMode.Active);
            var (failOpen, failOpenSource) = Resolve(overrides?.FailOpen, descriptor.FailOpen, false);
            var hint = overrides?.PriorityHint ?? descriptor.PriorityHint;

            return new ResolvedEntry
            {
                Entry = entry,
                Mode = mode,
                ModeSource = modeSource,
                FailOpen = failOpen,
                FailOpenSource = failOpenSource,
                Priority = descriptor.Type == InterceptorType.Mutation ? hint?.GetEffective(phase) ?? 0 : 0,
                TimeoutMs = overrides?.TimeoutMs,
                Hooks = overrides?.Hooks ?? descriptor.Hooks,
            };
        }

        public bool Matches(string eventName, InterceptorPhase phase) =>
            Hooks.Any(hook => hook.Phase == phase && MatchesEvent(hook.Events, eventName));

        private static (T Value, InterceptorPolicySource Source) Resolve<T>(T? overridden, T? declared, T fallback)
            where T : struct =>
            overridden is { } o ? (o, InterceptorPolicySource.Override)
            : declared is { } d ? (d, InterceptorPolicySource.Declared)
            : (fallback, InterceptorPolicySource.Default);
    }

    private sealed class ExecutionState(InterceptorExecutionRequest request, InterceptorDirection direction, ILogger logger)
    {
        public InterceptorExecutionRequest Request { get; } = request;
        public ILogger Logger { get; } = logger;
        public JsonNode CurrentPayload { get; set; } = request.Payload;
        public List<InterceptorResult> Results { get; } = [];
        public List<InterceptorInvocation> Invocations { get; } = [];
        public ValidationSummary Summary { get; } = new();
        public List<InterceptorValidationError> ValidationErrors { get; } = [];
        public string? InFlight { get; set; }

        private InterceptorExecutionStatus _status = InterceptorExecutionStatus.Success;
        private InterceptorAbortInfo? _abort;

        public InterceptorInvocation Record(ResolvedEntry entry, InvocationOutcome outcome)
        {
            var invocation = new InterceptorInvocation
            {
                Interceptor = entry.Name,
                Server = entry.Entry.Server,
                Type = entry.Type,
                Mode = entry.Mode,
                ModeSource = entry.ModeSource,
                FailOpen = entry.FailOpen,
                FailOpenSource = entry.FailOpenSource,
                Priority = entry.Priority,
                TimeoutMs = entry.TimeoutMs,
                Outcome = outcome.Kind,
                Result = outcome.Result,
                Error = outcome.Error,
                DurationMs = outcome.DurationMs,
            };
            Invocations.Add(invocation);

            Logger.LogDebug(
                "Interceptor '{Interceptor}' ({Type}) on '{Event}' {Phase}: outcome={Outcome} mode={Mode} ({ModeSource}) failOpen={FailOpen} ({FailOpenSource}) in {DurationMs}ms.",
                invocation.Interceptor, invocation.Type, Request.Event, Request.Phase, invocation.Outcome,
                invocation.Mode, invocation.ModeSource, invocation.FailOpen, invocation.FailOpenSource, invocation.DurationMs);

            return invocation;
        }

        public void Fail(InterceptorExecutionStatus status, InterceptorAbortInfo abort)
        {
            _status = status;
            _abort = abort;
        }

        public InterceptorExecutionResult ToResult(long totalDurationMs) => new()
        {
            Status = _status,
            Event = Request.Event,
            Phase = Request.Phase,
            Direction = direction,
            Results = Results,
            Invocations = Invocations,
            OriginalPayload = Request.Payload,
            // Mutations are atomic: a rejected operation keeps its original payload.
            FinalPayload = _status == InterceptorExecutionStatus.Success ? CurrentPayload : Request.Payload,
            ValidationSummary = Summary,
            ValidationErrors = ValidationErrors,
            TotalDurationMs = totalDurationMs,
            AbortedAt = _abort,
        };
    }
}
