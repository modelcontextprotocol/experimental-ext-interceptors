// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package gomiddleware

import (
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/jsonrpc"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/chain"
)

type validationErrorData struct {
	ValidationErrors []chain.AbortInfo `json:"validationErrors"`
}

type mutationErrorData struct {
	FailedInterceptor string `json:"failedInterceptor"`
	Reason            string `json:"reason"`
}

type timeoutErrorData struct {
	Interceptor string `json:"interceptor"`
	TimeoutMs   int64  `json:"timeoutMs,omitempty"`
	Phase       string `json:"phase"`
}

const codeServerError = -32000

// abortToJSONRPCError converts a list of abort infos into a *jsonrpc.Error.
func abortToJSONRPCError(aborts []chain.AbortInfo) *jsonrpc.Error {
	first := aborts[0]

	var code int64
	switch first.Type {
	case chain.AbortValidation:
		code = jsonrpc.CodeInvalidParams
	case chain.AbortTimeout:
		code = codeServerError
	default:
		code = jsonrpc.CodeInternalError
	}

	var data json.RawMessage
	switch first.Type {
	case chain.AbortValidation:
		d, _ := json.Marshal(validationErrorData{ValidationErrors: aborts})
		data = d
	case chain.AbortTimeout:
		d, _ := json.Marshal(timeoutErrorData{
			Interceptor: first.Interceptor,
			Phase:       first.Phase,
		})
		data = d
	default:
		d, _ := json.Marshal(mutationErrorData{
			FailedInterceptor: first.Interceptor,
			Reason:            first.Reason,
		})
		data = d
	}

	msg := "interceptor abort: " + first.Reason
	if len(aborts) > 1 {
		msg = "interceptor abort: multiple validation failures"
	}

	return &jsonrpc.Error{
		Code:    code,
		Message: msg,
		Data:    data,
	}
}
