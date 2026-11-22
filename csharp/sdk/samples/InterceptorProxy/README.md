# InterceptorProxy

Put interceptors in front of an MCP server without changing the server or the client.

InterceptorProxy is a stdio MCP server that looks exactly like the server it wraps. Every request
and response passes through the interceptors hosted on one or more *interceptor servers* before it
is forwarded. This is the "common interceptor sidecar" described in SEP-2624's reference
implementation section, built on the library's `McpInterceptorGateway`.

```
Claude Code ── MCP ──▶ InterceptorProxy ── MCP ──▶ the server you already use
                          │ interceptors/list, interceptor/invoke
                          ▼
                    interceptor server(s)
```

## Try it

From `csharp/sdk`:

```bash
dotnet build
cd samples/InterceptorProxy

# Connect everything, validate the policy, describe what will run, and exit.
dotnet bin/Debug/net9.0/InterceptorProxy.dll demo.json --check
```

`demo.json` wraps the *Acme Workspace* server from [WhyInterceptorsDemo](../WhyInterceptorsDemo)
(email, CRM and web tools, one of which serves a prompt injection) with its *Security Guard*
interceptor server (`exfiltration-guard`, `pii-redactor`, `audit-trail`).

### In Claude Code

```bash
claude mcp add acme-workspace -- dotnet "$PWD/bin/Debug/net9.0/InterceptorProxy.dll" "$PWD/demo.json"
```

Then ask Claude to *"catch me up on my inbox and look up the Contoso account"*. The CRM record
reaches Claude with the card number redacted. If Claude follows the newsletter's instruction and
calls `fetch_url`, the call comes back as a tool error that says why it was blocked. Whether a
given model follows the instruction varies; the allowlist applies either way.

### In Claude Desktop or VS Code

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "acme-workspace": {
      "command": "dotnet",
      "args": ["/abs/path/to/InterceptorProxy.dll", "/abs/path/to/demo.json"]
    }
  }
}
```

VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "acme-workspace": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["/abs/path/to/InterceptorProxy.dll", "/abs/path/to/demo.json"]
    }
  }
}
```

### In front of an existing server

`filesystem.json` puts the `pii-redactor` in front of the reference filesystem server
(TypeScript, from npm), so card numbers and tax IDs in files never reach the model:

```bash
FS_ROOT=~/Documents dotnet bin/Debug/net9.0/InterceptorProxy.dll filesystem.json --check
```

That run mixes a TypeScript server, a C# proxy and a C# interceptor server. The proxy has also
been driven by the MCP Inspector CLI (a TypeScript client):

```bash
npx @modelcontextprotocol/inspector --cli dotnet bin/Debug/net9.0/InterceptorProxy.dll demo.json \
  --method tools/call --tool-name lookup_customer --tool-arg name=Contoso
```

## What you see

The proxy writes one line per intercepted phase to stderr (MCP clients keep server stderr in their
logs):

```
[interceptors] tools/call request  validationfailed audit-trail=ok exfiltration-guard=BLOCKED(enrich-profiles.example is not an approved destination…) (21 ms)
[interceptors] tools/call response success          audit-trail=ok pii-redactor=modified (19 ms)
```

With `auditLog` set, it also appends a JSON record per phase: every invocation with its resolved
`mode` and `failOpen` and whether each came from an override or the interceptor's declared default
(SEP Security Best Practice 3). Payloads are not logged.

A blocked `tools/call` is returned to the client as a tool result with `isError: true`, a readable
reason, and the SEP's `validationErrors` under `_meta["io.modelcontextprotocol/interceptors"]`.
Set `"toolCallRejection": "protocolError"` to get the SEP's JSON-RPC error
(`-32602 Interceptor validation failed`) instead. Other methods always use the JSON-RPC error.

## Configuration

The format belongs to this sample, not to the library. JSON with comments; `${VAR}` in a string value
expands to an environment variable; relative paths and `cwd` resolve against the config file's directory.

```jsonc
{
  "name": "acme-workspace",                 // optional; defaults to the backend's server name
  "backend": { "command": "...", "args": [], "env": {}, "cwd": "." },
  //   or    { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ${TOKEN}" } }

  "interceptorServers": {                    // name -> connection, same shape as "backend"
    "security-guard": { "command": "...", "args": [] }
  },

  "invokerRole": "client",                   // "client": protect the agent from the server (default)
                                             // "server": protect a server you own from its clients
  "events": ["tools/call"],                  // optional: only intercept these events
  "timeoutMs": 15000,                        // optional: aggregate timeout per phase
  "toolCallRejection": "errorResult",        // or "protocolError"
  "auditLog": "proxy-audit.jsonl",           // optional

  "policy": {
    "include": ["exfiltration-guard"],       // optional; fail closed if any is not discovered
    "defaultTimeoutMs": 5000,                // per interceptor, unless overridden
    "interceptors": {
      "exfiltration-guard": {                // SEP-2624 InterceptorOverrides, plus config
        "mode": "audit",                     // "active" | "audit"
        "failOpen": false,
        "timeoutMs": 2000,
        "priorityHint": -1000,               // or { "request": -1000, "response": 1000 }
        "hooks": [{ "events": ["tools/call"], "phase": "request" }],  // may only narrow
        "config": { "allowedDomains": ["acme.example"] }               // sent on every invoke
      }
    }
  }
}
```

The policy section is the SEP's split between *capability* and *policy*. The interceptor server
declares what an interceptor can do and its defaults; whoever deploys the proxy decides how it
runs. `--check` binds the policy against the discovered interceptors, so a missing required
interceptor or a hook override that widens declared hooks is reported before any client connects.

## Limits

- The proxy speaks stdio to its client. To serve over Streamable HTTP, host `McpInterceptorGateway`
  with the SDK's ASP.NET Core transport (`AddMcpServer().WithHttpTransport()`, `MapMcp()`).
- One backend connection is shared by all requests. Server-to-client requests from the backend
  (sampling, elicitation, roots) are not forwarded.
