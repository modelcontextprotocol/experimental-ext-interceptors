// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { InterceptionEvents } from '../protocol/constants.js';
import type {
  Interceptor,
  InterceptorHook,
  InterceptorMode,
  InterceptorPhase,
  InterceptorType,
  PriorityHint,
} from '../protocol/types.js';

export type InterceptorPhaseOption = InterceptorPhase | 'both';

export interface InterceptorDefinitionOptions {
  name: string;
  type: InterceptorType;
  description?: string;
  events?: string[];
  phase?: InterceptorPhaseOption;
  priorityHint?: PriorityHint;
  mode?: InterceptorMode;
  failOpen?: boolean;
}

export function buildInterceptorDescriptor(options: InterceptorDefinitionOptions): Interceptor {
  const events = options.events ?? [InterceptionEvents.All];
  const phase = options.phase ?? 'both';

  const hooks: InterceptorHook[] =
    phase === 'both'
      ? [
          { events: [...events], phase: 'request' },
          { events: [...events], phase: 'response' },
        ]
      : [{ events: [...events], phase }];

  return {
    name: options.name,
    description: options.description,
    type: options.type,
    hooks,
    mode: options.mode,
    failOpen: options.failOpen,
    priorityHint: options.priorityHint,
  };
}
