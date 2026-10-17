// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * `cross-boundary-guard` - the reference security VALIDATOR (open tier).
 *
 * Enforces causal cross-boundary non-interference over verbatim secrets:
 * a secret value that appeared in a strictly-prior response from server A
 * may not appear in a later request to a different server B. That composed
 * read-then-send flow is the exfiltration class where every per-call check
 * legitimately passes - only the cross-call, cross-server view denies it.
 *
 * Attribution: a request payload names its server (tool name / resource URI,
 * via `serverOf`); a response payload does not. The guard therefore hooks
 * BOTH phases and correlates: the request phase records the session's
 * in-flight server, and the response phase attributes any ingested secret to
 * it. This models a proxy/host that runs request → operation → response
 * sequentially per session; concurrent in-flight operations within one
 * session would need host-supplied attribution (a closed-tier concern).
 *
 * Open tier boundaries (exact, by design):
 *   - verbatim (exact-match) secrets only - no fragment, paraphrase, or
 *     semantic detection;
 *   - the public secret-FORMAT catalog in `secret-formats.ts` only;
 *   - in-memory per-session taint, isolated per guard instance.
 *
 * Causality is by construction: taint is only ever recorded from responses,
 * so a request-phase check can only see taint from strictly-prior reads. A
 * secret sent with no prior cross-boundary read passes - the guard tracks
 * flows, it does not moralize about values.
 */
import { INTERCEPTION_EVENT, INTERCEPTOR_PHASE } from "../../protocol/constants.js";
import type { InterceptorPhase } from "../../protocol/constants.js";
import type { InvokeParams } from "../../protocol/types.js";
import { block, defineValidator, pass } from "../../server/define-interceptor.js";
import type { RegisteredInterceptor, ValidationVerdict } from "../../server/define-interceptor.js";
import { findSecrets } from "./secret-formats.js";
import { serverOf } from "./server-of.js";

export const CROSS_BOUNDARY_GUARD_NAME = "formalcore/cross-boundary-guard";

interface TaintOrigin {
  readonly formatId: string;
  readonly server: string;
}

interface SessionState {
  /** Secret value → where it was FIRST read (first origin wins). */
  readonly taint: Map<string, TaintOrigin>;
  /** Server of the session's in-flight request, for response attribution. */
  inFlightServer: string | null;
}

const DEFAULT_SESSION = "default";

function sessionKey(params: InvokeParams): string {
  return params.context?.sessionId ?? params.context?.traceId ?? DEFAULT_SESSION;
}

function payloadText(payload: unknown): string {
  return payload === undefined ? "" : JSON.stringify(payload);
}

export function createCrossBoundaryGuard(): RegisteredInterceptor {
  const sessions = new Map<string, SessionState>();

  const sessionFor = (params: InvokeParams): SessionState => {
    const key = sessionKey(params);
    const existing = sessions.get(key);
    if (existing !== undefined) return existing;
    const fresh: SessionState = { taint: new Map(), inFlightServer: null };
    sessions.set(key, fresh);
    return fresh;
  };

  /** Request phase: enforce, then note the in-flight server for the response. */
  const enforce = (params: InvokeParams): ValidationVerdict => {
    const session = sessionFor(params);
    const target = serverOf(params);
    for (const hit of findSecrets(payloadText(params.payload))) {
      const origin = session.taint.get(hit.value);
      if (origin !== undefined && origin.server !== target) {
        return block(
          `cross-boundary secret flow: ${origin.formatId} read from '${origin.server}' ` +
            `may not be sent to '${target}'`,
        );
      }
    }
    session.inFlightServer = target;
    return pass({ target, tainted: session.taint.size });
  };

  /** Response phase: ingest, attributing to the correlated in-flight server. */
  const ingest = (params: InvokeParams): ValidationVerdict => {
    const session = sessionFor(params);
    const server = session.inFlightServer ?? serverOf(params);
    let ingested = 0;
    for (const hit of findSecrets(payloadText(params.payload))) {
      if (!session.taint.has(hit.value)) {
        session.taint.set(hit.value, { formatId: hit.formatId, server });
        ingested += 1;
      }
    }
    return pass({ origin: server, ingested, tainted: session.taint.size });
  };

  /** RULE 2: phase → behavior, exhaustively. */
  const BY_PHASE: Record<InterceptorPhase, (params: InvokeParams) => ValidationVerdict> = {
    [INTERCEPTOR_PHASE.Request]: enforce,
    [INTERCEPTOR_PHASE.Response]: ingest,
  };

  return defineValidator({
    name: CROSS_BOUNDARY_GUARD_NAME,
    description:
      "Blocks verbatim secrets read from one server from being sent to another " +
      "(causal cross-boundary taint, open tier).",
    version: "0.1.0",
    events: [
      INTERCEPTION_EVENT.ToolsCall,
      INTERCEPTION_EVENT.ResourcesRead,
      INTERCEPTION_EVENT.SamplingCreateMessage,
      INTERCEPTION_EVENT.ElicitationCreate,
    ],
    validate: (params) => BY_PHASE[params.phase](params),
  });
}
