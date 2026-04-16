# SEP Conformance

Status of this Go SDK implementation against the
[SEP-1763](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763)
interceptor proposal.

## Implemented

| Area | Notes |
|------|-------|
| Validation interceptors | Parallel execution, severity-based blocking, fail-open support |
| Mutation interceptors | Sequential execution, priority ordering, atomic payload updates |
| Interceptor metadata | Name, version, description, events, phase, priorityHint (polymorphic JSON), compat, configSchema, mode, failOpen, timeout |
| Event names | Constants for all standard server-side MCP methods; JSON-RPC method names used directly as event names |
| Unified result envelope | ValidationResult, MutationResult, ExecutionResult with base envelope fields |
| Chain result | ChainResult with status, results, finalPayload, validationSummary, abortedAt |
| JSON-RPC error mapping | Typed error data structs: -32602 for validation, -32603 for mutation, -32000 for timeout |
| Trust-boundary execution order | Receiving: validate (parallel) then mutate (sequential); Sending: mutate (sequential) then validate (parallel) |
| Priority ordering | Mutators sorted by `priorityHint.Resolve(phase)` ascending, alphabetical tiebreak |
| Fail-open behavior | `FailOpen: true` interceptors log errors without aborting the chain |
| Audit mode | `ModeAudit` records results without blocking or applying mutations |
| Timeout & context | Per-interceptor timeouts, chain-level context cancellation, `InvocationContext` with principal/traceId via `mcpserver.WithContextProvider` |
| Receiving direction (client → server) | All server-side method calls intercepted via `AddReceivingMiddleware` |
| Capability declaration | Interceptor metadata injected into `initialize` response via `Capabilities.Experimental` |
| First-party (in-process) deployment | Interceptors run as Go functions within the server process |
| Third-party and hybrid deployment | Handlers can call remote services; local and remote interceptors can be mixed freely. No built-in remote interceptor abstraction yet |

## Not Implemented

| Area | SEP expects | Notes |
|------|-------------|-------|
| Wildcard event matching | `type InterceptorEvent = ... \| "*/request" \| "*/response" \| "*"` | `matchesEvent` does exact match only; wildcard patterns are planned |
| Protocol methods | `interceptors/list`, `interceptor/invoke`, `interceptor/executeChain` | Requires upstream go-sdk changes to register custom JSON-RPC methods |
| Server → client interception | Client features as interceptable events: `"sampling/createMessage"`, `"elicitation/create"`, `"roots/list"` | Requires a `sendingMiddleware` installed via `Server.AddSendingMiddleware`. Outgoing requests run mutate → validate, incoming responses run validate → mutate (same `executeForSending`/`executeForReceiving` methods). Interceptors match by event name (no API changes needed for registration) |
