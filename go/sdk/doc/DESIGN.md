# Go SDK Interceptors — Design Document

## Architecture: SEP-Compliant Chain with `interceptor/invoke`

The Go SDK follows the SEP execution model where interceptors are
first-class MCP primitives:

1. **Interceptor Extension** (`extension.Extension`) — registers
   interceptors and installs them on one or more `mcp.Server` instances,
   exposing them via `interceptors/list` and `interceptor/invoke` JSON-RPC
   methods.
2. **Chain** (`chain.Chain`) — the SDK-level orchestrator in the
   `interceptors/chain` sub-package. It holds `ChainEntry` objects
   (interceptor descriptor + MCP client session) and invokes interceptors
   via `interceptor/invoke` on the appropriate server.
3. **Middleware** (`gomiddleware.Middleware`) — hooks into the go-sdk's
   receiving middleware pipeline, marshals payloads to `json.RawMessage`,
   calls `chain.Execute()`, and applies mutated payloads back.

```
Transport (SSE / stdio / HTTP)
  → JSON-RPC decode
  → Params deserialization (json.RawMessage → typed struct)
  → Receiving middleware chain ← gomiddleware hooks in here
    → chain.Execute() → interceptor/invoke RPC (per entry)
  → Method handler (e.g. tool handler)
  → Result returned through middleware
    → chain.Execute() → interceptor/invoke RPC (per entry)
  → JSON-RPC encode
  → Transport
```

## LocalChain: In-Memory Transport

`Extension.LocalChain(ctx, server)` creates a chain connected to the given
server via in-memory transport. Under the hood it:

1. Creates an `InMemoryTransport` pair via `mcp.NewInMemoryTransports()`
2. Connects the server side via `mcp.Server.Connect(ctx, serverTransport)`
3. Creates an MCP client and connects via the client transport
4. Calls `chain.AddMCPServer(ctx, clientSession)` which discovers
   interceptors via `interceptors/list`
5. Returns the ready-to-use chain

This means interceptors are invoked through the full MCP JSON-RPC
pathway — even when running in the same process — ensuring the same
behavior as remote interceptor servers.

## Capability Declaration

During initialization, a lightweight middleware intercepts the `"initialize"`
response and injects interceptor metadata into
`Capabilities.Experimental["io.modelcontextprotocol/interceptors"]`. This
follows the same pattern as the variants extension
(`io.modelcontextprotocol/server-variants`).

The capability payload includes:
- `supportedEvents` — deduplicated list of events with registered interceptors

## Request/Response Lifecycle

When a JSON-RPC request arrives, `gomiddleware.Middleware` runs:

```
0.  If method is skipped (initialize, notifications/*, interceptor/*) → passthrough
1.  Marshal request params to json.RawMessage
2.  chain.Execute(ctx, {event, PhaseRequest, payload})
    → For each interceptor: interceptor/invoke RPC via ClientSession
3.  If aborted → return JSON-RPC error
4.  Unmarshal mutated payload back into request params
5.  Call next handler                        next(ctx, method, req)
6.  Marshal response result to json.RawMessage
7.  chain.Execute(ctx, {event, PhaseResponse, payload})
    → For each interceptor: interceptor/invoke RPC via ClientSession
8.  If aborted → return JSON-RPC error
9.  Unmarshal mutated payload back into response result
10. Return result
```

### JSON-RPC Payload Model

Interceptor handlers receive `json.RawMessage` as `inv.Payload` when
invoked via `interceptor/invoke`. This is the SEP-correct behavior —
payloads are JSON at the protocol level. Handlers unmarshal, inspect or
modify, and (for mutators) set the updated JSON back on `inv.Payload`:

```go
// Validator — unmarshal, inspect, return:
raw := inv.Payload.(json.RawMessage)
var params struct{ Name string `json:"name"` }
json.Unmarshal(raw, &params)
// inspect params ...
return &ValidationResult{Valid: true}, nil

// Mutator — unmarshal, modify, marshal back:
raw := inv.Payload.(json.RawMessage)
var result map[string]any
json.Unmarshal(raw, &result)
result["redacted"] = true
data, _ := json.Marshal(result)
return &MutationResult{Modified: true, Payload: data}, nil
```

