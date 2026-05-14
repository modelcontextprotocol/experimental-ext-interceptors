package gomiddleware_test

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/extension"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/integrations/gomiddleware"
)

// setup creates a test server with middleware installed and returns a connected client session.
func setup(t *testing.T, is ...interceptors.Interceptor) *mcp.ClientSession {
	t.Helper()

	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "test-server",
		Version: "0.1.0",
	}, nil)

	mcpServer.AddTool(&mcp.Tool{
		Name:        "echo",
		Description: "echoes input",
		InputSchema: map[string]any{"type": "object"},
	}, func(_ context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		return &mcp.CallToolResult{
			Content: []mcp.Content{&mcp.TextContent{Text: fmt.Sprintf("echo: %s", req.Params.Arguments)}},
		}, nil
	})

	ext := extension.New()
	for _, i := range is {
		ext.AddInterceptor(i)
	}
	ext.Install(mcpServer)

	// Create chain via LocalChain (in-memory transport).
	chain, err := ext.LocalChain(context.Background(), mcpServer)
	require.NoError(t, err)

	// Install the middleware.
	mcpServer.AddReceivingMiddleware(gomiddleware.Middleware(chain))

	handler := mcp.NewStreamableHTTPHandler(
		func(r *http.Request) *mcp.Server { return mcpServer },
		nil,
	)
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

func resultText(t *testing.T, result *mcp.CallToolResult) string {
	t.Helper()
	require.NotEmpty(t, result.Content)
	tc, ok := result.Content[0].(*mcp.TextContent)
	require.True(t, ok, "expected TextContent, got %T", result.Content[0])
	return tc.Text
}

func TestMiddlewareBlocksOnValidationError(t *testing.T) {
	t.Parallel()

	blocker := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "block-echo",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var params struct {
				Name string `json:"name"`
			}
			if err := json.Unmarshal(raw, &params); err != nil {
				return nil, err
			}
			if params.Name == "echo" {
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: "echo is blocked", Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	cs := setup(t, blocker)

	_, err := cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
	assert.Error(t, err)
}

func TestMiddlewarePassesOnSuccess(t *testing.T) {
	t.Parallel()

	allow := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "allow-all",
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

	cs := setup(t, allow)

	result, err := cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "echo:"))
}

func TestMiddlewareMutatesPayload(t *testing.T) {
	t.Parallel()

	mutator := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "add-prefix",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var result mcp.CallToolResult
			if err := json.Unmarshal(raw, &result); err != nil {
				return nil, err
			}
			for _, c := range result.Content {
				if tc, ok := c.(*mcp.TextContent); ok {
					tc.Text = "[mutated] " + tc.Text
				}
			}
			data, err := json.Marshal(&result)
			if err != nil {
				return nil, err
			}
			return &interceptors.MutationResult{Modified: true, Payload: json.RawMessage(data)}, nil
		},
	}

	cs := setup(t, mutator)

	result, err := cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "[mutated] echo:"))
}

func TestMiddlewareAuditModeNonBlocking(t *testing.T) {
	t.Parallel()

	// Audit-mode validator returns Valid=false but should NOT block the request.
	auditValidator := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "audit-reject",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeAudit,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			return &interceptors.ValidationResult{
				Valid: false,
				Messages: []interceptors.ValidationMessage{
					{Message: "would reject", Severity: interceptors.SeverityError},
				},
			}, nil
		},
	}

	cs := setup(t, auditValidator)

	result, err := cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "echo:"))
}

func TestMiddlewareWithContextProvider(t *testing.T) {
	t.Parallel()

	// Validator that checks the invocation context for a principal.
	principalCheck := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "check-principal",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			if inv.Context == nil || inv.Context.Principal == nil || inv.Context.Principal.ID != "test-user" {
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: "missing or invalid principal", Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	// Setup with context provider.
	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "test-server",
		Version: "0.1.0",
	}, nil)

	mcpServer.AddTool(&mcp.Tool{
		Name:        "echo",
		Description: "echoes input",
		InputSchema: map[string]any{"type": "object"},
	}, func(_ context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		return &mcp.CallToolResult{
			Content: []mcp.Content{&mcp.TextContent{Text: fmt.Sprintf("echo: %s", req.Params.Arguments)}},
		}, nil
	})

	ext := extension.New()
	ext.AddInterceptor(principalCheck)
	ext.Install(mcpServer)

	chain, err := ext.LocalChain(context.Background(), mcpServer)
	require.NoError(t, err)

	mcpServer.AddReceivingMiddleware(gomiddleware.Middleware(chain,
		gomiddleware.WithContextProvider(func(_ context.Context, _ mcp.Request) *interceptors.InvocationContext {
			return &interceptors.InvocationContext{
				Principal: &interceptors.Principal{
					Type: "user",
					ID:   "test-user",
				},
			}
		}),
	))

	handler := mcp.NewStreamableHTTPHandler(
		func(r *http.Request) *mcp.Server { return mcpServer },
		nil,
	)
	httpServer := httptest.NewServer(handler)
	t.Cleanup(httpServer.Close)

	client := mcp.NewClient(&mcp.Implementation{Name: "test-client", Version: "0.1.0"}, nil)
	cs, err := client.Connect(context.Background(), &mcp.StreamableClientTransport{
		Endpoint: httpServer.URL,
	}, nil)
	require.NoError(t, err)
	t.Cleanup(func() { cs.Close() })

	result, err := cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "echo:"))
}

func TestMiddlewareRequestMutatorModifiesArgs(t *testing.T) {
	t.Parallel()

	// Request mutator that adds a field to the arguments.
	reqMutator := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "inject-field",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var params map[string]any
			if err := json.Unmarshal(raw, &params); err != nil {
				return nil, err
			}
			args, _ := params["arguments"].(map[string]any)
			if args == nil {
				args = map[string]any{}
			}
			args["injected"] = "yes"
			params["arguments"] = args
			data, err := json.Marshal(params)
			if err != nil {
				return nil, err
			}
			return &interceptors.MutationResult{Modified: true, Payload: json.RawMessage(data)}, nil
		},
	}

	cs := setup(t, reqMutator)

	result, err := cs.CallTool(context.Background(), &mcp.CallToolParams{
		Name:      "echo",
		Arguments: map[string]any{"text": "hello"},
	})
	require.NoError(t, err)
	text := resultText(t, result)
	assert.Contains(t, text, "injected")
}
