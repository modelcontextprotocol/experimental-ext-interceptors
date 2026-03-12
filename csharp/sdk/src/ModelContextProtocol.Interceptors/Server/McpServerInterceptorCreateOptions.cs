using System.Text.Json;

namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// Options used when creating an <see cref="McpServerInterceptor"/> instance.
/// </summary>
public sealed class McpServerInterceptorCreateOptions
{
    /// <summary>Gets or sets an optional service provider for resolving DI services during invocation.</summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>Gets or sets the JSON serializer options used for parameter deserialization.</summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }
}
