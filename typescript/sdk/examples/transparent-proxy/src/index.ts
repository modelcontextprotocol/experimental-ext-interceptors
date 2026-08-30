/**
 * Transparent proxy — stdio MCP server that looks like the backend but runs interceptor chains.
 * C# equivalent: TransparentProxySample.
 */
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { Server } from "@modelcontextprotocol/server";
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import { Client } from "@modelcontextprotocol/client";
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import {
  McpInterceptorGateway,
  InterceptionEvents,
} from '../../../dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const interceptorServerEntry = join(here, '../../interceptor-server/src/index.ts');

async function main(): Promise<void> {
  const interceptorClient = new Client(
    { name: 'InterceptorServer', version: '1.0.0' },
    { capabilities: {} },
  );
  await interceptorClient.connect(
    new StdioClientTransport({
      command: 'npx',
      args: ['tsx', interceptorServerEntry],
    }),
  );

  const backendClient = new Client(
    { name: 'EverythingServer', version: '1.0.0' },
    { capabilities: {} },
  );
  await backendClient.connect(
    new StdioClientTransport({
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-everything'],
    }),
  );

  const gateway = new McpInterceptorGateway({
    backendClient,
    interceptorClients: [interceptorClient],
    events: [InterceptionEvents.ToolsCall],
  });

  const server = new Server(
    { name: 'interceptor-proxy', version: '1.0.0' },
    { capabilities: {} },
  );
  gateway.configureServer(server);
  gateway.registerNotificationForwarding(server);

  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
