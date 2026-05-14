// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package extension

import (
	"context"
	"fmt"
	"slices"
	"time"

	"github.com/modelcontextprotocol/go-sdk/jsonrpc"
	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
)

// handleList implements the "interceptors/list" JSON-RPC method.
// It returns all registered interceptors, optionally filtered by event.
func (e *Extension) handleList(_ context.Context, req *mcp.ServerRequest[*interceptors.ListParams]) (*interceptors.ListResult, error) {
	var event string
	if req.Params != nil {
		event = req.Params.Event
	}

	all := e.getInterceptors()
	infos := make([]interceptors.InterceptorInfo, 0, len(all))
	for _, ri := range all {
		if event != "" && !slices.Contains(ri.GetMetadata().Hook.Events, event) {
			continue
		}
		infos = append(infos, interceptors.InfoFromInterceptor(ri))
	}

	return &interceptors.ListResult{Interceptors: infos}, nil
}

// handleInvoke implements the "interceptor/invoke" JSON-RPC method.
// It invokes a single interceptor by name and returns its result.
func (e *Extension) handleInvoke(ctx context.Context, req *mcp.ServerRequest[*interceptors.InvokeParams]) (*interceptors.InvokeResult, error) {
	if req.Params == nil || req.Params.Name == "" {
		return nil, &jsonrpc.Error{
			Code:    jsonrpc.CodeInvalidParams,
			Message: "name is required",
		}
	}
	params := req.Params

	// Look up the interceptor by name.
	i := e.findByName(params.Name)
	if i == nil {
		return nil, &jsonrpc.Error{
			Code:    jsonrpc.CodeInvalidParams,
			Message: fmt.Sprintf("interceptor %q not found", params.Name),
		}
	}

	// Apply timeout if specified.
	if params.TimeoutMs > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, time.Duration(params.TimeoutMs)*time.Millisecond)
		defer cancel()
	}

	inv := &interceptors.Invocation{
		Event:   params.Event,
		Phase:   params.Phase,
		Payload: params.Payload,
		Config:  params.Config,
		Context: params.Context,
	}

	start := time.Now()

	result := &interceptors.InvokeResult{
		Interceptor: params.Name,
		Type:        i.GetType(),
		Phase:       params.Phase,
	}
	defer func() { result.DurationMs = time.Since(start).Milliseconds() }()

	switch v := i.(type) {
	case *interceptors.Validator:
		vr, err := v.Handler(ctx, inv)
		if err != nil {
			return nil, err
		}
		result.Validation = vr

	case *interceptors.Mutator:
		mr, err := v.Handler(ctx, inv)
		if err != nil {
			return nil, err
		}
		result.Mutation = mr
		result.Payload = mr.Payload

	default:
		return nil, &jsonrpc.Error{
			Code:    jsonrpc.CodeInternalError,
			Message: fmt.Sprintf("unsupported interceptor type %T", i),
		}
	}

	return result, nil
}
