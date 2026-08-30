// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
import type { Client } from "@modelcontextprotocol/client";


export class GatewayResolvedInterceptorClients {
  readonly clients: Client[];
  private readonly ownedDisposables: Array<{ dispose(): Promise<void> }>;

  constructor(
    clients: Client[],
    ownedDisposables: Array<{ dispose(): Promise<void> }> = [],
  ) {
    this.clients = clients;
    this.ownedDisposables = ownedDisposables;
  }

  async dispose(): Promise<void> {
    // Settle every disposable even if one throws so a failing close cannot
    // leak the remaining owned clients.
    const settled = await Promise.allSettled(this.ownedDisposables.map((d) => d.dispose()));
    const failure = settled.find((s): s is PromiseRejectedResult => s.status === 'rejected');
    if (failure) {
      throw failure.reason;
    }
  }
}
