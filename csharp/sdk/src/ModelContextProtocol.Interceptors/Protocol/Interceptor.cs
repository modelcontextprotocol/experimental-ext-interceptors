using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Represents the protocol-level definition of an interceptor, analogous to <c>Tool</c> in the MCP spec.
/// </summary>
public sealed class Interceptor
{
    /// <summary>Gets or sets the unique name identifying this interceptor.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Gets or sets the semantic version of this interceptor.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Gets or sets a human-readable description of what this interceptor does.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the interceptor type (validation, mutation, or sink).</summary>
    [JsonPropertyName("type")]
    public InterceptorType Type { get; set; }

    /// <summary>
    /// Gets or sets the hook entries declaring which lifecycle events and phases this interceptor fires on.
    /// Each entry pairs an event set with a single phase; supply two entries to subscribe to both phases.
    /// </summary>
    [JsonPropertyName("hooks")]
    public IList<InterceptorHook> Hooks { get; set; } = [];

    /// <summary>Gets or sets the priority hint for ordering mutation interceptors. Lower values execute first.</summary>
    [JsonPropertyName("priorityHint")]
    public int? PriorityHint { get; set; }

    /// <summary>Gets or sets protocol version compatibility constraints.</summary>
    [JsonPropertyName("compat")]
    public InterceptorCompatibility? Compat { get; set; }

    /// <summary>Gets or sets the JSON Schema for this interceptor's configuration.</summary>
    [JsonPropertyName("configSchema")]
    public JsonElement? ConfigSchema { get; set; }

    /// <summary>Gets or sets optional metadata.</summary>
    [JsonPropertyName("_meta")]
    public JsonObject? Meta { get; set; }
}
