// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package chain

import (
	"context"
	"encoding/json"
	"log/slog"
	"slices"
	"sort"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
)

// ChainEntry pairs an interceptor descriptor with the MCP client
// session that hosts it.
type ChainEntry struct {
	Interceptor interceptors.InterceptorInfo
	Server      *mcp.ClientSession
}

// Chain is the SEP-compliant interceptor chain orchestrator. It holds
// ChainEntry objects (interceptor descriptors + MCP server connections),
// discovers interceptors via interceptors/list, and invokes them via
// interceptor/invoke on the appropriate server.
type Chain struct {
	mu      sync.Mutex
	entries []ChainEntry
	logger  *slog.Logger
}

// ChainOption configures a Chain.
type ChainOption func(*Chain)

// WithChainLogger sets the logger for chain execution.
// If not set, slog.Default() is used.
func WithChainLogger(l *slog.Logger) ChainOption {
	return func(c *Chain) {
		c.logger = l
	}
}

// NewChain creates a new Chain with optional configuration.
func NewChain(opts ...ChainOption) *Chain {
	c := &Chain{}
	for _, opt := range opts {
		opt(c)
	}
	if c.logger == nil {
		c.logger = slog.Default()
	}
	return c
}

// ExecutionParams configures a chain execution run. These are
// SDK-level types used by Chain.Execute, not wire-protocol types.
type ExecutionParams struct {
	Event        string                          `json:"event"`
	Phase        interceptors.InterceptionPhase  `json:"phase"`
	Payload      json.RawMessage                 `json:"payload"`
	Interceptors []string                        `json:"interceptors,omitempty"` // optional: restrict to named interceptors
	Config       map[string]map[string]any       `json:"config,omitempty"`       // optional: per-interceptor config
	TimeoutMs    int64                           `json:"timeoutMs,omitempty"`
	Context      *interceptors.InvocationContext `json:"context,omitempty"`
}

// ExecutionResult aggregates results from executing the full
// interceptor chain via interceptor/invoke RPCs.
type ExecutionResult struct {
	Status            ChainStatus                    `json:"status"`
	Event             string                         `json:"event"`
	Phase             interceptors.InterceptionPhase `json:"phase"`
	Results           []interceptors.InvokeResult    `json:"results"`
	FinalPayload      json.RawMessage                `json:"finalPayload,omitempty"`
	ValidationSummary ValidationSummary              `json:"validationSummary"`
	TotalDurationMs   int64                          `json:"totalDurationMs"`
	AbortedAt         []AbortInfo                    `json:"abortedAt,omitempty"`
}

// AddMCPServer discovers interceptors from an MCP server via
// interceptors/list and adds entries for each. The client session's
// transport determines how interceptor/invoke calls are made
// (in-memory, stdio, HTTP, etc.).
func (c *Chain) AddMCPServer(ctx context.Context, cs *mcp.ClientSession) error {
	var result interceptors.ListResult
	if err := cs.CallCustom(ctx, interceptors.MethodList, nil, &result); err != nil {
		return err
	}

	c.mu.Lock()
	defer c.mu.Unlock()
	for _, info := range result.Interceptors {
		c.entries = append(c.entries, ChainEntry{
			Interceptor: info,
			Server:      cs,
		})
	}
	return nil
}

