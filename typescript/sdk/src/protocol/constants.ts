// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/** JSON-RPC method names for the interceptors extension. */
export const InterceptorRequestMethods = {
  InterceptorsList: 'interceptors/list',
  InterceptorInvoke: 'interceptor/invoke',
} as const;

/**
 * SEP-2133 extensions key under which interceptor hosts advertise support:
 * `capabilities.extensions["io.modelcontextprotocol/interceptors"]`.
 */
export const InterceptorExtensionCapabilityKey = 'io.modelcontextprotocol/interceptors';

/** Well-known lifecycle event identifiers (SEP-2624). */
export const InterceptionEvents = {
  ToolsList: 'tools/list',
  ToolsCall: 'tools/call',
  PromptsList: 'prompts/list',
  PromptsGet: 'prompts/get',
  ResourcesList: 'resources/list',
  ResourcesRead: 'resources/read',
  ResourcesSubscribe: 'resources/subscribe',
  SamplingCreateMessage: 'sampling/createMessage',
  ElicitationCreate: 'elicitation/create',
  RootsList: 'roots/list',
  LlmCompletion: 'llm/completion',
  All: '*',
} as const;
