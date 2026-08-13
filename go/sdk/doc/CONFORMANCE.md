# SEP Conformance

Status of this Go SDK implementation against the SEP-2624 interceptor proposal.

## Implemented

| Area | Notes |
|------|-------|
| Validation interceptors | Parallel execution, severity-based blocking, fail-open support |
| Mutation interceptors | Sequential execution, priority ordering, payload threading via `json.RawMessage` |
| Interceptor metadata | Name, version, description, events, phase, priorityHint (polymorphic JSON), compat, configSchema, mode, failOpen |
| Event names | Constants for common server-side MCP methods; other JSON-RPC method names can be used directly as event names |
| Protocol methods | `interceptors/list` for discovery, `interceptor/invoke` for invocation — both registered as custom JSON-RPC methods |
| MCP Go SDK integration | Uses `github.com/modelcontextprotocol/go-sdk` custom method APIs (`AddReceivingCustomMethod`, `AddSendingCustomMethod`, `CallCustomMethod`, `ParamsBase`, `ResultBase`) from v1.7.0; no vendored SDK patch is required |
| `InterceptorChain` (SEP) | `chain.Chain` type with `chain.ChainEntry` objects pairing interceptor descriptors with MCP client sessions |
| `ChainEntry` (SEP) | `chain.ChainEntry` struct holds `InterceptorInfo` + `*mcp.ClientSession` |
| Chain discovery | `chain.Chain.AddMCPServer()` calls `interceptors/list` to discover interceptors from an MCP server |
| Chain execution | `chain.Chain.Execute()` invokes interceptors via `interceptor/invoke` on the appropriate server per entry |
| `InvokeResult` envelope | Per-interceptor result with interceptor name, type, phase, duration, validation/mutation result, mutated payload |
| `chain.ExecutionResult` | Aggregated chain result with status, results, finalPayload, validationSummary, abortedAt, totalDurationMs |
| JSON-RPC error mapping | Typed error data structs: -32602 for validation, -32603 for mutation, -32000 for timeout |
| Trust-boundary execution order | Request: validate (parallel) then mutate (sequential); Response: mutate (sequential) then validate (parallel) |
| Priority ordering | Mutators sorted by `priorityHint.Resolve(phase)` ascending, alphabetical tiebreak |
| Fail-open behavior | `FailOpen: true` interceptors log errors without aborting the chain |
| Audit mode | `ModeAudit` records results without blocking; mutated payloads not propagated |
| Timeout & context | Per-interceptor timeouts via `InvokeParams.TimeoutMs`, chain-level context cancellation, `InvocationContext` with principal/traceId |
| Receiving direction (client → server) | Server-side messages routed through `AddReceivingMiddleware` are intercepted, except skipped lifecycle and interceptor methods |
| Capability declaration | Interceptor metadata injected into `initialize` response via `Capabilities.Extensions` |
| First-party (in-process) deployment | `extension.Extension.LocalChain()` creates an in-memory transport `chain.Chain`; interceptors invoked via JSON-RPC even in-process |
| Third-party and hybrid deployment | Chain entries can point to any `*mcp.ClientSession` — local (in-memory), stdio, or HTTP transport |
| `json.RawMessage` payloads | Interceptor handlers receive `json.RawMessage` via `interceptor/invoke`, matching the SEP's JSON-level payload model |

## Not Implemented

| Area | SEP expects | Notes |
|------|-------------|-------|
| Wildcard event matching | `"*"` MUST match all lifecycle events; namespace wildcards such as `"tools/*"` MAY be supported | `matchesHooks` does exact event matching only today |
| Sending-side interception | Sending flow uses `Mutate → Validate → Send` for client→server and server→client boundaries | Only the server receiving middleware integration exists. `go-sdk` v1.7.0 now provides `Client.AddSendingMiddleware` and `Server.AddSendingMiddleware`, so this is a local implementation gap rather than an upstream SDK blocker |
| Client feature events | `"sampling/createMessage"`, `"elicitation/create"`, `"roots/list"` | No client-side middleware integration yet; callers may still use raw method-name strings as events |
| Per-interceptor config source and validation | `ChainExecutionParams.Config` overrides and `configSchema` validation | `ExecutionParams.Config` is passed through to `InvokeParams.Config`, but middleware does not load config and `configSchema` is not validated |
| Remote interceptor server helpers | Convenient setup for stdio/HTTP interceptor servers | Infrastructure is ready (`chain.Chain.AddMCPServer` accepts any `*mcp.ClientSession`); no config-driven or transport-specific helper APIs yet |
