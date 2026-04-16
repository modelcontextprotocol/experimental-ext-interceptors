// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package interceptors

import (
	"context"
	"fmt"
	"log/slog"
	"sync"
	"sync/atomic"
)

// Chain is a protocol-agnostic interceptor chain engine. It manages a set
// of interceptors and executes them in the correct order for a given
// event and phase. It has no dependency on any specific transport and
// can be used with MCP, gRPC, custom HTTP, or any other server.
type Chain struct {
	mu       sync.Mutex
	snapshot atomic.Pointer[interceptorSnapshot]
	logger   *slog.Logger
}

// ChainOption configures a Chain.
type ChainOption func(*Chain)

// WithChainLogger sets the logger for chain execution.
// If not set, slog.Default() is used.
func WithChainLogger(l *slog.Logger) ChainOption {
	return func(c *Chain) {
		c.logger = l
	}
}

// NewChain creates a new Chain with optional configuration.
func NewChain(opts ...ChainOption) *Chain {
	c := &Chain{}
	for _, opt := range opts {
		opt(c)
	}
	if c.logger == nil {
		c.logger = slog.Default()
	}
	c.snapshot.Store(&interceptorSnapshot{logger: c.logger})
	return c
}

// Add registers an interceptor. It panics if the interceptor has a nil
// handler or is an unsupported type. It is safe to call while the chain
// is in use. Returns the receiver for chaining.
func (c *Chain) Add(i Interceptor) *Chain {
	switch v := i.(type) {
	case *Validator:
		if v.Handler == nil {
			panic("interceptors: validator " + v.Name + " has nil handler")
		}
	case *Mutator:
		if v.Handler == nil {
			panic("interceptors: mutator " + v.Name + " has nil handler")
		}
	default:
		panic(fmt.Sprintf("interceptors: unsupported interceptor type %T", i))
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	old := c.snapshot.Load()
	newAll := make([]Interceptor, len(old.all)+1)
	copy(newAll, old.all)
	newAll[len(old.all)] = i
	c.snapshot.Store(&interceptorSnapshot{
		all:    newAll,
		logger: c.logger,
	})
	return c
}

// ExecuteForReceiving runs the interceptor chain for the given invocation
// using receive-side ordering (validate then mutate). Returns nil, nil if
// no interceptors match the invocation's event and phase.
func (c *Chain) ExecuteForReceiving(ctx context.Context, inv *Invocation) (*ChainResult, error) {
	snap := c.snapshot.Load()
	ce := snap.getChain(inv.Event, inv.Phase)
	if ce.empty {
		return nil, nil
	}
	return ce.executeForReceiving(ctx, inv)
}

// ExecuteForSending runs the interceptor chain for the given invocation
// using send-side ordering (mutate then validate). Returns nil, nil if
// no interceptors match the invocation's event and phase.
func (c *Chain) ExecuteForSending(ctx context.Context, inv *Invocation) (*ChainResult, error) {
	snap := c.snapshot.Load()
	ce := snap.getChain(inv.Event, inv.Phase)
	if ce.empty {
		return nil, nil
	}
	return ce.executeForSending(ctx, inv)
}

// IsEmpty reports whether no interceptors match the given event and phase.
func (c *Chain) IsEmpty(event string, phase InterceptionPhase) bool {
	snap := c.snapshot.Load()
	ce := snap.getChain(event, phase)
	return ce.empty
}

// Interceptors returns the current list of registered interceptors.
func (c *Chain) Interceptors() []Interceptor {
	return c.snapshot.Load().all
}
