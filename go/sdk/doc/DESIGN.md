# Go SDK Interceptors — Design Document

## Integration Point: Receiving Middleware

The go-sdk processes an incoming JSON-RPC message in this order:

```
Transport (SSE / stdio)
  → JSON-RPC decode
  → Params deserialization (json.RawMessage → typed struct)
  → Receiving middleware chain ← we hook in here
  → Method handler (e.g. tool handler)
  → Result returned through middleware
  → JSON-RPC encode
  → Transport
```

## Capability Declaration

During initialization, the middleware intercepts the `"initialize"` response
and injects interceptor metadata into
`Capabilities.Experimental["io.modelcontextprotocol/interceptors"]`. This
follows the same pattern as the variants extension
(`io.modelcontextprotocol/server-variants`).

The capability payload includes:
- `supportedEvents` — deduplicated list of events with registered interceptors
- `interceptors` — full metadata array in wire format

## Request/Response Lifecycle

When a JSON-RPC request arrives, `receivingMiddleware` in
`mcpserver/server.go` runs the following sequence:

```
0.  If method == "initialize" → enrich result with capability declaration
1.  Assign typed params to Invocation        inv.Payload = req.GetParams()
2.  Run request-phase chain                  (validate → mutate)
3.  If aborted → return error
4.  Params already modified in place — no unmarshal needed
5.  Call next handler                        next(ctx, method, req)
6.  Assign result to Invocation              inv.Payload = result
7.  Run response-phase chain                 (mutate → validate)
8.  If aborted → return error
9.  Result already modified in place — no unmarshal needed
10. Return result
```

The JSON-RPC method name is used directly as the event name (e.g. `"tools/call"`).

### Typed Payload — Zero JSON Operations

`Invocation.Payload` is `any`, the live Go value from the go-sdk
(e.g. `*mcp.CallToolParamsRaw`). Handlers type-assert directly, the
same pattern as gRPC-Go interceptors (`req any`). No JSON marshaling
or unmarshaling occurs in the normal path.

```go
// Validator — type-assert, inspect, return:
params, ok := inv.Payload.(*mcp.CallToolParamsRaw)
if !ok {
    return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
}

// Mutator — type-assert, modify in place, return:
result, ok := inv.Payload.(*mcp.CallToolResult)
if !ok {
    return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
}
result.Content[0] = &mcp.TextContent{Text: "modified"}
return &MutationResult{Modified: true}, nil
```

**Audit mode:** Audit-mode mutators receive a deep-copied payload (via
`Invocation.withCopiedPayload()`) so their in-place modifications don't
affect the real struct. The deep copy uses a JSON round-trip
(`json.Marshal` → `reflect.New` → `json.Unmarshal`). Only audit-mode
mutators pay this cost.

### Limitations

- **Params must round-trip through JSON faithfully** for audit-mode deep
  copy. All go-sdk param and result types use standard `encoding/json`
  tags, so this holds in practice.
- **Type assertions require knowing the concrete type.** Interceptors must
  know which type to expect for a given event (e.g. `*mcp.CallToolParamsRaw`
  for `tools/call` requests). The `Events` field on `Metadata` narrows
  which events reach a handler, so single-event interceptors always see
  the expected type.

---

## What Is and Is Not Intercepted

### Intercepted

All JSON-RPC **method calls** routed through the server's receiving middleware:

| Method | Event |
|--------|-------|
| `tools/call` | `EventToolsCall` |
| `tools/list` | `EventToolsList` |
| `prompts/get` | `EventPromptsGet` |
| `prompts/list` | `EventPromptsList` |
| `resources/read` | `EventResourcesRead` |
| `resources/list` | `EventResourcesList` |
| `resources/subscribe` | `EventResourcesSubscribe` |

Unknown methods pass through the middleware untouched.

### Not Intercepted

