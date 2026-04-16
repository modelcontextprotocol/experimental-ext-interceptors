// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

// --- Interceptor result types ---

// ValidationMessage is a single validation finding.
type ValidationMessage struct {
	Path     string   `json:"path,omitempty"`
	Message  string   `json:"message"`
	Severity Severity `json:"severity"`
}

// ValidationSuggestion is an optional suggested correction.
type ValidationSuggestion struct {
	Path  string `json:"path"`
	Value any    `json:"value"`
}

// ValidationResult is returned by validation interceptors.
type ValidationResult struct {
	Valid       bool                   `json:"valid"`
	Severity    Severity               `json:"severity,omitempty"`
	Messages    []ValidationMessage    `json:"messages,omitempty"`
	Suggestions []ValidationSuggestion `json:"suggestions,omitempty"`
}

// MutationResult is returned by mutation interceptors.
type MutationResult struct {
	Modified bool           `json:"modified"`
	Info     map[string]any `json:"info,omitempty"`
}

// --- Chain result ---

// ChainStatus describes the outcome of a full interceptor chain execution.
type ChainStatus string

const (
	ChainSuccess          ChainStatus = "success"
	ChainValidationFailed ChainStatus = "validation_failed"
	ChainMutationFailed   ChainStatus = "mutation_failed"
	ChainTimeout          ChainStatus = "timeout"
)

// AbortType classifies the reason an interceptor chain was aborted.
type AbortType string

const (
	AbortValidation AbortType = "validation"
	AbortMutation   AbortType = "mutation"
	AbortTimeout    AbortType = "timeout"
)

// ChainResult aggregates results from executing the full interceptor chain.
type ChainResult struct {
	Status            ChainStatus       `json:"status"`
	Event             string            `json:"event"`
	Phase             InterceptionPhase `json:"phase"`
	Results           []ExecutionResult `json:"results"`
	FinalPayload      any               `json:"finalPayload,omitempty"`
	ValidationSummary ValidationSummary `json:"validationSummary"`
	TotalDurationMs   int64             `json:"totalDurationMs"`
	AbortedAt         []AbortInfo       `json:"abortedAt,omitempty"`
}

// ExecutionResult tracks a single interceptor's execution result.
type ExecutionResult struct {
	Interceptor string            `json:"interceptor"` // Name of the interceptor
	Type        InterceptorType   `json:"type"`
	Phase       InterceptionPhase `json:"phase"`
	DurationMs  int64             `json:"durationMs,omitempty"`
	Error       string            `json:"error,omitempty"` // Non-empty when the handler returned an error
	Info        map[string]any    `json:"info,omitempty"`

	// One of these is set depending on type:
	Validation *ValidationResult `json:"validation,omitempty"`
	Mutation   *MutationResult   `json:"mutation,omitempty"`
}

// ValidationSummary counts validation outcomes.
type ValidationSummary struct {
	Errors   int `json:"errors"`
	Warnings int `json:"warnings"`
	Infos    int `json:"infos"`
}

// AbortInfo describes where and why a chain was aborted.
type AbortInfo struct {
	Interceptor string    `json:"interceptor"`
	Reason      string    `json:"reason"`
	Type        AbortType `json:"type"`
	Phase       string    `json:"phase,omitempty"` // phase at which the abort occurred
}
