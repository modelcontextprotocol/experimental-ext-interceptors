// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"fmt"
	"log/slog"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

// stubPayload is a minimal payload for chain-level tests.
type stubPayload struct{ Value string }

func TestChain_FailOpenRecordsExecutionResult(t *testing.T) {
	t.Parallel()
	// Verifies that fail-open interceptors that return errors still
	// have an ExecutionResult recorded in ChainResult.Results, even
	// though they don't produce an AbortInfo.

	t.Run("fail-open validator error is recorded", func(t *testing.T) {
		t.Parallel()
		failOpenValidator := &Validator{
			Metadata: Metadata{
				Name: "fo-validator",
				Hook: Hook{
					Events: []string{"test/event"},
					Phase:  PhaseRequest,
				},
				Mode:     ModeEnforce,
				FailOpen: true,
			},
			Handler: func(_ context.Context, _ *Invocation) (*ValidationResult, error) {
				return nil, fmt.Errorf("transient failure")
			},
		}
		passingValidator := &Validator{
			Metadata: Metadata{
				Name: "passing-validator",
				Hook: Hook{
					Events: []string{"test/event"},
					Phase:  PhaseRequest,
				},
				Mode: ModeEnforce,
			},
			Handler: func(_ context.Context, _ *Invocation) (*ValidationResult, error) {
				return &ValidationResult{Valid: true}, nil
			},
		}

		chain := NewChain(WithChainLogger(slog.Default()))
		chain.Add(failOpenValidator).Add(passingValidator)

		inv := &Invocation{
			Event:   "test/event",
			Phase:   PhaseRequest,
			Payload: &stubPayload{Value: "hello"},
		}

		cr, err := chain.ExecuteForReceiving(context.Background(), inv)
		require.NoError(t, err)

		// Chain should succeed — fail-open doesn't abort.
		assert.Equal(t, ChainSuccess, cr.Status)
		assert.Empty(t, cr.AbortedAt)

		// Both interceptors should have an ExecutionResult.
		require.Len(t, cr.Results, 2)
		var found bool
		for _, r := range cr.Results {
			if r.Interceptor == "fo-validator" {
				found = true
				assert.Equal(t, TypeValidation, r.Type)
				assert.Equal(t, "transient failure", r.Error)
			}
		}
		assert.True(t, found, "fail-open validator should have an ExecutionResult")
	})

	t.Run("fail-open mutator error is recorded", func(t *testing.T) {
		t.Parallel()
		failOpenMutator := &Mutator{
			Metadata: Metadata{
				Name: "fo-mutator",
				Hook: Hook{
					Events: []string{"test/event"},
					Phase:  PhaseResponse,
				},
				Mode:         ModeEnforce,
				FailOpen:     true,
				PriorityHint: NewPriority(10),
			},
			Handler: func(_ context.Context, _ *Invocation) (*MutationResult, error) {
				return nil, fmt.Errorf("transient failure")
			},
		}
		passingMutator := &Mutator{
			Metadata: Metadata{
				Name: "passing-mutator",
				Hook: Hook{
					Events: []string{"test/event"},
					Phase:  PhaseResponse,
				},
				Mode:         ModeEnforce,
				PriorityHint: NewPriority(20),
			},
			Handler: func(_ context.Context, _ *Invocation) (*MutationResult, error) {
				return &MutationResult{Modified: false}, nil
			},
		}

		chain := NewChain(WithChainLogger(slog.Default()))
		chain.Add(failOpenMutator).Add(passingMutator)

		inv := &Invocation{
			Event:   "test/event",
			Phase:   PhaseResponse,
			Payload: &stubPayload{Value: "hello"},
		}

		cr, err := chain.ExecuteForSending(context.Background(), inv)
		require.NoError(t, err)

		// Chain should succeed — fail-open doesn't abort.
		assert.Equal(t, ChainSuccess, cr.Status)
		assert.Empty(t, cr.AbortedAt)

		// Both interceptors should have an ExecutionResult.
		require.Len(t, cr.Results, 2)
		var found bool
		for _, r := range cr.Results {
			if r.Interceptor == "fo-mutator" {
				found = true
				assert.Equal(t, TypeMutation, r.Type)
				assert.Equal(t, "transient failure", r.Error)
			}
		}
		assert.True(t, found, "fail-open mutator should have an ExecutionResult")
	})
}
