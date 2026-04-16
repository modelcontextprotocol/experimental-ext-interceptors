// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"encoding/json"
	"fmt"
	"reflect"
)

// Invocation is the context passed to every interceptor handler.
type Invocation struct {
	Event   string             // e.g. "tools/call"
	Phase   InterceptionPhase  // "request" or "response"
	Payload any                // The typed payload (use type assertion directly)
	Config  map[string]any     // Per-invocation config
	Context *InvocationContext // Optional caller context (identity, trace, etc.)
	Session any                // The server session; nil for protocol-level invocations

	mutatedParams any // set via SetMutatedParams in response phase only
}

// MutatedParams returns the request params after request-phase mutators
// have run. Only available in the response phase; returns nil in the
// request phase.
func (inv *Invocation) MutatedParams() any {
	return inv.mutatedParams
}

// SetMutatedParams sets the mutated request params on the invocation.
// This is intended for server integrations that build response-phase
// invocations; interceptor handlers should use MutatedParams() to read.
func (inv *Invocation) SetMutatedParams(p any) {
	inv.mutatedParams = p
}

// withCopiedPayload returns a shallow copy of the Invocation with a
// deep-copied Payload. Used for audit-mode mutators so their in-place
// modifications don't affect the real struct.
//
// The clone is a JSON round-trip: marshal the typed value, then
// unmarshal into a fresh instance of the same concrete type via
// reflect.New. The payload must be a pointer.
func (inv *Invocation) withCopiedPayload() (*Invocation, error) {
	raw, err := json.Marshal(inv.Payload)
	if err != nil {
		return nil, fmt.Errorf("clone payload: marshal: %w", err)
	}
	cp := reflect.New(reflect.TypeOf(inv.Payload).Elem())
	if err := json.Unmarshal(raw, cp.Interface()); err != nil {
		return nil, fmt.Errorf("clone payload: unmarshal: %w", err)
	}
	return &Invocation{
		Event:         inv.Event,
		Phase:         inv.Phase,
		Payload:       cp.Interface(),
		mutatedParams: inv.mutatedParams,
		Config:        inv.Config,
		Context:       inv.Context,
		Session:       inv.Session,
	}, nil
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
