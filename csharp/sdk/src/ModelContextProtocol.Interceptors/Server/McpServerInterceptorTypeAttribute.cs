namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// Marks a class as containing MCP server interceptor methods for assembly-level discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class McpServerInterceptorTypeAttribute : Attribute;
