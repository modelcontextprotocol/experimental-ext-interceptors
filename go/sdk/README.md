# MCP Interceptors - Go Implementation

Go implementation of the MCP Interceptor Extension based on
[SEP-1763](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763).

Note: Currently the MCP SDK is vendored, in-order to add the Protocol Methods needed for interceptors.

## Quick Start

```go
mcpServer := mcp.NewServer(&mcp.Implementation{
    Name:    "my-server",
    Version: "0.1.0",
}, nil)

// Create an extension and register interceptors.
ext := extension.New()

// Register a validator that blocks dangerous tool calls.
ext.AddInterceptor(&interceptors.Validator{
    Metadata: interceptors.Metadata{
        Name: "block-dangerous",
        Hook: interceptors.Hook{
            Events: []string{interceptors.EventToolsCall},
            Phase:  interceptors.PhaseRequest,
        },
        Mode: interceptors.ModeEnforce,
    },
    Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
        raw := inv.Payload.(json.RawMessage)
        var params struct{ Name string `json:"name"` }
        json.Unmarshal(raw, &params)
        // validate the request...
        return &interceptors.ValidationResult{Valid: true}, nil
    },
})

// Install on the server and create a chain for middleware.
ext.Install(mcpServer)
chain, err := ext.LocalChain(ctx, mcpServer)
mcpServer.AddReceivingMiddleware(gomiddleware.Middleware(chain))

mcpServer.Run(context.Background(), &mcp.StdioTransport{})
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
