// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Server, CallToolRequest, GetPromptRequest, Implementation, ListPromptsRequest, ListResourcesRequest, ListToolsRequest, Progress, ReadResourceRequest, ServerCapabilities, SubscribeRequest } from "@modelcontextprotocol/server";
import type { Client } from "@modelcontextprotocol/client";

import { InterceptorChainRunner } from '../client/interceptor-chain-runner.js';
import { InterceptionEvents } from '../protocol/constants.js';
import type { InvokeInterceptorContext } from '../protocol/types.js';
import type { GatewayInterceptorClientProvider } from './gateway-interceptor-client-provider.js';
import type { GatewayMessageContext } from './gateway-message-context.js';
import { runProxiedRequest } from './proxy-request.js';

export interface GatewayProxyConfiguratorOptions {
  backendClient: Client;
  interceptorClientProvider: GatewayInterceptorClientProvider;
  events?: string[];
  timeoutMs?: number;
  defaultContext?: InvokeInterceptorContext;
  serverInfo?: Implementation;
}

function cloneBackendCapabilities(capabilities: ServerCapabilities): ServerCapabilities {
  const cloned = structuredClone(capabilities) as ServerCapabilities & { tasks?: unknown };
  delete cloned.tasks;
  return cloned;
}

function coerceParams<T extends { params?: unknown }>(
  request: T,
  mutated: unknown,
): NonNullable<T['params']> {
  if (typeof mutated === 'object' && mutated !== null) {
    return mutated as NonNullable<T['params']>;
  }
  return request.params as NonNullable<T['params']>;
}

function messageContextFromRequest(
  method: string,
  params: unknown,
  ctx: { http?: { authInfo?: unknown }; sessionId?: string },
): GatewayMessageContext {
  return {
    method,
    params,
    authInfo: ctx.http?.authInfo,
    sessionId: ctx.sessionId,
  };
}

export class GatewayProxyConfigurator {
  private readonly backend: Client;
  private readonly provider: GatewayInterceptorClientProvider;
  private readonly events?: string[];
  private readonly timeoutMs?: number;
  private readonly defaultContext?: InvokeInterceptorContext;
  private readonly serverInfo?: Implementation;

  constructor(options: GatewayProxyConfiguratorOptions) {
    this.backend = options.backendClient;
    this.provider = options.interceptorClientProvider;
    this.events = options.events;
    this.timeoutMs = options.timeoutMs;
    this.defaultContext = options.defaultContext;
    this.serverInfo = options.serverInfo;
  }

  configure(server: Server): void {
    const backendCaps = this.backend.getServerCapabilities();

    if (backendCaps) {
      server.registerCapabilities(cloneBackendCapabilities(backendCaps));
    }

    if (this.serverInfo) {
      // v1 Server info is fixed at construction; callers should pass the same override there.
    }

    if (backendCaps?.tools) {
      this.configureTools(server);
    }
    if (backendCaps?.prompts) {
      this.configurePrompts(server);
    }
    if (backendCaps?.resources) {
      this.configureResources(server, backendCaps);
    }
    if (backendCaps?.completions) {
      server.setRequestHandler('completion/complete', (request, ctx) =>
        this.backend.complete(request.params, { signal: ctx.mcpReq.signal }),
      );
    }
    if (backendCaps?.logging) {
      server.setRequestHandler('logging/setLevel', async (request, ctx) => {
        await this.backend.setLoggingLevel(request.params.level, { signal: ctx.mcpReq.signal });
        return {};
      });
    }
  }

  private createChainRunner(interceptorClients: Client[]): InterceptorChainRunner {
    return new InterceptorChainRunner({
      interceptorClients,
      events: this.events,
      timeoutMs: this.timeoutMs,
      defaultContext: this.defaultContext,
    });
  }

  private async runProxied<T>(
    context: GatewayMessageContext,
    operation: string,
    eventName: string,
    requestParams: unknown,
    forward: (mutatedParams: unknown, signal?: AbortSignal) => Promise<T>,
    signal?: AbortSignal,
  ): Promise<T> {
    const resolved = await this.provider.resolve(context, eventName, signal);
    try {
      return await runProxiedRequest({
        operation,
        eventName,
        requestParams,
        chainRunner: this.createChainRunner(resolved.clients),
        signal,
        forward,
      });
    } finally {
      try {
        await resolved.dispose();
      } catch {
        // A failing per-request client close must not mask the request outcome.
      }
    }
  }

