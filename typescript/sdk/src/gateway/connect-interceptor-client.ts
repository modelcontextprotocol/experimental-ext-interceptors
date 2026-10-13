// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import { Client } from "@modelcontextprotocol/client";

import type { McpInterceptorServerConnectionOptions } from './mcp-interceptor-server-connection-options.js';

export async function connectInterceptorClient(
  connection: McpInterceptorServerConnectionOptions,
  signal?: AbortSignal,
): Promise<Client> {
  const client = new Client(
    connection.clientInfo ?? { name: 'interceptor-client', version: '1.0.0' },
    connection.clientOptions ?? { capabilities: {} },
  );
  await client.connect(connection.transport, { signal });
  return client;
}
