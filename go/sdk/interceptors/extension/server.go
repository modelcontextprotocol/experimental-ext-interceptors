// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package extension

import (
	"context"
	"log/slog"
	"sync"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/chain"
)

// extensionID is the capability key used in Capabilities.Experimental
const extensionID = "io.modelcontextprotocol/interceptors"

// Option configures an Extension.
type Option func(*Extension)

// WithLogger sets the logger for interceptor chain execution.
// If not set, slog.Default() is used.
func WithLogger(l *slog.Logger) Option {
	return func(e *Extension) {
		e.logger = l
	}
}

// Extension manages interceptors and can install them on one or more
// *mcp.Server instances. Interceptors are registered as first-class
// MCP resources discoverable via the "interceptors/list" JSON-RPC
// method and invocable via "interceptor/invoke".
//
// Extension is a pure interceptor container — it does not hold a
// reference to any particular server. Call [Extension.Install] to
// register the JSON-RPC methods and capability middleware on a server.
type Extension struct {
	mu           sync.RWMutex
	interceptors []interceptors.Interceptor
	logger       *slog.Logger
}

// New creates a new interceptor Extension.
func New(opts ...Option) *Extension {
	e := &Extension{}
	for _, opt := range opts {
		opt(e)
	}
	if e.logger == nil {
		e.logger = slog.Default()
	}
	return e
}

// Install registers the "interceptors/list" and "interceptor/invoke"
// JSON-RPC methods and installs a receiving middleware to enrich the
// initialize response with interceptor capabilities on the given
// server. It can be called on multiple servers.
func (e *Extension) Install(server *mcp.Server) {
	// Register JSON-RPC methods for interceptor discovery and invocation.
	mcp.AddReceivingCustomMethod(server, interceptors.MethodList, e.handleList)
	mcp.AddReceivingCustomMethod(server, interceptors.MethodInvoke, e.handleInvoke)
	server.AddReceivingMiddleware(e.initMiddleware())
}

// AddInterceptor registers an interceptor. It panics if the interceptor
// has a nil handler. Except for the panic it is safe to call while the server is running,
// returns the receiver for chaining.
func (e *Extension) AddInterceptor(i interceptors.Interceptor) *Extension {
	switch v := i.(type) {
	case *interceptors.Validator:
		if v.Handler == nil {
			panic("interceptors: validator " + v.Name + " has nil handler")
		}
	case *interceptors.Mutator:
		if v.Handler == nil {
			panic("interceptors: mutator " + v.Name + " has nil handler")
		}
	}
	e.mu.Lock()
	e.interceptors = append(e.interceptors, i)
	e.mu.Unlock()
	return e
}

// findByName returns the interceptor with the given name, or nil if not found.
func (e *Extension) findByName(name string) interceptors.Interceptor {
	e.mu.RLock()
	defer e.mu.RUnlock()
	for _, i := range e.interceptors {
		if i.GetMetadata().Name == name {
			return i
		}
	}
	return nil
}

// getInterceptors returns a snapshot of the current interceptor list.
func (e *Extension) getInterceptors() []interceptors.Interceptor {
	e.mu.RLock()
	defer e.mu.RUnlock()
	cp := make([]interceptors.Interceptor, len(e.interceptors))
	copy(cp, e.interceptors)
	return cp
}

// LocalChain creates an interceptor Chain connected to the given server
// via in-memory transport. This is the primary way to get a Chain
// for use with middleware.
//
// The returned chain discovers interceptors from the server via
// interceptors/list and invokes them via interceptor/invoke, exactly
// as a remote chain would — but over an in-memory transport.
func (e *Extension) LocalChain(ctx context.Context, server *mcp.Server) (*chain.Chain, error) {
	serverTransport, clientTransport := mcp.NewInMemoryTransports()

	// Connect the server side.
	ss, err := server.Connect(ctx, serverTransport, nil)
	if err != nil {
		return nil, err
	}

	// Connect a client through the other end.
	client := mcp.NewClient(&mcp.Implementation{
		Name:    "interceptor-chain-client",
		Version: "internal",
	}, nil)
	cs, err := client.Connect(ctx, clientTransport, nil)
	if err != nil {
		ss.Close()
		return nil, err
	}

	ch := chain.NewChain(chain.WithChainLogger(e.logger))
	if err := ch.AddMCPServer(ctx, cs); err != nil {
		cs.Close()
		ss.Close()
		return nil, err
	}

	return ch, nil
}

// initMiddleware returns a lightweight mcp.Middleware that enriches the
// initialize response with interceptor capability metadata.
func (e *Extension) initMiddleware() mcp.Middleware {
	return func(next mcp.MethodHandler) mcp.MethodHandler {
		return func(ctx context.Context, method string, req mcp.Request) (mcp.Result, error) {
			result, err := next(ctx, method, req)
			if err != nil {
				return nil, err
			}
			if method == "initialize" {
				return e.enrichInitResult(result)
			}
			return result, nil
		}
	}
}

// enrichInitResult injects interceptor capability metadata into the
// InitializeResult, following the same pattern as the variants extension.
// The capability is declared under Capabilities.Experimental so it works
// without upstream go-sdk changes.
func (e *Extension) enrichInitResult(result mcp.Result) (mcp.Result, error) {
	initResult, ok := result.(*mcp.InitializeResult)
	if !ok {
		return result, nil
	}

	if initResult.Capabilities == nil {
		initResult.Capabilities = &mcp.ServerCapabilities{}
	}
	if initResult.Capabilities.Experimental == nil {
		initResult.Capabilities.Experimental = make(map[string]any)
	}

	all := e.getInterceptors()
	supportedEvents := map[string]bool{}
	for _, ri := range all {
		for _, ev := range ri.GetMetadata().Hook.Events {
			supportedEvents[ev] = true
		}
	}

	events := make([]string, 0, len(supportedEvents))
	for ev := range supportedEvents {
		events = append(events, ev)
	}

	initResult.Capabilities.Experimental[extensionID] = map[string]any{
		"supportedEvents": events,
	}

	return initResult, nil
}