// Execute runs the chain for a given event and phase per the SEP
// execution model:
//
//   - Filter entries by event + phase
//   - Separate into validators and mutators
//   - Sort mutators by priorityHint (ascending, alphabetical tiebreak)
//   - Request phase: validate (parallel) then mutate (sequential)
//   - Response phase: mutate (sequential) then validate (parallel)
//   - Call interceptor/invoke via CallCustom for each entry
//   - For mutators: pass mutated payload from previous to next
//   - Handle abort, timeout, fail-open
func (c *Chain) Execute(ctx context.Context, params *ExecutionParams) (*ExecutionResult, error) {
	c.mu.Lock()
	entries := make([]ChainEntry, len(c.entries))
	copy(entries, c.entries)
	c.mu.Unlock()

	// Apply timeout if specified.
	if params.TimeoutMs > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, time.Duration(params.TimeoutMs)*time.Millisecond)
		defer cancel()
	}

	start := time.Now()

	// Filter entries by event + phase, respecting optional name filter.
	nameFilter := make(map[string]bool, len(params.Interceptors))
	for _, n := range params.Interceptors {
		nameFilter[n] = true
	}

	var validators []ChainEntry
	var mutators []ChainEntry

	for _, e := range entries {
		if len(nameFilter) > 0 && !nameFilter[e.Interceptor.Name] {
			continue
		}
		if !matchesPhase(e.Interceptor.Hook.Phase, params.Phase) {
			continue
		}
		if !slices.Contains(e.Interceptor.Hook.Events, params.Event) {
			continue
		}
		switch e.Interceptor.Type {
		case interceptors.TypeValidation:
			validators = append(validators, e)
		case interceptors.TypeMutation:
			mutators = append(mutators, e)
		default:
			c.logger.Warn("unknown interceptor type, skipping",
				"interceptor", e.Interceptor.Name,
				"type", e.Interceptor.Type,
			)
		}
	}

	// Sort mutators by priority (ascending), alphabetical tiebreak.
	sort.Slice(mutators, func(i, j int) bool {
		pi := mutators[i].Interceptor.PriorityHint.Resolve(params.Phase)
		pj := mutators[j].Interceptor.PriorityHint.Resolve(params.Phase)
		if pi != pj {
			return pi < pj
		}
		return mutators[i].Interceptor.Name < mutators[j].Interceptor.Name
	})

	cr := &ExecutionResult{
		Event:   params.Event,
		Phase:   params.Phase,
		Results: make([]interceptors.InvokeResult, 0, len(validators)+len(mutators)),
	}

	// Empty chain fast path.
	if len(validators) == 0 && len(mutators) == 0 {
		cr.Status = ChainSuccess
		cr.TotalDurationMs = time.Since(start).Milliseconds()
		return cr, nil
	}

	// Trust-boundary-aware ordering.
	switch params.Phase {
	case interceptors.PhaseRequest:
		// Request (receiving): validate → mutate
		c.runValidators(ctx, params, validators, cr)
		if len(cr.AbortedAt) > 0 {
			cr.Status = ChainValidationFailed
			cr.TotalDurationMs = time.Since(start).Milliseconds()
			return cr, nil
		}
		if ctx.Err() != nil {
			c.timeoutResult(cr, start)
			return cr, nil
		}
		c.runMutators(ctx, params, mutators, cr)
		if len(cr.AbortedAt) > 0 {
			cr.Status = ChainMutationFailed
			cr.TotalDurationMs = time.Since(start).Milliseconds()
			return cr, nil
		}

	case interceptors.PhaseResponse:
		// Response (sending): mutate → validate
		c.runMutators(ctx, params, mutators, cr)
		if len(cr.AbortedAt) > 0 {
			cr.Status = ChainMutationFailed
			cr.TotalDurationMs = time.Since(start).Milliseconds()
			return cr, nil
		}
		if ctx.Err() != nil {
			c.timeoutResult(cr, start)
			return cr, nil
		}
		// Validators must see the post-mutation payload.
		valParams := *params
		if cr.FinalPayload != nil {
			valParams.Payload = cr.FinalPayload
		}
		c.runValidators(ctx, &valParams, validators, cr)
		if len(cr.AbortedAt) > 0 {
			cr.Status = ChainValidationFailed
			cr.TotalDurationMs = time.Since(start).Milliseconds()
			return cr, nil
		}
	}

	cr.Status = ChainSuccess
	cr.TotalDurationMs = time.Since(start).Milliseconds()
	return cr, nil
}

// runValidators invokes all validators in parallel via interceptor/invoke.
func (c *Chain) runValidators(
	ctx context.Context,
	params *ExecutionParams,
	validators []ChainEntry,
	cr *ExecutionResult,
) {
	if len(validators) == 0 {
		return
	}

	var (
		mu sync.Mutex
		wg sync.WaitGroup
	)
	for _, v := range validators {
		wg.Add(1)
		go func(v ChainEntry) {
			defer wg.Done()
			result := c.callInvoke(ctx, params, v)
			mu.Lock()
			c.recordValidation(v, result, cr)
			mu.Unlock()
		}(v)
	}
	wg.Wait()
}

