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
	return setupChainWithOpts(t, nil, is...)
}

func setupChainWithOpts(t *testing.T, opts []chain.ChainOption, is ...interceptors.Interceptor) *chain.Chain {
	t.Helper()

	mcpServer := mcp.NewServer(&mcp.Implementation{
		Name:    "chain-test-server",
		Version: "0.1.0",
	}, nil)

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

	allOpts := append([]chain.ChainOption{chain.WithChainLogger(slog.Default())}, opts...)
	ch := chain.NewChain(allOpts...)
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
					for _, h := range i.GetMetadata().Hooks {
						for _, e := range h.Events {
							if e == event {
								match = true
								break
							}
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

func TestChain_ExecutionHandler(t *testing.T) {
	t.Parallel()

	tests := []struct {
		name          string
		interceptor   interceptors.Interceptor
		directive     *chain.Directive
		phase         interceptors.InterceptionPhase
		wantStatus    chain.ChainStatus
		wantAborted   bool
		wantNoPayload bool
	}{
		{
			name: "audit-to-enforce validator override aborts chain",
			interceptor: &interceptors.Validator{
				Metadata: interceptors.Metadata{
					Name:  "v",
					Hooks: []interceptors.Hook{{Events: []string{"test/event"}, Phase: interceptors.PhaseRequest}},
					Mode:  interceptors.ModeAudit,
				},
				Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
					return &interceptors.ValidationResult{
						Valid: false, Severity: interceptors.SeverityError,
						Messages: []interceptors.ValidationMessage{{Message: "blocked", Severity: interceptors.SeverityError}},
					}, nil
				},
			},
			directive:   &chain.Directive{Mode: modePtr(interceptors.ModeEnforce)},
			phase:       interceptors.PhaseRequest,
			wantStatus:  chain.ChainValidationFailed,
			wantAborted: true,
		},
		{
			name: "enforce-to-audit validator override does not abort",
			interceptor: &interceptors.Validator{
				Metadata: interceptors.Metadata{
					Name:  "v",
					Hooks: []interceptors.Hook{{Events: []string{"test/event"}, Phase: interceptors.PhaseRequest}},
					Mode:  interceptors.ModeEnforce,
				},
				Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
					return &interceptors.ValidationResult{
						Valid: false, Severity: interceptors.SeverityError,
						Messages: []interceptors.ValidationMessage{{Message: "would block", Severity: interceptors.SeverityError}},
					}, nil
				},
			},
			directive:  &chain.Directive{Mode: modePtr(interceptors.ModeAudit)},
			phase:      interceptors.PhaseRequest,
			wantStatus: chain.ChainSuccess,
		},
		{
			name: "mutator audit override skips payload application",
			interceptor: &interceptors.Mutator{
				Metadata: interceptors.Metadata{
					Name:  "m",
					Hooks: []interceptors.Hook{{Events: []string{"test/event"}, Phase: interceptors.PhaseResponse}},
					Mode:  interceptors.ModeEnforce,
				},
				Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
					modified, _ := json.Marshal(map[string]any{"value": "mutated"})
					return &interceptors.MutationResult{Modified: true, Payload: modified}, nil
				},
			},
			directive:     &chain.Directive{Mode: modePtr(interceptors.ModeAudit)},
			phase:         interceptors.PhaseResponse,
			wantStatus:    chain.ChainSuccess,
			wantNoPayload: true,
		},
		{
			name: "nil directive uses descriptor mode",
			interceptor: &interceptors.Validator{
				Metadata: interceptors.Metadata{
					Name:  "v",
					Hooks: []interceptors.Hook{{Events: []string{"test/event"}, Phase: interceptors.PhaseRequest}},
					Mode:  interceptors.ModeEnforce,
				},
				Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
					return &interceptors.ValidationResult{
						Valid: false, Severity: interceptors.SeverityError,
						Messages: []interceptors.ValidationMessage{{Message: "blocked", Severity: interceptors.SeverityError}},
					}, nil
				},
			},
			directive:   nil,
			phase:       interceptors.PhaseRequest,
			wantStatus:  chain.ChainValidationFailed,
			wantAborted: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			handler := chain.ExecutionHandler(
				func(ctx context.Context, entry chain.ChainEntry, params *interceptors.InvokeParams,
					next func(ctx context.Context, params *interceptors.InvokeParams) (interceptors.InvokeResult, error),
				) (interceptors.InvokeResult, *chain.Directive, error) {
					result, err := next(ctx, params)
					return result, tt.directive, err
				},
			)

			ch := setupChainWithOpts(t, []chain.ChainOption{chain.WithExecutionHandler(handler)}, tt.interceptor)
			payload, _ := json.Marshal(map[string]any{"value": "test"})
			cr, err := ch.Execute(context.Background(), &chain.ExecutionParams{
				Event:   "test/event",
				Phase:   tt.phase,
				Payload: payload,
			})
			require.NoError(t, err)
			assert.Equal(t, tt.wantStatus, cr.Status)
			if tt.wantAborted {
				assert.NotEmpty(t, cr.AbortedAt)
			} else {
				assert.Empty(t, cr.AbortedAt)
			}
			if tt.wantNoPayload {
				assert.Nil(t, cr.FinalPayload)
			}
		})
	}
}

