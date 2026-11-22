using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Thrown when interceptors reject an operation: a validator blocked it, a mutator failed closed,
/// or the interceptors did not finish in time.
/// </summary>
/// <remarks>
/// Derives from <see cref="McpProtocolException"/>, so when it escapes a server request handler the
/// SDK returns a JSON-RPC error with <see cref="McpProtocolException.ErrorCode"/>, the message, and the
/// entries of <see cref="Exception.Data"/> as <c>error.data</c>. The gateway converts a rejected
/// <c>tools/call</c> into a tool error result instead, unless configured otherwise, because clients
/// show tool error results to the model more reliably than protocol errors.
/// </remarks>
public class McpInterceptorException : McpProtocolException
{
    /// <summary>Creates a new <see cref="McpInterceptorException"/> caused by <paramref name="innerException"/>.</summary>
    public McpInterceptorException(string message, Exception? innerException, McpErrorCode errorCode)
        : base(message, innerException, errorCode)
    {
    }

    /// <summary>Creates a new <see cref="McpInterceptorException"/>.</summary>
    public McpInterceptorException(string message, McpErrorCode errorCode, InterceptorExecutionResult? executionResult = null)
        : base(message, errorCode)
    {
        ExecutionResult = executionResult;
        if (executionResult is not null)
        {
            Data["event"] = executionResult.Event;
            Data["phase"] = executionResult.Phase == InterceptorPhase.Response ? "response" : "request";
            if (executionResult.AbortedAt is { } abort)
            {
                Data["failedInterceptor"] = abort.Interceptor;
            }
        }
    }

    /// <summary>Gets the execution result that caused the rejection, when available.</summary>
    public InterceptorExecutionResult? ExecutionResult { get; }

    /// <summary>Creates the exception that reports <paramref name="result"/>'s failure.</summary>
    /// <exception cref="ArgumentException"><paramref name="result"/> succeeded.</exception>
    public static McpInterceptorException FromResult(InterceptorExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var where = $"{result.Event} {(result.Phase == InterceptorPhase.Response ? "response" : "request")}";
        var abort = result.AbortedAt;
        return result.Status switch
        {
            InterceptorExecutionStatus.ValidationFailed => new McpInterceptorValidationException(result),
            InterceptorExecutionStatus.MutationFailed => new McpInterceptorException(
                $"Interceptor mutation failed ({where}): {abort?.Interceptor ?? "unknown"}: {abort?.Reason ?? "unknown error"}",
                McpErrorCode.InternalError,
                result),
            InterceptorExecutionStatus.Timeout => new McpInterceptorException(
                $"Interceptor execution timeout ({where}): {abort?.Reason ?? "the interceptors did not complete in time"}",
                (McpErrorCode)(-32000),
                result),
            _ => throw new ArgumentException("The execution succeeded; there is nothing to report.", nameof(result)),
        };
    }
}

/// <summary>
/// Thrown when one or more validators reject an operation. Carries every blocking error, in the
/// SEP's <c>Interceptor validation failed</c> shape (code <c>-32602</c>, <c>data.validationErrors</c>).
/// </summary>
public sealed class McpInterceptorValidationException : McpInterceptorException
{
    /// <summary>Creates the exception for a <see cref="InterceptorExecutionStatus.ValidationFailed"/> result.</summary>
    public McpInterceptorValidationException(InterceptorExecutionResult executionResult)
        : base(FormatMessage(executionResult), McpErrorCode.InvalidParams, executionResult)
    {
        ValidationErrors = [.. executionResult.ValidationErrors];
        Data["validationErrors"] = JsonSerializer.SerializeToElement(
            ValidationErrors, InterceptorJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IReadOnlyList<InterceptorValidationError>)));
    }

    /// <summary>Creates the exception from a message, without an execution result.</summary>
    public McpInterceptorValidationException(string message, IReadOnlyList<InterceptorValidationError>? validationErrors = null)
        : base(message, McpErrorCode.InvalidParams)
    {
        ValidationErrors = validationErrors ?? [];
        Data["validationErrors"] = JsonSerializer.SerializeToElement(
            ValidationErrors, InterceptorJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IReadOnlyList<InterceptorValidationError>)));
    }

    /// <summary>Gets every reason validation rejected the operation.</summary>
    public IReadOnlyList<InterceptorValidationError> ValidationErrors { get; }

    private static string FormatMessage(InterceptorExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var reasons = result.ValidationErrors.Count > 0
            ? string.Join("; ", result.ValidationErrors.Select(e => $"{e.Interceptor}: {e.Message}"))
            : $"{result.AbortedAt?.Interceptor ?? "unknown"}: {result.AbortedAt?.Reason ?? "validation failed"}";
        var phase = result.Phase == InterceptorPhase.Response ? "response" : "request";
        return $"Interceptor validation failed ({result.Event} {phase}): {reasons}";
    }
}
