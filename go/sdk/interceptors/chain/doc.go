// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

// Package chain provides the SEP-compliant interceptor chain
// orchestrator. It discovers interceptors from MCP servers via
// "interceptors/list" and invokes them via "interceptor/invoke",
// implementing trust-boundary-aware execution ordering.
//
// # Execution Model
//
// [Chain.Execute] filters registered interceptors by event and phase,
// separates them into validators and mutators, and runs them in the
// SEP-defined order:
//
// Request phase (untrusted → trusted):
//
//	Validate (parallel) → Mutate (sequential)
//
// Response phase (trusted → untrusted):
//
//	Mutate (sequential) → Validate (parallel)
//
// # Usage
//
// A [Chain] is created with [NewChain] and populated by adding MCP
// servers via [Chain.AddMCPServer]:
//
//	chain := chain.NewChain()
//	chain.AddMCPServer(ctx, clientSession)
//	mcpServer.AddReceivingMiddleware(
//	    gomiddleware.Middleware(chain),
//	)
//
// For convenience, [extension.Extension.LocalChain] creates a chain
// pre-configured with a local in-process MCP server:
//
//	chain, err := ext.LocalChain(ctx, mcpServer)
package chain
