// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * MCP Interceptors TypeScript SDK
 *
 * Implements MCP Interceptors based on SEP-2624:
 * https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624
 */

export {
  InterceptorRequestMethods,
  InterceptionEvents,
  InterceptorExtensionCapabilityKey,
} from './protocol/constants.js';

export type {
  ChainAbortInfo,
  ChainValidationSummary,
  ExecuteChainRequestParams,
  Interceptor,
  InterceptorOverrides,
  ChainInterceptorEntry,
  InterceptorChainResult,
  InterceptorChainStatus,
  InterceptorCompatibility,
  InterceptorHook,
  InterceptorMode,
  PriorityHint,
  PriorityHintByPhase,
  InterceptorPhase,
  InterceptorPrincipal,
  InterceptorResult,
  InterceptorType,
  InterceptorsCapability,
  InvokeInterceptorContext,
  InvokeInterceptorRequestParams,
  ListInterceptorsRequestParams,
  ListInterceptorsResult,
  MutationInterceptorResult,
  SinkInterceptorResult,
  ValidationInterceptorResult,
  ValidationMessage,
  ValidationSeverity,
  ValidationSuggestion,
} from './protocol/types.js';

export {
  isMutationResult,
  isSinkResult,
  isValidationResult,
  parseInterceptorResult,
  validationFailure,
  validationSuccess,
} from './protocol/results.js';

export {
  InterceptorHookSchema,
  InterceptorOverridesSchema,
  InterceptorResultSchema,
  InterceptorSchema,
  ListInterceptorsResultSchema,
  MutationInterceptorResultSchema,
  SinkInterceptorResultSchema,
  ValidationInterceptorResultSchema,
} from './protocol/zod-schemas.js';

export {
  executeInterceptorChain,
  matchesEvent,
  type InterceptorInvoker,
} from './client/chain-orchestrator.js';

export { resolvePriority } from './protocol/resolve-priority.js';

export {
  executeInterceptorChainOnClient,
  invokeInterceptor,
  listAllInterceptors,
  listInterceptors,
  type InterceptorRequestOptions,
} from './client/client-extensions.js';

export { executeInterceptorChainOnClients } from './client/execute-interceptor-chain-on-clients.js';
export type { ExecuteInterceptorChainOnClientsOptions } from './client/execute-interceptor-chain-on-clients.js';

export {
  listInterceptorChainEntries,
  mergeInterceptorChainEntries,
  interceptorsFromEntries,
  clientByInterceptorName,
} from './client/merge-interceptor-chain-entries.js';

export type {
  InterceptorChainEntry,
  InterceptorChainHost,
  DuplicateInterceptorNamePolicy,
  MergeInterceptorChainEntriesOptions,
} from './client/interceptor-chain-entry.js';

export { InterceptorChainRunner, type InterceptorChainRunnerOptions } from './client/interceptor-chain-runner.js';

export {
  InterceptingMcpClient,
  type InterceptingMcpClientOptions,
} from './client/intercepting-client.js';

export {
  collectValidationErrorMessages,
  DuplicateInterceptorNameError,
  InterceptorOverrideHookError,
  formatValidationFailedChainMessage,
  McpInterceptorChainException,
  McpInterceptorValidationException,
  throwChainFailure,
} from './protocol/errors.js';

export {
  buildInterceptorsCapability,
  collectSupportedEvents,
  registerInterceptorCapabilities,
} from './server/capabilities.js';

export {
  registerInterceptorsOnServer,
  type InterceptorHandler,
  type RegisteredInterceptor,
  type RegisterInterceptorsOptions,
} from './server/register-interceptors.js';

export {
  buildInterceptorDescriptor,
  type InterceptorDefinitionOptions,
  type InterceptorPhaseOption,
} from './server/interceptor-definition.js';

export {
  defineInterceptor,
  invokeHandlerFunction,
  type InterceptorHandlerFn,
} from './server/reflection.js';

export {
  McpInterceptorGateway,
  type McpInterceptorGatewayOptions,
} from './gateway/mcp-interceptor-gateway.js';

export type { McpInterceptorServerConnectionOptions } from './gateway/mcp-interceptor-server-connection-options.js';
export type { GatewayMessageContext } from './gateway/gateway-message-context.js';
export type { InterceptorServerConnectionResolver } from './gateway/gateway-interceptor-client-provider.js';

export { GatewayProxyConfigurator, type GatewayProxyConfiguratorOptions } from './gateway/gateway-proxy-configurator.js';

export type {
  LlmCompletionRequestPayload,
  LlmCompletionResponsePayload,
  LlmMessage,
  LlmUsage,
} from './protocol/llm-payload.js';

export {
  InvokeInterceptorRequestSchema,
  ListInterceptorsRequestSchema,
} from './protocol/zod-schemas.js';
