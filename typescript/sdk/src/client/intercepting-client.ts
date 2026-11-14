// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { CompatibilityCallToolResultSchema } from "@modelcontextprotocol/core";
import type { Client, CallToolResult, GetPromptResult, ListPromptsResult, ListResourcesResult, ListToolsResult, ReadResourceResult } from "@modelcontextprotocol/client";

import { InterceptionEvents } from '../protocol/constants.js';
import type {
  InvokeInterceptorContext,
  ListInterceptorsRequestParams,
  ListInterceptorsResult,
} from '../protocol/types.js';
import { listInterceptors } from './client-extensions.js';
import { InterceptorChainRunner } from './interceptor-chain-runner.js';

export interface InterceptingMcpClientOptions {
  interceptorClient: Client;
  /** When omitted or empty, all configured events are intercepted. */
  events?: string[];
  timeoutMs?: number;
  defaultContext?: InvokeInterceptorContext;
}

/**
 * Gateway-style client: runs interceptor chains on request/response, then forwards to the backend.
 */
export class InterceptingMcpClient {
  readonly inner: Client;
  readonly interceptorClient: Client;
  private readonly chainRunner: InterceptorChainRunner;

  constructor(inner: Client, options: InterceptingMcpClientOptions) {
    this.inner = inner;
    this.interceptorClient = options.interceptorClient;
    this.chainRunner = new InterceptorChainRunner({
      interceptorClients: [options.interceptorClient],
      events: options.events,
      timeoutMs: options.timeoutMs,
      defaultContext: options.defaultContext,
    });
  }

  async callTool(
    name: string,
    args?: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<CallToolResult> {
    // request() with the compatibility schema: v2 callTool() no longer takes a
    // result schema, and this client keeps v1's tolerant legacy-result parsing.
    const resultSchema = CompatibilityCallToolResultSchema;
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.ToolsCall)) {
      return (await this.inner.request(
        { method: 'tools/call', params: { name, arguments: args } },
        resultSchema,
        { signal },
      )) as CallToolResult;
    }

    const callParams = { name, arguments: args };
    let requestPayload: unknown = callParams;

    requestPayload = await this.chainRunner.runChainPhaseOrThrow(
      'tools/call',
      InterceptionEvents.ToolsCall,
      'request',
      requestPayload,
      signal,
    );

    if (
      typeof requestPayload !== 'object' ||
      requestPayload === null ||
      typeof (requestPayload as { name?: unknown }).name !== 'string'
    ) {
      throw new Error(
        'Interceptor chain produced an invalid tools/call payload: expected an object with a string `name`',
      );
    }
    // Forward the mutated payload as-is: a mutation that removed `arguments`
    // (e.g. redaction) must not have the original arguments restored.
    const mutated = requestPayload as { name: string; arguments?: Record<string, unknown> };

    const result = await this.inner.request(
      { method: 'tools/call', params: { name: mutated.name, arguments: mutated.arguments } },
      resultSchema,
      { signal },
    );

    const processedResponse = await this.chainRunner.runChainPhaseOrThrow(
      'tools/call',
      InterceptionEvents.ToolsCall,
      'response',
      result,
      signal,
    );

