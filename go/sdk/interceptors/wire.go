// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import "encoding/json"

// JSON-RPC method names for the interceptor protocol.
const (
	MethodList   = "interceptors/list"
	MethodInvoke = "interceptor/invoke"
)

// Event name constants for standard MCP methods.
const (
	// Server Features
	EventToolsList          = "tools/list"
	EventToolsCall          = "tools/call"
	EventPromptsList        = "prompts/list"
	EventPromptsGet         = "prompts/get"
	EventResourcesList      = "resources/list"
	EventResourcesRead      = "resources/read"
	EventResourcesSubscribe = "resources/subscribe"
)

// --- interceptors/list ---

// ListParams are the optional parameters for the interceptors/list method.
type ListParams struct {
	Event string `json:"event,omitempty"` // Filter by event name; empty = all
}

// InterceptorInfo describes a registered interceptor in wire format.
type InterceptorInfo struct {
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

// InfoFromInterceptor builds an InterceptorInfo from an Interceptor.
func InfoFromInterceptor(i Interceptor) InterceptorInfo {
	m := i.GetMetadata()
	return InterceptorInfo{
		Name:         m.Name,
		Version:      m.Version,
		Description:  m.Description,
		Type:         i.GetType(),
		Hook:         m.Hook,
		PriorityHint: m.PriorityHint,
		Compat:       m.Compat,
		ConfigSchema: m.ConfigSchema,
		Mode:         m.Mode,
		FailOpen:     m.FailOpen,
	}
}

// ListResult is the result of the interceptors/list method.
type ListResult struct {
	Interceptors []InterceptorInfo `json:"interceptors"`
}

// --- interceptor/invoke ---

// InvokeParams are the parameters for the interceptor/invoke method.
// The caller specifies which interceptor to invoke by name.
type InvokeParams struct {
	Name      string             `json:"name"`
	Event     string             `json:"event"`
	Phase     InterceptionPhase  `json:"phase"`
	Payload   json.RawMessage    `json:"payload"`
	TimeoutMs int64              `json:"timeoutMs,omitempty"`
	Config    map[string]any     `json:"config,omitempty"`
	Context   *InvocationContext `json:"context,omitempty"`
}

// InvokeResult is the result of the interceptor/invoke method.
// Fields are type-specific: validators populate Validation, mutators populate Mutation + Payload.
type InvokeResult struct {
	Interceptor string            `json:"interceptor"`
	Type        InterceptorType   `json:"type"`
	Phase       InterceptionPhase `json:"phase"`
	DurationMs  int64             `json:"durationMs"`

	// Validation result (type == "validation")
	Validation *ValidationResult `json:"validation,omitempty"`

	// Mutation result (type == "mutation")
	Mutation *MutationResult `json:"mutation,omitempty"`
	Payload  json.RawMessage `json:"payload,omitempty"` // Mutated payload (only for mutators)
}
