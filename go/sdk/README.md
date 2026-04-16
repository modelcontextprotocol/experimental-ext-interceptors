# MCP Interceptors - Go Implementation

Go implementation of the MCP Interceptor Extension based on
[SEP-1763](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763).

## Quick Start

```go
mcpServer := mcp.NewServer(&mcp.Implementation{
    Name:    "my-server",
    Version: "0.1.0",
}, nil)

// Wrap with interceptor support.
srv := interceptors.NewServer(mcpServer,
    // Optional Context Provider
    interceptors.WithContextProvider(
        func(_ context.Context, _ mcp.Request) *interceptors.InvocationContext {
            return &interceptors.InvocationContext{
                Principal: &interceptors.Principal{Type: "user", ID: "alice"},
            }
        },
    ),
)

// Register a validator that blocks dangerous tool calls.
srv.AddInterceptor(&interceptors.Validator{
    Metadata: interceptors.Metadata{
        Name:   "block-dangerous",
        Events: []string{interceptors.EventToolsCall},
        Phase:  interceptors.PhaseRequest,
        Mode:   interceptors.ModeOn,
    },
    Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
        // validate the request...
        return &interceptors.ValidationResult{Valid: true}, nil
    },
})

srv.Run(context.Background(), &mcp.StdioTransport{})
```

See [`examples/`](examples/) for complete working examples.

## Documentation

- [**DESIGN.md**](doc/DESIGN.md) — architecture, execution model, integration
  with the go-sdk.
- [**PERFORMANCE.md**](doc/PERFORMANCE.md) — per-request cost model, allocation
  summary, and optimization notes.
- [**CONFORMANCE.md**](doc/CONFORMANCE.md) — SEP conformance status.

Package API documentation is available via `go doc`:

```sh
go doc github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors
```
