namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// String constants for the lifecycle events defined by SEP-1763 (Interception Events).
/// </summary>
/// <remarks>
/// The SEP defines these as the <c>InterceptionEvent</c> type union; in C# the equivalent is
/// a plain <see langword="string"/>, so this class just holds the well-known names.
/// Implementations MAY define additional event names following the <c>namespace/operation</c>
/// convention; namespace wildcards (e.g. <c>tools/*</c>) MAY be supported by individual SDKs.
/// </remarks>
public static class InterceptionEvents
{
    // Server feature events
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
    public const string PromptsList = "prompts/list";
    public const string PromptsGet = "prompts/get";
    public const string ResourcesList = "resources/list";
    public const string ResourcesRead = "resources/read";
    public const string ResourcesSubscribe = "resources/subscribe";

    // Client feature events
    public const string SamplingCreateMessage = "sampling/createMessage";
    public const string ElicitationCreate = "elicitation/create";
    public const string RootsList = "roots/list";

    // LLM interaction events
    public const string LlmCompletion = "llm/completion";

    /// <summary>Matches all lifecycle events on the phase specified by the enclosing hook entry.</summary>
    public const string All = "*";
}
