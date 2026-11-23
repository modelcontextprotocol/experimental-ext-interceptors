using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Interceptors;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;

namespace WhyInterceptorsDemo;

/// <summary>
/// "Security Guard": the interceptor server a security team writes once. It is an ordinary MCP
/// server that answers <c>interceptors/list</c> and <c>interceptor/invoke</c>. It knows nothing about
/// Acme Workspace or about the agent; any client, gateway or server can call it.
/// </summary>
[McpServerInterceptorType]
public sealed partial class SecurityGuardInterceptors
{
    /// <summary>Where <c>audit-trail</c> appends its records. Set from <c>--audit-log</c>.</summary>
    public static string? AuditLogPath { get; set; }

    private static readonly string[] DefaultAllowedDomains = ["acme.example", "contoso.example"];

    /// <summary>
    /// Validator: blocks any tool call that would send data to a domain outside the allowlist.
    /// The allowlist is invoker configuration, passed as <c>config</c> on each call.
    /// </summary>
    [McpServerInterceptor(
        Name = "exfiltration-guard",
        Version = "1.0.0",
        Description = "Blocks tool calls that would send data to a domain outside the allowlist.",
        Type = InterceptorType.Validation,
        Events = [InterceptionEvents.ToolsCall],
        Phase = InterceptorPhase.Request,
        ConfigSchema = """{"type":"object","properties":{"allowedDomains":{"type":"array","items":{"type":"string"}}}}""")]
    public static ValidationInterceptorResult BlockUnapprovedDomains(JsonNode payload, JsonNode? config)
    {
        var allowed = config?["allowedDomains"]?.AsArray().Select(d => d!.GetValue<string>()).ToArray() ?? DefaultAllowedDomains;

        foreach (var (path, url) in UrlsIn(payload["arguments"], "$.arguments"))
        {
            // Fail closed: a destination the guard cannot read is not one it can approve.
            if (url is null)
            {
                return Blocked(path, "This value contains a URL that could not be parsed, so its destination cannot be approved.");
            }

            // The allowlist is for web destinations. A URL with any other scheme is blocked, not skipped.
            if (url.Scheme is not ("http" or "https"))
            {
                return Blocked(path, $"{url.Scheme} URLs are not allowed; only http and https destinations can be approved.");
            }

            if (allowed.Any(domain => url.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var carried = EmailPattern().Matches(Uri.UnescapeDataString(url.Query)).Count;
            return Blocked(path, $"{url.Host} is not an approved destination" +
                                 (carried > 0 ? $", and the request carries {carried} email address(es)." : "."));
        }

        return ValidationInterceptorResult.Success();
    }

    private static ValidationInterceptorResult Blocked(string path, string message) =>
        ValidationInterceptorResult.Failure(new ValidationMessage { Path = path, Severity = ValidationSeverity.Error, Message = message });

    /// <summary>
    /// Mutator: redacts card numbers and tax IDs from tool results before they reach the model,
    /// and with it the LLM provider's logs.
    /// </summary>
    [McpServerInterceptor(
        Name = "pii-redactor",
        Version = "1.0.0",
        Description = "Redacts card numbers and tax IDs from tool results before the model sees them.",
        Type = InterceptorType.Mutation,
        Events = [InterceptionEvents.ToolsCall],
        Phase = InterceptorPhase.Response,
        PriorityHint = -50_000)] // the SEP's recommended range for security sanitization
    public static MutationInterceptorResult RedactPii(JsonNode payload)
    {
        var redacted = new List<string>();
        var result = Rewrite(payload.DeepClone(), text =>
        {
            text = CardPattern().Replace(text, _ => { redacted.Add("card"); return "[REDACTED:card]"; });
            return TaxIdPattern().Replace(text, _ => { redacted.Add("tax-id"); return "[REDACTED:tax-id]"; });
        });

        return new MutationInterceptorResult
        {
            Modified = redacted.Count > 0,
            Payload = redacted.Count > 0 ? result : payload,
            Info = redacted.Count > 0 ? new Dictionary<string, object> { ["redacted"] = string.Join(", ", redacted) } : null,
        };
    }

    /// <summary>
    /// Audit-mode validator (the SEP's audit-logger pattern): records every request and response,
    /// including the ones another interceptor blocks, and never blocks anything itself.
    /// It logs metadata only, so the audit trail does not become a second copy of the PII.
    /// </summary>
    [McpServerInterceptor(
        Name = "audit-trail",
        Version = "1.0.0",
        Description = "Records every MCP operation for compliance (metadata only). Never blocks.",
        Type = InterceptorType.Validation,
        Events = [InterceptionEvents.All],
        Mode = InterceptorMode.Audit,
        FailOpen = true)]
    public static ValidationInterceptorResult Record(JsonNode payload, string @event, InterceptorPhase phase, InvokeInterceptorContext? context)
    {
        if (AuditLogPath is { } path)
        {
            var entry = new JsonObject
            {
                ["time"] = context?.Timestamp ?? DateTimeOffset.UtcNow.ToString("O"),
                ["user"] = context?.Principal?.Id,
                ["event"] = @event,
                ["phase"] = phase == InterceptorPhase.Request ? "request" : "response",
                ["tool"] = payload["name"]?.GetValue<string>(),
                ["bytes"] = payload.ToJsonString().Length,
            };
            lock (AuditLock)
            {
                File.AppendAllText(path, entry.ToJsonString() + Environment.NewLine);
            }
        }

        return new ValidationInterceptorResult { Valid = true, Info = new Dictionary<string, object> { ["recorded"] = true } };
    }

    private static readonly Lock AuditLock = new();

    /// <summary>
    /// Finds the URLs in every string value under <paramref name="node"/>. A URL-like candidate that does not
    /// parse is returned as <see langword="null"/>, so that the caller blocks it.
    /// </summary>
    private static IEnumerable<(string Path, Uri? Url)> UrlsIn(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    foreach (var found in UrlsIn(value, $"{path}.{key}")) yield return found;
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var found in UrlsIn(array[i], $"{path}[{i}]")) yield return found;
                }
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();

                // A value that is itself a URL is read whole, with the parser fetch_url uses. Reading pieces of it
                // would disagree with that parser: "https://acme.example'@evil.example/" has host evil.example.
                if (Uri.TryCreate(text, UriKind.Absolute, out var whole) && whole.Host.Length > 0)
                {
                    yield return (path, whole);
                }

                // URLs inside longer text, such as a message body. A candidate runs to the next whitespace,
                // without closing punctuation.
                foreach (Match match in UrlPattern().Matches(text))
                {
                    yield return (path, Uri.TryCreate(match.Value.TrimEnd(ClosingPunctuation), UriKind.Absolute, out var url) ? url : null);
                }
                break;
        }
    }

    /// <summary>Applies <paramref name="rewrite"/> to every string value in <paramref name="node"/>, in place.</summary>
    private static JsonNode Rewrite(JsonNode node, Func<string, string> rewrite)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        obj[key] = rewrite(value.GetValue<string>());
                    }
                    else if (obj[key] is { } child)
                    {
                        Rewrite(child, rewrite);
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        array[i] = rewrite(value.GetValue<string>());
                    }
                    else if (array[i] is { } child)
                    {
                        Rewrite(child, rewrite);
                    }
                }
                break;
        }

        return node;
    }

    // Any scheme, in any case, with either slash: HTTPS://..., ftp://... and http:/\... must not slip past the
    // allowlist. A scheme here has at least two characters, so a Windows drive letter is never read as one.
    [GeneratedRegex(@"[a-z][a-z0-9+.-]+:[/\\]{2}\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static readonly char[] ClosingPunctuation = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\''];

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?:\d{4}[ -]?){3}\d{4}\b")]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex TaxIdPattern();
}
