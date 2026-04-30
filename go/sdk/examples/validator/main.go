// Example: a simple validator interceptor that rejects tool calls
// to a tool named "dangerous_tool".
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
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/extension"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/integrations/gomiddleware"
)

func main() {
	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "example-server",
		Version: "0.1.0",
	}, nil)

	mcpServer.AddTool(&mcp.Tool{Name: "echo", Description: "echoes input", InputSchema: map[string]any{"type": "object"}},
		func(ctx context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
			return &mcp.CallToolResult{
				Content: []mcp.Content{&mcp.TextContent{Text: fmt.Sprintf("you said: %s", req.Params.Arguments)}},
			}, nil
		},
	)

	// Build the validator.
	v := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "block-dangerous-tool",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
			}
			var params struct {
				Name string `json:"name"`
			}
			if err := json.Unmarshal(raw, &params); err != nil {
				return nil, err
			}
			if params.Name == "dangerous_tool" {
				reason := "dangerous_tool is not allowed"
				if inv.Context != nil && inv.Context.Principal != nil {
					reason = fmt.Sprintf("dangerous_tool is not allowed for %s", inv.Context.Principal.ID)
				}
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: reason, Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	// Create the interceptor extension (registers interceptors as discoverable
	// resources via interceptors/list and interceptor/invoke).
	ext := extension.New()
	ext.AddInterceptor(v)
	ext.Install(mcpServer)

	// Create a chain connected via in-memory transport.
	chain, err := ext.LocalChain(context.Background(), mcpServer)
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

	handler := mcp.NewStreamableHTTPHandler(
		func(r *http.Request) *mcp.Server { return mcpServer },
		nil,
	)
	log.Println("listening on :8080")
	if err := http.ListenAndServe(":8080", handler); err != nil {
		log.Fatal(err)
	}
}
