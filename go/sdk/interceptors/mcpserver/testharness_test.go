package mcpserver_test

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/stretchr/testify/require"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/integrations/gomiddleware"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/mcpserver"
)

// --- Server setup ---

// setup creates an HTTP test server with interceptor support and a connected
// client session. The server has a single "echo" tool registered for
// EventToolsCall. Cleanup is registered via t.Cleanup.
func setup(t *testing.T, is ...interceptors.Interceptor) *mcp.ClientSession {
	t.Helper()
	return setupWithTools(t, defaultTools(), is...)
}

// setupWithTools creates an HTTP test server with the given tools and
// interceptors. Uses LocalChain to create a chain that invokes
// interceptors via interceptor/invoke over in-memory transport.
// Cleanup is registered via t.Cleanup.
func setupWithTools(t *testing.T, tools []testTool, is ...interceptors.Interceptor) *mcp.ClientSession {
	t.Helper()

	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "test-server",
		Version: "0.1.0",
	}, nil)

	for _, tool := range tools {
		mcpServer.AddTool(tool.tool, tool.handler)
	}

	srv := mcpserver.NewServer(mcpServer)
	for _, i := range is {
		srv.AddInterceptor(i)
	}

	// Create a chain via LocalChain (in-memory transport).
	chain, err := srv.LocalChain(context.Background())
	require.NoError(t, err)

	// Install the middleware for automatic interceptor execution.
	mcpServer.AddReceivingMiddleware(gomiddleware.Middleware(chain))

	handler := mcpserver.NewStreamableHTTPHandler(srv, nil)
	httpServer := httptest.NewServer(handler)
	t.Cleanup(httpServer.Close)

	client := mcp.NewClient(&mcp.Implementation{Name: "test-client", Version: "0.1.0"}, nil)
	cs, err := client.Connect(context.Background(), &mcp.StreamableClientTransport{
		Endpoint: httpServer.URL,
	}, nil)
	require.NoError(t, err)
	t.Cleanup(func() { cs.Close() })

	return cs
}

// --- Tool definitions ---

// testTool pairs a tool definition with its handler for use with setupWithTools.
type testTool struct {
	tool    *mcp.Tool
	handler mcp.ToolHandler
}

// defaultTools returns the standard "echo" tool used by most tests.
func defaultTools() []testTool {
	return []testTool{
		{
			tool: &mcp.Tool{
				Name:        "echo",
				Description: "echoes input",
				InputSchema: map[string]any{"type": "object"},
			},
			handler: func(_ context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
				return &mcp.CallToolResult{
					Content: []mcp.Content{&mcp.TextContent{Text: fmt.Sprintf("echo: %s", req.Params.Arguments)}},
				}, nil
			},
		},
	}
}

// --- Call helpers ---

// callEcho calls the "echo" tool and returns the result.
func callEcho(t *testing.T, cs *mcp.ClientSession) (*mcp.CallToolResult, error) {
	t.Helper()
	return cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
}

// callEchoWithTimeout calls the "echo" tool with a context timeout.
func callEchoWithTimeout(t *testing.T, cs *mcp.ClientSession, timeout time.Duration) (*mcp.CallToolResult, error) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	return cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
}

// --- Assertion helpers ---

// resultText extracts the first TextContent string from a CallToolResult.
// Fails the test if the result has no content or the first item isn't TextContent.
func resultText(t *testing.T, result *mcp.CallToolResult) string {
	t.Helper()
	require.NotEmpty(t, result.Content)
	tc, ok := result.Content[0].(*mcp.TextContent)
	require.True(t, ok, "expected TextContent, got %T", result.Content[0])
	return tc.Text
}

// --- RPC server setup ---

// setupRPCServer creates an interceptor server with the given interceptors
// and returns a connected client session. The client can call custom methods
// via raw JSON-RPC.
func setupRPCServer(t *testing.T, is ...interceptors.Interceptor) *mcp.ClientSession {
	t.Helper()

	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "test-server",
		Version: "0.1.0",
	}, nil)

	// A minimal tool so the server has something to initialize with.
	mcpServer.AddTool(&mcp.Tool{
		Name:        "echo",
		Description: "echoes input",
		InputSchema: map[string]any{"type": "object"},
	}, func(_ context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		return &mcp.CallToolResult{
			Content: []mcp.Content{&mcp.TextContent{Text: fmt.Sprintf("echo: %s", req.Params.Arguments)}},
		}, nil
	})

	srv := mcpserver.NewServer(mcpServer)
	for _, i := range is {
		srv.AddInterceptor(i)
	}

	handler := mcpserver.NewStreamableHTTPHandler(srv, nil)
	httpServer := httptest.NewServer(handler)
	t.Cleanup(httpServer.Close)

	client := mcp.NewClient(&mcp.Implementation{Name: "test-client", Version: "0.1.0"}, nil)
	cs, err := client.Connect(context.Background(), &mcp.StreamableClientTransport{
		Endpoint: httpServer.URL,
	}, nil)
	require.NoError(t, err)
	t.Cleanup(func() { cs.Close() })

	return cs
}

// --- Interceptor builders ---
// All handlers work with json.RawMessage payloads since that's what
// interceptor/invoke delivers.

// prefixMutator creates a mutator that prepends tag to each TextContent
// in a CallToolResult (response phase).
func prefixMutator(name, tag string, phase interceptors.InterceptionPhase, priority int, mode interceptors.Mode) *interceptors.Mutator {
	return &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: name,
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  phase,
			},
			Mode:         mode,
			PriorityHint: interceptors.NewPriority(priority),
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
}

// failMutator creates a mutator that always returns an error.
func failMutator(name string, phase interceptors.InterceptionPhase, priority int) *interceptors.Mutator {
	return &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: name,
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  phase,
			},
			Mode:         interceptors.ModeEnforce,
			PriorityHint: interceptors.NewPriority(priority),
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
			return nil, fmt.Errorf("simulated failure")
		},
	}
}

// blockToolValidator creates a request validator that rejects calls to the named tool.
func blockToolValidator(toolName string) *interceptors.Validator {
	return &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "block-" + toolName,
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
			if params.Name == toolName {
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: toolName + " is blocked", Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}
}

// allowAllValidator creates a request validator that allows all calls.
func allowAllValidator(name string) *interceptors.Validator {
	return &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: name,
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}
}
