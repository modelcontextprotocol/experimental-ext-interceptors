// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

// Package interceptors provides a protocol-agnostic validation and
// mutation middleware framework. It defines interceptor types
// ([Validator], [Mutator]), the chain engine ([Chain]), and all
// supporting types needed to build interceptor pipelines for any
// server protocol.
//
// This package has no dependency on MCP or any specific transport.
// For MCP-specific integration (wrapping an [mcp.Server] with
// middleware), see the [interceptors/mcpserver] sub-package.
//
// # Standalone Usage (any protocol)
//
// Create a [Chain], register interceptors, and call
// [Chain.ExecuteForReceiving] or [Chain.ExecuteForSending] from your
// server's request/response pipeline:
//
//	chain := interceptors.NewChain(
//	    interceptors.WithChainLogger(logger),
//	)
//	chain.Add(myValidator)
//	chain.Add(myMutator)
//
//	// In your request handler:
//	inv := &interceptors.Invocation{
//	    Event:   "tools/call",
//	    Phase:   interceptors.PhaseRequest,
//	    Payload: typedParams,
//	}
//	cr, err := chain.ExecuteForReceiving(ctx, inv)
//	if err != nil { ... }
//	if cr != nil && len(cr.AbortedAt) > 0 {
//	    // handle abort
//	}
//
// # Validators
//
// A [Validator] inspects the typed payload and decides whether the
// request or response should proceed. All validators for a given
// event run in parallel; because they share the same [Invocation]
// pointer, handlers MUST treat the Invocation and its Payload as
// read-only — mutating either is a data race. If any validator in
// enforced mode ([ModeEnforce]) returns an error-severity message, the
// chain aborts before any mutators run. Only error-severity messages
// cause an abort; warn and info findings are recorded in the
// [ChainResult] but do not block the chain.
//
// Type-assert the payload to its concrete type:
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
//	        params, ok := inv.Payload.(*MyRequestParams)
//	        if !ok {
//	            return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
//	        }
//	        // inspect params ...
//	        return &interceptors.ValidationResult{Valid: true}, nil
//	    },
//	}
//
// # Mutators
//
// A [Mutator] transforms the payload in place. Mutators run sequentially
// in priority order (see [Priority]). Each mutator receives the typed
// value and can modify it directly. If any mutator fails (and is not
// configured with FailOpen), the chain aborts. FailOpen mutators
// record an [ExecutionResult] (with the error captured) for
// observability but do not block.
//
// Type-assert the payload and modify the value in place:
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
//	        result, ok := inv.Payload.(*MyResponseResult)
//	        if !ok {
//	            return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
//	        }
//	        // modify result in place ...
//	        return &interceptors.MutationResult{Modified: true}, nil
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
//   - [ModeEnforce]: fully enforced — validation failures block, mutations
//     apply in place.
//   - [ModeAudit]: the handler runs and results are recorded, but
//     validation findings do not block and mutations run on a
//     deep-copied payload so the real data is unaffected.
//
// FailOpen (default false) controls crash resilience:
//
//   - FailOpen=false: a handler error aborts the chain. An
//     [ExecutionResult] (with Error populated) and an [AbortInfo]
//     are both recorded.
//   - FailOpen=true: a handler error is logged and an
//     [ExecutionResult] is recorded, but the chain continues.
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
//	ModeAudit, FailOpen=false → error aborts, findings recorded only
//	ModeAudit, FailOpen=true  → error continues, findings recorded only
//
// Behavior matrix for mutators:
//
//	Mode=Enforce,  FailOpen=false → error aborts, mutations applied in place
//	Mode=Enforce,  FailOpen=true  → error continues, mutations applied in place
//	ModeAudit, FailOpen=false → error aborts, mutations recorded (deep copy)
//	ModeAudit, FailOpen=true  → error continues, mutations recorded (deep copy)
package interceptors
