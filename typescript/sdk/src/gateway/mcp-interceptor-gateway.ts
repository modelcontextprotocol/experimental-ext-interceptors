// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Server, Implementation } from "@modelcontextprotocol/server";
import type { Client } from "@modelcontextprotocol/client";

import type { InvokeInterceptorContext } from '../protocol/types.js';
import { connectInterceptorClient } from './connect-interceptor-client.js';
import {
  GatewayInterceptorClientProvider,
  type InterceptorServerConnectionResolver,
} from './gateway-interceptor-client-provider.js';
import { GatewayInterceptorProtocolBridge } from './gateway-protocol-bridge.js';
import { GatewayProxyConfigurator } from './gateway-proxy-configurator.js';
import type { McpInterceptorServerConnectionOptions } from './mcp-interceptor-server-connection-options.js';

export interface McpInterceptorGatewayOptions {
  /** Connected client for the application (backend) MCP server. */
  backendClient: Client;
  /** Pre-connected interceptor host clients, executed in order. */
  interceptorClients?: Client[];
  /**
   * Outbound interceptor connections the gateway should open.
   * Use {@link McpInterceptorGateway.createAsync} when this is set.
   */
  interceptorServerConnections?: McpInterceptorServerConnectionOptions[];
  /**
   * Per-request resolver for additional interceptor host connections (transparent proxy only).
   * Not supported with {@link McpInterceptorGatewayOptions.exposeInterceptorProtocol}.
   */
  interceptorServerConnectionResolver?: InterceptorServerConnectionResolver;
  /** When set, only these lifecycle events run interceptor chains. `'*'` runs them for every event. */
  events?: string[];
  timeoutMs?: number;
  defaultContext?: InvokeInterceptorContext;
  /**
   * When true, expose `interceptors/list` and `interceptor/invoke` on the proxy server
   * (aggregated across static interceptor clients only).
   */
  exposeInterceptorProtocol?: boolean;
  /**
   * Optional server identity for connecting clients. v1 `Server` info is set at construction;
   * pass the same value to your `new Server(...)` call when overriding.
   */
  serverInfo?: Implementation;
}

function validateGatewayOptions(options: McpInterceptorGatewayOptions): void {
  if (!options.backendClient) {
    throw new Error('backendClient is required');
  }

  if (options.exposeInterceptorProtocol && options.interceptorServerConnectionResolver) {
    throw new Error(
      'interceptorServerConnectionResolver is only supported for the transparent proxy path. ' +
        'Disable exposeInterceptorProtocol or use static interceptorClients for SEP passthrough.',
    );
  }

  const hasStatic = (options.interceptorClients?.length ?? 0) > 0;
  const hasConnections = (options.interceptorServerConnections?.length ?? 0) > 0;
  const hasResolver = options.interceptorServerConnectionResolver !== undefined;

  if (!hasStatic && !hasConnections && !hasResolver) {
    throw new Error(
      'At least one of interceptorClients, interceptorServerConnections, or interceptorServerConnectionResolver is required',
    );
  }

  if (hasConnections) {
    throw new Error(
      'Use McpInterceptorGateway.createAsync when interceptorServerConnections is configured',
    );
  }
}

/**
 * Transparent MCP gateway: presents as the backend server while routing requests
 * through interceptor host chains.
 */
export class McpInterceptorGateway {
  readonly backendClient: Client;
  readonly interceptorClients: Client[];
  private readonly interceptorClientProvider: GatewayInterceptorClientProvider;
  private readonly proxyConfigurator: GatewayProxyConfigurator;
  private readonly protocolBridge?: GatewayInterceptorProtocolBridge;
  private readonly notificationCleanups: Array<() => void> = [];
  private readonly ownedClients: Client[];

  constructor(options: McpInterceptorGatewayOptions, ownedClients: Client[] = []) {
    validateGatewayOptions(options);

    this.backendClient = options.backendClient;
    this.interceptorClients = options.interceptorClients ?? [];
    this.ownedClients = ownedClients;

    this.interceptorClientProvider = new GatewayInterceptorClientProvider(
      this.interceptorClients,
      options.interceptorServerConnectionResolver,
    );

    this.proxyConfigurator = new GatewayProxyConfigurator({
      backendClient: options.backendClient,
      interceptorClientProvider: this.interceptorClientProvider,
      events: options.events,
      timeoutMs: options.timeoutMs,
      defaultContext: options.defaultContext,
      serverInfo: options.serverInfo,
    });

    if (options.exposeInterceptorProtocol) {
      this.protocolBridge = new GatewayInterceptorProtocolBridge(this.interceptorClients);
    }
  }

