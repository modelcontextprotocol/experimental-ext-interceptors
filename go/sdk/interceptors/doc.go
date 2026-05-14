// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

// Package interceptors defines the core types for the MCP interceptor
// framework: interceptor descriptors ([Validator], [Mutator]), wire
// protocol types ([InvokeParams], [InvokeResult]), result types
// ([ValidationResult], [MutationResult]), and supporting enums.
//
// This package is transport-agnostic — it has no dependency on the
// MCP SDK's server or client types. The chain orchestrator lives in
// the [interceptors/chain] sub-package, MCP server integration in
// [interceptors/extension], and middleware in
// [interceptors/integrations/gomiddleware].
//
// # Usage with MCP
//
// Create an [extension.Extension], register interceptors, install on
// an [mcp.Server], then use [extension.Extension.LocalChain] to get a
// chain for middleware:
//
//	ext := extension.New()
//	ext.AddInterceptor(myValidator)
//	ext.AddInterceptor(myMutator)
//	ext.Install(mcpServer)
//
//	chain, err := ext.LocalChain(ctx, mcpServer)
//	mcpServer.AddReceivingMiddleware(
//	    gomiddleware.Middleware(chain),
//	)
//
// # Validators
//
// A [Validator] inspects the payload and decides whether the request
// or response should proceed. All validators for a given event run
// in parallel. If any validator in enforced mode ([ModeEnforce])
// returns an error-severity message, the chain aborts before any
// mutators run. Only error-severity messages cause an abort; warn
// and info findings are recorded in the chain execution result but
// do not block the chain.
//
// Interceptor handlers receive [json.RawMessage] payloads when
// invoked via interceptor/invoke:
//
//	v := &interceptors.Validator{
//	    Metadata: interceptors.Metadata{
//	        Name: "block-dangerous-tool",
//	        Hook: interceptors.Hook{
//	            Events: []string{"tools/call"},
//	            Phase:  interceptors.PhaseRequest,
//	        },
//	        Mode: interceptors.ModeEnforce,
//	    },
//	    Handler: func(ctx context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
//	        raw := inv.Payload.(json.RawMessage)
//	        var params struct{ Name string `json:"name"` }
//	        json.Unmarshal(raw, &params)
//	        // inspect params ...
//	        return &interceptors.ValidationResult{Valid: true}, nil
//	    },
//	}
//
// # Mutators
//
// A [Mutator] transforms the payload. Mutators run sequentially in
// priority order (see [Priority]). Each mutator receives the payload
// as [json.RawMessage], unmarshals it, modifies it, and sets the
// updated JSON back on inv.Payload. If any mutator fails (and is not
// configured with FailOpen), the chain aborts. FailOpen mutators
// record an [InvokeResult] (with the error captured) for
// observability but do not block.
//
//	m := &interceptors.Mutator{
//	    Metadata: interceptors.Metadata{
//	        Name: "redact-pii",
//	        Hook: interceptors.Hook{
//	            Events: []string{"tools/call"},
//	            Phase:  interceptors.PhaseResponse,
//	        },
//	        Mode: interceptors.ModeEnforce,
//	    },
//	    Handler: func(ctx context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
//	        raw := inv.Payload.(json.RawMessage)
//	        var result map[string]any
//	        json.Unmarshal(raw, &result)
//	        // modify result ...
//	        data, _ := json.Marshal(result)
//	        return &interceptors.MutationResult{Modified: true, Payload: data}, nil
//	    },
//	}
//
// # Execution Order
//
// The chain execution order depends on direction:
//
// Request phase (untrusted → trusted):
//
//	Validate (parallel) → Mutate (sequential)
//
// Response phase (trusted → untrusted):
//
//	Mutate (sequential) → Validate (parallel)
//
// Validators act as a security gate on the trust boundary side,
// while mutators prepare or sanitize data on the other side.
//
// # Modes and FailOpen
//
// Each interceptor has a [Mode] that controls what happens with
// successful results, and a FailOpen flag that controls what happens
// when the handler returns a Go error. These are orthogonal:
//
//   - [ModeEnforce]: fully enforced — validation failures block,
//     mutations are applied.
//   - [ModeAudit]: the handler runs and results are recorded, but
//     validation findings do not block and mutated payloads are
//     not propagated to subsequent interceptors.
//
// FailOpen (default false) controls crash resilience:
//
//   - FailOpen=false: a handler error aborts the chain. An
//     [InvokeResult] and a [chain.AbortInfo] are both recorded.
//   - FailOpen=true: a handler error is logged and an
//     [InvokeResult] is recorded, but the chain continues.
//
// Note that [ModeAudit] does NOT imply FailOpen. Audit mode only
// suppresses enforcement of successful results (validation findings
// and mutations). If the handler itself returns an error and
// FailOpen is false, the chain still aborts. For truly safe
// observation-only interceptors, set both ModeAudit and FailOpen:
//
//	Metadata: interceptors.Metadata{
//	    Mode:     interceptors.ModeAudit,
//	    FailOpen: true,
//	}
//
// Behavior matrix for validators:
//
//	Mode=Enforce,  FailOpen=false → error aborts, Valid=false+SeverityError aborts
//	Mode=Enforce,  FailOpen=true  → error continues, Valid=false+SeverityError aborts
//	Mode=Audit, FailOpen=false → error aborts, findings recorded only
//	Mode=Audit, FailOpen=true  → error continues, findings recorded only
//
// Behavior matrix for mutators:
//
//	Mode=Enforce,  FailOpen=false → error aborts, mutations applied
//	Mode=Enforce,  FailOpen=true  → error continues, mutations applied
//	Mode=Audit, FailOpen=false → error aborts, mutations not propagated
//	Mode=Audit, FailOpen=true  → error continues, mutations not propagated
package interceptors
