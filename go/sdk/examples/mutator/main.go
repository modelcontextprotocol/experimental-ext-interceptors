// Example: a simple mutator interceptor that adds an "[audited]" prefix
// to every tool call result's text content.
//
// Demonstrates WithContextProvider to populate Invocation.Context with
// caller identity, which interceptor handlers can inspect.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/integrations/gomiddleware"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/mcpserver"
)

func main() {
	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "example-server",
		Version: "0.1.0",
	}, nil)

	mcpServer.AddTool(&mcp.Tool{Name: "greet", Description: "says hello", InputSchema: map[string]any{"type": "object"}},
		func(ctx context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
			return &mcp.CallToolResult{
				Content: []mcp.Content{&mcp.TextContent{Text: "hello world"}},
			}, nil
		},
	)

	// Build the mutator.
	m := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "audit-tag",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
			}
			var result mcp.CallToolResult
			if err := json.Unmarshal(raw, &result); err != nil {
				return nil, err
			}

			// Use caller identity from the context provider if available.
			tag := "[audited]"
			if inv.Context != nil && inv.Context.Principal != nil {
				tag = fmt.Sprintf("[audited by %s]", inv.Context.Principal.ID)
			}

			for _, c := range result.Content {
				if tc, ok := c.(*mcp.TextContent); ok {
					tc.Text = tag + " " + tc.Text
				}
			}

			data, err := json.Marshal(&result)
			if err != nil {
				return nil, err
			}
			return &interceptors.MutationResult{Modified: true, Payload: json.RawMessage(data)}, nil
		},
	}

	// Create the interceptor server (registers interceptors as discoverable
	// resources via interceptors/list and interceptor/invoke).
	srv := mcpserver.NewServer(mcpServer)
	srv.AddInterceptor(m)

	// Create a chain connected via in-memory transport.
	chain, err := srv.LocalChain(context.Background())
	if err != nil {
		log.Fatal(err)
	}

	// Install middleware for automatic execution on every request/response.
	// WithContextProvider populates caller identity for interceptor handlers.
	mcpServer.AddReceivingMiddleware(
		gomiddleware.Middleware(chain,
			gomiddleware.WithContextProvider(
				func(_ context.Context, _ mcp.Request) *interceptors.InvocationContext {
					return &interceptors.InvocationContext{
						Principal: &interceptors.Principal{
							Type: "user",
							ID:   "example-user",
						},
					}
				},
			),
		),
	)

	handler := mcpserver.NewStreamableHTTPHandler(srv, nil)
	log.Println("listening on :8080")
	if err := http.ListenAndServe(":8080", handler); err != nil {
		log.Fatal(err)
	}
}
