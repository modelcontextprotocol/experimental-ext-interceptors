// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"fmt"
	"log/slog"
	"sort"
	"sync"
	"time"
)

// interceptorSnapshot is an immutable snapshot of registered interceptors,
// created once per Add call. It holds the interceptor list and
// lazily-built chain executors keyed by "event|phase". Swapping the snapshot
// pointer implicitly invalidates all cached chains.
type interceptorSnapshot struct {
	all    []Interceptor
	logger *slog.Logger
	chains sync.Map // "event|phase" -> *chainExecutor
}

// getChain returns a cached chainExecutor for the given event and phase,
// building one on first access via newChainExecutor.
func (snap *interceptorSnapshot) getChain(event string, phase InterceptionPhase) *chainExecutor {
	key := event + "|" + string(phase)
	if v, ok := snap.chains.Load(key); ok {
		return v.(*chainExecutor)
	}
	ce := newChainExecutor(snap.all, event, phase, snap.logger)
	v, _ := snap.chains.LoadOrStore(key, ce)
	return v.(*chainExecutor)
}

// chainExecutor holds the filtered and sorted interceptors applicable to a
// specific event+phase pair, and orchestrates their execution according to
// trust-boundary-aware ordering.
//
// Validators and mutators are separated at construction time so that each
// execution method (executeForReceiving, executeForSending) can run them
// in the correct order without re-filtering.
type chainExecutor struct {
	empty      bool         // true when no validators or mutators matched
	validators []*Validator // validators matching this event+phase (run in parallel)
	mutators   []*Mutator   // mutators matching this event+phase (run sequentially by priority)
	resultsCap int          // len(validators) + len(mutators); used to pre-allocate ChainResult.Results

	logger *slog.Logger
}

// newChainExecutor builds a chainExecutor by filtering the global interceptor
// list down to those that match the target phase and event. Mutators are then
// sorted by priority (ascending) with alphabetical name as a tiebreaker,
// which guarantees deterministic execution order.
func newChainExecutor(
	all []Interceptor,
	event string,
	phase InterceptionPhase,
	logger *slog.Logger,
) *chainExecutor {
	ce := &chainExecutor{logger: logger}
	for _, i := range all {
		meta := i.GetMetadata()
		if !matchesPhase(meta.Hook.Phase, phase) || !matchesEvent(meta.Hook.Events, event) {
			continue
		}
		switch v := i.(type) {
		case *Validator:
			ce.validators = append(ce.validators, v)
		case *Mutator:
			ce.mutators = append(ce.mutators, v)
		default:
			logger.Warn("unknown interceptor type, skipping",
				"interceptor", meta.Name,
				"type", fmt.Sprintf("%T", i),
			)
		}
	}
	// Sort mutators by priority (ascending), alphabetical tiebreak.
	sort.Slice(ce.mutators, func(i, j int) bool {
		pi := ce.mutators[i].PriorityHint.Resolve(phase)
		pj := ce.mutators[j].PriorityHint.Resolve(phase)
		if pi != pj {
			return pi < pj
		}
		return ce.mutators[i].Name < ce.mutators[j].Name
	})
	ce.empty = len(ce.validators) == 0 && len(ce.mutators) == 0
	ce.resultsCap = len(ce.validators) + len(ce.mutators)
	return ce
}

// executeForReceiving runs the chain for incoming requests (server receiving)
// using the receive-side ordering:
//
//	Receive -> Validate (parallel) -> Mutate (sequential)
//
// Validators run first as a security barrier: if any enforced
// validator produces an error-severity message, the chain aborts with
// "validation_failed" before any mutators run, so the payload is
// unmodified. Only after all validators pass do mutators run
// sequentially, modifying the typed payload in place. If a mutator
// aborts, the chain returns with the abort recorded.
func (ce *chainExecutor) executeForReceiving(ctx context.Context, inv *Invocation) (*ChainResult, error) {
	start := time.Now()
	cr := &ChainResult{
		Event:        inv.Event,
		Phase:        inv.Phase,
		FinalPayload: inv.Payload,
		Results:      make([]ExecutionResult, 0, ce.resultsCap),
	}

	// 1. Run validators in parallel.
	ce.runValidators(ctx, inv, cr)
	if len(cr.AbortedAt) > 0 {
		cr.Status = ChainValidationFailed
		cr.TotalDurationMs = time.Since(start).Milliseconds()
		return cr, nil
	}
	if err := ctx.Err(); err != nil {
		return ce.timeoutResult(cr, start), nil
	}

	// 2. Run mutators sequentially (in-place on inv.Payload).
	ce.runMutators(ctx, inv, cr)
	if len(cr.AbortedAt) > 0 {
		cr.Status = ChainMutationFailed
		cr.TotalDurationMs = time.Since(start).Milliseconds()
		return cr, nil
	}

	cr.Status = ChainSuccess
	cr.TotalDurationMs = time.Since(start).Milliseconds()
	return cr, nil
}

// executeForSending runs the chain for outgoing responses (server sending)
// using the send-side ordering:
//
//	Mutate (sequential) -> Validate (parallel) -> Send
//
// Mutators run first to prepare/sanitize outgoing data, then
// validators check the (now mutated) payload before it leaves the server.
// Mutators modify the typed value in place, so validators automatically
// see the post-mutation state.
func (ce *chainExecutor) executeForSending(ctx context.Context, inv *Invocation) (*ChainResult, error) {
	start := time.Now()
	cr := &ChainResult{
		Event:        inv.Event,
		Phase:        inv.Phase,
		FinalPayload: inv.Payload,
		Results:      make([]ExecutionResult, 0, ce.resultsCap),
	}

	// 1. Run mutators sequentially (in-place on inv.Payload).
	ce.runMutators(ctx, inv, cr)
	if len(cr.AbortedAt) > 0 {
		cr.Status = ChainMutationFailed
		cr.TotalDurationMs = time.Since(start).Milliseconds()
		return cr, nil
	}
	if err := ctx.Err(); err != nil {
		return ce.timeoutResult(cr, start), nil
	}

	// 2. Run validators in parallel.
	ce.runValidators(ctx, inv, cr)
	if len(cr.AbortedAt) > 0 {
		cr.Status = ChainValidationFailed
		cr.TotalDurationMs = time.Since(start).Milliseconds()
		return cr, nil
	}

	cr.Status = ChainSuccess
	cr.TotalDurationMs = time.Since(start).Milliseconds()
	return cr, nil
}

// timeoutResult sets the ChainResult to "timeout" status and appends
// an AbortInfo entry. Used when the parent context is cancelled between
// chain stages (e.g. after validators but before mutators).
func (ce *chainExecutor) timeoutResult(cr *ChainResult, start time.Time) *ChainResult {
	cr.Status = ChainTimeout
	cr.TotalDurationMs = time.Since(start).Milliseconds()
	cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
		Reason: "chain execution timeout exceeded",
		Type:   AbortTimeout,
		Phase:  string(cr.Phase),
	})
	return cr
}

// matchesPhase checks if an interceptor's configured phase covers the target
// phase. An interceptor with PhaseBoth matches any target phase.
func matchesPhase(interceptorPhase, targetPhase InterceptionPhase) bool {
	return interceptorPhase == PhaseBoth || interceptorPhase == targetPhase
}

// matchesEvent checks if any of the interceptor's registered event patterns
// match the given event string. Currently uses exact string comparison only.
//
// TODO: support wildcard patterns (e.g. "*", "*/request", "tools/*").
func matchesEvent(interceptorEvents []string, event string) bool {
	for _, pattern := range interceptorEvents {
		if pattern == event {
			return true
		}
	}
	return false
}
