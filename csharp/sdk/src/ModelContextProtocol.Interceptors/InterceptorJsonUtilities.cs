using System.Text.Json;

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
    /// <remarks>
    /// Out-of-order metadata is allowed so that a result's <c>type</c> discriminator does not have to
    /// be the first property: other SDKs serialize <c>interceptor</c> or <c>phase</c> first.
    /// </remarks>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new(McpJsonUtilities.DefaultOptions)
        {
            AllowOutOfOrderMetadataProperties = true,
        };

        // Chain with the interceptor source-generated context
        options.TypeInfoResolverChain.Add(InterceptorJsonContext.Default);

        options.MakeReadOnly();
        return options;
    }
}
