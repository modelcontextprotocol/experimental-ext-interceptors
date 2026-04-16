// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package mcpserver

import (
	"context"
	"log/slog"
	"net/http"
	"sync"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/chain"
)

// extensionID is the capability key used in Capabilities.Experimental
const extensionID = "io.modelcontextprotocol/interceptors"

// ServerOption configures a Server.
type ServerOption func(*Server)

// WithLogger sets the logger for interceptor chain execution.
// If not set, slog.Default() is used.
func WithLogger(l *slog.Logger) ServerOption {
	return func(s *Server) {
		s.logger = l
	}
}

// Server wraps an *mcp.Server with interceptor support. Interceptors are
// registered as first-class MCP resources discoverable via the
// "interceptors/list" JSON-RPC method and invocable via
// "interceptor/invoke".
type Server struct {
	inner        *mcp.Server
	mu           sync.RWMutex
	interceptors []interceptors.Interceptor
	logger       *slog.Logger
}

// NewServer creates a new interceptor server wrapping the given mcp.Server.
// It registers the "interceptors/list" and "interceptor/invoke" JSON-RPC
// methods and installs a receiving middleware to enrich the initialize
// response with interceptor capabilities.
func NewServer(server *mcp.Server, opts ...ServerOption) *Server {
	s := &Server{
		inner: server,
	}
	for _, opt := range opts {
		opt(s)
	}
	if s.logger == nil {
		s.logger = slog.Default()
	}

	// Register JSON-RPC methods for interceptor discovery and invocation.
	server.AddCustomMethod(interceptors.MethodList, s.handleList)
	server.AddCustomMethod(interceptors.MethodInvoke, s.handleInvoke)

	// Install lightweight middleware to enrich initialize responses.
	server.AddReceivingMiddleware(s.initMiddleware())

	return s
}

// AddInterceptor registers an interceptor. It panics if the interceptor
// has a nil handler. Except for the panic it is safe to call while the server is running,
// returns the receiver for chaining.
func (s *Server) AddInterceptor(i interceptors.Interceptor) *Server {
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
	s.mu.Lock()
	s.interceptors = append(s.interceptors, i)
	s.mu.Unlock()
	return s
}

// findByName returns the interceptor with the given name, or nil if not found.
func (s *Server) findByName(name string) interceptors.Interceptor {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, i := range s.interceptors {
		if i.GetMetadata().Name == name {
			return i
		}
	}
	return nil
}

// getInterceptors returns a snapshot of the current interceptor list.
func (s *Server) getInterceptors() []interceptors.Interceptor {
	s.mu.RLock()
	defer s.mu.RUnlock()
	cp := make([]interceptors.Interceptor, len(s.interceptors))
	copy(cp, s.interceptors)
	return cp
}

// LocalChain creates an interceptor Chain connected to this server
// via in-memory transport. This is the primary way to get a Chain
// for use with middleware.
//
// The returned chain discovers interceptors from the server via
// interceptors/list and invokes them via interceptor/invoke, exactly
// as a remote chain would — but over an in-memory transport.
func (s *Server) LocalChain(ctx context.Context) (*chain.Chain, error) {
	serverTransport, clientTransport := mcp.NewInMemoryTransports()

	// Connect the server side.
	ss, err := s.inner.Connect(ctx, serverTransport, nil)
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

	ch := chain.NewChain(chain.WithChainLogger(s.logger))
	if err := ch.AddMCPServer(ctx, cs); err != nil {
		cs.Close()
		ss.Close()
		return nil, err
	}

	return ch, nil
}

// MCPServer returns the underlying *mcp.Server. This is useful when
// composing with other extensions (e.g., variants):
//
//	is := mcpserver.NewServer(mcpServer)
//	is.AddInterceptor(myValidator)
//	vs.WithVariant(variant, is.MCPServer(), 0)
func (s *Server) MCPServer() *mcp.Server {
	return s.inner
}

// Run delegates to the inner server's Run method. Convenience for standalone
// use (e.g., stdio).
func (s *Server) Run(ctx context.Context, t mcp.Transport) error {
	return s.inner.Run(ctx, t)
}

// initMiddleware returns a lightweight mcp.Middleware that enriches the
// initialize response with interceptor capability metadata.
func (s *Server) initMiddleware() mcp.Middleware {
	return func(next mcp.MethodHandler) mcp.MethodHandler {
		return func(ctx context.Context, method string, req mcp.Request) (mcp.Result, error) {
			result, err := next(ctx, method, req)
			if err != nil {
				return nil, err
			}
			if method == "initialize" {
				return s.enrichInitResult(result)
			}
			return result, nil
		}
	}
}

// enrichInitResult injects interceptor capability metadata into the
// InitializeResult, following the same pattern as the variants extension.
// The capability is declared under Capabilities.Experimental so it works
// without upstream go-sdk changes.
func (s *Server) enrichInitResult(result mcp.Result) (mcp.Result, error) {
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

	all := s.getInterceptors()
	supportedEvents := map[string]bool{}
	for _, ri := range all {
		for _, e := range ri.GetMetadata().Hook.Events {
			supportedEvents[e] = true
		}
	}

	events := make([]string, 0, len(supportedEvents))
	for e := range supportedEvents {
		events = append(events, e)
	}

	initResult.Capabilities.Experimental[extensionID] = map[string]any{
		"supportedEvents": events,
	}

	return initResult, nil
}

// NewStreamableHTTPHandler returns a new [mcp.StreamableHTTPHandler] for
// serving multiple concurrent clients over HTTP. It mirrors
// [mcp.NewStreamableHTTPHandler].
//
//	handler := mcpserver.NewStreamableHTTPHandler(srv, nil)
//	http.ListenAndServe(":8080", handler)
func NewStreamableHTTPHandler(s *Server, opts *mcp.StreamableHTTPOptions) *mcp.StreamableHTTPHandler {
	if s == nil {
		panic("mcpserver: nil Server")
	}
	srv := s.MCPServer()
	return mcp.NewStreamableHTTPHandler(
		func(r *http.Request) *mcp.Server { return srv },
		opts,
	)
}
