using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;

namespace AvatarMoodInterceptorSample;

/// <summary>
/// A sink interceptor that classifies conversational mood from each
/// <c>llm/completion</c> response using a smaller, secondary model (Haiku) and
/// updates a console avatar. Demonstrates that sink interceptors can do
/// real work — driving downstream state — without gating the main conversation.
/// </summary>
[McpServerInterceptorType]
public sealed class AvatarInterceptors
{
    private readonly IChatClient _haiku;
    private readonly string _haikuModelId;
    private readonly AvatarState _state;

    public AvatarInterceptors(IChatClient haiku, string haikuModelId, AvatarState state)
    {
        _haiku = haiku;
        _haikuModelId = haikuModelId;
        _state = state;
    }

    [McpServerInterceptor(
        Name = "avatar-mood",
        Description = "Classifies conversation mood via a secondary model and updates the avatar state.",
        Type = InterceptorType.Sink,
        Events = [InterceptionEvents.LlmCompletion],
        Phase = InterceptorPhase.Response)]
    public SinkInterceptorResult OnLlmCompletion(
        JsonNode payload,
        string @event,
        InterceptorPhase phase,
        InvokeInterceptorContext? context,
        CancellationToken cancellationToken)
    {
        // Sink contract: parallel, fire-and-forget, failures swallowed.
        // We return immediately so the main conversation never waits on Haiku.
        _ = Task.Run(async () =>
        {
            var verdict = await MoodClassifier.ClassifyAsync(_haiku, payload, cancellationToken);
            _state.Update(verdict.Label, verdict.Confidence, _haikuModelId);
            AvatarRenderer.Render(_state);
        }, cancellationToken);

        return new SinkInterceptorResult { Recorded = true };
    }
}
