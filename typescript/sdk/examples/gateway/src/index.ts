/**
 * Gateway sample — InterceptingMcpClient over a backend + interceptor host.
 * C# equivalent: GatewaySample.
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
  console.log('=== MCP Interceptors Gateway Sample (client API) ===\n');

  const interceptorClient = new Client(
    { name: 'gateway-interceptor', version: '1.0.0' },
    { capabilities: {} },
  );
  await interceptorClient.connect(
    new StdioClientTransport({
      command: 'npx',
      args: ['tsx', interceptorServerEntry],
    }),
  );

  const backendClient = new Client(
    { name: 'gateway-backend', version: '1.0.0' },
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

  const listed = await gateway.listInterceptors();
  console.log('Interceptors:');
  for (const i of listed.interceptors) {
    console.log(`  - ${i.name} (${i.type})`);
  }

  console.log('\n── echo (should pass) ──');
  const ok = await gateway.callTool('echo', { message: 'Hello from gateway sample!' });
  console.log(' ', ok.content?.[0]);

  console.log('\n── echo with SSN (should be blocked) ──');
  try {
    await gateway.callTool('echo', { message: 'My SSN is 123-45-6789' });
  } catch (err) {
    console.log(' BLOCKED:', err instanceof Error ? err.message : err);
  }

  await gateway.close();
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
