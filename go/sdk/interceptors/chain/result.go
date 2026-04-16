// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

package chain

// ChainStatus describes the outcome of a full interceptor chain execution.
type ChainStatus string

const (
	ChainSuccess          ChainStatus = "success"
	ChainValidationFailed ChainStatus = "validation_failed"
	ChainMutationFailed   ChainStatus = "mutation_failed"
	ChainTimeout          ChainStatus = "timeout"
)

// AbortType classifies the reason an interceptor chain was aborted.
type AbortType string

const (
	AbortValidation AbortType = "validation"
	AbortMutation   AbortType = "mutation"
	AbortTimeout    AbortType = "timeout"
)

// ValidationSummary counts validation outcomes.
type ValidationSummary struct {
	Errors   int `json:"errors"`
	Warnings int `json:"warnings"`
	Infos    int `json:"infos"`
}

// AbortInfo describes where and why a chain was aborted.
type AbortInfo struct {
	Interceptor string    `json:"interceptor"`
	Reason      string    `json:"reason"`
	Type        AbortType `json:"type"`
	Phase       string    `json:"phase,omitempty"` // phase at which the abort occurred
}
