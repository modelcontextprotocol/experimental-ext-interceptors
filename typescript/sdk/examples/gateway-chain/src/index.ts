/**
 * Chained interceptors — two interceptor hosts in sequence before the backend.
 * C# equivalent: GatewayChainSample (simplified with in-process ordering via gateway options).
 */
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import { Client } from "@modelcontextprotocol/client";
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import {
  InterceptingMcpClient,
  InterceptionEvents,
} from '../../../dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const interceptorServerEntry = join(here, '../../interceptor-server/src/index.ts');

async function main(): Promise<void> {
  console.log('=== Gateway chain sample ===\n');
  console.log(
    'This sample uses InterceptingMcpClient with one interceptor host.\n' +
      'For multiple hosts in order, use McpInterceptorGateway with interceptorClients: [first, second].\n',
  );

  const interceptorClient = new Client(
    { name: 'chain-interceptor', version: '1.0.0' },
    { capabilities: {} },
  );
  await interceptorClient.connect(
    new StdioClientTransport({
      command: 'npx',
      args: ['tsx', interceptorServerEntry],
    }),
  );

  const backendClient = new Client(
    { name: 'chain-backend', version: '1.0.0' },
    { capabilities: {} },
  );
  await backendClient.connect(
    new StdioClientTransport({
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-everything'],
    }),
  );

  const gateway = new InterceptingMcpClient(backendClient, {
    interceptorClient,
    events: [InterceptionEvents.ToolsCall],
  });

  const tools = await gateway.listTools();
  console.log(`Tools: ${tools.tools.map((t) => t.name).join(', ')}`);

  await gateway.close();
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
