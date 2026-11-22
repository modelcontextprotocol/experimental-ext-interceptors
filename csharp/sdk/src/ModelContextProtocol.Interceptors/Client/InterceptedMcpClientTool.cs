using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Interceptors.Client;

/// <summary>
/// A tool listed through an <see cref="InterceptingMcpClient"/>. Calling it, directly or as an
/// <see cref="AIFunction"/> handed to an <c>IChatClient</c>, goes through the same interceptors as
/// <see cref="InterceptingMcpClient.CallToolAsync"/>.
/// </summary>
/// <remarks>
/// The SDK's <see cref="McpClientTool"/> is bound to the client that listed it, so a tool listed
/// through the wrapper would otherwise call the server directly and skip interceptors.
/// </remarks>
public sealed class InterceptedMcpClientTool : AIFunction
{
    private readonly InterceptingMcpClient _client;

    internal InterceptedMcpClientTool(InterceptingMcpClient client, Tool tool)
    {
        _client = client;
        ProtocolTool = tool;
    }

    /// <summary>Gets the protocol-level tool definition.</summary>
    public Tool ProtocolTool { get; }

    /// <inheritdoc />
    public override string Name => ProtocolTool.Name;

    /// <inheritdoc />
    public override string Description => ProtocolTool.Description ?? string.Empty;

    /// <inheritdoc />
    public override JsonElement JsonSchema => ProtocolTool.InputSchema;

    /// <inheritdoc />
    public override JsonElement? ReturnJsonSchema => ProtocolTool.OutputSchema;

    /// <inheritdoc />
    public override JsonSerializerOptions JsonSerializerOptions => McpJsonUtilities.DefaultOptions;

    /// <summary>Calls the tool through the interceptors.</summary>
    public ValueTask<CallToolResult> CallAsync(
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(Name, arguments, cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await CallAsync(arguments, cancellationToken).ConfigureAwait(false);

        // Same translation as McpClientTool: plain content becomes AIContent so chat clients can handle
        // it natively; anything carrying more than content stays a JSON CallToolResult.
        if (result.IsError is not true && result.StructuredContent is null && !HasApplicationMetadata(result.Meta))
        {
            switch (result.Content.Count)
            {
                case 1 when result.Content[0].ToAIContent(JsonSerializerOptions) is { } content:
                    return content;
                case > 1 when result.Content.Select(c => c.ToAIContent(JsonSerializerOptions)).ToArray() is { } contents && contents.All(c => c is not null):
                    return contents;
            }
        }

        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)));
    }

    private static bool HasApplicationMetadata(JsonObject? meta) =>
        meta is not null && meta.Any(p => !string.Equals(p.Key, MetaKeys.ServerInfo, StringComparison.Ordinal));
}
