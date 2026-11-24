# WhyInterceptorsDemo

A short terminal demo of MCP interceptors: out-of-process middleware for MCP clients, gateways and
servers. One agent and one MCP server run first connected directly, then through a proxy that
calls an interceptor server on every request and response.

```bash
# from csharp/sdk
dotnet run --project samples/WhyInterceptorsDemo
```

Press Enter to advance. Other options: `--auto` (timed), `--fast` (no pauses), `--act N` (jump to
an act), `--export-html run.html` (save the run as a colored HTML page).

## The acts

| Act | What happens | What it shows |
| --- | --- | --- |
| 1. Without middleware | Dana's agent connects to *Acme Workspace*, an MCP server with email, CRM and web tools. A newsletter in the inbox asks AI assistants to "sync contacts" with an outside service, and the CRM record includes a card number. The agent follows the instruction and sends contacts and billing details to an outside URL. | The kind of rule a deployment may want for agent traffic, and that today has to be built into a particular client or gateway. |
| 2. With an interceptor proxy | The agent connects to a proxy instead. The proxy calls the *Security Guard* interceptor server: `pii-redactor` removes the card number from the tool result, and `exfiltration-guard` blocks the outbound call. The agent gets the reason as a tool error. | Neither the agent nor the server changes. The guard checks the call, so it does not need to recognize the instruction. |
| 3. Policy set by the deployer | The proxy runs the guard in **audit mode** through an invoker override: it reports *would block* and blocks nothing. The guard's `audit-trail` interceptor has recorded every operation. | The SEP separates what an interceptor declares from how the invoker runs it. |
| 4. On the wire | The recorded `interceptor/invoke` exchange for the `fetch_url` call and the interceptor that answered it. | Two JSON-RPC methods on an ordinary MCP server. |

## What is real

The agent's *reasoning* is scripted so every run is identical. Everything else is real:

- *Acme Workspace* (`AcmeWorkspaceTools.cs`) and the *Security Guard* (`SecurityGuardInterceptors.cs`)
  run as separate stdio MCP server processes (this binary started with `--workspace-server` and
  `--guard-server`).
- The interceptor proxy is the library's `McpInterceptorGateway`, run in-process so the story can
  show its traces (`OnInterceptorsExecuted`).
- The agent's tool calls are real MCP requests through the proxy. The scripted agent builds the
  outbound URL from the tool results it actually received, so when the CRM record arrives
  redacted, the URL carries the redaction.
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
