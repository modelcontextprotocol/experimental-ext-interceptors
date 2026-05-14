namespace ModelContextProtocol.Interceptors;

/// <summary>
/// Defines the JSON-RPC method names for the MCP interceptors extension.
/// </summary>
public static class InterceptorRequestMethods
{
    /// <summary>Lists all interceptors available on the server.</summary>
    public const string InterceptorsList = "interceptors/list";

    /// <summary>Invokes a single interceptor by name.</summary>
    public const string InterceptorInvoke = "interceptor/invoke";
}