// recordValidation processes a validator's invoke result and updates the chain result.
func (c *Chain) recordValidation(entry ChainEntry, result invokeOutcome, cr *ExecutionResult) {
	if result.err != nil {
		c.logger.Warn("validator invoke error",
			"interceptor", entry.Interceptor.Name,
			"error", result.err,
		)
		cr.Results = append(cr.Results, interceptors.InvokeResult{
			Interceptor: entry.Interceptor.Name,
			Type:        interceptors.TypeValidation,
			Phase:       cr.Phase,
		})
		if !entry.Interceptor.FailOpen {
			cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
				Interceptor: entry.Interceptor.Name,
				Reason:      result.err.Error(),
				Type:        AbortValidation,
				Phase:       string(cr.Phase),
			})
		}
		return
	}

	cr.Results = append(cr.Results, result.result)

	// Tally validation summary and check for abort in a single pass.
	if result.result.Validation != nil {
		shouldAbort := entry.Interceptor.Mode != interceptors.ModeAudit && !result.result.Validation.Valid
		aborted := false
		for _, msg := range result.result.Validation.Messages {
			switch msg.Severity {
			case interceptors.SeverityError:
				cr.ValidationSummary.Errors++
				if shouldAbort && !aborted {
					cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
						Interceptor: entry.Interceptor.Name,
						Reason:      msg.Message,
						Type:        AbortValidation,
						Phase:       string(cr.Phase),
					})
					aborted = true
				}
			case interceptors.SeverityWarn:
				cr.ValidationSummary.Warnings++
			case interceptors.SeverityInfo:
				cr.ValidationSummary.Infos++
			}
		}
	}
}

// runMutators invokes mutators sequentially via interceptor/invoke,
// passing the mutated payload from each mutator to the next.
func (c *Chain) runMutators(
	ctx context.Context,
	params *ExecutionParams,
	mutators []ChainEntry,
	cr *ExecutionResult,
) {
	if len(mutators) == 0 {
		return
	}

	currentPayload := params.Payload
	mutated := false

	for _, m := range mutators {
		if ctx.Err() != nil {
			cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
				Reason: "context cancelled during mutation chain",
				Type:   AbortTimeout,
				Phase:  string(params.Phase),
			})
			return
		}

		// Build invoke params with current payload.
		mutParams := *params
		mutParams.Payload = currentPayload

		result := c.callInvoke(ctx, &mutParams, m)

		if result.err != nil {
			c.logger.Warn("mutator invoke error",
				"interceptor", m.Interceptor.Name,
				"error", result.err,
			)
			cr.Results = append(cr.Results, interceptors.InvokeResult{
				Interceptor: m.Interceptor.Name,
				Type:        interceptors.TypeMutation,
				Phase:       params.Phase,
			})
			if !m.Interceptor.FailOpen {
				cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
					Interceptor: m.Interceptor.Name,
					Reason:      result.err.Error(),
					Type:        AbortMutation,
					Phase:       string(params.Phase),
				})
				return
			}
			continue
		}

		cr.Results = append(cr.Results, result.result)

		// For audit-mode mutators, don't apply the mutated payload.
		if m.Interceptor.Mode == interceptors.ModeAudit {
			continue
		}

		// Apply mutated payload for the next mutator.
		if result.result.Payload != nil {
			currentPayload = result.result.Payload
			mutated = true
		}
	}

	if mutated {
		cr.FinalPayload = currentPayload
	}
}

// invokeOutcome wraps the result of a single interceptor/invoke call.
type invokeOutcome struct {
	result interceptors.InvokeResult
	err    error
}

// callInvoke calls interceptor/invoke on the appropriate server for
// a single chain entry.
func (c *Chain) callInvoke(
	ctx context.Context,
	params *ExecutionParams,
	entry ChainEntry,
) invokeOutcome {
	invokeParams := &interceptors.InvokeParams{
		Name:    entry.Interceptor.Name,
		Event:   params.Event,
		Phase:   params.Phase,
		Payload: params.Payload,
		Context: params.Context,
	}

	// Apply per-interceptor config if provided.
	if cfg, ok := params.Config[entry.Interceptor.Name]; ok {
		invokeParams.Config = cfg
	}

	var result interceptors.InvokeResult
	err := entry.Server.CallCustom(ctx, interceptors.MethodInvoke, invokeParams, &result)
	if err != nil {
		return invokeOutcome{err: err}
	}
	return invokeOutcome{result: result}
}

// timeoutResult sets the chain result to timeout status.
func (c *Chain) timeoutResult(cr *ExecutionResult, start time.Time) {
	cr.Status = ChainTimeout
	cr.TotalDurationMs = time.Since(start).Milliseconds()
	cr.AbortedAt = append(cr.AbortedAt, AbortInfo{
		Reason: "chain execution timeout exceeded",
		Type:   AbortTimeout,
		Phase:  string(cr.Phase),
	})
}

// matchesPhase checks if an interceptor's configured phase covers the target
// phase. An interceptor with PhaseBoth matches any target phase.
func matchesPhase(interceptorPhase, targetPhase interceptors.InterceptionPhase) bool {
	return interceptorPhase == interceptors.PhaseBoth || interceptorPhase == targetPhase
}
