// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

/** Request payload for the `llm/completion` lifecycle event. */
export interface LlmCompletionRequestPayload {
  model?: string;
  messages?: LlmMessage[];
  maxTokens?: number;
  temperature?: number;
  metadata?: Record<string, unknown>;
}

/** Response payload for the `llm/completion` lifecycle event. */
export interface LlmCompletionResponsePayload {
  model?: string;
  message?: LlmMessage;
  stopReason?: string;
  usage?: LlmUsage;
  metadata?: Record<string, unknown>;
}

export interface LlmMessage {
  role: string;
  content: string;
}

export interface LlmUsage {
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
}
