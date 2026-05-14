# Go SDK Interceptors — Performance

Analysis of per-request costs and allocation patterns.

---

## Design Rationale: JSON-RPC Invocation

The interceptor chain follows the SEP execution model: each interceptor
is invoked via `interceptor/invoke` JSON-RPC calls through an MCP client
session. For in-process interceptors, `Server.LocalChain()` creates an
in-memory transport, so the JSON-RPC overhead is minimal (no network I/O).

Payloads are `json.RawMessage` at the protocol level. The middleware
marshals request params and response results once per phase, and
interceptor handlers unmarshal the specific fields they need.

---

## Per-Request Cost Model

Every intercepted request passes through `gomiddleware.Middleware`.
The cost depends on whether interceptors match the event.

### Fast Path (skipped methods)

The middleware skips `initialize`, `notifications/*`, `interceptors/list`,
and `interceptor/invoke` with a map lookup. Zero allocations, zero JSON
operations.

### Intercepted Path

With interceptors active, each active phase incurs:

| Step | Operation | Allocations | JSON ops |
|------|-----------|-------------|----------|
| 1 | Marshal request params / response result | 1 `json.RawMessage` | 1 marshal |
| 2 | `ChainExecutionResult` struct (with pre-allocated `Results` slice) | 1 struct + 1 slice | 0 |
| 3 | Per-interceptor `interceptor/invoke` RPC | 1 `InvokeParams` + 1 `InvokeResult` per interceptor | 1 marshal + 1 unmarshal per interceptor (handled by go-sdk JSON-RPC layer) |
| 4 | Validator execution (N=1 inline, N>1 goroutines) | 0 (N=1) / goroutines (N>1) | Handler-specific |
| 5 | Mutator payload threading | 0 | Payload passed as `json.RawMessage` between mutators |
| 6 | Unmarshal mutated payload back to params/result | 0 | 1 unmarshal (if mutated) |

### In-Memory Transport Overhead

When using `LocalChain()`, the in-memory transport uses newline-delimited
JSON over a `net.Pipe()`. Each `interceptor/invoke` call involves:

1. JSON marshal of `InvokeParams` (client side)
2. Write to pipe
3. JSON unmarshal of `InvokeParams` (server side)
4. Handler execution
5. JSON marshal of `InvokeResult` (server side)
6. Write to pipe
7. JSON unmarshal of `InvokeResult` (client side)

The JSON-RPC overhead is bounded by the payload size and is negligible
for typical MCP payloads. The in-memory pipe avoids network I/O
entirely.

### Audit Mode

Audit-mode mutators run normally but their mutated payload is not
propagated to subsequent interceptors. No deep-copy is needed — the
chain simply skips updating `FinalPayload` for audit-mode entries.

---

## Optimization Opportunities

1. **Batch invoke**: A future `interceptor/invokeValidators` method could
   reduce round-trips for validators that run in parallel.
2. **Payload caching**: For validators that don't need the full payload,
   a lightweight invoke variant could skip payload serialization.
3. **Connection pooling**: For remote interceptor servers, reusing
   client sessions across requests avoids connection setup overhead.
