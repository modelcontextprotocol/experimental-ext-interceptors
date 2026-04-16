// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

// Package mcpserver registers interceptors as first-class MCP
// primitives. Adding an interceptor to the server makes it
// discoverable via "interceptors/list" and invocable via
// "interceptor/invoke", so any MCP client — local or remote — can
// discover and call interceptors using standard JSON-RPC.
//
// # Getting Started
//
// Wrap an existing [mcp.Server] with [NewServer] and register
// interceptors:
//
//	srv := mcpserver.NewServer(mcpServer, mcpserver.WithLogger(logger))
//	srv.AddInterceptor(myValidator)
//	srv.AddInterceptor(myMutator)
//	srv.Run(ctx, transport)
//
// At this point any connected MCP client can call interceptors/list
// and interceptor/invoke.
//
// For convenience, [Server.LocalChain] creates a [chain.Chain] wired
// to the server over an in-memory transport, ready for use with the
// gomiddleware package:
//
//	chain, err := srv.LocalChain(ctx)
//	mcpServer.AddReceivingMiddleware(
//	    gomiddleware.Middleware(chain,
//	        gomiddleware.WithContextProvider(myProvider),
//	    ),
//	)
//
// # JSON-RPC Methods
//
// The server registers two JSON-RPC methods:
//
//   - "interceptors/list": Returns all registered interceptors,
//     optionally filtered by event name.
//   - "interceptor/invoke": Invokes a single interceptor by name
//     for a given event, phase, and payload.
//
// # Transports
//
// The server works with any transport supported by the go-sdk.
// For stdio, use [Server.Run]. For HTTP, use
// [NewStreamableHTTPHandler]:
//
//	handler := mcpserver.NewStreamableHTTPHandler(srv, nil)
//	http.ListenAndServe(":8080", handler)
package mcpserver
