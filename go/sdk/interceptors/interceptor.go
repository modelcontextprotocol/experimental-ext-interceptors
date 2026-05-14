// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"encoding/json"
)

// Interceptor is the common interface for all interceptors. It is implemented by both Validator and Mutator.
type Interceptor interface {
	GetMetadata() *Metadata
	GetType() InterceptorType
}

// --- Enums ---

// InterceptionPhase determines when an interceptor runs.
type InterceptionPhase string

const (
	PhaseRequest  InterceptionPhase = "request"
	PhaseResponse InterceptionPhase = "response"
	PhaseBoth     InterceptionPhase = "both"
)

// InterceptionEvent identifies a lifecycle event that can be intercepted.
type InterceptionEvent = string

// Mode controls enforcement behavior.
type Mode string

const (
	ModeEnforce Mode = "enforce" // Enforced: validation failures block, mutations apply
	ModeAudit   Mode = "audit"   // Audit: log results but don't block or apply mutations
)

// InterceptorType identifies the category of an interceptor.
type InterceptorType string

const (
	TypeValidation InterceptorType = "validation"
	TypeMutation   InterceptorType = "mutation"
)

// Severity represents validation message severity.
type Severity string

const (
	SeverityInfo  Severity = "info"
	SeverityWarn  Severity = "warn"
	SeverityError Severity = "error" // Only error blocks execution
)

// --- Metadata ---

// Compat represents protocol version compatibility.
type Compat struct {
	MinProtocol string `json:"minProtocol"`
	MaxProtocol string `json:"maxProtocol,omitempty"`
}

// Hook defines which lifecycle events and phase trigger an interceptor.
type Hook struct {
	Events []InterceptionEvent `json:"events"`
	Phase  InterceptionPhase   `json:"phase"`
}

// Metadata holds all common interceptor metadata.
type Metadata struct {
	Name         string          `json:"name"`
	Version      string          `json:"version,omitempty"`
	Description  string          `json:"description,omitempty"`
	Type         InterceptorType `json:"type"`
	Hook         Hook            `json:"hook"`
	PriorityHint Priority        `json:"priorityHint,omitempty"`
	Compat       *Compat         `json:"compat,omitempty"`
	ConfigSchema json.RawMessage `json:"configSchema,omitempty"`
	Mode         Mode            `json:"mode"`
	FailOpen     bool            `json:"failOpen,omitempty"`
}

// --- Validator ---

// ValidatorHandler is the function signature for validation handlers.
//
// Handlers MUST treat the Invocation and its Payload as read-only.
// Multiple validators for the same event run concurrently and share
// the same Invocation pointer, so any mutation of the Payload (or
// other Invocation fields) is a data race.
type ValidatorHandler func(ctx context.Context, inv *Invocation) (*ValidationResult, error)

// Validator is a validation interceptor.
type Validator struct {
	Metadata
	Handler ValidatorHandler
}

func (v *Validator) GetMetadata() *Metadata   { return &v.Metadata }
func (v *Validator) GetType() InterceptorType { return TypeValidation }

// --- Mutator ---

// MutatorHandler is the function signature for raw mutation handlers.
type MutatorHandler func(ctx context.Context, inv *Invocation) (*MutationResult, error)

// Mutator is a mutation interceptor.
type Mutator struct {
	Metadata
	Handler MutatorHandler
}

func (m *Mutator) GetMetadata() *Metadata   { return &m.Metadata }
func (m *Mutator) GetType() InterceptorType { return TypeMutation }

// --- Invocation ---

// Invocation is the context passed to every interceptor handler.
type Invocation struct {
	Event   string             // e.g. "tools/call"
	Phase   InterceptionPhase  // "request" or "response"
	Payload any                // The payload (json.RawMessage when invoked via interceptor/invoke)
	Config  map[string]any     // Per-invocation config
	Context *InvocationContext // Optional caller context (identity, trace, etc.)
}

// InvocationContext holds optional context passed to interceptors.
type InvocationContext struct {
	Principal *Principal `json:"principal,omitempty"`
	TraceID   string     `json:"traceId,omitempty"`
	SpanID    string     `json:"spanId,omitempty"`
	Timestamp string     `json:"timestamp,omitempty"`
	SessionID string     `json:"sessionId,omitempty"`
}

// Principal identifies the caller.
type Principal struct {
	Type   string         `json:"type"`
	ID     string         `json:"id,omitempty"`
	Claims map[string]any `json:"claims,omitempty"`
}

// --- Results ---

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
	Modified bool            `json:"modified"`
	Info     map[string]any  `json:"info,omitempty"`
	Payload  json.RawMessage `json:"payload,omitempty"`
}
