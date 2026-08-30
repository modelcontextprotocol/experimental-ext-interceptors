// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import {
  InterceptionEvents,
  validationFailure,
  validationSuccess,
  type RegisteredInterceptor,
} from '../../../dist/index.js';

function payloadText(payload: unknown): string {
  return JSON.stringify(payload);
}

export const sampleInterceptors: RegisteredInterceptor[] = [
  {
    descriptor: {
      name: 'pii-validator',
      description: 'Checks tool call arguments for PII patterns',
      type: 'validation',
      hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
    },
    handler: (params) => {
      const json = payloadText(params.payload);
      if (/ssn|social security/i.test(json)) {
        return validationFailure(params.phase, {
          path: '$.arguments',
          message: 'Payload may contain Social Security Number data',
          severity: 'error',
        });
      }
      return validationSuccess(params.phase);
    },
  },
  {
    descriptor: {
      name: 'email-redactor',
      description: 'Redacts email addresses from payloads',
      type: 'mutation',
      hooks: [{ events: [InterceptionEvents.ToolsCall], phase: 'request' }],
      priorityHint: -1000,
    },
    handler: (params) => {
      const json = payloadText(params.payload);
      const redacted = json.replace(
        /[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g,
        '[EMAIL_REDACTED]',
      );
      if (redacted === json) {
        return { type: 'mutation', phase: params.phase, modified: false, payload: params.payload };
      }
      return {
        type: 'mutation',
        phase: params.phase,
        modified: true,
        payload: JSON.parse(redacted) as unknown,
      };
    },
  },
  {
    descriptor: {
      name: 'request-logger',
      description: 'Logs intercepted events to stderr',
      type: 'sink',
      hooks: [
        { events: [InterceptionEvents.All], phase: 'request' },
        { events: [InterceptionEvents.All], phase: 'response' },
      ],
    },
    handler: (params) => {
      const size = payloadText(params.payload).length;
      const trace = params.context?.traceId ?? 'none';
      console.error(
        `[interceptor] event=${params.event} phase=${params.phase} traceId=${trace} payloadBytes=${size}`,
      );
      return {
        type: 'sink',
        phase: params.phase,
        recorded: true,
        metrics: { payloadBytes: size },
      };
    },
  },
];
