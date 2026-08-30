#!/usr/bin/env node
// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
/**
 * Interceptor Client Sample — spawns interceptor-server and exercises list / invoke / chain.
 */

import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import { Client } from "@modelcontextprotocol/client";
import {
  executeInterceptorChainOnClient,
  InterceptionEvents,
  invokeInterceptor,
  listInterceptors,
  isMutationResult,
  isValidationResult,
} from '../../../dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const serverEntry = join(here, '../../interceptor-server/src/index.ts');

console.log('=== MCP Interceptors Client Sample ===\n');
console.log('[setup] Spawning interceptor server...');

const transport = new StdioClientTransport({
  command: 'npx',
  args: ['tsx', serverEntry],
  cwd: join(here, '../..'),
});

const client = new Client({ name: 'interceptor-client-sample', version: '1.0.0' }, { capabilities: {} });
await client.connect(transport);
console.log('[setup] Connected.\n');

console.log('── Demo 1: List interceptors ──');
const listResult = await listInterceptors(client);
for (const i of listResult.interceptors) {
  const hooks = i.hooks
    .map((h) => `${h.phase}:[${h.events.join(',')}]`)
    .join('; ');
  console.log(`  ${i.name.padEnd(20)} type=${i.type.padEnd(12)} hooks=${hooks}`);
  if (i.description) {
    console.log(`  ${''.padEnd(20)} ${i.description}`);
  }
}

console.log('\n── Demo 2: Invoke email-redactor ──');
const emailPayload = {
  name: 'echo',
  arguments: { message: 'Contact alice@example.com' },
};
const redactResult = await invokeInterceptor(client, {
  name: 'email-redactor',
  event: InterceptionEvents.ToolsCall,
  phase: 'request',
  payload: emailPayload,
});
if (isMutationResult(redactResult)) {
  console.log(`  Modified: ${redactResult.modified}`);
  console.log(`  Payload:  ${JSON.stringify(redactResult.payload)}`);
}

console.log('\n── Demo 3: Invoke pii-validator (clean) ──');
const safeResult = await invokeInterceptor(client, {
  name: 'pii-validator',
  event: InterceptionEvents.ToolsCall,
  phase: 'request',
  payload: { name: 'echo', arguments: { message: 'Hello world' } },
});
if (isValidationResult(safeResult)) {
  console.log(`  Valid: ${safeResult.valid}`);
}

console.log('\n── Demo 4: Invoke pii-validator (PII) ──');
const piiResult = await invokeInterceptor(client, {
  name: 'pii-validator',
  event: InterceptionEvents.ToolsCall,
  phase: 'request',
  payload: { name: 'echo', arguments: { message: 'My SSN is 123-45-6789' } },
});
if (isValidationResult(piiResult)) {
  console.log(`  Valid: ${piiResult.valid}`);
  for (const msg of piiResult.messages ?? []) {
    console.log(`  [${msg.severity}] ${msg.message}`);
  }
}

console.log('\n── Demo 5: Execute chain ──');
const chainResult = await executeInterceptorChainOnClient(client, {
  event: InterceptionEvents.ToolsCall,
  phase: 'request',
  payload: {
    name: 'echo',
    arguments: { message: 'Email bob@corp.com about SSN' },
  },
  context: { traceId: crypto.randomUUID().replace(/-/g, '') },
});
console.log(`  Status: ${chainResult.status}`);
console.log(`  Duration: ${chainResult.totalDurationMs}ms`);
console.log(`  Results: ${chainResult.results.length} interceptor(s)`);
if (chainResult.abortedAt) {
  console.log(`  Aborted: ${chainResult.abortedAt.interceptor} — ${chainResult.abortedAt.reason}`);
}
for (const r of chainResult.results) {
  console.log(`    ${(r.interceptor ?? '?').padEnd(20)} type=${r.type.padEnd(12)} duration=${r.durationMs ?? 0}ms`);
}
if (chainResult.finalPayload !== undefined) {
  console.log(`  Final payload: ${JSON.stringify(chainResult.finalPayload)}`);
}

console.log('\n=== Done ===');
await client.close();
