package extension_test

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
)

func TestServer_ValidatorRejectsBlockedTool(t *testing.T) {
	t.Parallel()
	cs := setup(t, blockToolValidator("echo"))

	_, err := callEcho(t, cs)
	assert.Error(t, err)
}

func TestServer_ValidatorAllowsTool(t *testing.T) {
	t.Parallel()
	cs := setup(t, allowAllValidator("allow-all"))

	result, err := callEcho(t, cs)
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "echo:"))
}

func TestServer_MutatorModifiesResponse(t *testing.T) {
	t.Parallel()
	cs := setup(t, prefixMutator("prefix", "[mutated]", interceptors.PhaseResponse, 0, interceptors.ModeEnforce))

	result, err := callEcho(t, cs)
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "[mutated] echo:"))
}

func TestServer_ChainedMutatorsWithAuditMode(t *testing.T) {
	t.Parallel()
	// 5 response mutators in priority order. Mutator 3 (priority 30) is
	// audit-mode: its "[AUDIT]" prefix must NOT appear in the final output.
	// Expected final text (applied innermost-first by ascending priority):
	//
	//   [M5] [M4] [M2] [M1] echo: ...
	cs := setup(t,
		prefixMutator("m1", "[M1]", interceptors.PhaseResponse, 10, interceptors.ModeEnforce),
		prefixMutator("m2", "[M2]", interceptors.PhaseResponse, 20, interceptors.ModeEnforce),
		prefixMutator("m3-audit", "[AUDIT]", interceptors.PhaseResponse, 30, interceptors.ModeAudit),
		prefixMutator("m4", "[M4]", interceptors.PhaseResponse, 40, interceptors.ModeEnforce),
		prefixMutator("m5", "[M5]", interceptors.PhaseResponse, 50, interceptors.ModeEnforce),
	)

	result, err := callEcho(t, cs)
	require.NoError(t, err)

	text := resultText(t, result)
	assert.True(t, strings.HasPrefix(text, "[M5] [M4] [M2] [M1] echo:"))
	assert.NotContains(t, text, "[AUDIT]")
}

func TestServer_MutatorFailureAbortsChain(t *testing.T) {
	t.Parallel()
	// 3 response mutators. Mutator 2 (priority 20) returns an error
	// and is fail-closed. The chain aborts; client receives an error.
	cs := setup(t,
		prefixMutator("m1", "[M1]", interceptors.PhaseResponse, 10, interceptors.ModeEnforce),
		failMutator("m2-fail", interceptors.PhaseResponse, 20),
		prefixMutator("m3", "[M3]", interceptors.PhaseResponse, 30, interceptors.ModeEnforce),
	)

	_, err := callEcho(t, cs)
	assert.Error(t, err)
}

func TestServer_ValidatorWarnDoesNotBlock(t *testing.T) {
	t.Parallel()
	// A validator returns Valid=false with only SeverityWarn messages.
	// The chain should continue — warn-only failures don't abort.
	warnValidator := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "warn-only",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			return &interceptors.ValidationResult{
				Valid: false,
				Messages: []interceptors.ValidationMessage{
					{Message: "something looks off", Severity: interceptors.SeverityWarn},
				},
			}, nil
		},
	}

	cs := setup(t, warnValidator)

	result, err := callEcho(t, cs)
	require.NoError(t, err)
	assert.True(t, strings.HasPrefix(resultText(t, result), "echo:"))
}

func TestServer_ValidationFailurePreventsM(t *testing.T) {
	t.Parallel()
	// A validator rejects the request. A mutator is also registered but
	// should never run because the chain aborts before mutators.
	mutatorRan := atomic.Bool{}
	spy := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "spy-mutator",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
			mutatorRan.Store(true)
			return &interceptors.MutationResult{Modified: false}, nil
		},
	}

	cs := setup(t, blockToolValidator("echo"), spy)

	_, err := callEcho(t, cs)
	assert.Error(t, err)
	assert.False(t, mutatorRan.Load(), "mutator should not run after validation failure")
}

