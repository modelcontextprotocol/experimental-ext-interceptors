// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import type { InterceptorChainRunner } from '../client/interceptor-chain-runner.js';

export interface ProxiedRequestOptions<T> {
  operation: string;
  eventName: string;
  requestParams: unknown;
  forward: (mutatedParams: unknown, signal?: AbortSignal) => Promise<T>;
  chainRunner: InterceptorChainRunner;
  signal?: AbortSignal;
}

/** Run request/response interceptor phases around a backend forward call. */
export async function runProxiedRequest<T>(
  options: ProxiedRequestOptions<T>,
): Promise<T> {
  let requestPayload = options.requestParams;

  if (options.chainRunner.shouldIntercept(options.eventName)) {
    requestPayload = await options.chainRunner.runChainPhaseOrThrow(
      options.operation,
      options.eventName,
      'request',
      requestPayload,
      options.signal,
    );
  }

  let result: T = await options.forward(requestPayload, options.signal);

  if (options.chainRunner.shouldIntercept(options.eventName)) {
    result = (await options.chainRunner.runChainPhaseOrThrow(
      options.operation,
      options.eventName,
      'response',
      result,
      options.signal,
    )) as T;
  }

  return result;
}