1. **Progress notifications.** During a tool call, a handler can call
   `session.NotifyProgress()`. These are JSON-RPC *notifications* sent
   directly over the transport — they do not flow through `MethodHandler`
   middleware. Interceptors never see them.

2. **Transport-level SSE streaming.** The Streamable HTTP transport
   multiplexes multiple JSON-RPC messages over a single SSE connection.
   This is connection management, not per-message streaming. Each individual
   method call is still a single request → single response, which the
   middleware intercepts normally.

3. **JSON-RPC notifications** (e.g. `notifications/initialized`,
   `notifications/cancelled`). The go-sdk routes notifications through a
   separate handler path, not through `MethodHandler` middleware.

Notification interception is not defined by the proposal.
If this becomes necessary, it would require a separate notification middleware
hook in the go-sdk.

---

## Chain Execution Model

The `chainExecutor` in `chain_executor.go` implements trust-boundary-aware
execution:

**Request phase** (receiving data — untrusted → trusted):
```
Validate (parallel) → Mutate (sequential)
```
Validation acts as a security gate before mutations process the data.

**Response phase** (sending data — trusted → untrusted):
```
Mutate (sequential) → Validate (parallel)
```
Mutations prepare/sanitize data, then validation verifies before sending.

### Validator execution
- All matching validators run in parallel (goroutines).
- A validator returning `Valid: false` with `Severity: "error"` in enforced
  mode (`Mode: ModeOn`) aborts the chain.
- `FailOpen: true` validators log errors and record an `ExecutionResult`
  (with `Error` populated) for observability, but don't abort.

### Mutator execution
- Mutators run sequentially, ordered by `PriorityHint.Resolve(phase)`
  (ascending), with alphabetical name tiebreak.
- Each mutator modifies the typed payload in place via type assertion on `inv.Payload`.
- If any mutator fails (and is not `FailOpen`), the chain aborts.
  `FailOpen` mutators record an `ExecutionResult` (with `Error` populated)
  and continue.
- In `ModeAudit`, the mutator runs on a deep-copied payload and its result
  is recorded, but the real payload is not affected.

### Filtering
`newChainExecutor` filters the full interceptor set by:
1. `Mode != ModeOff`
2. Phase matches (or interceptor phase is `PhaseBoth`)
3. Event matches (exact match only; wildcard support is planned via `matchesEvent`)

---

## File Map

### `interceptors/` — protocol-agnostic core (zero MCP imports)

| File | Responsibility |
|------|---------------|
| `interceptor.go` | Types (Phase, Mode, InterceptorType, Priority, Severity, Compat), Metadata struct, Interceptor interface, Validator/Mutator structs and handler types |
| `invocation.go` | Invocation (with audit-mode payload cloning), InvocationContext, Principal — the input to every handler |
| `result.go` | All outcome types: ValidationResult, MutationResult, ExecutionResult, ChainResult, AbortInfo |
| `chain.go` | `Chain` public API: `NewChain`, `Add`, `ExecuteForReceiving`, `ExecuteForSending`, `IsEmpty`, `Interceptors` |
| `chain_executor.go` | `interceptorSnapshot` (atomic snapshot with lazy chain cache), `chainExecutor` struct, `newChainExecutor` (filtering + sorting), `executeForReceiving`, `executeForSending`, `timeoutResult`, `matchesPhase`, `matchesEvent` |
| `chain_validate.go` | `validatorResult` struct, `runValidators` (parallel dispatch + N=1 fast path), `executeValidator`, `recordValidation` |
| `chain_mutate.go` | `mutatorOutcome` type + constants, `runMutators` (sequential loop + audit-mode copy), `executeMutator` |

### `interceptors/mcpserver/` — MCP server integration

| File | Responsibility |
|------|---------------|
| `server.go` | `Server` wrapper, `receivingMiddleware`, `WithContextProvider`, capability declaration, `NewStreamableHTTPHandler`, `abortToJSONRPCError` |
| `events.go` | Event name constants for standard MCP methods |
