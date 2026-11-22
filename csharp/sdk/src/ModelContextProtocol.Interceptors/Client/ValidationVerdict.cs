using ModelContextProtocol.Interceptors.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// Decides whether a <see cref="ValidationInterceptorResult"/> blocks an operation.
/// </summary>
/// <remarks>
/// <para>
/// SEP-2624 says only <c>severity: "error"</c> blocks, but a result carries three signals that can
/// disagree: <c>valid</c>, the top-level <c>severity</c>, and each message's <c>severity</c>. Until the
/// working group settles which one decides (experimental-ext-interceptors#51), this implementation uses
/// the fail-secure reading:
/// </para>
/// <list type="number">
/// <item><c>valid: true</c> never blocks.</item>
/// <item>Otherwise the top-level <c>severity</c> decides, when present.</item>
/// <item>Otherwise the most severe message decides; a message without a severity counts as <c>error</c>.</item>
/// <item>An invalid result with no severity anywhere blocks (SEP Security Best Practice 4, "Fail Secure").</item>
/// </list>
/// </remarks>
public static class ValidationVerdict
{
    /// <summary>Gets the severity that decides the verdict, or <see langword="null"/> for a valid result without one.</summary>
    public static ValidationSeverity? EffectiveSeverity(ValidationInterceptorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Severity is { } severity)
        {
            return severity;
        }

        if (result.Messages is { Count: > 0 } messages)
        {
            return messages.Max(m => SeverityOf(m, result));
        }

        return result.Valid ? null : ValidationSeverity.Error;
    }

    /// <summary>
    /// Gets a message's severity. A message without one counts as <c>error</c> in an invalid result and
    /// <c>info</c> in a valid one, so that a validator that forgets severities still fails closed.
    /// </summary>
    public static ValidationSeverity SeverityOf(ValidationMessage message, ValidationInterceptorResult result)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(result);
        return message.Severity ?? (result.Valid ? ValidationSeverity.Info : ValidationSeverity.Error);
    }

    /// <summary>Returns whether <paramref name="result"/> blocks the operation (ignoring audit mode).</summary>
    public static bool Blocks(ValidationInterceptorResult result) =>
        !result.Valid && EffectiveSeverity(result) == ValidationSeverity.Error;

    /// <summary>Gets a one-line reason for a failing result.</summary>
    public static string Describe(ValidationInterceptorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var message = result.Messages?.FirstOrDefault(m => SeverityOf(m, result) == ValidationSeverity.Error)
            ?? result.Messages?.FirstOrDefault();
        return message?.Message ?? "Validation failed";
    }

    /// <summary>Gets the errors to report for a blocking result.</summary>
    internal static IEnumerable<InterceptorValidationError> ErrorsFor(string interceptor, ValidationInterceptorResult result)
    {
        var messages = result.Messages ?? [];
        var reported = messages.Where(m => SeverityOf(m, result) == ValidationSeverity.Error).ToList();
        if (reported.Count == 0)
        {
            reported = [.. messages];
        }

        if (reported.Count == 0)
        {
            yield return new InterceptorValidationError { Interceptor = interceptor, Message = "Validation failed" };
            yield break;
        }

        foreach (var message in reported)
        {
            yield return new InterceptorValidationError
            {
                Interceptor = interceptor,
                Severity = SeverityOf(message, result),
                Message = message.Message,
                Path = message.Path,
            };
        }
    }
}