  /**
   * Creates a gateway and connects any configured external interceptor servers.
   */
  static async createAsync(
    options: McpInterceptorGatewayOptions,
    signal?: AbortSignal,
  ): Promise<McpInterceptorGateway> {
    if (!options.backendClient) {
      throw new Error('backendClient is required');
    }

    if (options.exposeInterceptorProtocol && options.interceptorServerConnectionResolver) {
      throw new Error(
        'interceptorServerConnectionResolver is only supported for the transparent proxy path. ' +
          'Disable exposeInterceptorProtocol or use static interceptorClients for SEP passthrough.',
      );
    }

    const ownedClients: Client[] = [];
    const interceptorClients: Client[] = [...(options.interceptorClients ?? [])];

    try {
      if (options.interceptorServerConnections?.length) {
        for (const connection of options.interceptorServerConnections) {
          const client = await connectInterceptorClient(connection, signal);
          interceptorClients.push(client);
          ownedClients.push(client);
        }
      }

      const hasStatic = interceptorClients.length > 0;
      const hasResolver = options.interceptorServerConnectionResolver !== undefined;
      if (!hasStatic && !hasResolver) {
        throw new Error(
          'At least one of interceptorClients, interceptorServerConnections, or interceptorServerConnectionResolver is required',
        );
      }

      return new McpInterceptorGateway(
        {
          ...options,
          interceptorClients,
          interceptorServerConnections: undefined,
        },
        ownedClients,
      );
    } catch (error) {
      await Promise.all(ownedClients.map((c) => c.close()));
      throw error;
    }
  }

  /** Wire proxy handlers and mirror backend capabilities. Call before `server.connect()`. */
  configureServer(server: Server): void {
    this.proxyConfigurator.configure(server);
    this.protocolBridge?.configure(server);
  }

  /** Forward backend list-changed notifications to clients connected to the proxy server. */
  registerNotificationForwarding(proxyServer: Server): void {
    const backendCaps = this.backendClient.getServerCapabilities();

    if (backendCaps?.tools?.listChanged) {
      this.backendClient.setNotificationHandler('notifications/tools/list_changed', async () => {
        await proxyServer.sendToolListChanged();
      });
      this.notificationCleanups.push(() =>
        this.backendClient.removeNotificationHandler('notifications/tools/list_changed'),
      );
    }

    if (backendCaps?.prompts?.listChanged) {
      this.backendClient.setNotificationHandler('notifications/prompts/list_changed', async () => {
        await proxyServer.sendPromptListChanged();
      });
      this.notificationCleanups.push(() =>
        this.backendClient.removeNotificationHandler('notifications/prompts/list_changed'),
      );
    }

    if (backendCaps?.resources?.listChanged) {
      this.backendClient.setNotificationHandler('notifications/resources/list_changed', async () => {
        await proxyServer.sendResourceListChanged();
      });
      this.notificationCleanups.push(() =>
        this.backendClient.removeNotificationHandler('notifications/resources/list_changed'),
      );
    }

    if (backendCaps?.resources?.subscribe) {
      // Without this relay, proxied resources/subscribe would be a silent no-op.
      this.backendClient.setNotificationHandler(
        'notifications/resources/updated',
        async (notification) => {
          await proxyServer.sendResourceUpdated(notification.params);
        },
      );
      this.notificationCleanups.push(() =>
        this.backendClient.removeNotificationHandler('notifications/resources/updated'),
      );
    }

    if (backendCaps?.logging) {
      this.backendClient.setNotificationHandler(
        'notifications/message',
        async (notification) => {
          await proxyServer.sendLoggingMessage(notification.params);
        },
      );
      this.notificationCleanups.push(() =>
        this.backendClient.removeNotificationHandler('notifications/message'),
      );
    }
  }

  disposeNotificationForwarding(): void {
    for (const cleanup of this.notificationCleanups.splice(0)) {
      cleanup();
    }
  }

  /** Closes connections opened by {@link McpInterceptorGateway.createAsync} and resolver pools. */
  async dispose(): Promise<void> {
    this.disposeNotificationForwarding();
    await Promise.all(this.ownedClients.splice(0).map((c) => c.close()));
    await this.interceptorClientProvider.dispose();
  }
}
