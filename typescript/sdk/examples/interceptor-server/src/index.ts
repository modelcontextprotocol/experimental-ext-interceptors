#!/usr/bin/env node
// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
/**
 * Interceptor Server Sample — stdio MCP host with validators, mutators, and a sink.
 * Spawned by interceptor-client (or any MCP client using StdioClientTransport).
 */
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { Server } from "@modelcontextprotocol/server";
import { registerInterceptorsOnServer } from '../../../dist/index.js';
import { sampleInterceptors } from './sample-interceptors.js';

const server = new Server(
  { name: 'interceptor-server-sample', version: '1.0.0' },
  { capabilities: {} },
);

registerInterceptorsOnServer(server, sampleInterceptors);

const transport = new StdioServerTransport();
await server.connect(transport);
