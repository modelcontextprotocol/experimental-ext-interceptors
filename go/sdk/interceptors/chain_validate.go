// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"sync"
	"time"
)

// validatorResult holds the output of a single validator handler call,
// separating execution from result recording so the handler can run
// without holding a lock.
type validatorResult struct {
	result     *ValidationResult
	err        error
	durationMs int64
	timedOut   bool
}

// runValidators runs all validators concurrently, collects their results, and
// records any abort conditions. All validators run to completion
// before any abort decision is made — this ensures the ChainResult contains a
// complete picture of all validation findings, not just the first failure.
//
// A mutex guards all writes to the shared ChainResult (Results, AbortedAt,
// ValidationSummary) since goroutines append concurrently.
//
// Error handling per validator:
//   - Handler returns error + FailOpen=false (fail-closed): records an
//     ExecutionResult (for timing/audit) and an AbortInfo to block the chain.
//   - Handler returns error + FailOpen=true: logs and records an
//     ExecutionResult (for observability) but does not abort.
//   - Handler returns a result with Valid=false + Mode!=Audit (enforced):
//     scans messages for the first error-severity entry and records an AbortInfo.
//   - Handler returns a result with Valid=false + Mode=Audit: records normally
//     without aborting (audit-only observation).
func (ce *chainExecutor) runValidators(ctx context.Context, inv *Invocation, cr *ChainResult) {
	if len(ce.validators) == 0 {
		return
	}

	// Fast path: single validator doesn't need goroutine, WaitGroup, or Mutex.
	if len(ce.validators) == 1 {
		v := ce.validators[0]
		vr := ce.executeValidator(ctx, v, inv)
		ce.recordValidation(v, inv, cr, vr)
		return
	}

	var (
		mu sync.Mutex
		wg sync.WaitGroup
	)

	for _, v := range ce.validators {
		wg.Add(1)
		go func(v *Validator) {
			defer wg.Done()

			vr := ce.executeValidator(ctx, v, inv)

			mu.Lock()
			ce.recordValidation(v, inv, cr, vr)
			mu.Unlock()
		}(v)
	}

	wg.Wait()
}

// executeValidator runs a single validator handler and returns its output.
// It does not modify ChainResult, so it is safe to call without a lock.
func (ce *chainExecutor) executeValidator(ctx context.Context, v *Validator, inv *Invocation) validatorResult {
	if v.Handler == nil {
		ce.logger.Warn("validator has nil handler, skipping",
			"interceptor", v.Name,
		)
		return validatorResult{}
	}

	vStart := time.Now()
	result, err := v.Handler(ctx, inv)
	dur := time.Since(vStart).Milliseconds()

	return validatorResult{
		result:     result,
		err:        err,
		durationMs: dur,
		timedOut:   ctx.Err() == context.DeadlineExceeded,
	}
}

// recordValidation writes a validator's execution outcome into the
// ChainResult. The caller must ensure exclusive access to cr (either
// by holding a lock or being the only writer in the N=1 fast path).
func (ce *chainExecutor) recordValidation(v *Validator, inv *Invocation, cr *ChainResult, vr validatorResult) {
	if vr.err != nil {
		// Distinguish timeout errors from general validation errors
		// so downstream consumers can differentiate root cause.
		abortType := AbortValidation
		if vr.timedOut {
			abortType = AbortTimeout
		}
		ce.logger.Warn("validator error",
			"interceptor", v.Name,
			"error", vr.err,
		)
		// Always record the execution result for observability,
		// regardless of fail-open/fail-closed.
		cr.Results = append(cr.Results, ExecutionResult{
			Interceptor: v.Name,
			Type:        TypeValidation,
			Phase:       inv.Phase,
			DurationMs:  vr.durationMs,
			Error:       vr.err.Error(),
		})
		// Fail-closed: record an abort entry to halt the chain.
		// Fail-open: the execution result above is the audit trail;
		// no abort is recorded.
		if !v.FailOpen {
			cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
				Interceptor: v.Name,
				Reason:      vr.err.Error(),
				Type:        abortType,
				Phase:       string(inv.Phase),
			})
		}
		return
	}

	// Guard against handlers that return (nil, nil).
	if vr.result == nil {
		return
	}

	cr.Results = append(cr.Results, ExecutionResult{
		Interceptor: v.Name,
		Type:        TypeValidation,
		Phase:       inv.Phase,
		DurationMs:  vr.durationMs,
		Validation:  vr.result,
	})

	// Tally validation summary.
	for _, msg := range vr.result.Messages {
		switch msg.Severity {
		case SeverityError:
			cr.ValidationSummary.Errors++
		case SeverityWarn:
			cr.ValidationSummary.Warnings++
		case SeverityInfo:
			cr.ValidationSummary.Infos++
		}
	}

	// Only error-severity messages cause an abort. A validator
	// returning Valid=false with only warn/info messages does NOT
	// block the chain — the findings are recorded in the
	// ValidationSummary and ExecutionResult but execution continues.
	//
	// In audit mode (ModeAudit), even error-severity messages don't
	// abort — the result is recorded for observability only.
	//
	// When multiple error messages exist, only the first is recorded
	// as the abort reason. All messages remain visible in the
	// ValidationResult attached to the ExecutionResult.
	if v.Mode != ModeAudit && !vr.result.Valid {
		for _, msg := range vr.result.Messages {
			if msg.Severity == SeverityError {
				cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
					Interceptor: v.Name,
					Reason:      msg.Message,
					Type:        AbortValidation,
					Phase:       string(inv.Phase),
				})
				break
			}
		}
	}
}
