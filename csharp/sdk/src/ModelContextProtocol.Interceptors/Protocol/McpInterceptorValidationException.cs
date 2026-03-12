namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Exception thrown when an interceptor validation fails with error severity,
/// aborting the interceptor chain.
/// </summary>
public sealed class McpInterceptorValidationException : Exception
{
    /// <summary>Gets the chain result that caused the validation failure.</summary>
    public InterceptorChainResult? ChainResult { get; }

    /// <summary>Gets the validation messages from the failed interceptor.</summary>
    public IReadOnlyList<ValidationMessage> ValidationMessages { get; }

    public McpInterceptorValidationException(string message, IReadOnlyList<ValidationMessage>? validationMessages = null, InterceptorChainResult? chainResult = null)
        : base(message)
    {
        ValidationMessages = validationMessages ?? [];
        ChainResult = chainResult;
    }

    public McpInterceptorValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        ValidationMessages = [];
    }
}
