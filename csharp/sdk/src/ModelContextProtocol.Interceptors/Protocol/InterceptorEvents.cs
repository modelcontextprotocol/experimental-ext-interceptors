namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Defines the interceptable event names from the MCP interceptors extension.
/// </summary>
public static class InterceptorEvents
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

    // Wildcard patterns
    public const string AllRequests = "*/request";
    public const string AllResponses = "*/response";
    public const string All = "*";
}
