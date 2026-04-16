# Go SDK Interceptors — Performance

Analysis of per-request costs and allocation patterns.

---

## Design Rationale: Typed Payload

The interceptor chain passes Go's typed params/result objects directly
to handlers, avoiding JSON serialization entirely in the normal path.
This follows the same pattern used by gRPC-Go, where interceptors
receive the request as `any` and type-assert to the concrete type.

`Invocation.Payload` is `any, the same pointer the go-sdk already
allocated during JSON-RPC deserialization. No wrapper struct, no
intermediate copies. Mutators modify the value in place through the
pointer (no marshal/unmarshal round-trip)

---

## Per-Request Cost Model

Every intercepted request passes through the middleware in
`mcpserver/server.go`. The cost depends on whether interceptors match
the event.

### Fast Path (no matching interceptors)

When no interceptors match, the middleware cost is:

1. One atomic pointer load: `s.chain.snapshot.Load()`
2. Two `sync.Map` lookups on the chain cache: `getChain(event, PhaseRequest)` and `getChain(event, PhaseResponse)`
3. One boolean check: `ce.empty` 

Zero allocations, zero JSON operations.

### Intercepted Path

With interceptors active, each active phase
incurs:

| Step | Operation | Allocations | JSON ops |
|------|-----------|-------------|----------|
| 1 | `Invocation` struct | 1 struct | 0 |
| 2 | `ChainResult` struct (with pre-allocated `Results` slice) | 1 struct + 1 slice | 0 |
| 3 | Validator execution | 0 (N=1) / goroutines (N>1) | 0 |
| 4 | Mutator execution | 0 | 0 |
| 5 | Audit-mode deep copy | 1 per audit mutator | 1 marshal + 1 unmarshal |

Zero JSON operations in the normal path. Phases with no matching
interceptors are skipped entirely.