    return processedResponse as CallToolResult;
  }

  async listTools(signal?: AbortSignal): Promise<ListToolsResult> {
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.ToolsList)) {
      return this.inner.listTools(undefined, { signal });
    }

    await this.chainRunner.runChainPhaseOrThrow(
      'tools/list',
      InterceptionEvents.ToolsList,
      'request',
      {},
      signal,
    );

    const result = await this.inner.listTools(undefined, { signal });

    const processedResponse = await this.chainRunner.runChainPhaseOrThrow(
      'tools/list',
      InterceptionEvents.ToolsList,
      'response',
      result,
      signal,
    );

    return processedResponse as ListToolsResult;
  }

  async listPrompts(signal?: AbortSignal): Promise<ListPromptsResult> {
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.PromptsList)) {
      return this.inner.listPrompts(undefined, { signal });
    }

    await this.chainRunner.runChainPhaseOrThrow(
      'prompts/list',
      InterceptionEvents.PromptsList,
      'request',
      {},
      signal,
    );

    const result = await this.inner.listPrompts(undefined, { signal });

    const processedResponse = await this.chainRunner.runChainPhaseOrThrow(
      'prompts/list',
      InterceptionEvents.PromptsList,
      'response',
      result,
      signal,
    );

    return processedResponse as ListPromptsResult;
  }

  async getPrompt(
    name: string,
    args?: Record<string, string>,
    signal?: AbortSignal,
  ): Promise<GetPromptResult> {
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.PromptsGet)) {
      return this.inner.getPrompt({ name, arguments: args }, { signal });
    }

    const getParams = { name, arguments: args };
    let requestPayload: unknown = getParams;

    requestPayload = await this.chainRunner.runChainPhaseOrThrow(
      'prompts/get',
      InterceptionEvents.PromptsGet,
      'request',
      requestPayload,
      signal,
    );

    if (
      typeof requestPayload !== 'object' ||
      requestPayload === null ||
      typeof (requestPayload as { name?: unknown }).name !== 'string'
    ) {
      throw new Error(
        'Interceptor chain produced an invalid prompts/get payload: expected an object with a string `name`',
      );
    }
    // Forward the mutated payload as-is: a mutation that removed `arguments`
    // (e.g. redaction) must not have the original arguments restored.
    const mutated = requestPayload as { name: string; arguments?: Record<string, string> };

    const result = await this.inner.getPrompt(
      { name: mutated.name, arguments: mutated.arguments },
      { signal },
    );

    const processed = await this.chainRunner.runChainPhaseOrThrow(
      'prompts/get',
      InterceptionEvents.PromptsGet,
      'response',
      result,
      signal,
    );

    return processed as GetPromptResult;
  }

  async listResources(signal?: AbortSignal): Promise<ListResourcesResult> {
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.ResourcesList)) {
      return this.inner.listResources(undefined, { signal });
    }

    await this.chainRunner.runChainPhaseOrThrow(
      'resources/list',
      InterceptionEvents.ResourcesList,
      'request',
      {},
      signal,
    );

    const result = await this.inner.listResources(undefined, { signal });

    const processedResponse = await this.chainRunner.runChainPhaseOrThrow(
      'resources/list',
      InterceptionEvents.ResourcesList,
      'response',
      result,
      signal,
    );

    return processedResponse as ListResourcesResult;
  }

  async readResource(uri: string, signal?: AbortSignal): Promise<ReadResourceResult> {
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.ResourcesRead)) {
      return this.inner.readResource({ uri }, { signal });
    }

    const readParams = { uri };
    let requestPayload: unknown = readParams;

    requestPayload = await this.chainRunner.runChainPhaseOrThrow(
      'resources/read',
      InterceptionEvents.ResourcesRead,
      'request',
      requestPayload,
      signal,
    );

    const mutated =
      typeof requestPayload === 'object' && requestPayload !== null && 'uri' in requestPayload
        ? (requestPayload as { uri: string })
        : readParams;

    const result = await this.inner.readResource({ uri: mutated.uri }, { signal });

    const processed = await this.chainRunner.runChainPhaseOrThrow(
      'resources/read',
      InterceptionEvents.ResourcesRead,
      'response',
      result,
      signal,
    );

    return processed as ReadResourceResult;
  }

  async subscribeResource(uri: string, signal?: AbortSignal): Promise<void> {
    if (!this.chainRunner.shouldIntercept(InterceptionEvents.ResourcesSubscribe)) {
      await this.inner.subscribeResource({ uri }, { signal });
      return;
    }

    let requestPayload: unknown = { uri };

    requestPayload = await this.chainRunner.runChainPhaseOrThrow(
      'resources/subscribe',
      InterceptionEvents.ResourcesSubscribe,
      'request',
      requestPayload,
      signal,
    );

    const mutated =
      typeof requestPayload === 'object' && requestPayload !== null && 'uri' in requestPayload
        ? (requestPayload as { uri: string })
        : { uri };

    await this.inner.subscribeResource({ uri: mutated.uri }, { signal });
  }

  listInterceptors(params?: ListInterceptorsRequestParams): Promise<ListInterceptorsResult> {
    return listInterceptors(this.interceptorClient, params);
  }

  async close(): Promise<void> {
    await Promise.all([this.inner.close(), this.interceptorClient.close()]);
  }
}
