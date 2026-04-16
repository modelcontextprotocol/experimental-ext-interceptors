package mcpserver_test

import (
	"context"
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
)

// --- interceptors/list tests ---

func TestListInterceptors(t *testing.T) {
	t.Parallel()
	cs := setupRPCServer(t,
		allowAllValidator("v1"),
		blockToolValidator("dangerous"),
	)

	var result interceptors.ListResult
	err := cs.CallCustom(context.Background(), interceptors.MethodList, nil, &result)
	require.NoError(t, err)

	assert.Len(t, result.Interceptors, 2)
	names := make([]string, len(result.Interceptors))
	for i, info := range result.Interceptors {
		names[i] = info.Name
	}
	assert.Contains(t, names, "v1")
	assert.Contains(t, names, "block-dangerous")
}

func TestListWithEventFilter(t *testing.T) {
	t.Parallel()

	toolsValidator := allowAllValidator("tools-v")
	promptsValidator := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "prompts-v",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventPromptsGet},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(_ context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			return &interceptors.ValidationResult{Valid: true}, nil
		},
	}

	cs := setupRPCServer(t, toolsValidator, promptsValidator)

	// Filter for tools/call — should only return tools-v
	var result interceptors.ListResult
	err := cs.CallCustom(context.Background(), interceptors.MethodList,
		&interceptors.ListParams{Event: interceptors.EventToolsCall}, &result)
	require.NoError(t, err)
	assert.Len(t, result.Interceptors, 1)
	assert.Equal(t, "tools-v", result.Interceptors[0].Name)

	// Filter for prompts/get — should only return prompts-v
	var result2 interceptors.ListResult
	err = cs.CallCustom(context.Background(), interceptors.MethodList,
		&interceptors.ListParams{Event: interceptors.EventPromptsGet}, &result2)
	require.NoError(t, err)
	assert.Len(t, result2.Interceptors, 1)
	assert.Equal(t, "prompts-v", result2.Interceptors[0].Name)

	// Filter for non-existent event — empty
	var result3 interceptors.ListResult
	err = cs.CallCustom(context.Background(), interceptors.MethodList,
		&interceptors.ListParams{Event: "nonexistent/method"}, &result3)
	require.NoError(t, err)
	assert.Empty(t, result3.Interceptors)
}

// --- interceptor/invoke tests ---

func TestInvokeValidator(t *testing.T) {
	t.Parallel()

	cs := setupRPCServer(t, allowAllValidator("v1"))

	payload, _ := json.Marshal(map[string]any{"name": "echo", "arguments": map[string]any{"text": "hello"}})
	var result interceptors.InvokeResult
	err := cs.CallCustom(context.Background(), interceptors.MethodInvoke,
		&interceptors.InvokeParams{
			Name:    "v1",
			Event:   interceptors.EventToolsCall,
			Phase:   interceptors.PhaseRequest,
			Payload: payload,
		}, &result)
	require.NoError(t, err)

	assert.Equal(t, "v1", result.Interceptor)
	assert.Equal(t, interceptors.TypeValidation, result.Type)
	assert.Equal(t, interceptors.PhaseRequest, result.Phase)
	require.NotNil(t, result.Validation)
	assert.True(t, result.Validation.Valid)
	assert.Nil(t, result.Mutation)
	assert.Nil(t, result.Payload)
}

func TestInvokeValidatorRejects(t *testing.T) {
	t.Parallel()

	rejectAll := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "reject-all",
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
					{Message: "rejected", Severity: interceptors.SeverityError},
				},
			}, nil
		},
	}

	cs := setupRPCServer(t, rejectAll)

	payload, _ := json.Marshal(map[string]any{"name": "echo"})
	var result interceptors.InvokeResult
	err := cs.CallCustom(context.Background(), interceptors.MethodInvoke,
		&interceptors.InvokeParams{
			Name:    "reject-all",
			Event:   interceptors.EventToolsCall,
			Phase:   interceptors.PhaseRequest,
			Payload: payload,
		}, &result)
	require.NoError(t, err)

	assert.Equal(t, "reject-all", result.Interceptor)
	assert.Equal(t, interceptors.TypeValidation, result.Type)
	require.NotNil(t, result.Validation)
	assert.False(t, result.Validation.Valid)
	assert.Len(t, result.Validation.Messages, 1)
	assert.Equal(t, "rejected", result.Validation.Messages[0].Message)
}