func TestServer_ValidatorTimeoutAbortsChain(t *testing.T) {
	t.Parallel()
	slowValidator := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "slow-validator",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(ctx context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(100 * time.Millisecond):
				return &interceptors.ValidationResult{Valid: true}, nil
			}
		},
	}

	cs := setup(t, slowValidator)

	// Per-invocation timeout: context expires before the 100ms handler completes.
	_, err := callEchoWithTimeout(t, cs, 5*time.Millisecond)
	require.Error(t, err)
	assert.Contains(t, err.Error(), "context deadline exceeded")
}

func TestServer_MutatorTimeoutAbortsChain(t *testing.T) {
	t.Parallel()
	// 3 response mutators. Mutator 2 (priority 20) blocks longer than
	// the invocation timeout. The chain aborts; client receives an error.
	slowMutator := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "m2-slow",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode:         interceptors.ModeEnforce,
			PriorityHint: interceptors.NewPriority(20),
		},
		Handler: func(ctx context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(100 * time.Millisecond):
				return &interceptors.MutationResult{Modified: false}, nil
			}
		},
	}

	cs := setup(t,
		prefixMutator("m1", "[M1]", interceptors.PhaseResponse, 10, interceptors.ModeEnforce),
		slowMutator,
		prefixMutator("m3", "[M3]", interceptors.PhaseResponse, 30, interceptors.ModeEnforce),
	)

	// Per-invocation timeout: context expires before the 100ms handler completes.
	_, err := callEchoWithTimeout(t, cs, 5*time.Millisecond)
	require.Error(t, err)
	assert.Contains(t, err.Error(), "context deadline exceeded")
}

func TestServer_MutatorFailOpenContinuesChain(t *testing.T) {
	t.Parallel()
	// 3 response mutators. Mutator 2 (priority 20) returns an error but
	// has FailOpen set. The chain continues; M1 and M3 both apply.
	failOpenMutator := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "m2-failopen",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode:         interceptors.ModeEnforce,
			FailOpen:     true,
			PriorityHint: interceptors.NewPriority(20),
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.MutationResult, error) {
			return nil, fmt.Errorf("simulated failure")
		},
	}

	cs := setup(t,
		prefixMutator("m1", "[M1]", interceptors.PhaseResponse, 10, interceptors.ModeEnforce),
		failOpenMutator,
		prefixMutator("m3", "[M3]", interceptors.PhaseResponse, 30, interceptors.ModeEnforce),
	)

	result, err := callEcho(t, cs)
	require.NoError(t, err)

	text := resultText(t, result)
	assert.Contains(t, text, "[M1]")
	assert.Contains(t, text, "[M3]")
}

func TestServer_ResponseMutatorSeesMutatedParams(t *testing.T) {
	t.Parallel()
	// A request mutator injects a marker into the arguments. A response
	// mutator reads the request payload and copies the marker into the
	// response text.
	//
	// NOTE: In the SEP model, interceptors receive json.RawMessage payloads.
	// Response-phase interceptors only see the response payload, not the
	// request params. This test is simplified to verify request mutation
	// flows through to the echo handler's output.
	reqMutator := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "inject-marker",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected payload type %T", inv.Payload)
			}
			var params map[string]any
			if err := json.Unmarshal(raw, &params); err != nil {
				return nil, err
			}
			args, _ := params["arguments"].(map[string]any)
			if args == nil {
				args = map[string]any{}
			}
			args["marker"] = "injected-by-request-mutator"
			params["arguments"] = args
			data, err := json.Marshal(params)
			if err != nil {
				return nil, err
			}
			return &interceptors.MutationResult{Modified: true, Payload: json.RawMessage(data)}, nil
		},
	}

	cs := setup(t, reqMutator)

	result, err := callEcho(t, cs)
	require.NoError(t, err)
	assert.Contains(t, resultText(t, result), "injected-by-request-mutator")
}

