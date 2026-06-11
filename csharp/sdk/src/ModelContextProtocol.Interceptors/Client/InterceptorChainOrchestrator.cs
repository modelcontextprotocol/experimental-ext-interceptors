using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Client-side chain orchestrator. Takes chain entries — interceptor descriptors (typically obtained
/// via <c>interceptors/list</c>, potentially from multiple servers) each paired with an invoker
/// delegate routing to the server hosting it — and runs them according to the SEP execution model.
/// </summary>
/// <remarks>
/// Per the SEP, chain execution is a convenience utility provided by SDKs — not a wire JSON-RPC
/// method. This orchestrator merges all entries into a single chain sorted by priority hint
/// (alphabetical tie-break) and enforces the trust-boundary-aware ordering:
/// <list type="bullet">
/// <item>Sending (request phase): mutations (sequential by priority) → validations (parallel) → sinks (fire-and-forget)</item>
/// <item>Receiving (response phase): validations (parallel) → sinks (fire-and-forget) → mutations (sequential by priority)</item>
/// </list>
/// Audit mode and fail-open are honored per interceptor descriptor.
/// </remarks>
internal static class InterceptorChainOrchestrator
{
    internal delegate ValueTask<InterceptorResult> InterceptorInvoker(
        InvokeInterceptorRequestParams request,
        CancellationToken cancellationToken);

    /// <summary>
    /// An interceptor descriptor paired with the invoker that routes <c>interceptor/invoke</c>
    /// to the server hosting it. Mirrors the SEP's <c>ChainEntry</c>.
    /// </summary>
    internal readonly record struct ChainExecutionEntry(Interceptor Descriptor, InterceptorInvoker Invoker);

    /// <summary>
    /// Convenience overload for the single-server case: every descriptor shares one invoker.
    /// </summary>
    internal static ValueTask<InterceptorChainResult> ExecuteAsync(
        IEnumerable<Interceptor> interceptors,
        InterceptorInvoker invoker,
        ExecuteChainRequestParams chainParams,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            interceptors.Select(i => new ChainExecutionEntry(i, invoker)).ToList(),
            chainParams,
            cancellationToken);
    }

    internal static async ValueTask<InterceptorChainResult> ExecuteAsync(
        IReadOnlyList<ChainExecutionEntry> entries,
        ExecuteChainRequestParams chainParams,
        CancellationToken cancellationToken)
    {
        if (chainParams.Phase is not (InterceptorPhase.Request or InterceptorPhase.Response))
        {
            throw new ArgumentException(
                $"Chain execution phase must be '{nameof(InterceptorPhase.Request)}' or '{nameof(InterceptorPhase.Response)}'; " +
                $"'{chainParams.Phase}' is an attribute-only convenience value that is invalid at execution time.",
                nameof(chainParams));
        }

        var sw = Stopwatch.StartNew();
        var results = new List<InterceptorResult>();
        var summary = new ChainValidationSummary();
        var currentPayload = chainParams.Payload;
        ChainAbortInfo? abortInfo = null;
        var status = InterceptorChainStatus.Success;

        // Merge & sort: one global order across all servers by effective priority
        // (ascending, alphabetical tie-break), then partition by type. The stable sort
        // preserves caller-supplied server order for exact ties.
        var applicable = FilterEntries(entries, chainParams)
            .OrderBy(e => e.Descriptor.PriorityHint?.GetEffective(chainParams.Phase) ?? 0)
            .ThenBy(e => e.Descriptor.Name, StringComparer.Ordinal)
            .ToList();

        var mutations = applicable.Where(e => e.Descriptor.Type == InterceptorType.Mutation).ToList();
        var validations = applicable.Where(e => e.Descriptor.Type == InterceptorType.Validation).ToList();
        var sinks = applicable.Where(e => e.Descriptor.Type == InterceptorType.Sink).ToList();

        using var timeoutCts = chainParams.TimeoutMs.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(chainParams.TimeoutMs!.Value);
        }
        var ct = timeoutCts?.Token ?? cancellationToken;

        try
        {
            if (chainParams.Phase == InterceptorPhase.Request)
            {
                // Sending: Mutations -> Validations -> Sinks
                (currentPayload, status, abortInfo) = await ExecuteMutationsAsync(mutations, chainParams, currentPayload, results, ct);
                if (status != InterceptorChainStatus.Success) goto Done;

                (status, abortInfo) = await ExecuteValidationsAsync(validations, chainParams, currentPayload, results, summary, ct);
                if (status != InterceptorChainStatus.Success) goto Done;

                await ExecuteSinksAsync(sinks, chainParams, currentPayload, results, ct);
            }
            else
            {
                // Receiving: Validations -> Sinks -> Mutations
                (status, abortInfo) = await ExecuteValidationsAsync(validations, chainParams, currentPayload, results, summary, ct);
                if (status != InterceptorChainStatus.Success) goto Done;

                await ExecuteSinksAsync(sinks, chainParams, currentPayload, results, ct);

                (currentPayload, status, abortInfo) = await ExecuteMutationsAsync(mutations, chainParams, currentPayload, results, ct);
            }
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            status = InterceptorChainStatus.Timeout;
        }

    Done:
        sw.Stop();
        return new InterceptorChainResult
        {
            Status = status,
            Event = chainParams.Event,
            Phase = chainParams.Phase,
            Results = results,
            FinalPayload = currentPayload,
            ValidationSummary = summary,
            TotalDurationMs = sw.ElapsedMilliseconds,
            AbortedAt = abortInfo,
        };
    }

    private static async ValueTask<(JsonNode payload, InterceptorChainStatus status, ChainAbortInfo? abort)> ExecuteMutationsAsync(
        List<ChainExecutionEntry> mutations,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload,
        List<InterceptorResult> results,
        CancellationToken ct)
    {
        foreach (var (descriptor, invoker) in mutations)
        {
            var isAudit = descriptor.Mode == InterceptorMode.Audit;
            var failOpen = descriptor.FailOpen == true;

            try
            {
                var invokeParams = CreateInvokeParams(descriptor, chainParams, currentPayload);
                var sw = Stopwatch.StartNew();
                var result = await invoker(invokeParams, ct);
                sw.Stop();
                result.InterceptorName = descriptor.Name;
                result.DurationMs = sw.ElapsedMilliseconds;
                results.Add(result);

                // Audit mode = shadow mutation: record the result but don't apply payload changes.
                if (!isAudit && result is MutationInterceptorResult mutation && mutation.Modified && mutation.Payload is not null)
                {
                    currentPayload = mutation.Payload;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Audit never blocks; fail-open allows the message to proceed on crash/timeout.
                if (isAudit || failOpen)
                {
                    continue;
                }

                return (currentPayload, InterceptorChainStatus.MutationFailed, new ChainAbortInfo
                {
                    Interceptor = descriptor.Name,
                    Reason = ex.Message,
                    Type = "mutation",
                });
            }
        }
        return (currentPayload, InterceptorChainStatus.Success, null);
    }

    private static async ValueTask<(InterceptorChainStatus status, ChainAbortInfo? abort)> ExecuteValidationsAsync(
        List<ChainExecutionEntry> validations,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload,
        List<InterceptorResult> results,
        ChainValidationSummary summary,
        CancellationToken ct)
    {
        var tasks = validations.Select(async entry =>
        {
            var descriptor = entry.Descriptor;
            try
            {
                var invokeParams = CreateInvokeParams(descriptor, chainParams, currentPayload);
                var sw = Stopwatch.StartNew();
                var result = await entry.Invoker(invokeParams, ct);
                sw.Stop();
                result.InterceptorName = descriptor.Name;
                result.DurationMs = sw.ElapsedMilliseconds;
                return (descriptor, result, error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (descriptor, (InterceptorResult?)null, error: ex);
            }
        });

        var completedResults = await Task.WhenAll(tasks);

        foreach (var (descriptor, result, error) in completedResults)
        {
            var isAudit = descriptor.Mode == InterceptorMode.Audit;
            var failOpen = descriptor.FailOpen == true;

            if (error is not null)
            {
                if (isAudit || failOpen)
                {
                    continue;
                }

                return (InterceptorChainStatus.ValidationFailed, new ChainAbortInfo
                {
                    Interceptor = descriptor.Name,
                    Reason = error.Message,
                    Type = "validation",
                });
            }

            results.Add(result!);

            if (result is ValidationInterceptorResult validation)
            {
                if (validation.Messages is not null)
                {
                    foreach (var msg in validation.Messages)
                    {
                        switch (msg.Severity)
                        {
                            case ValidationSeverity.Error: summary.Errors++; break;
                            case ValidationSeverity.Warn: summary.Warnings++; break;
                            case ValidationSeverity.Info: summary.Infos++; break;
                        }
                    }
                }

                if (!isAudit && !validation.Valid && validation.Severity == ValidationSeverity.Error)
                {
                    return (InterceptorChainStatus.ValidationFailed, new ChainAbortInfo
                    {
                        Interceptor = descriptor.Name,
                        Reason = validation.Messages?.FirstOrDefault()?.Message ?? "Validation failed",
                        Type = "validation",
                    });
                }
            }
        }

        return (InterceptorChainStatus.Success, null);
    }

    private static async ValueTask ExecuteSinksAsync(
        List<ChainExecutionEntry> sinks,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload,
        List<InterceptorResult> results,
        CancellationToken ct)
    {
        var tasks = sinks.Select(async entry =>
        {
            var descriptor = entry.Descriptor;
            try
            {
                var invokeParams = CreateInvokeParams(descriptor, chainParams, currentPayload);
                var sw = Stopwatch.StartNew();
                var result = await entry.Invoker(invokeParams, ct);
                sw.Stop();
                result.InterceptorName = descriptor.Name;
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }
            catch
            {
                return new SinkInterceptorResult
                {
                    InterceptorName = descriptor.Name,
                    Recorded = false,
                };
            }
        });

        var completedResults = await Task.WhenAll(tasks);
        results.AddRange(completedResults);
    }

    private static List<ChainExecutionEntry> FilterEntries(
        IReadOnlyList<ChainExecutionEntry> entries,
        ExecuteChainRequestParams chainParams)
    {
        var result = new List<ChainExecutionEntry>();

        foreach (var entry in entries)
        {
            var descriptor = entry.Descriptor;
            if (chainParams.InterceptorNames is { Count: > 0 } names && !names.Contains(descriptor.Name))
            {
                continue;
            }

            var matchesHook = false;
            foreach (var hook in descriptor.Hooks)
            {
                if (hook.Phase != chainParams.Phase)
                {
                    continue;
                }

                if (MatchesEvent(hook.Events, chainParams.Event))
                {
                    matchesHook = true;
                    break;
                }
            }

            if (!matchesHook)
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    internal static bool MatchesEvent(IList<string> hookEvents, string requestEvent)
    {
        foreach (var ev in hookEvents)
        {
            if (ev == InterceptionEvents.All) return true;
            if (ev == requestEvent) return true;
        }
        return false;
    }

    private static InvokeInterceptorRequestParams CreateInvokeParams(
        Interceptor descriptor,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload)
    {
        return new InvokeInterceptorRequestParams
        {
            Name = descriptor.Name,
            Event = chainParams.Event,
            Phase = chainParams.Phase,
            Payload = currentPayload,
            Context = chainParams.Context,
            TimeoutMs = chainParams.TimeoutMs,
        };
    }
}
