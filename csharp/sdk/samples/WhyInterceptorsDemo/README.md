# Why Interceptors?

A three-minute terminal demo for people who have not met MCP interceptors yet. It answers one
question: *why does MCP need this at all?*

```bash
# from csharp/sdk
dotnet run --project samples/WhyInterceptorsDemo
```

Press Enter to advance. Other options: `--auto` (timed), `--fast` (no pauses), `--act N` (jump to
an act), `--export-html run.html` (save the run as a colored HTML page).

## The story

| Act | What happens | The point |
| --- | --- | --- |
| 1. An ordinary request | Dana's agent talks straight to *Acme Workspace*, a third-party MCP server. A newsletter in the inbox tells AI assistants to "sync contacts" with an outside service; the CRM returns a card number the task never needed. The agent leaks contacts and billing data. | Nobody wrote a bug. The checks have to live *between* the agent and the server, and today every client and gateway invents its own. |
| 2. Same agent, same server, one config change | The agent connects to an interceptor proxy instead. The proxy calls the *Security Guard* interceptor server on every request and response: `pii-redactor` keeps the card number out of the model's context, and `exfiltration-guard` blocks the outbound call. The agent gets the reason as a tool error and tells Dana. | The guard checks the *action*, so it does not need to detect the injection. Neither the client nor the server changed. |
| 3. Policy belongs to whoever deploys it | The platform team runs the guard in **audit mode** through an invoker override: it reports *would block* and blocks nothing. The guard's `audit-trail` interceptor recorded every operation, including the attempt Act 2 blocked. | The SEP separates what an interceptor *can* do (declared by its author) from how it *runs* (decided by the deployer). |
| 4. Under the hood | The recorded `interceptor/invoke` exchange for the blocked call, the ten-line interceptor behind it, and the M × N → M + N argument. | Two JSON-RPC methods and an ordinary MCP server. |

## What is real

The agent's *reasoning* is scripted so every run is identical. Everything else is real:

- *Acme Workspace* (`AcmeWorkspaceTools.cs`) and the *Security Guard* (`SecurityGuardInterceptors.cs`)
  run as separate stdio MCP server processes (this binary started with `--workspace-server` and
  `--guard-server`).
- The interceptor proxy is the library's `McpInterceptorGateway`, run in-process so the story can
  show its traces (`OnInterceptorsExecuted`).
- The agent's tool calls are real MCP requests through the proxy. The scripted agent builds its
  exfiltration URL from the tool results it actually received, so when the CRM record arrives
  redacted, the leak attempt carries the redaction.
- Act 4 shows the JSON-RPC messages recorded off the guard's transport, not a reconstruction.

## Reading the code

Start with [`SecurityGuardInterceptors.cs`](SecurityGuardInterceptors.cs): three interceptors, one
of each kind the SEP describes.

| Interceptor | Type | Hooks | Notes |
| --- | --- | --- | --- |
| `exfiltration-guard` | validation | `tools/call` request | Blocks URLs in tool arguments whose host is not allowlisted. The allowlist is invoker `config` (declared in `configSchema`). |
| `pii-redactor` | mutation | `tools/call` response | Redacts card numbers and tax IDs. `priorityHint: -50000`, the SEP's range for security sanitization. |
| `audit-trail` | validation, `mode: audit`, `failOpen: true` | `*` request and response | The SEP's audit-logger pattern. Logs metadata only, so the audit trail is not a second copy of the PII. |

To try the same servers with a real agent, see [InterceptorProxy](../InterceptorProxy).