func modePtr(m interceptors.Mode) *interceptors.Mode { return &m }

func TestChain_ExecutionHandler_ShortCircuit(t *testing.T) {
	t.Parallel()
	invoked := false
	v := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name:  "v",
			Hooks: []interceptors.Hook{{Events: []string{"test/event"}, Phase: interceptors.PhaseRequest}},
			Mode:  interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			invoked = true
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	handler := chain.ExecutionHandler(
		func(ctx context.Context, entry chain.ChainEntry, params *interceptors.InvokeParams,
			next func(ctx context.Context, params *interceptors.InvokeParams) (interceptors.InvokeResult, error),
		) (interceptors.InvokeResult, *chain.Directive, error) {
			return interceptors.InvokeResult{
				Interceptor: entry.Interceptor.Name,
				Type:        interceptors.TypeValidation,
				Phase:       interceptors.PhaseRequest,
				Validation:  &interceptors.ValidationResult{Valid: true},
			}, nil, nil
		},
	)

	ch := setupChainWithOpts(t, []chain.ChainOption{chain.WithExecutionHandler(handler)}, v)
	payload, _ := json.Marshal(map[string]any{"value": "test"})
	cr, err := ch.Execute(context.Background(), &chain.ExecutionParams{
		Event:   "test/event",
		Phase:   interceptors.PhaseRequest,
		Payload: payload,
	})
	require.NoError(t, err)
	assert.Equal(t, chain.ChainSuccess, cr.Status)
	assert.False(t, invoked, "handler short-circuited, server should not be invoked")
}

func TestChain_FailOpenRecordsExecutionResult(t *testing.T) {
	t.Parallel()

	t.Run("fail-open validator error is recorded", func(t *testing.T) {
		t.Parallel()
		failOpenValidator := &interceptors.Validator{
			Metadata: interceptors.Metadata{
				Name: "fo-validator",
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseRequest,
				}},
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
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseRequest,
				}},
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
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseResponse,
				}},
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
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseResponse,
				}},
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

func TestChain_AuditModeErrorsDoNotAbort(t *testing.T) {
	t.Parallel()

	t.Run("audit validator error is recorded without aborting", func(t *testing.T) {
		t.Parallel()
		auditValidator := &interceptors.Validator{
			Metadata: interceptors.Metadata{
				Name: "audit-validator",
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseRequest,
				}},
				Mode: interceptors.ModeAudit,
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
				return nil, fmt.Errorf("audit sink failed")
			},
		}

		ch := setupChainWithInterceptors(t, auditValidator)

		payload, _ := json.Marshal(map[string]any{"value": "hello"})
		cr, err := ch.Execute(context.Background(), &chain.ExecutionParams{
			Event:   "test/event",
			Phase:   interceptors.PhaseRequest,
			Payload: payload,
		})
		require.NoError(t, err)

		assert.Equal(t, chain.ChainSuccess, cr.Status)
		assert.Empty(t, cr.AbortedAt)
		require.Len(t, cr.Results, 1)
		assert.Equal(t, "audit-validator", cr.Results[0].Interceptor)
	})

	t.Run("audit mutator error is recorded without aborting", func(t *testing.T) {
		t.Parallel()
		auditMutator := &interceptors.Mutator{
			Metadata: interceptors.Metadata{
				Name: "audit-mutator",
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseResponse,
				}},
				Mode:         interceptors.ModeAudit,
				PriorityHint: interceptors.NewPriority(10),
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
				return nil, fmt.Errorf("shadow mutation failed")
			},
		}
		passingMutator := &interceptors.Mutator{
			Metadata: interceptors.Metadata{
				Name: "passing-mutator",
				Hooks: []interceptors.Hook{{
					Events: []string{"test/event"},
					Phase:  interceptors.PhaseResponse,
				}},
				Mode:         interceptors.ModeEnforce,
				PriorityHint: interceptors.NewPriority(20),
			},
			Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
				modified, _ := json.Marshal(map[string]any{"value": "mutated"})
				return &interceptors.MutationResult{Modified: true, Payload: modified}, nil
			},
		}

		ch := setupChainWithInterceptors(t, auditMutator, passingMutator)

		payload, _ := json.Marshal(map[string]any{"value": "hello"})
		cr, err := ch.Execute(context.Background(), &chain.ExecutionParams{
			Event:   "test/event",
			Phase:   interceptors.PhaseResponse,
			Payload: payload,
		})
		require.NoError(t, err)

		assert.Equal(t, chain.ChainSuccess, cr.Status)
		assert.Empty(t, cr.AbortedAt)
		require.Len(t, cr.Results, 2)
		require.NotNil(t, cr.FinalPayload)
	})
}
