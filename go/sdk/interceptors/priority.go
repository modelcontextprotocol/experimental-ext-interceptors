// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import "encoding/json"

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
