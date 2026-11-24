using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace WhyInterceptorsDemo;

/// <summary>Spectre.Console building blocks for the story.</summary>
internal static partial class Render
{
    public static void Act(int number, string title, string subtitle)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold cornflowerblue]Act {number}[/] [bold]{Markup.Escape(title)}[/]").LeftJustified().RuleStyle("grey35"));
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(subtitle)}[/]");
        AnsiConsole.WriteLine();
    }

    public static void Narrate(string markup) => AnsiConsole.MarkupLine($"  {markup}");

    public static void Takeaway(string markup)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(markup)).BorderColor(Color.CornflowerBlue).Padding(2, 1).Expand());
    }

    public static void Diagram(string markup)
    {
        AnsiConsole.Write(new Panel(new Markup(markup)).Border(BoxBorder.None).Padding(4, 0));
        AnsiConsole.WriteLine();
    }

    public static async Task AgentSays(Pace pace, string text)
    {
        var quoted = $"“{text}”";
        if (quoted.Length + 11 < AnsiConsole.Profile.Width)
        {
            AnsiConsole.Markup("  [bold deepskyblue1]Agent[/]  ");
            await pace.Type(quoted, "italic grey85");
            AnsiConsole.WriteLine();
            return;
        }

        // Too long for one line: render as a block so wrapped lines stay indented.
        await pace.Breath(400);
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(2)).AddColumn();
        grid.AddRow(new Markup("[bold deepskyblue1]Agent[/]"), new Markup($"[italic grey85]{Markup.Escape(quoted)}[/]"));
        AnsiConsole.Write(grid);
    }

    public static void ToolCall(string tool, string arguments) =>
        AnsiConsole.MarkupLine($"         [grey]→ tools/call[/] [bold]{Markup.Escape(tool)}[/][grey]({Markup.Escape(arguments)})[/]");

    /// <summary>A tool error result, as the agent receives it.</summary>
    public static void ToolError(CallToolResult result) =>
        AnsiConsole.Write(new Padder(new Panel(new Markup($"[red]{Markup.Escape(Text(result))}[/]"))
            .Header("[bold red] what the agent got back (isError: true) [/]").BorderColor(Color.Red).Padding(1, 0)).Padding(9, 0, 0, 1));

    public static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";

    /// <summary>The inbox, with the injected instruction called out.</summary>
    public static void Inbox(string inboxJson)
    {
        var rows = new List<IRenderable>();
        foreach (var email in JsonNode.Parse(inboxJson)!.AsArray())
        {
            var from = email!["from"]!.GetValue<string>();
            var subject = email["subject"]!.GetValue<string>();
            var body = email["body"]!.GetValue<string>();
            var injection = body.IndexOf("Note for AI assistants", StringComparison.Ordinal);

            if (injection < 0)
            {
                rows.Add(new Markup($"[grey]{Markup.Escape(from)}[/]  [bold]{Markup.Escape(subject)}[/]  [grey]{Markup.Escape(Truncate(body, 44))}[/]"));
                continue;
            }

            rows.Add(new Panel(new Markup(
                    $"[grey]{Markup.Escape(from)}[/]  [bold]{Markup.Escape(subject)}[/]\n" +
                    $"[grey]{Markup.Escape(body[..injection].Trim())}[/]\n\n" +
                    $"[bold red]{Markup.Escape(body[injection..])}[/]"))
                .Header("[bold red] newsletter with an instruction for AI assistants [/]")
                .BorderColor(Color.Red)
                .Padding(1, 0));
        }

        AnsiConsole.Write(new Padder(new Rows(rows)).Padding(9, 0, 0, 1));
    }

    /// <summary>A CRM record, with sensitive or redacted values highlighted.</summary>
    public static void Record(string text, string header, Color border)
    {
        var markup = Markup.Escape(text.Trim());
        markup = SensitivePattern().Replace(markup, m => $"[bold yellow on grey15]{m.Value}[/]");
        markup = markup.Replace("[[REDACTED:card]]", "[bold green][[REDACTED:card]][/]")
                       .Replace("[[REDACTED:tax-id]]", "[bold green][[REDACTED:tax-id]][/]");
        AnsiConsole.Write(new Padder(new Panel(new Markup(markup)).Header(header).BorderColor(border).Padding(1, 0)).Padding(9, 0, 0, 1));
    }

    /// <summary>What the external service received, read from the URL the agent fetched.</summary>
    public static void ReceivedBy(string? leakedUrl)
    {
        IRenderable body;
        Color border;
        if (leakedUrl is null)
        {
            body = new Markup("[bold green]Nothing.[/] [grey]The request never left the proxy.[/]");
            border = Color.Green;
        }
        else
        {
            var query = new Uri(leakedUrl).Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv.ElementAtOrDefault(1) ?? ""));
            var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("").AddColumn("");
            foreach (var (key, value) in query)
            {
                var shown = Markup.Escape(value).Replace("[[REDACTED:card]]", "[green][[REDACTED:card]][/]").Replace("[[REDACTED:tax-id]]", "[green][[REDACTED:tax-id]][/]");
                table.AddRow($"[grey]{Markup.Escape(key)}[/]", $"[bold red]{shown}[/]");
            }

            body = table;
            border = Color.Red;
        }

        AnsiConsole.Write(new Padder(new Panel(body)
            .Header($"[bold] what {AcmeWorkspaceTools.ExternalDomain} received [/]")
            .BorderColor(border)
            .Padding(1, 0)).Padding(9, 0, 0, 1));
    }

    /// <summary>
    /// The interceptor checkpoint for one tool call: every invocation, in order, across both phases.
    /// </summary>
    public static void Checkpoint(IReadOnlyList<InterceptorExecutionResult> traces)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .AddColumn(new TableColumn("[grey]phase[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]interceptor[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]effect[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]detail[/]"));
        table.Columns[1].NoWrap();

        var count = 0;
        foreach (var trace in traces)
        {
            var phase = trace.Phase == InterceptorPhase.Request ? "→ request" : "← response";
            foreach (var invocation in trace.Invocations)
            {
                count++;
                var (label, detail) = Describe(invocation);
                table.AddRow(new Markup($"[grey]{phase}[/]"), new Markup($"[bold]{Markup.Escape(invocation.Interceptor)}[/]"), new Markup(label), new Markup(detail));
            }
        }

        var totalMs = traces.Sum(t => t.TotalDurationMs);
        table.Title($"[grey]interceptor checkpoint · {count} invocation(s) on the Security Guard · {totalMs} ms[/]");
        AnsiConsole.Write(new Padder(table).Padding(9, 0, 0, 1));
    }

    public static (string Label, string Detail) Describe(InterceptorInvocation invocation)
    {
        var validation = invocation.Result as ValidationInterceptorResult;
        var reason = Markup.Escape(validation?.Messages?.FirstOrDefault()?.Message ?? invocation.Error ?? "");
        var policy = invocation.Mode == InterceptorMode.Audit
            ? $" [grey](audit · {invocation.ModeSource.ToString().ToLowerInvariant()})[/]"
            : "";

        return invocation.Effect switch
        {
            InterceptorEffect.Blocked => ("[bold red]✗ BLOCKED[/]", $"[red]{reason}[/]"),
            InterceptorEffect.Modified => ("[bold yellow]✎ modified[/]", $"[yellow]{Markup.Escape(InfoSummary(invocation.Result))}[/]"),
            InterceptorEffect.WouldBlock => ("[bold orange1]◌ would block[/]", $"[orange1]{reason}[/]{policy}"),
            InterceptorEffect.ShadowModified => ("[bold orange1]◌ would modify[/]", policy),
            InterceptorEffect.FailedOpen => ("[grey]↷ failed open[/]", $"[grey]{reason}[/]"),
            _ when invocation.Type == InterceptorType.Sink => ("[blue]· dispatched[/]", ""),
            _ when invocation.Type == InterceptorType.Mutation => ("[green]✓ no change[/]", ""),
            _ when validation?.Info?.ContainsKey("recorded") == true => ("[green]✓ recorded[/]", $"[grey]never blocks[/]{policy}"),
            _ => ("[green]✓ passed[/]", policy),
        };
    }

    public static string InfoSummary(InterceptorResult? result) =>
        result?.Info is { Count: > 0 } info
            ? string.Join(", ", info.Select(kv => $"{kv.Key}: {(kv.Value is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : kv.Value)}"))
            : "";

    public static void Json(string header, JsonNode? node, int maxStringLength = 72)
    {
        var json = Shorten(node?.DeepClone(), maxStringLength)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
        AnsiConsole.Write(new Padder(new Panel(new Text(json, new Style(Color.Grey85))).Header(header).BorderColor(Color.Grey35).Padding(1, 0)).Padding(4, 0, 0, 0));
    }

    public static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";

    /// <summary>Truncates long strings and drops protocol <c>_meta</c>, in place, for display.</summary>
    private static JsonNode? Shorten(JsonNode? node, int max)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("_meta");
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        obj[key] = Truncate(value.GetValue<string>().ReplaceLineEndings(" "), max);
                    }
                    else
                    {
                        Shorten(obj[key], max);
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        array[i] = Truncate(value.GetValue<string>().ReplaceLineEndings(" "), max);
                    }
                    else
                    {
                        Shorten(array[i], max);
                    }
                }

                return array;
            default:
                return node;
        }
    }

    [GeneratedRegex(@"\b(?:\d{4} ){3}\d{4}\b|\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SensitivePattern();
}
