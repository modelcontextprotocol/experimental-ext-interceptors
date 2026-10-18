/**
 * Transparent proxy over Streamable HTTP — same gateway as ../transparent-proxy, but the proxy is
 * served at http://127.0.0.1:<PORT>/mcp instead of on stdio. The backend and interceptor host are
 * still spawned over stdio.
 * C# equivalent: TransparentProxySample is stdio-only; this is the HTTP variant of it.
 */
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { randomUUID } from 'node:crypto';
import {
  Server,
  WebStandardStreamableHTTPServerTransport,
  hostHeaderValidationResponse,
  isInitializeRequest,
  localhostAllowedHostnames,
} from "@modelcontextprotocol/server";
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

const host = '127.0.0.1';
const port = Number(process.env.PORT ?? 3000);

interface Session {
  transport: WebStandardStreamableHTTPServerTransport;
  gateway: McpInterceptorGateway;
  backendClient: Client;
}

const sessions = new Map<string, Session>();

async function connectStdio(name: string, command: string, args: string[]): Promise<Client> {
  const client = new Client({ name, version: '1.0.0' }, { capabilities: {} });
  await client.connect(new StdioClientTransport({ command, args }));
  return client;
}

async function closeSession(session: Session): Promise<void> {
  await session.gateway.dispose();
  await session.backendClient.close();
}

// One backend connection per HTTP session. registerNotificationForwarding() keeps a single
// list_changed handler on the backend client, so sharing one backend across sessions would only
// forward notifications to whichever session connected last. The interceptor host is shared.
async function createSession(interceptorClient: Client): Promise<WebStandardStreamableHTTPServerTransport> {
  const backendClient = await connectStdio('EverythingServer', 'npx', [
    '-y',
    '@modelcontextprotocol/server-everything',
  ]);

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

  const transport = new WebStandardStreamableHTTPServerTransport({
    sessionIdGenerator: () => randomUUID(),
    onsessioninitialized: (sessionId) => {
      sessions.set(sessionId, { transport, gateway, backendClient });
      console.log(`session ${sessionId} opened (${sessions.size} active)`);
    },
  });

  await server.connect(transport);
  server.onclose = () => {
    const sessionId = transport.sessionId;
    if (sessionId) {
      sessions.delete(sessionId);
      console.log(`session ${sessionId} closed (${sessions.size} active)`);
    }
    closeSession({ transport, gateway, backendClient }).catch(console.error);
  };

  return transport;
}

async function readBody(req: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) {
    chunks.push(chunk as Buffer);
  }
  return Buffer.concat(chunks).toString('utf8');
}

function toWebRequest(req: IncomingMessage, body: string | undefined): Request {
  const headers = new Headers();
  for (const [key, value] of Object.entries(req.headers)) {
    for (const v of Array.isArray(value) ? value : value === undefined ? [] : [value]) {
      headers.append(key, v);
    }
  }
  return new Request(new URL(req.url ?? '/', `http://${host}:${port}`), {
    method: req.method,
    headers,
    body,
  });
}

// Copies a fetch Response onto the Node response. SSE responses stay open until the client
// disconnects or the transport ends the stream.
async function sendWebResponse(res: ServerResponse, response: Response): Promise<void> {
  res.writeHead(response.status, Object.fromEntries(response.headers));
  if (!response.body) {
    res.end();
    return;
  }
  const reader = response.body.getReader();
  res.on('close', () => {
    reader.cancel().catch(() => {});
  });
  for (;;) {
    const chunk = await reader.read();
    if (chunk.done) break;
    res.write(chunk.value as Uint8Array);
  }
  res.end();
}

function jsonRpcError(status: number, message: string): Response {
  return Response.json(
    { jsonrpc: '2.0', error: { code: -32000, message }, id: null },
    { status },
  );
}

async function handle(
  req: IncomingMessage,
  res: ServerResponse,
  interceptorClient: Client,
): Promise<void> {
  if (new URL(req.url ?? '/', `http://${host}`).pathname !== '/mcp') {
    res.writeHead(404).end();
    return;
  }

  const body = req.method === 'POST' ? await readBody(req) : undefined;
  const request = toWebRequest(req, body);

  const rejected = hostHeaderValidationResponse(request, localhostAllowedHostnames());
  if (rejected) {
    await sendWebResponse(res, rejected);
    return;
  }

  let parsedBody: unknown;
  try {
    parsedBody = body ? JSON.parse(body) : undefined;
  } catch {
    // Leave it to the transport to answer with a JSON-RPC parse error.
  }

  const sessionId = request.headers.get('mcp-session-id');
  let transport: WebStandardStreamableHTTPServerTransport;
  if (sessionId) {
    const session = sessions.get(sessionId);
    if (!session) {
      await sendWebResponse(res, jsonRpcError(404, 'Session not found'));
      return;
    }
    transport = session.transport;
  } else if (req.method === 'POST' && isInitializeRequest(parsedBody)) {
    transport = await createSession(interceptorClient);
  } else {
    await sendWebResponse(res, jsonRpcError(400, 'Bad Request: no valid session ID provided'));
    return;
  }

  await sendWebResponse(res, await transport.handleRequest(request, { parsedBody }));
}

async function main(): Promise<void> {
  const interceptorClient = await connectStdio('InterceptorServer', 'npx', [
    'tsx',
    interceptorServerEntry,
  ]);

  const httpServer = createServer((req, res) => {
    handle(req, res, interceptorClient).catch((err) => {
      console.error(err);
      if (!res.headersSent) res.writeHead(500);
      res.end();
    });
  });

  httpServer.listen(port, host, () => {
    console.log(`Interceptor proxy listening on http://${host}:${port}/mcp`);
  });

  async function shutdown(): Promise<void> {
    httpServer.close();
    await Promise.all([...sessions.values()].map((s) => s.transport.close()));
    await interceptorClient.close();
    process.exit(0);
  }
  process.on('SIGINT', () => {
    void shutdown();
  });
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
