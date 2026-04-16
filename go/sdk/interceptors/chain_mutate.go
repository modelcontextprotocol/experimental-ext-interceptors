// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"time"
)

// mutatorOutcome describes the result of executing a single mutator, used by
// the runMutators loop to decide whether to continue, skip, or halt the chain.
type mutatorOutcome int

const (
	mutatorOK      mutatorOutcome = iota // handler succeeded, payload may have been updated
	mutatorSkipped                       // handler failed (fail-open) or audit mode; continue chain
	mutatorAborted                       // handler failed (fail-closed); halt chain
)

// runMutators runs mutators sequentially in priority order. Mutators modify
// the typed payload in place via type assertion on inv.Payload. For
// audit-mode mutators, a deep-copied invocation is used so modifications
// don't affect the real payload.
func (ce *chainExecutor) runMutators(ctx context.Context, inv *Invocation, cr *ChainResult) {
	if len(ce.mutators) == 0 {
		return
	}

	for _, m := range ce.mutators {
		if ctx.Err() != nil {
			cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
				Reason: "context cancelled during mutation chain",
				Type:   AbortTimeout,
				Phase:  string(inv.Phase),
			})
			return
		}

		// Audit-mode mutators work on a deep copy so in-place
		// modifications don't affect the real payload.
		mInv := inv
		if m.Mode == ModeAudit {
			var err error
			mInv, err = inv.withCopiedPayload()
			if err != nil {
				ce.logger.Warn("failed to deep-copy payload for audit mutator, skipping",
					"interceptor", m.Name,
					"error", err,
				)
				continue
			}
		}

		if ce.executeMutator(ctx, m, mInv, cr) == mutatorAborted {
			return
		}
	}
}

// executeMutator runs a single mutator handler and manages its full lifecycle:
//
//  1. Timeout setup: wraps ctx with a per-interceptor deadline if configured.
//  2. Handler invocation: calls the mutator's Handler with the current payload.
//  3. Error handling: on failure, checks FailOpen to decide between abort and skip.
//     Fail-closed records both an AbortInfo and an ExecutionResult for audit.
//  4. Audit mode: records the ExecutionResult but does not apply payload changes
//     (the caller already passed a deep-copied invocation for audit mutators).
//  5. In-place mutation: the handler modifies the typed value directly via type
//     assertion on inv.Payload. result.Modified is advisory (recorded in the
//     ExecutionResult for observability) but not checked by the chain.
func (ce *chainExecutor) executeMutator(
	ctx context.Context, m *Mutator, mInv *Invocation,
	cr *ChainResult,
) mutatorOutcome {
	if m.Handler == nil {
		ce.logger.Warn("mutator has nil handler, skipping",
			"interceptor", m.Name,
		)
		return mutatorSkipped
	}

	mStart := time.Now()
	result, err := m.Handler(ctx, mInv)
	dur := time.Since(mStart).Milliseconds()

	if err != nil {
		// Distinguish timeout from general mutation errors for downstream consumers.
		abortType := AbortMutation
		if ctx.Err() == context.DeadlineExceeded {
			abortType = AbortTimeout
		}
		ce.logger.Warn("mutator error",
			"interceptor", m.Name,
			"error", err,
		)
		// Always record the execution result for observability,
		// regardless of fail-open/fail-closed.
		cr.Results = append(cr.Results, ExecutionResult{
			Interceptor: m.Name,
			Type:        TypeMutation,
			Phase:       mInv.Phase,
			DurationMs:  dur,
			Error:       err.Error(),
		})
		if !m.FailOpen {
			// Fail-closed: record abort entry and halt chain.
			cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
				Interceptor: m.Name,
				Reason:      err.Error(),
				Type:        abortType,
				Phase:       string(mInv.Phase),
			})
			return mutatorAborted
		}
		// Fail-open: execution result above is the audit trail;
		// no abort, continue chain.
		return mutatorSkipped
	}

	cr.Results = append(cr.Results, ExecutionResult{
		Interceptor: m.Name,
		Type:        TypeMutation,
		Phase:       mInv.Phase,
		DurationMs:  dur,
		Mutation:    result,
	})

	// Guard against handlers that return (nil, nil).
	if result == nil {
		return mutatorSkipped
	}

	// In audit mode, the mutation result is recorded for observability but
	// the payload is not modified — the caller already gave us a deep copy.
	if m.Mode == ModeAudit {
		return mutatorSkipped
	}

	return mutatorOK
}
