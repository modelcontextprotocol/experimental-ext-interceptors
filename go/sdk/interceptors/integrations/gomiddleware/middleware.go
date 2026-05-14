// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package gomiddleware

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors"
	"github.com/modelcontextprotocol/ext-interceptors/go/sdk/interceptors/chain"
)

// Option configures the middleware.
type Option func(*config)

type config struct {
	logger          *slog.Logger
	contextProvider ContextProviderFunc
}

// ContextProviderFunc extracts an InvocationContext from an incoming MCP
// request. This is called once per intercepted request and the result is
// passed to all interceptor handlers via the chain execution params.
//
// Typical use: extract principal identity from OAuth tokens available on
// the request's session (via RequestExtra.TokenInfo).
type ContextProviderFunc func(ctx context.Context, req mcp.Request) *interceptors.InvocationContext

// WithLogger sets the logger for the middleware.
// If not set, slog.Default() is used.
func WithLogger(l *slog.Logger) Option {
	return func(c *config) {
		c.logger = l
	}
}

// WithContextProvider sets a function that populates InvocationContext
// for every intercepted request. Without this, the context is nil.
func WithContextProvider(f ContextProviderFunc) Option {
	return func(c *config) {
		c.contextProvider = f
	}
}

// skipMethods are methods that should not be intercepted to prevent
// recursion when the chain connects back to the same server, plus
// lifecycle methods that don't need interception.
var skipMethods = map[string]bool{
	"initialize":                true,
	"notifications/initialized": true,
	"notifications/cancelled":   true,
	interceptors.MethodList:     true,
	interceptors.MethodInvoke:   true,
}

// Middleware creates an [mcp.Middleware] that intercepts incoming requests
// and outgoing responses, running the interceptor chain for each.
//
// The middleware calls chain.Execute() which invokes interceptors via
// interceptor/invoke RPCs. Install it on an [mcp.Server] via
// [mcp.Server.AddReceivingMiddleware]:
//
//	chain, _ := srv.LocalChain(ctx)
//	mcpServer.AddReceivingMiddleware(
//	    gomiddleware.Middleware(chain, gomiddleware.WithLogger(logger)),
//	)
func Middleware(ch *chain.Chain, opts ...Option) mcp.Middleware {
	cfg := &config{}
	for _, opt := range opts {
		opt(cfg)
	}
	if cfg.logger == nil {
		cfg.logger = slog.Default()
	}

	return func(next mcp.MethodHandler) mcp.MethodHandler {
		return func(ctx context.Context, method string, req mcp.Request) (mcp.Result, error) {
			// Skip interceptor and lifecycle methods to prevent recursion.
			if skipMethods[method] {
				return next(ctx, method, req)
			}

			event := method

			// Extract invocation context from the request using the context provider, if set.
			var invCtx *interceptors.InvocationContext
			if cfg.contextProvider != nil {
				invCtx = cfg.contextProvider(ctx, req)
			}

			// --- Request phase ---
			reqPayload, err := json.Marshal(req.GetParams())
			if err != nil {
				return nil, err
			}

			reqResult, err := ch.Execute(ctx, &chain.ExecutionParams{
				Event:   event,
				Phase:   interceptors.PhaseRequest,
				Payload: reqPayload,
				Context: invCtx,
			})
			if err != nil {
				return nil, err
			}
			if len(reqResult.AbortedAt) > 0 {
				logAborts(cfg.logger, "request", event, reqResult.AbortedAt)
				return nil, abortToJSONRPCError(reqResult.AbortedAt)
			}

			// Apply mutated payload back to the request params if modified.
			if reqResult.FinalPayload != nil {
				if err := json.Unmarshal(reqResult.FinalPayload, req.GetParams()); err != nil {
					return nil, fmt.Errorf("interceptors: failed to apply mutated request payload: %w", err)
				}
			}

			// Call the next handler.
			result, err := next(ctx, method, req)
			if err != nil {
				return result, err
			}

			// Notification methods return nil result — skip response phase.
			if result == nil {
				return nil, nil
			}

			// --- Response phase ---
			respPayload, err := json.Marshal(result)
			if err != nil {
				return nil, fmt.Errorf("interceptors: failed to marshal response payload for interception: %w", err)
			}

			respResult, err := ch.Execute(ctx, &chain.ExecutionParams{
				Event:   event,
				Phase:   interceptors.PhaseResponse,
				Payload: respPayload,
				Context: invCtx,
			})
			if err != nil {
				return nil, err
			}
			if len(respResult.AbortedAt) > 0 {
				logAborts(cfg.logger, "response", event, respResult.AbortedAt)
				return nil, abortToJSONRPCError(respResult.AbortedAt)
			}

			// Apply mutated response payload if it was modified.
			if respResult.FinalPayload != nil {
				if err := json.Unmarshal(respResult.FinalPayload, result); err != nil {
					return nil, fmt.Errorf("interceptors: failed to apply mutated response payload: %w", err)
				}
			}

			return result, nil
		}
	}
}

// logAborts logs each abort entry at warn level.
func logAborts(logger *slog.Logger, phase, event string, aborts []chain.AbortInfo) {
	for _, a := range aborts {
		logger.Warn("interceptors: "+phase+" aborted",
			"event", event,
			"interceptor", a.Interceptor,
			"reason", a.Reason,
		)
	}
}
