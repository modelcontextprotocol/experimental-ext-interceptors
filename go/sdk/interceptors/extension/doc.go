// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

// Package extension registers interceptors as first-class MCP
// primitives. Adding an interceptor to the extension makes it
// discoverable via "interceptors/list" and invocable via
// "interceptor/invoke", so any MCP client — local or remote — can
// discover and call interceptors using standard JSON-RPC.
//
// # Getting Started
//
// Create an [Extension], register interceptors, and install on an
// [mcp.Server]:
//
//	ext := extension.New(extension.WithLogger(logger))
//	ext.AddInterceptor(myValidator)
//	ext.AddInterceptor(myMutator)
//	ext.Install(mcpServer)
//
// At this point any connected MCP client can call interceptors/list
// and interceptor/invoke.
//
// For convenience, [Extension.LocalChain] creates a [chain.Chain] wired
// to the server over an in-memory transport, ready for use with the
// gomiddleware package:
//
//	chain, err := ext.LocalChain(ctx, mcpServer)
//	mcpServer.AddReceivingMiddleware(
//	    gomiddleware.Middleware(chain,
//	        gomiddleware.WithContextProvider(myProvider),
//	    ),
//	)
//
// # JSON-RPC Methods
//
// The extension installs two JSON-RPC methods on the server:
//
//   - "interceptors/list": Returns all registered interceptors,
//     optionally filtered by event name.
//   - "interceptor/invoke": Invokes a single interceptor by name
//     for a given event, phase, and payload.
//
// # Transports
//
// Users interact with the [mcp.Server] directly for transport
// configuration. For stdio, use [mcp.Server.Run]. For HTTP, use
// [mcp.NewStreamableHTTPHandler]:
//
//	handler := mcp.NewStreamableHTTPHandler(
//	    func(r *http.Request) *mcp.Server { return mcpServer },
//	    nil,
//	)
//	http.ListenAndServe(":8080", handler)
package extension
