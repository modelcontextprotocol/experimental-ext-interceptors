using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Interceptors.Protocol;

namespace AvatarMoodInterceptorSample;

public sealed record MoodVerdict(Mood Label, float Confidence);

public static class MoodClassifier
{
    private const string SystemPrompt =
        "You classify the dominant mood the USER is expressing in a short conversation. " +
        "Respond with ONLY a single JSON object and nothing else, matching this shape: " +
        "{\"mood\":\"neutral|happy|curious|focused|confused|frustrated\",\"confidence\":0.0-1.0}. " +
        "Pick exactly one mood. No prose, no code fences.";

    public static async Task<MoodVerdict> ClassifyAsync(
        IChatClient haiku,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        var transcript = BuildTranscript(payload);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new MoodVerdict(Mood.Neutral, 0f);
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, transcript),
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = 64,
            Temperature = 0f,
        };

        ChatResponse response;
        try
        {
            response = await haiku.GetResponseAsync(messages, options, cancellationToken);
        }
        catch
        {
            return new MoodVerdict(Mood.Neutral, 0f);
        }

        return Parse(response.Text);
    }

    private static string BuildTranscript(JsonNode payload)
    {
        // Response-phase payload has `message` (assistant reply) plus `metadata`.
        // We also accept the companion request payload's `messages` array when metadata carries it.
        // Keep the classifier input tight: last user turn + assistant reply is usually enough.
        var node = payload.AsObject();

        string? assistantText = null;
        if (node.TryGetPropertyValue("message", out var msgNode) && msgNode is JsonObject msgObj)
        {
            assistantText = msgObj["content"]?.GetValue<string>();
        }

        var lines = new List<string>();
        if (node.TryGetPropertyValue("metadata", out var metaNode)
            && metaNode is JsonObject meta
            && meta.TryGetPropertyValue("recentMessages", out var recent)
            && recent is JsonArray recentArr)
        {
            foreach (var item in recentArr)
            {
                if (item is JsonObject m)
                {
                    var role = m["role"]?.GetValue<string>() ?? "user";
                    var content = m["content"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        lines.Add($"{role}: {content}");
                    }
                }
            }
        }

        if (assistantText is not null)
        {
            lines.Add($"assistant: {assistantText}");
        }

        return string.Join('\n', lines);
    }

    private static MoodVerdict Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new MoodVerdict(Mood.Neutral, 0f);

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return new MoodVerdict(Mood.Neutral, 0f);
        var json = text.Substring(start, end - start + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var mood = root.TryGetProperty("mood", out var m) ? m.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? (float)c.GetDouble()
                : 0f;

            return new MoodVerdict(ParseMood(mood), Math.Clamp(confidence, 0f, 1f));
        }
        catch
        {
            return new MoodVerdict(Mood.Neutral, 0f);
        }
    }

    private static Mood ParseMood(string? s) => s?.ToLowerInvariant() switch
    {
        "happy" => Mood.Happy,
        "curious" => Mood.Curious,
        "focused" => Mood.Focused,
        "confused" => Mood.Confused,
        "frustrated" => Mood.Frustrated,
        _ => Mood.Neutral,
    };
}
