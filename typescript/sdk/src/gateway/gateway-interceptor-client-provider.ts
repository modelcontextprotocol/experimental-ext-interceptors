// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Client } from "@modelcontextprotocol/client";

import type { GatewayMessageContext } from './gateway-message-context.js';
import type { McpInterceptorServerConnectionOptions } from './mcp-interceptor-server-connection-options.js';
import { GatewayInterceptorClientPool } from './gateway-interceptor-client-pool.js';
import { GatewayResolvedInterceptorClients } from './gateway-resolved-interceptor-clients.js';

export type InterceptorServerConnectionResolver = (
  context: GatewayMessageContext,
  event: string,
  signal?: AbortSignal,
) => Promise<McpInterceptorServerConnectionOptions[]>;

export class GatewayInterceptorClientProvider {
  private readonly staticClients: Client[];
  private readonly connectionResolver?: InterceptorServerConnectionResolver;
  private readonly clientPool?: GatewayInterceptorClientPool;

  constructor(staticClients: Client[], connectionResolver?: InterceptorServerConnectionResolver) {
    this.staticClients = staticClients;
    this.connectionResolver = connectionResolver;
    this.clientPool = connectionResolver ? new GatewayInterceptorClientPool() : undefined;
  }

  async resolve(
    messageContext: GatewayMessageContext,
    event: string,
    signal?: AbortSignal,
  ): Promise<GatewayResolvedInterceptorClients> {
    if (!this.connectionResolver) {
      return new GatewayResolvedInterceptorClients(this.staticClients);
    }

    const resolvedConnections = (await this.connectionResolver(messageContext, event, signal)) ?? [];
    const resolvedDynamic = await this.clientPool!.resolveClients(resolvedConnections, signal);

    if (this.staticClients.length === 0) {
      return resolvedDynamic;
    }

    if (resolvedDynamic.clients.length === 0) {
      await resolvedDynamic.dispose();
      return new GatewayResolvedInterceptorClients(this.staticClients);
    }

    return new GatewayResolvedInterceptorClients(
      [...this.staticClients, ...resolvedDynamic.clients],
      [resolvedDynamic],
    );
  }

  async dispose(): Promise<void> {
    if (this.clientPool) {
      await this.clientPool.dispose();
    }
  }
}