func TestServer_CombinedValidatorsAndMutators(t *testing.T) {
	t.Parallel()
	// Exercises the full chain with 3 validators and 3 mutators on each
	// side, verifying:
	//
	//   Request:  Validate (parallel) → Mutate (sequential by priority)
	//   Response: Mutate (sequential by priority) → Validate (parallel)
	//
	// Request mutators inject fields into arguments (reflected in echo output).
	// Response mutators prepend tags to text (visible in final result).
	// Atomic counters confirm every interceptor actually ran.

	var (
		reqValCount  atomic.Int32
		reqMutCount  atomic.Int32
		respMutCount atomic.Int32
		respValCount atomic.Int32
	)

	// --- Request validators (3, parallel) ---

	// req-v1: passes if tool name is "echo".
	reqV1 := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "req-v1-tool-check",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			reqValCount.Add(1)
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var params struct {
				Name string `json:"name"`
			}
			if err := json.Unmarshal(raw, &params); err != nil {
				return nil, err
			}
			if params.Name != "echo" {
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: "only echo allowed", Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	// req-v2: passes if arguments contain "text".
	reqV2 := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "req-v2-args-check",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			reqValCount.Add(1)
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var params struct {
				Arguments json.RawMessage `json:"arguments"`
			}
			if err := json.Unmarshal(raw, &params); err != nil {
				return nil, err
			}
			var args map[string]any
			if err := json.Unmarshal(params.Arguments, &args); err != nil {
				return nil, err
			}
			if _, ok := args["text"]; !ok {
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: "missing text argument", Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	// req-v3: always passes with a warning (warn doesn't block).
	reqV3 := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "req-v3-warn",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			reqValCount.Add(1)
			return &interceptors.ValidationResult{
				Valid: true,
				Messages: []interceptors.ValidationMessage{
					{Message: "request noted", Severity: interceptors.SeverityWarn},
				},
			}, nil
		},
	}

	// --- Request mutators (3, sequential by priority 10 → 20 → 30) ---
	// Each adds a field to the arguments.

	requestArgMutator := func(name, key, value string, priority int) *interceptors.Mutator {
		return &interceptors.Mutator{
			Metadata: interceptors.Metadata{
				Name: name,
				Hook: interceptors.Hook{
					Events: []string{interceptors.EventToolsCall},
					Phase:  interceptors.PhaseRequest,
				},
				Mode:         interceptors.ModeEnforce,
				PriorityHint: interceptors.NewPriority(priority),
			},
			Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
				reqMutCount.Add(1)
				raw, ok := inv.Payload.(json.RawMessage)
				if !ok {
					return nil, fmt.Errorf("unexpected type %T", inv.Payload)
				}
				var params map[string]any
				if err := json.Unmarshal(raw, &params); err != nil {
					return nil, err
				}
				argsRaw := params["arguments"]
				var args map[string]any
				if argsRaw != nil {
					argsBytes, _ := json.Marshal(argsRaw)
					json.Unmarshal(argsBytes, &args)
				}
				if args == nil {
					args = map[string]any{}
				}
				args[key] = value
				params["arguments"] = args
				data, err := json.Marshal(params)
				if err != nil {
					return nil, err
				}
				return &interceptors.MutationResult{Modified: true, Payload: json.RawMessage(data)}, nil
			},
		}
	}

	reqM1 := requestArgMutator("req-m1", "step1", "done", 10)
	reqM2 := requestArgMutator("req-m2", "step2", "done", 20)
	reqM3 := requestArgMutator("req-m3", "step3", "done", 30)

	// --- Response mutators (3, sequential by priority 10 → 20 → 30) ---
	// Each prepends a tag. Applied innermost-first, so final text is:
	//   [RM3] [RM2] [RM1] echo: ...

	respPrefixMutator := func(name, tag string, priority int) *interceptors.Mutator {
		return &interceptors.Mutator{
			Metadata: interceptors.Metadata{
				Name: name,
				Hook: interceptors.Hook{
					Events: []string{interceptors.EventToolsCall},
					Phase:  interceptors.PhaseResponse,
				},
				Mode:         interceptors.ModeEnforce,
				PriorityHint: interceptors.NewPriority(priority),
			},
			Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.MutationResult, error) {
				respMutCount.Add(1)
				raw, ok := inv.Payload.(json.RawMessage)
				if !ok {
					return nil, fmt.Errorf("unexpected type %T", inv.Payload)
				}
				var result mcp.CallToolResult
				if err := json.Unmarshal(raw, &result); err != nil {
					return nil, err
				}
				for _, c := range result.Content {
					if tc, ok := c.(*mcp.TextContent); ok {
						tc.Text = tag + " " + tc.Text
					}
				}
				data, err := json.Marshal(&result)
				if err != nil {
					return nil, err
				}
				return &interceptors.MutationResult{Modified: true, Payload: json.RawMessage(data)}, nil
			},
		}
	}

	respM1 := respPrefixMutator("resp-m1", "[RM1]", 10)
	respM2 := respPrefixMutator("resp-m2", "[RM2]", 20)
	respM3 := respPrefixMutator("resp-m3", "[RM3]", 30)

	// --- Response validators (3, parallel) ---
	// Run after response mutators.

	// resp-v1: passes if response has content.
	respV1 := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "resp-v1-has-content",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			respValCount.Add(1)
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var result mcp.CallToolResult
			if err := json.Unmarshal(raw, &result); err != nil {
				return nil, err
			}
			if len(result.Content) == 0 {
				return &interceptors.ValidationResult{
					Valid: false,
					Messages: []interceptors.ValidationMessage{
						{Message: "empty response", Severity: interceptors.SeverityError},
					},
				}, nil
			}
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	// resp-v2: rejects if response text doesn't contain "[RM1]".
	// This proves the response-side ordering (Mutate → Validate): if
	// this validator ran before mutators, it wouldn't find the tag.
	respV2 := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "resp-v2-sees-mutation",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, inv *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			respValCount.Add(1)
			raw, ok := inv.Payload.(json.RawMessage)
			if !ok {
				return nil, fmt.Errorf("unexpected type %T", inv.Payload)
			}
			var result mcp.CallToolResult
			if err := json.Unmarshal(raw, &result); err != nil {
				return nil, err
			}
			for _, c := range result.Content {
				if tc, ok := c.(*mcp.TextContent); ok {
					if strings.Contains(tc.Text, "[RM1]") {
						return &interceptors.ValidationResult{Valid: true}, nil
					}
				}
			}
			return &interceptors.ValidationResult{
				Valid: false,
				Messages: []interceptors.ValidationMessage{
					{Message: "response missing mutator tag [RM1]", Severity: interceptors.SeverityError},
				},
			}, nil
		},
	}

	// resp-v3: always passes with an info message.
	respV3 := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "resp-v3-info",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseResponse,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			respValCount.Add(1)
			return &interceptors.ValidationResult{
				Valid: true,
				Messages: []interceptors.ValidationMessage{
					{Message: "response looks good", Severity: interceptors.SeverityInfo},
				},
			}, nil
		},
	}

	cs := setup(t,
		reqV1, reqV2, reqV3,
		reqM1, reqM2, reqM3,
		respM1, respM2, respM3,
		respV1, respV2, respV3,
	)

	result, err := callEcho(t, cs)
	require.NoError(t, err)

	text := resultText(t, result)

	// Response mutators applied in priority order (outermost = highest priority).
	assert.True(t, strings.HasPrefix(text, "[RM3] [RM2] [RM1] echo:"),
		"expected response mutator tags in priority order, got: %s", text)

	// Request mutators injected fields into arguments (visible in echo output).
	assert.Contains(t, text, "step1")
	assert.Contains(t, text, "step2")
	assert.Contains(t, text, "step3")

	// Every interceptor ran exactly once.
	assert.Equal(t, int32(3), reqValCount.Load(), "expected 3 request validators to run")
	assert.Equal(t, int32(3), reqMutCount.Load(), "expected 3 request mutators to run")
	assert.Equal(t, int32(3), respMutCount.Load(), "expected 3 response mutators to run")
	assert.Equal(t, int32(3), respValCount.Load(), "expected 3 response validators to run")
}
