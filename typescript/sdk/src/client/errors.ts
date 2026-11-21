// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/** Errors raised by the client layer. */
import type { ChainResult } from "./chain.js";

/**
 * Thrown when an enforce-mode chain blocked a payload. Carries the full
 * {@link ChainResult} so callers can inspect every result, not just a string.
 */
export class ChainBlockedError extends Error {
  readonly chain: ChainResult;

  constructor(chain: ChainResult) {
    const at = chain.abortedAt;
    super(
      at === null
        ? `interceptor chain blocked (${chain.status})`
        : `interceptor '${at.interceptor}' blocked ${chain.event}/${chain.phase}: ${at.reason}`,
    );
    this.name = "ChainBlockedError";
    this.chain = chain;
  }
}

/** Thrown when two hosts advertise the same interceptor name (SEP-global). */
export class DuplicateInterceptorNameError extends Error {
  readonly interceptor: string;
  readonly hosts: readonly string[];

  constructor(interceptor: string, hosts: readonly string[]) {
    super(
      `interceptor '${interceptor}' advertised by multiple hosts: ${hosts.join(", ")}`,
    );
    this.name = "DuplicateInterceptorNameError";
    this.interceptor = interceptor;
    this.hosts = hosts;
  }
}