  private configureTools(server: Server): void {
    server.setRequestHandler('tools/list', async (request, ctx) =>
      this.runProxied(
        messageContextFromRequest('tools/list', request.params ?? {}, ctx),
        'tools/list',
        InterceptionEvents.ToolsList,
        request.params ?? {},
        // request() instead of listTools(): the v2 typed list verbs aggregate
        // every page, but the proxy must forward the caller's page verbatim.
        (params, sig) =>
          this.backend.request(
            { method: 'tools/list', params: params as ListToolsRequest['params'] },
            { signal: sig },
          ),
        ctx.mcpReq.signal,
      ),
    );

    server.setRequestHandler('tools/call', async (request, ctx) => {
      const progressToken = (request.params?._meta as { progressToken?: string | number } | undefined)
        ?.progressToken;
      return this.runProxied(
        messageContextFromRequest('tools/call', request.params, ctx),
        'tools/call',
        InterceptionEvents.ToolsCall,
        request.params,
        (params, sig) =>
          this.backend.callTool(
            coerceParams(request, params) as CallToolRequest['params'],
            {
              signal: sig,
              // Relay backend progress to the proxied caller under its own token.
              ...(progressToken != null
                ? {
                    onprogress: (progress: Progress) => {
                      void server
                        .notification({
                          method: 'notifications/progress',
                          params: { ...progress, progressToken },
                        })
                        .catch(() => {});
                    },
                  }
                : {}),
            },
          ),
        ctx.mcpReq.signal,
      );
    });
  }

  private configurePrompts(server: Server): void {
    server.setRequestHandler('prompts/list', async (request, ctx) =>
      this.runProxied(
        messageContextFromRequest('prompts/list', request.params ?? {}, ctx),
        'prompts/list',
        InterceptionEvents.PromptsList,
        request.params ?? {},
        (params, sig) =>
          this.backend.request(
            { method: 'prompts/list', params: params as ListPromptsRequest['params'] },
            { signal: sig },
          ),
        ctx.mcpReq.signal,
      ),
    );

    server.setRequestHandler('prompts/get', async (request, ctx) =>
      this.runProxied(
        messageContextFromRequest('prompts/get', request.params, ctx),
        'prompts/get',
        InterceptionEvents.PromptsGet,
        request.params,
        (params, sig) =>
          this.backend.getPrompt(
            coerceParams(request, params) as GetPromptRequest['params'],
            { signal: sig },
          ),
        ctx.mcpReq.signal,
      ),
    );
  }

  private configureResources(server: Server, backendCaps: ServerCapabilities): void {
    server.setRequestHandler('resources/list', async (request, ctx) =>
      this.runProxied(
        messageContextFromRequest('resources/list', request.params ?? {}, ctx),
        'resources/list',
        InterceptionEvents.ResourcesList,
        request.params ?? {},
        (params, sig) =>
          this.backend.request(
            { method: 'resources/list', params: params as ListResourcesRequest['params'] },
            { signal: sig },
          ),
        ctx.mcpReq.signal,
      ),
    );

    server.setRequestHandler('resources/read', async (request, ctx) =>
      this.runProxied(
        messageContextFromRequest('resources/read', request.params, ctx),
        'resources/read',
        InterceptionEvents.ResourcesRead,
        request.params,
        (params, sig) =>
          this.backend.readResource(
            coerceParams(request, params) as ReadResourceRequest['params'],
            { signal: sig },
          ),
        ctx.mcpReq.signal,
      ),
    );

    server.setRequestHandler('resources/templates/list', (request, ctx) =>
      this.backend.request(
        { method: 'resources/templates/list', params: request.params },
        { signal: ctx.mcpReq.signal },
      ),
    );

    if (backendCaps.resources?.subscribe) {
      server.setRequestHandler('resources/subscribe', async (request, ctx) => {
        await this.runProxied(
          messageContextFromRequest('resources/subscribe', request.params, ctx),
          'resources/subscribe',
          InterceptionEvents.ResourcesSubscribe,
          request.params,
          async (params, sig) => {
            await this.backend.subscribeResource(
              coerceParams(request, params) as SubscribeRequest['params'],
              { signal: sig },
            );
            return {};
          },
          ctx.mcpReq.signal,
        );
        return {};
      });

      server.setRequestHandler('resources/unsubscribe', async (request, ctx) => {
        await this.backend.unsubscribeResource(request.params, { signal: ctx.mcpReq.signal });
        return {};
      });
    }
  }
}
