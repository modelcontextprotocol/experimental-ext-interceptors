// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * A transparent MCP gateway: presents as the backend server while every
 * proxied operation passes through the interceptor chain — request phase
 * before the backend sees it, response phase before the caller does.
 *
 * The proxy surface is ONE route table (RULE 2): each entry binds an MCP
 * request schema to its SEP lifecycle event and a backend forward. Handlers
 * are registered only for capabilities the backend actually advertises, so
 * the gateway mirrors the backend instead of inventing surface area. Backend
 * list-changed notifications are re-emitted to the proxy's own clients. When
 * `exposeInterceptorProtocol` is set the gateway is itself an interceptor
 * host: `interceptors/list` aggregates across hosts and `interceptor/invoke`
 * routes by name.
 */
import type { Client } from "@modelcontextprotocol/sdk/client/index.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
  CallToolRequestSchema,
  ErrorCode,
  GetPromptRequestSchema,
  ListPromptsRequestSchema,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  McpError,
  PromptListChangedNotificationSchema,
  ReadResourceRequestSchema,
  ResourceListChangedNotificationSchema,
  SubscribeRequestSchema,
  ToolListChangedNotificationSchema,
} from "@modelcontextprotocol/sdk/types.js";
import type { ServerCapabilities } from "@modelcontextprotocol/sdk/types.js";
import {
  INTERCEPTION_EVENT,
  INTERCEPTOR_PHASE,
  INTERCEPTORS_CAPABILITY,
} from "../protocol/constants.js";
import type { InterceptionEvent } from "../protocol/constants.js";
import {
  InvokeInterceptorRequestSchema,
  ListInterceptorsRequestSchema,
} from "../protocol/rpc-schemas.js";
import { serializeInterceptor, serializeResult } from "../protocol/serialize.js";
import { normalizeInvokeParams } from "../protocol/wire.js";
import { ChainBlockedError } from "../client/errors.js";
import { createChainRunner, discoverInterceptors, routedInvoker } from "../client/runner.js";
import type { ChainRunnerSpec } from "../client/runner.js";

export interface GatewaySpec extends Omit<ChainRunnerSpec, "hosts"> {
  /** Connected client for the application (backend) MCP server. */
  readonly backend: Client;
  /** Connected interceptor-host clients, merged into one chain. */
  readonly hosts: readonly Client[];
  /** Also serve `interceptors/list` / `interceptor/invoke` from the proxy. */
  readonly exposeInterceptorProtocol?: boolean;
}

export interface InterceptorGateway {
  /** Wire proxy handlers + mirrored capabilities. Call before `server.connect()`. */
  readonly configureServer: (server: Server) => void;
  /** Re-emit backend list-changed notifications. Returns a disposer. */
  readonly forwardNotifications: (server: Server) => () => void;
  readonly refresh: () => void;
}

type Params = Record<string, unknown> | undefined;
type Forward = (backend: Client, params: Params) => Promise<Record<string, unknown>>;

/** Which backend capability gates each proxied route. */
const CAPABILITY = {
  Tools: "tools",
  Prompts: "prompts",
  Resources: "resources",
} as const;
type CapabilityKey = (typeof CAPABILITY)[keyof typeof CAPABILITY];

interface Route {
  readonly schema:
    | typeof ListToolsRequestSchema
    | typeof CallToolRequestSchema
    | typeof ListPromptsRequestSchema
    | typeof GetPromptRequestSchema
    | typeof ListResourcesRequestSchema
    | typeof ReadResourceRequestSchema
    | typeof SubscribeRequestSchema;
  readonly event: InterceptionEvent;
  readonly capability: CapabilityKey;
  readonly forward: Forward;
}

/** The entire proxy surface (RULE 2): schema × event × capability × forward. */
const ROUTES: readonly Route[] = [
  {
    schema: ListToolsRequestSchema,
    event: INTERCEPTION_EVENT.ToolsList,
    capability: CAPABILITY.Tools,
    forward: (backend, params) => backend.listTools(params),
  },
  {
    schema: CallToolRequestSchema,
    event: INTERCEPTION_EVENT.ToolsCall,
    capability: CAPABILITY.Tools,
    forward: (backend, params) =>
      backend.callTool(params as { name: string; arguments?: Record<string, unknown> }),
  },
  {
    schema: ListPromptsRequestSchema,
    event: INTERCEPTION_EVENT.PromptsList,
    capability: CAPABILITY.Prompts,
    forward: (backend, params) => backend.listPrompts(params),
  },
  {
    schema: GetPromptRequestSchema,
    event: INTERCEPTION_EVENT.PromptsGet,
    capability: CAPABILITY.Prompts,
    forward: (backend, params) =>
      backend.getPrompt(params as { name: string; arguments?: Record<string, string> }),
  },
  {
    schema: ListResourcesRequestSchema,
    event: INTERCEPTION_EVENT.ResourcesList,
    capability: CAPABILITY.Resources,
    forward: (backend, params) => backend.listResources(params),
  },
  {
    schema: ReadResourceRequestSchema,
    event: INTERCEPTION_EVENT.ResourcesRead,
    capability: CAPABILITY.Resources,
    forward: (backend, params) => backend.readResource(params as { uri: string }),
  },
  {
    schema: SubscribeRequestSchema,
    event: INTERCEPTION_EVENT.ResourcesSubscribe,
    capability: CAPABILITY.Resources,
    forward: (backend, params) => backend.subscribeResource(params as { uri: string }),
  },
];

