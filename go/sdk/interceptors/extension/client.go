// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package extension

import (
	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
)

// RegisterSendingMethods registers the interceptor custom RPC methods on a
// client before it connects to interceptor-capable servers.
func RegisterSendingMethods(client *mcp.Client) error {
	if err := mcp.AddSendingCustomMethod[*interceptors.ListParams, *interceptors.ListResult](
		client, interceptors.MethodList,
	); err != nil {
		return err
	}
	return mcp.AddSendingCustomMethod[*interceptors.InvokeParams, *interceptors.InvokeResult](
		client, interceptors.MethodInvoke,
	)
}
