using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// Executes a chain of interceptors according to the SEP-1763 execution model.
/// </summary>
/// <remarks>
/// Sending (request phase): Mutations (sequential by priority) -> Validations (parallel) -> Sinks (fire-and-forget)
/// Receiving (response phase): Validations (parallel) -> Sinks (fire-and-forget) -> Mutations (sequential by priority)
/// </remarks>
internal static class InterceptorChainExecutor
{
    internal static async ValueTask<InterceptorChainResult> ExecuteAsync(
        IReadOnlyList<McpServerInterceptor> interceptors,
        ExecuteChainRequestParams chainParams,
        McpServer server,
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<InterceptorResult>();
        var summary = new ChainValidationSummary();
        var currentPayload = chainParams.Payload;
        ChainAbortInfo? abortInfo = null;
        var status = InterceptorChainStatus.Success;

        // Filter interceptors by event and phase
        var applicable = FilterInterceptors(interceptors, chainParams);

        var mutations = applicable.Where(i => i.ProtocolInterceptor.Type == InterceptorType.Mutation)
            .OrderBy(i => i.ProtocolInterceptor.PriorityHint ?? 0)
            .ThenBy(i => i.ProtocolInterceptor.Name, StringComparer.Ordinal)
            .ToList();

        var validations = applicable.Where(i => i.ProtocolInterceptor.Type == InterceptorType.Validation).ToList();
        var sinks = applicable.Where(i => i.ProtocolInterceptor.Type == InterceptorType.Sink).ToList();

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
                (currentPayload, status, abortInfo) = await ExecuteMutationsAsync(mutations, chainParams, currentPayload, server, services, results, ct);
                if (status != InterceptorChainStatus.Success) goto Done;

                (status, abortInfo) = await ExecuteValidationsAsync(validations, chainParams, currentPayload, server, services, results, summary, ct);
                if (status != InterceptorChainStatus.Success) goto Done;

                await ExecuteSinksAsync(sinks, chainParams, currentPayload, server, services, results, ct);
            }
            else
            {
                // Receiving: Validations -> Sinks -> Mutations
                (status, abortInfo) = await ExecuteValidationsAsync(validations, chainParams, currentPayload, server, services, results, summary, ct);
                if (status != InterceptorChainStatus.Success) goto Done;

                await ExecuteSinksAsync(sinks, chainParams, currentPayload, server, services, results, ct);

                (currentPayload, status, abortInfo) = await ExecuteMutationsAsync(mutations, chainParams, currentPayload, server, services, results, ct);
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
        List<McpServerInterceptor> mutations,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload,
        McpServer server,
        IServiceProvider? services,
        List<InterceptorResult> results,
        CancellationToken ct)
    {
        foreach (var interceptor in mutations)
        {
            var protocol = interceptor.ProtocolInterceptor;
            var isAudit = protocol.Mode == InterceptorMode.Audit;
            var failOpen = protocol.FailOpen == true;

            try
            {
                var invokeParams = CreateInvokeParams(interceptor, chainParams, currentPayload);
                var sw = Stopwatch.StartNew();
                var result = await interceptor.InvokeAsync(invokeParams, server, services, ct);
                sw.Stop();
                result.InterceptorName = protocol.Name;
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
                    Interceptor = protocol.Name,
                    Reason = ex.Message,
                    Type = "mutation",
                });
            }
        }
        return (currentPayload, InterceptorChainStatus.Success, null);
    }

    private static async ValueTask<(InterceptorChainStatus status, ChainAbortInfo? abort)> ExecuteValidationsAsync(
        List<McpServerInterceptor> validations,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload,
        McpServer server,
        IServiceProvider? services,
        List<InterceptorResult> results,
        ChainValidationSummary summary,
        CancellationToken ct)
    {
        // Validations run in parallel. Each task captures its own crash as a per-interceptor error
        // so the executor can apply per-interceptor fail-open/audit semantics rather than blowing up.
        var tasks = validations.Select(async interceptor =>
        {
            try
            {
                var invokeParams = CreateInvokeParams(interceptor, chainParams, currentPayload);
                var sw = Stopwatch.StartNew();
                var result = await interceptor.InvokeAsync(invokeParams, server, services, ct);
                sw.Stop();
                result.InterceptorName = interceptor.ProtocolInterceptor.Name;
                result.DurationMs = sw.ElapsedMilliseconds;
                return (interceptor, result, error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (interceptor, (InterceptorResult?)null, error: ex);
            }
        });

        var completedResults = await Task.WhenAll(tasks);

        foreach (var (interceptor, result, error) in completedResults)
        {
            var protocol = interceptor.ProtocolInterceptor;
            var isAudit = protocol.Mode == InterceptorMode.Audit;
            var failOpen = protocol.FailOpen == true;

            if (error is not null)
            {
                // Crash/timeout: audit never blocks; fail-open allows the message to proceed.
                if (isAudit || failOpen)
                {
                    continue;
                }

                return (InterceptorChainStatus.ValidationFailed, new ChainAbortInfo
                {
                    Interceptor = protocol.Name,
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

                // Audit mode never blocks regardless of validation outcome.
                if (!isAudit && !validation.Valid && validation.Severity == ValidationSeverity.Error)
                {
                    return (InterceptorChainStatus.ValidationFailed, new ChainAbortInfo
                    {
                        Interceptor = protocol.Name,
                        Reason = validation.Messages?.FirstOrDefault()?.Message ?? "Validation failed",
                        Type = "validation",
                    });
                }
            }
        }

        return (InterceptorChainStatus.Success, null);
    }

    private static async ValueTask ExecuteSinksAsync(
        List<McpServerInterceptor> sinks,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload,
        McpServer server,
        IServiceProvider? services,
        List<InterceptorResult> results,
        CancellationToken ct)
    {
        // Sinks run in parallel, fire-and-forget (failures swallowed)
        var tasks = sinks.Select(async interceptor =>
        {
            try
            {
                var invokeParams = CreateInvokeParams(interceptor, chainParams, currentPayload);
                var sw = Stopwatch.StartNew();
                var result = await interceptor.InvokeAsync(invokeParams, server, services, ct);
                sw.Stop();
                result.InterceptorName = interceptor.ProtocolInterceptor.Name;
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }
            catch
            {
                return new SinkInterceptorResult
                {
                    InterceptorName = interceptor.ProtocolInterceptor.Name,
                    Recorded = false,
                };
            }
        });

        var completedResults = await Task.WhenAll(tasks);
        results.AddRange(completedResults);
    }

    private static List<McpServerInterceptor> FilterInterceptors(
        IReadOnlyList<McpServerInterceptor> interceptors,
        ExecuteChainRequestParams chainParams)
    {
        var result = new List<McpServerInterceptor>();

        foreach (var interceptor in interceptors)
        {
            // Filter by name if specified
            if (chainParams.InterceptorNames is { Count: > 0 } names &&
                !names.Contains(interceptor.ProtocolInterceptor.Name))
            {
                continue;
            }

            // Filter by hooks: match if any hook entry covers this phase and includes the event
            var matchesHook = false;
            foreach (var hook in interceptor.ProtocolInterceptor.Hooks)
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

            result.Add(interceptor);
        }

        return result;
    }

    internal static bool MatchesEvent(IList<string> interceptorEvents, string requestEvent)
    {
        foreach (var ev in interceptorEvents)
        {
            if (ev == InterceptorEvents.All) return true;
            if (ev == requestEvent) return true;

            // Wildcard matching: "*/request" matches any request event
            if (ev == InterceptorEvents.AllRequests && !requestEvent.Contains('/'))
                return true;
            if (ev == InterceptorEvents.AllResponses && !requestEvent.Contains('/'))
                return true;
        }
        return false;
    }

    private static InvokeInterceptorRequestParams CreateInvokeParams(
        McpServerInterceptor interceptor,
        ExecuteChainRequestParams chainParams,
        JsonNode currentPayload)
    {
        return new InvokeInterceptorRequestParams
        {
            Name = interceptor.ProtocolInterceptor.Name,
            Event = chainParams.Event,
            Phase = chainParams.Phase,
            Payload = currentPayload,
            Context = chainParams.Context,
            TimeoutMs = chainParams.TimeoutMs,
        };
    }
}