/** Backend list-changed notifications the proxy re-emits (RULE 2). */
const NOTIFICATION_FORWARDS = [
  {
    capability: CAPABILITY.Tools,
    schema: ToolListChangedNotificationSchema,
    method: "notifications/tools/list_changed",
    send: (server: Server) => server.sendToolListChanged(),
  },
  {
    capability: CAPABILITY.Prompts,
    schema: PromptListChangedNotificationSchema,
    method: "notifications/prompts/list_changed",
    send: (server: Server) => server.sendPromptListChanged(),
  },
  {
    capability: CAPABILITY.Resources,
    schema: ResourceListChangedNotificationSchema,
    method: "notifications/resources/list_changed",
    send: (server: Server) => server.sendResourceListChanged(),
  },
] as const;

function blockedToMcpError(err: unknown): never {
  if (err instanceof ChainBlockedError) {
    throw new McpError(ErrorCode.InvalidRequest, err.message, {
      status: err.chain.status,
      abortedAt: err.chain.abortedAt,
    });
  }
  throw err;
}

export function createGateway(spec: GatewaySpec): InterceptorGateway {
  const { backend, exposeInterceptorProtocol, ...runnerSpec } = spec;
  const runner = createChainRunner(runnerSpec);

  const proxied = async (
    event: InterceptionEvent,
    params: Params,
    forward: (params: Params) => Promise<Record<string, unknown>>,
  ): Promise<Record<string, unknown>> => {
    if (!runner.shouldIntercept(event)) return forward(params);
    try {
      const request = await runner.runOrThrow(
        event,
        INTERCEPTOR_PHASE.Request,
        params ?? {},
      );
      const result = await forward(request as Params);
      return (await runner.runOrThrow(
        event,
        INTERCEPTOR_PHASE.Response,
        result,
      )) as Record<string, unknown>;
    } catch (err) {
      blockedToMcpError(err);
    }
  };

  const backendCapabilities = (): ServerCapabilities =>
    backend.getServerCapabilities() ?? {};

  return {
    configureServer: (server) => {
      const caps = backendCapabilities();
      const mirrored: ServerCapabilities = {
        ...(caps.tools === undefined ? {} : { tools: caps.tools }),
        ...(caps.prompts === undefined ? {} : { prompts: caps.prompts }),
        ...(caps.resources === undefined ? {} : { resources: caps.resources }),
      };
      if (exposeInterceptorProtocol === true) {
        mirrored.extensions = { [INTERCEPTORS_CAPABILITY]: {} };
      }
      server.registerCapabilities(mirrored);

      for (const route of ROUTES.filter((r) => caps[r.capability] !== undefined)) {
        server.setRequestHandler(route.schema, (request) =>
          proxied(route.event, request.params, (p) => route.forward(backend, p)),
        );
      }

      if (exposeInterceptorProtocol === true) {
        server.setRequestHandler(ListInterceptorsRequestSchema, async () => ({
          interceptors: (await runner.interceptors()).map(serializeInterceptor),
        }));
        server.setRequestHandler(InvokeInterceptorRequestSchema, async (request) => {
          const params = normalizeInvokeParams(request.params);
          const hosted = await discoverInterceptors(spec.hosts);
          return serializeResult(await routedInvoker(hosted)(params, null));
        });
      }
    },

    forwardNotifications: (server) => {
      const caps = backendCapabilities();
      const active = NOTIFICATION_FORWARDS.filter(
        (f) => caps[f.capability]?.listChanged === true,
      );
      for (const f of active) {
        backend.setNotificationHandler(f.schema, async () => {
          await f.send(server);
        });
      }
      return () => {
        for (const f of active) backend.removeNotificationHandler(f.method);
      };
    },

    refresh: () => {
      runner.refresh();
    },
  };
}
