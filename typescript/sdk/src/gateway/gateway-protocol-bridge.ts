// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { ProtocolError, ProtocolErrorCode } from '@modelcontextprotocol/server';
import type { Server, ServerCapabilities } from '@modelcontextprotocol/server';
import type { Client } from '@modelcontextprotocol/client';
import { invokeInterceptor, listAllInterceptors } from '../client/client-extensions.js';
import {
  InterceptorExtensionCapabilityKey,
  InterceptorRequestMethods,
} from '../protocol/constants.js';
import {
  InvokeInterceptorParamsSchema,
  ListInterceptorsParamsSchema,
} from '../protocol/zod-schemas.js';
import type {
  Interceptor,
  InterceptorsCapability,
  InvokeInterceptorRequestParams,
} from '../protocol/types.js';
import { interceptorNotFoundError, isInvalidParamsError } from '../protocol/mcp-errors.js';

function readInterceptorCapability(client: Client): InterceptorsCapability | undefined {
  const caps = client.getServerCapabilities() as ServerCapabilities & {
    extensions?: Record<string, unknown>;
  };
  return caps?.extensions?.[InterceptorExtensionCapabilityKey] as InterceptorsCapability | undefined;
}

export class GatewayInterceptorProtocolBridge {
  private readonly interceptorClients: Client[];
  private clientByInterceptorName: Map<string, Client> | undefined;
  private duplicateInterceptorNames = new Set<string>();
  private routingBuild: Promise<void> | undefined;

  constructor(interceptorClients: Client[]) {
    this.interceptorClients = interceptorClients;
  }

  configure(server: Server): void {
    const allEvents = new Set<string>();
    let anyCapability = false;

    for (const client of this.interceptorClients) {
      const cap = readInterceptorCapability(client);
      if (!cap) {
        continue;
      }
      anyCapability = true;
      for (const ev of cap.supportedEvents) {
        allEvents.add(ev);
      }
    }

    if (anyCapability) {
      server.registerCapabilities({
        extensions: {
          [InterceptorExtensionCapabilityKey]: {
            supportedEvents: [...allEvents].sort(),
          },
        },
      } as ServerCapabilities);
    }

    server.setRequestHandler(
      InterceptorRequestMethods.InterceptorsList,
      { params: ListInterceptorsParamsSchema.optional() },
      async (params) => {
        if (params?.cursor != null) {
          // The bridge aggregates every host page into one response and never
          // mints a cursor, so any incoming cursor is stale/foreign.
          throw new ProtocolError(
            ProtocolErrorCode.InvalidParams,
            'Unknown interceptors/list cursor',
          );
        }

        const aggregated: Interceptor[] = [];
        for (const client of this.interceptorClients) {
          const result = await listAllInterceptors(client, { event: params?.event });
          aggregated.push(...result.interceptors);
        }

        return { interceptors: aggregated };
      },
    );

    server.setRequestHandler(
      InterceptorRequestMethods.InterceptorInvoke,
      { params: InvokeInterceptorParamsSchema },
      async (params) =>
        (await this.invokeOnInterceptorHost(
          params as InvokeInterceptorRequestParams,
        )) as unknown as Record<string, unknown>,
    );
  }

  /** Drop cached name→client routing (e.g. after interceptor host reconnect). */
  invalidateRoutingCache(): void {
    this.clientByInterceptorName = undefined;
    this.duplicateInterceptorNames = new Set();
    this.routingBuild = undefined;
  }

  private async ensureRoutingTable(): Promise<Map<string, Client>> {
    if (this.clientByInterceptorName) {
      return this.clientByInterceptorName;
    }

    if (!this.routingBuild) {
      this.routingBuild = this.buildRoutingTable();
    }
    try {
      await this.routingBuild;
    } catch (err) {
      // Drop the failed build so a transient host error (e.g. restart blip)
      // does not poison every subsequent invoke with the same rejection.
      this.routingBuild = undefined;
      throw err;
    }
    return this.clientByInterceptorName ?? new Map();
  }

  private async buildRoutingTable(): Promise<void> {
    const map = new Map<string, Client>();
    const duplicates = new Set<string>();
    for (const client of this.interceptorClients) {
      const listed = await listAllInterceptors(client);
      for (const descriptor of listed.interceptors) {
        if (map.has(descriptor.name)) {
          duplicates.add(descriptor.name);
        } else {
          map.set(descriptor.name, client);
        }
      }
    }
    this.clientByInterceptorName = map;
    this.duplicateInterceptorNames = duplicates;
  }

  private async invokeOnInterceptorHost(
    params: InvokeInterceptorRequestParams,
  ): Promise<Awaited<ReturnType<typeof invokeInterceptor>>> {
    const routing = await this.ensureRoutingTable();
    if (this.duplicateInterceptorNames.has(params.name)) {
      // `interceptor/invoke` only carries a name, so picking a host would be a
      // guess. Names unique across hosts still route normally.
      throw new ProtocolError(
        ProtocolErrorCode.InvalidParams,
        `Interceptor name '${params.name}' is registered on more than one host; interceptor names must be unique across hosts`,
      );
    }
    const client = routing.get(params.name);
    if (!client) {
      throw interceptorNotFoundError(params.name);
    }

    try {
      return await invokeInterceptor(client, params);
    } catch (err) {
      if (isInvalidParamsError(err)) {
        this.invalidateRoutingCache();
      }
      throw err;
    }
  }
}
