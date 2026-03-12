using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors;

/// <summary>
/// Provides JSON serialization utilities for interceptor types, chaining with the MCP SDK's default options.
/// </summary>
public static class InterceptorJsonUtilities
{
    /// <summary>
    /// Gets the default <see cref="JsonSerializerOptions"/> that includes both MCP SDK types
    /// and interceptor extension types.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new(McpJsonUtilities.DefaultOptions);

        // Chain with the interceptor source-generated context
        options.TypeInfoResolverChain.Add(InterceptorJsonContext.Default);

        options.MakeReadOnly();
        return options;
    }
}
