// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

// Package gomiddleware provides an optional MCP middleware that
// automatically executes interceptors for every matching
// request/response. It uses the [chain.Chain] from the
// [interceptors/chain] package, which invokes interceptors via
// interceptor/invoke RPCs per the SEP execution model.
//
// The middleware is installed on an [mcp.Server] via
// [mcp.Server.AddReceivingMiddleware]. It marshals request params
// and response results to [json.RawMessage], calls
// [chain.Chain.Execute], and applies any mutated payloads back to
// the request or response.
//
// # Usage
//
//	// Create interceptor server (registers interceptors as resources)
//	srv := mcpserver.NewServer(mcpServer)
//	srv.AddInterceptor(myValidator)
//	srv.AddInterceptor(myMutator)
//
//	// Create chain via in-memory transport
//	chain, err := srv.LocalChain(ctx)
//
//	// Install middleware for automatic execution
//	mcpServer.AddReceivingMiddleware(
//	    gomiddleware.Middleware(chain,
//	        gomiddleware.WithLogger(logger),
//	    ),
//	)
package gomiddleware