### Skipped Methods

The middleware skips the following methods to prevent recursion and
avoid intercepting lifecycle events:

- `initialize` — capability enrichment handled separately
- `notifications/initialized` — lifecycle notification
- `notifications/cancelled` — lifecycle notification
- `interceptors/list` — interceptor discovery (would cause recursion)
- `interceptor/invoke` — interceptor invocation (would cause recursion)

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

Unknown methods pass through the middleware and are intercepted normally
(the JSON-RPC method name is used as the event name).

### Not Intercepted

1. **Progress notifications.** These are JSON-RPC *notifications* sent
   directly over the transport — they do not flow through `MethodHandler`
   middleware.

2. **Transport-level SSE streaming.** Connection management, not
   per-message streaming. Each individual method call is still a single
   request → single response.

3. **JSON-RPC notifications** (e.g. `notifications/initialized`,
   `notifications/cancelled`). Explicitly skipped by the middleware.

---

## Chain Execution Model

`Chain.Execute()` implements trust-boundary-aware execution per the SEP:

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
Response-phase validators see the post-mutation payload.

### Validator execution
- All matching validators run in parallel (goroutines).
- A validator returning `Valid: false` with `Severity: "error"` in enforced
  mode aborts the chain.
- `FailOpen: true` validators log errors and record an `InvokeResult`
  for observability, but don't abort.

### Mutator execution
- Mutators run sequentially, ordered by `PriorityHint.Resolve(phase)`
  (ascending), with alphabetical name tiebreak.
- Each mutator receives `json.RawMessage`, unmarshals, modifies, and sets
  the updated JSON back on `inv.Payload`. The chain passes the mutated
  payload from each mutator to the next.
- If any mutator fails (and is not `FailOpen`), the chain aborts.
- In `ModeAudit`, the mutated payload is not propagated to subsequent
  interceptors.

### Filtering
`Chain.Execute` filters entries by:
1. Phase matches (or interceptor phase is `PhaseBoth`)
2. Event matches (exact match only; wildcard support is planned)
3. Optional name filter (`ChainExecutionParams.Interceptors`)

---

## File Map

### `interceptors/` — core types (transport-agnostic)

| File | Responsibility |
|------|---------------|
| `interceptor.go` | Interceptor interface, enums (Phase, Mode, InterceptorType, Severity), Metadata/Compat/Hook, Validator/Mutator structs and handler types, Invocation/InvocationContext/Principal, result types (ValidationResult, MutationResult) |
| `priority.go` | Priority struct, NewPriority, Resolve, MarshalJSON, UnmarshalJSON |
| `wire.go` | JSON-RPC method/event constants, wire types (ListParams, ListResult, InvokeParams, InvokeResult, InterceptorInfo) |

### `interceptors/chain/` — chain orchestrator

| File | Responsibility |
|------|---------------|
| `chain.go` | `Chain`, `ChainEntry`, `ExecutionParams`, `ExecutionResult`, `NewChain`, `AddMCPServer`, `Execute`; filtering, sorting, parallel validation, sequential mutation, trust-boundary ordering |
| `result.go` | Chain result types: ChainStatus, AbortType, AbortInfo, ValidationSummary |

### `interceptors/extension/` — MCP server integration

| File | Responsibility |
|------|---------------|
| `server.go` | `Extension`, interceptor management, `Install`, `LocalChain`, capability declaration |
| `rpc.go` | `handleList` and `handleInvoke` JSON-RPC method handlers |
| `events.go` | Event name constants for standard MCP methods |

### `interceptors/integrations/gomiddleware/` — middleware

| File | Responsibility |
|------|---------------|
| `middleware.go` | `Middleware` function, request/response interception, abort-to-JSON-RPC-error conversion |
