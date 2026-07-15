// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * A client-side wrapper that runs the interceptor chain around every MCP
 * operation: request phase (mutate → validate) before the backend sees the
 * payload, response phase (validate → mutate) before the caller sees the
 * result. ONE generic `intercepted` primitive carries every method (RULE 9) —
 * each MCP operation is a single delegating line, not a copied block.
 */
import type { Client } from "@modelcontextprotocol/sdk/client/index.js";
import type {
  CallToolResult,
  GetPromptResult,
  ListPromptsResult,
  ListResourcesResult,
  ListToolsResult,
  ReadResourceResult,
} from "@modelcontextprotocol/sdk/types.js";
import { INTERCEPTION_EVENT, INTERCEPTOR_PHASE } from "../protocol/constants.js";
import type { InterceptionEvent } from "../protocol/constants.js";
import type { Interceptor, ListInterceptorsResult } from "../protocol/types.js";
import { listInterceptors } from "./invoke.js";
import { createChainRunner } from "./runner.js";
import type { ChainRunnerSpec } from "./runner.js";

export interface InterceptingClientSpec extends Omit<ChainRunnerSpec, "hosts"> {
  /** The application (backend) MCP server connection. */
  readonly backend: Client;
  /** Connected interceptor-host clients; the chain merges across them. */
  readonly hosts: readonly Client[];
}

export interface InterceptingClient {
  readonly callTool: (params: {
    readonly name: string;
    readonly arguments?: Record<string, unknown>;
  }) => Promise<CallToolResult>;
  readonly listTools: () => Promise<ListToolsResult>;
  readonly getPrompt: (params: {
    readonly name: string;
    readonly arguments?: Record<string, string>;
  }) => Promise<GetPromptResult>;
  readonly listPrompts: () => Promise<ListPromptsResult>;
  readonly readResource: (params: {
    readonly uri: string;
  }) => Promise<ReadResourceResult>;
  readonly listResources: () => Promise<ListResourcesResult>;
  /** Aggregated `interceptors/list` across every configured host. */
  readonly listInterceptors: () => Promise<ListInterceptorsResult>;
  readonly interceptors: () => Promise<readonly Interceptor[]>;
  /** Escape hatch: intercept an arbitrary (custom) event around a forward. */
  readonly intercepted: <T>(
    event: InterceptionEvent,
    payload: unknown,
    forward: (payload: unknown) => Promise<T>,
  ) => Promise<T>;
  readonly refresh: () => void;
  readonly close: () => Promise<void>;
}

export function createInterceptingClient(
  spec: InterceptingClientSpec,
): InterceptingClient {
  const { backend, ...runnerSpec } = spec;
  const runner = createChainRunner(runnerSpec);

  const intercepted = async <T>(
    event: InterceptionEvent,
    payload: unknown,
    forward: (payload: unknown) => Promise<T>,
  ): Promise<T> => {
    if (!runner.shouldIntercept(event)) return forward(payload);
    const request = await runner.runOrThrow(
      event,
      INTERCEPTOR_PHASE.Request,
      payload,
    );
    const result = await forward(request);
    return (await runner.runOrThrow(
      event,
      INTERCEPTOR_PHASE.Response,
      result,
    )) as T;
  };

  return {
    callTool: (params) =>
      intercepted(INTERCEPTION_EVENT.ToolsCall, params, (p) =>
        backend.callTool(p as { name: string; arguments?: Record<string, unknown> }),
      ) as Promise<CallToolResult>,
    listTools: () =>
      intercepted(INTERCEPTION_EVENT.ToolsList, {}, () => backend.listTools()),
    getPrompt: (params) =>
      intercepted(INTERCEPTION_EVENT.PromptsGet, params, (p) =>
        backend.getPrompt(p as { name: string; arguments?: Record<string, string> }),
      ),
    listPrompts: () =>
      intercepted(INTERCEPTION_EVENT.PromptsList, {}, () => backend.listPrompts()),
    readResource: (params) =>
      intercepted(INTERCEPTION_EVENT.ResourcesRead, params, (p) =>
        backend.readResource(p as { uri: string }),
      ),
    listResources: () =>
      intercepted(INTERCEPTION_EVENT.ResourcesList, {}, () =>
        backend.listResources(),
      ),
    listInterceptors: async () => {
      const perHost = await Promise.all(
        spec.hosts.map((h) => listInterceptors(h)),
      );
      return {
        interceptors: perHost.flatMap((r) => r.interceptors),
        nextCursor: null,
      };
    },
    interceptors: () => runner.interceptors(),
    intercepted,
    refresh: () => {
      runner.refresh();
    },
    close: async () => {
      await Promise.all([backend.close(), ...spec.hosts.map((h) => h.close())]);
    },
  };
}
