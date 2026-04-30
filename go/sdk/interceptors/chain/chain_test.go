// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package chain_test

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/chain"
)

// setupChainWithInterceptors creates an MCP server with the given
// interceptors registered via custom methods, connects a chain via
// in-memory transport, and returns the chain ready for testing.
func setupChainWithInterceptors(t *testing.T, is ...interceptors.Interceptor) *chain.Chain {
	t.Helper()

	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "chain-test-server",
		Version: "0.1.0",
	}, nil)

	// Register interceptors/list and interceptor/invoke handlers.
	registerInterceptorMethods(mcpServer, is)

	serverTransport, clientTransport := mcp.NewInMemoryTransports()

	ss, err := mcpServer.Connect(context.Background(), serverTransport, nil)
	require.NoError(t, err)
	t.Cleanup(func() { ss.Close() })

	client := mcp.NewClient(&mcp.Implementation{
		Name:    "chain-test-client",
		Version: "0.1.0",
	}, nil)
	cs, err := client.Connect(context.Background(), clientTransport, nil)
	require.NoError(t, err)
	t.Cleanup(func() { cs.Close() })

	ch := chain.NewChain(chain.WithChainLogger(slog.Default()))
	err = ch.AddMCPServer(context.Background(), cs)
	require.NoError(t, err)

	return ch
}

// registerInterceptorMethods adds interceptors/list and interceptor/invoke
// custom methods to the server, backed by the given interceptor list.
func registerInterceptorMethods(server *mcp.Server, is []interceptors.Interceptor) {
	mcp.AddReceivingCustomMethod(server, interceptors.MethodList,
		func(_ context.Context, req *mcp.ServerRequest[*interceptors.ListParams]) (*interceptors.ListResult, error) {
			var event string
			if req.Params != nil {
				event = req.Params.Event
			}
			infos := make([]interceptors.InterceptorInfo, 0, len(is))
			for _, i := range is {
				if event != "" {
					match := false
					for _, e := range i.GetMetadata().Hook.Events {
						if e == event {
							match = true
							break
						}
					}
					if !match {
						continue
					}
				}
				infos = append(infos, interceptors.InfoFromInterceptor(i))
			}
			return &interceptors.ListResult{Interceptors: infos}, nil
		},
	)

	mcp.AddReceivingCustomMethod(server, interceptors.MethodInvoke,
		func(ctx context.Context, req *mcp.ServerRequest[*interceptors.InvokeParams]) (*interceptors.InvokeResult, error) {
			if req.Params == nil {
				return nil, fmt.Errorf("params required")
			}
			params := req.Params
			var target interceptors.Interceptor
			for _, i := range is {
				if i.GetMetadata().Name == params.Name {
					target = i
					break
				}
			}
			if target == nil {
				return nil, fmt.Errorf("interceptor %q not found", params.Name)
			}

			inv := &interceptors.Invocation{
				Event:   params.Event,
				Phase:   params.Phase,
				Payload: params.Payload,
				Config:  params.Config,
				Context: params.Context,
			}

			result := &interceptors.InvokeResult{
				Interceptor: params.Name,
				Type:        target.GetType(),
				Phase:       params.Phase,
			}

			switch v := target.(type) {
			case *interceptors.Validator:
				vr, err := v.Handler(ctx, inv)
				if err != nil {
					return nil, err
				}
				result.Validation = vr
			case *interceptors.Mutator:
				mr, err := v.Handler(ctx, inv)
				if err != nil {
					return nil, err
				}
				result.Mutation = mr
				result.Payload = mr.Payload
			}
			return result, nil
		},
	)
}

func TestChain_FailOpenRecordsExecutionResult(t *testing.T) {
	t.Parallel()

	t.Run("fail-open validator error is recorded", func(t *testing.T) {
		t.Parallel()
		failOpenValidator := &interceptors.Validator{
			Metadata: interceptors.Metadata{
				Name: "fo-validator",
				Hook: interceptors.Hook{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseRequest,
				},
				Mode:     interceptors.ModeEnforce,
				FailOpen: true,
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
				return nil, fmt.Errorf("transient failure")
			},
		}
		passingValidator := &interceptors.Validator{
			Metadata: interceptors.Metadata{
				Name: "passing-validator",
				Hook: interceptors.Hook{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseRequest,
				},
				Mode: interceptors.ModeEnforce,
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
				return &interceptors.ValidationResult{Valid: true}, nil
			},
		}

		ch := setupChainWithInterceptors(t, failOpenValidator, passingValidator)

		payload, _ := json.Marshal(map[string]any{"value": "hello"})
		cr, err := ch.Execute(context.Background(), &chain.ExecutionParams{
			Event:   "test/event",
			Phase:   interceptors.PhaseRequest,
			Payload: payload,
		})
		require.NoError(t, err)

		// Chain should succeed — fail-open doesn't abort.
		assert.Equal(t, chain.ChainSuccess, cr.Status)
		assert.Empty(t, cr.AbortedAt)

		// Both interceptors should have a result entry.
		require.Len(t, cr.Results, 2)
	})

	t.Run("fail-open mutator error is recorded", func(t *testing.T) {
		t.Parallel()
		failOpenMutator := &interceptors.Mutator{
			Metadata: interceptors.Metadata{
				Name: "fo-mutator",
				Hook: interceptors.Hook{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseResponse,
				},
				Mode:         interceptors.ModeEnforce,
				FailOpen:     true,
				PriorityHint: interceptors.NewPriority(10),
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
				return nil, fmt.Errorf("transient failure")
			},
		}
		passingMutator := &interceptors.Mutator{
			Metadata: interceptors.Metadata{
				Name: "passing-mutator",
				Hook: interceptors.Hook{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseResponse,
				},
				Mode:         interceptors.ModeEnforce,
				PriorityHint: interceptors.NewPriority(20),
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
				return &interceptors.MutationResult{Modified: false}, nil
			},
		}

		ch := setupChainWithInterceptors(t, failOpenMutator, passingMutator)

		payload, _ := json.Marshal(map[string]any{"value": "hello"})
		cr, err := ch.Execute(context.Background(), &chain.ExecutionParams{
			Event:   "test/event",
			Phase:   interceptors.PhaseResponse,
			Payload: payload,
		})
		require.NoError(t, err)

		// Chain should succeed — fail-open doesn't abort.
		assert.Equal(t, chain.ChainSuccess, cr.Status)
		assert.Empty(t, cr.AbortedAt)

		// Both interceptors should have a result entry.
		require.Len(t, cr.Results, 2)
	})
}