func TestInvokeMutator(t *testing.T) {
	t.Parallel()

	mutator := &interceptors.Mutator{
		Metadata: interceptors.Metadata{
			Name: "add-field",
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
			var m map[string]any
			if err := json.Unmarshal(raw, &m); err != nil {
				return nil, err
			}
			m["injected"] = true
			data, err := json.Marshal(m)
			if err != nil {
				return nil, err
			}
			return &interceptors.MutationResult{Modified: true, Info: map[string]any{"added": "injected"}, Payload: json.RawMessage(data)}, nil
		},
	}

	cs := setupRPCServer(t, mutator)

	payload, _ := json.Marshal(map[string]any{"name": "echo"})
	var result interceptors.InvokeResult
	err := cs.CallCustom(context.Background(), interceptors.MethodInvoke,
		&interceptors.InvokeParams{
			Name:    "add-field",
			Event:   interceptors.EventToolsCall,
			Phase:   interceptors.PhaseRequest,
			Payload: payload,
		}, &result)
	require.NoError(t, err)

	assert.Equal(t, "add-field", result.Interceptor)
	assert.Equal(t, interceptors.TypeMutation, result.Type)
	require.NotNil(t, result.Mutation)
	assert.True(t, result.Mutation.Modified)
	assert.Equal(t, "injected", result.Mutation.Info["added"])

	// Verify mutated payload contains the injected field.
	require.NotNil(t, result.Payload)
	var mutated map[string]any
	require.NoError(t, json.Unmarshal(result.Payload, &mutated))
	assert.Equal(t, true, mutated["injected"])
	assert.Equal(t, "echo", mutated["name"])
}

func TestInvokeUnknownName(t *testing.T) {
	t.Parallel()

	cs := setupRPCServer(t, allowAllValidator("v1"))

	payload, _ := json.Marshal(map[string]any{})
	var result interceptors.InvokeResult
	err := cs.CallCustom(context.Background(), interceptors.MethodInvoke,
		&interceptors.InvokeParams{
			Name:    "nonexistent",
			Event:   interceptors.EventToolsCall,
			Phase:   interceptors.PhaseRequest,
			Payload: payload,
		}, &result)

	require.Error(t, err)
	assert.Contains(t, err.Error(), "nonexistent")
}

func TestInvokeTimeout(t *testing.T) {
	t.Parallel()

	slowValidator := &interceptors.Validator{
		Metadata: interceptors.Metadata{
			Name: "slow-v",
			Hook: interceptors.Hook{
				Events: []string{interceptors.EventToolsCall},
				Phase:  interceptors.PhaseRequest,
			},
			Mode: interceptors.ModeEnforce,
		},
		Handler: func(ctx context.Context, _ *interceptors.Invocation) (*interceptors.ValidationResult, error) {
			select {
			case <-time.After(5 * time.Second):
				return &interceptors.ValidationResult{Valid: true}, nil
			case <-ctx.Done():
				return nil, ctx.Err()
			}
		},
	}

	cs := setupRPCServer(t, slowValidator)

	payload, _ := json.Marshal(map[string]any{})
	var result interceptors.InvokeResult
	err := cs.CallCustom(context.Background(), interceptors.MethodInvoke,
		&interceptors.InvokeParams{
			Name:      "slow-v",
			Event:     interceptors.EventToolsCall,
			Phase:     interceptors.PhaseRequest,
			Payload:   payload,
			TimeoutMs: 50,
		}, &result)

	require.Error(t, err)
}
