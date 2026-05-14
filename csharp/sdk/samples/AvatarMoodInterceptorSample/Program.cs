// ──────────────────────────────────────────────────────────────────────
// Avatar Mood Interceptor Sample
//
// Demonstrates sink interceptors as PARALLEL CONTEXT-ANALYSIS
// PIPELINES — not the usual "guardrail" framing (PII, validation, audit).
//
// The primary conversation runs against Claude Sonnet. After each reply,
// we fire an `llm/completion` response-phase event into the avatar-mood
// sink interceptor. The interceptor calls a smaller, faster
// model (Haiku) in the background to classify the user's mood, then
// updates a console avatar. The main conversation is never blocked:
// the sink contract is fire-and-forget with exceptions swallowed.
//
// For clarity, this sample runs the driver and the interceptor in the
// same process, invoking the interceptor method directly. In a real
// deployment the interceptor would be hosted by an MCP interceptor
// server (see InterceptorServerSample) and dispatched by a gateway
// (see TransparentProxySample / ConfigDrivenGatewaySample) — the
// sink semantics are identical either way. We skip that
// plumbing here to keep the pattern center-stage.
// ──────────────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using AvatarMoodInterceptorSample;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Interceptors.Protocol;

const string SonnetModelId = "claude-sonnet-4-6";
const string HaikuModelId = "claude-haiku-4-5-20251001";

var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true)
    .Build();

var apiKey = config["ANTHROPIC_API_KEY"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("error: set ANTHROPIC_API_KEY (env var or user secret).");
    return 1;
}

using var sonnet = new AnthropicClient(new() { ApiKey = apiKey })
    .AsIChatClient(SonnetModelId)
    .AsBuilder()
    .Build();

using var haiku = new AnthropicClient(new() { ApiKey = apiKey })
    .AsIChatClient(HaikuModelId)
    .AsBuilder()
    .Build();

var avatarState = new AvatarState();
var interceptors = new AvatarInterceptors(haiku, HaikuModelId, avatarState);

AvatarRenderer.Initialize();
Console.CancelKeyPress += (_, e) =>
{
    AvatarRenderer.Reset();
    // let default handler terminate the process
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => AvatarRenderer.Reset();

AvatarRenderer.Render(avatarState);

Console.WriteLine("=== Avatar Mood Interceptor Sample ===");
Console.WriteLine($"primary: {SonnetModelId}    mood classifier: {HaikuModelId}");
Console.WriteLine("type a message and press enter. 'exit' to quit.");
Console.WriteLine();

var history = new List<ChatMessage>();
var sonnetOptions = new ChatOptions { MaxOutputTokens = 1024 };

while (true)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("> ");
    Console.ResetColor();

    var input = Console.ReadLine();
    if (input is null) break;
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase)) break;

    history.Add(new ChatMessage(ChatRole.User, input));

    ChatResponse response;
    try
    {
        response = await sonnet.GetResponseAsync(history, sonnetOptions);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[sonnet] error: {ex.Message}");
        history.RemoveAt(history.Count - 1);
        continue;
    }

    var replyText = response.Text ?? string.Empty;
    history.Add(new ChatMessage(ChatRole.Assistant, replyText));

    Console.WriteLine(replyText);

    // Synthesize an llm/completion response-phase payload and dispatch it
    // to the sink interceptor. The method returns immediately;
    // the Haiku call and avatar update happen on a background task.
    var payload = BuildCompletionPayload(SonnetModelId, input, replyText, response);
    _ = interceptors.OnLlmCompletion(
        payload,
        InterceptionEvents.LlmCompletion,
        InterceptorPhase.Response,
        new InvokeInterceptorContext { TraceId = Guid.NewGuid().ToString("N") },
        cancellationToken: default);
}

AvatarRenderer.Reset();
return 0;

static JsonNode BuildCompletionPayload(string modelId, string userInput, string assistantReply, ChatResponse response)
{
    var recent = new JsonArray
    {
        new JsonObject { ["role"] = "user", ["content"] = userInput },
    };

    var payload = new LlmCompletionResponsePayload
    {
        Model = modelId,
        Message = new LlmMessage { Role = "assistant", Content = assistantReply },
        StopReason = response.FinishReason?.ToString(),
        Usage = response.Usage is { } u
            ? new LlmUsage
            {
                InputTokens = (int?)u.InputTokenCount,
                OutputTokens = (int?)u.OutputTokenCount,
            }
            : null,
        Metadata = new JsonObject { ["recentMessages"] = recent },
    };

    return JsonSerializer.SerializeToNode(payload)!;
}
