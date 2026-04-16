// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"encoding/json"
)

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

// Priority represents an interceptor's ordering hint.
// Can be a single value (applies to both phases) or per-phase.
//
// JSON representation is polymorphic: a single number when both
// phases are equal, or {"request": N, "response": N} when they differ.
type Priority struct {
	Request  int
	Response int
}

// NewPriority creates a Priority with the same value for both phases.
func NewPriority(v int) Priority {
	return Priority{Request: v, Response: v}
}

// Resolve returns the priority for the given phase.
func (p Priority) Resolve(phase InterceptionPhase) int {
	if phase == PhaseResponse {
		return p.Response
	}
	return p.Request
}

// MarshalJSON implements polymorphic serialization:
// emits a single number when both phases are equal, or an object otherwise.
func (p Priority) MarshalJSON() ([]byte, error) {
	if p.Request == p.Response {
		return json.Marshal(p.Request)
	}
	return json.Marshal(struct {
		Request  int `json:"request,omitempty"`
		Response int `json:"response,omitempty"`
	}{p.Request, p.Response})
}

// UnmarshalJSON handles both number and {request, response} forms.
func (p *Priority) UnmarshalJSON(data []byte) error {
	var n int
	if err := json.Unmarshal(data, &n); err == nil {
		p.Request = n
		p.Response = n
		return nil
	}
	var obj struct {
		Request  int `json:"request"`
		Response int `json:"response"`
	}
	if err := json.Unmarshal(data, &obj); err != nil {
		return err
	}
	p.Request = obj.Request
	p.Response = obj.Response
	return nil
}

// Severity represents validation message severity.
type Severity string

const (
	SeverityInfo  Severity = "info"
	SeverityWarn  Severity = "warn"
	SeverityError Severity = "error" // Only error blocks execution
)

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

// Interceptor is the common interface for all interceptors. It is implemented by both Validator and Mutator.
type Interceptor interface {
	GetMetadata() *Metadata
	GetType() InterceptorType
}

// --- ValidatorHandler ---

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

// --- MutatorHandler ---

// MutatorHandler is the function signature for raw mutation handlers.
type MutatorHandler func(ctx context.Context, inv *Invocation) (*MutationResult, error)

// Mutator is a mutation interceptor.
type Mutator struct {
	Metadata
	Handler MutatorHandler
}

func (m *Mutator) GetMetadata() *Metadata   { return &m.Metadata }
func (m *Mutator) GetType() InterceptorType { return TypeMutation }
