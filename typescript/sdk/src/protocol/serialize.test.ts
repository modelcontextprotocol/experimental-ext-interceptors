// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * The outbound boundary must round-trip through the inbound boundary
 * (serialize → normalize = identity on the interior), and interior
 * defaults must vanish from the wire (SEP optional shape).
 */
import { describe, expect, it } from "vitest";
import {
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "./constants.js";
import { matchesEvent } from "./match-event.js";
import {
  serializeInterceptor,
  serializeInvokeParams,
  serializeResult,
} from "./serialize.js";
import type {
  Interceptor,
  InvokeParams,
  MutationResult,
  ValidationResult,
} from "./types.js";
import {
  normalizeInterceptor,
  normalizeInvokeParams,
  normalizeResult,
  WireError,
} from "./wire.js";

const MINIMAL: Interceptor = {
  name: "min",
  version: null,
  description: null,
  type: INTERCEPTOR_TYPE.Validation,
  hooks: [{ events: ["*"], phase: INTERCEPTOR_PHASE.Request }],
  mode: INTERCEPTOR_MODE.Enforce,
  failOpen: false,
  priorityHint: null,
  compat: null,
  configSchema: null,
};

const MAXIMAL: Interceptor = {
  name: "max",
  version: "2.0.0",
  description: "everything set",
  type: INTERCEPTOR_TYPE.Mutation,
  hooks: [
    { events: ["tools/call", "prompts/get"], phase: INTERCEPTOR_PHASE.Request },
    { events: ["*"], phase: INTERCEPTOR_PHASE.Response },
  ],
  mode: INTERCEPTOR_MODE.Audit,
  failOpen: true,
  priorityHint: { request: 5, response: null },
  compat: { minProtocol: "2025-06-18", maxProtocol: null },
  configSchema: { type: "object" },
};

describe("serializeInterceptor", () => {
  it("omits interior defaults from the wire (mode, failOpen, nulls)", () => {
    const wire = serializeInterceptor(MINIMAL);
    expect(wire).toEqual({
      name: "min",
      type: "validation",
      hooks: [{ events: ["*"], phase: "request" }],
    });
  });

  it("round-trips: normalize(serialize(x)) === x, minimal and maximal", () => {
    expect(normalizeInterceptor(serializeInterceptor(MINIMAL))).toEqual(MINIMAL);
    expect(normalizeInterceptor(serializeInterceptor(MAXIMAL))).toEqual(MAXIMAL);
  });
});

describe("serializeResult", () => {
  const validation: ValidationResult = {
    type: INTERCEPTOR_TYPE.Validation,
    interceptor: "v",
    phase: INTERCEPTOR_PHASE.Response,
    durationMs: 12,
    info: { rule: "r1" },
    valid: false,
    severity: VALIDATION_SEVERITY.Error,
    messages: [{ path: "/a", message: "bad", severity: VALIDATION_SEVERITY.Error }],
    suggestions: [{ path: "/a", value: "good" }],
  };
  const mutation: MutationResult = {
    type: INTERCEPTOR_TYPE.Mutation,
    interceptor: "m",
    phase: INTERCEPTOR_PHASE.Request,
    durationMs: 3,
    info: null,
    modified: true,
    payload: { rewritten: true },
  };

  it("round-trips both members of the result union", () => {
    expect(normalizeResult(serializeResult(validation))).toEqual(validation);
    expect(normalizeResult(serializeResult(mutation))).toEqual(mutation);
  });
});

describe("normalizeInvokeParams", () => {
  const full: InvokeParams = {
    name: "v",
    event: "tools/call",
    phase: INTERCEPTOR_PHASE.Request,
    payload: { args: 1 },
    config: { strict: true },
    timeoutMs: 250,
    context: {
      principal: { type: "user", id: "u1", claims: { role: "admin" } },
      traceId: "t",
      spanId: "s",
      timestamp: "2025-01-01T00:00:00Z",
      sessionId: "sess",
    },
  };

  it("round-trips through serializeInvokeParams", () => {
    expect(normalizeInvokeParams(serializeInvokeParams(full))).toEqual(full);
  });

  it("defaults every omitted optional to null", () => {
    expect(
      normalizeInvokeParams({ name: "v", event: "e", phase: "request" }),
    ).toEqual({
      name: "v",
      event: "e",
      phase: INTERCEPTOR_PHASE.Request,
      payload: null,
      config: null,
      timeoutMs: null,
      context: null,
    });
  });

  it("rejects missing name/event/phase at the boundary", () => {
    expect(() => normalizeInvokeParams({ event: "e", phase: "request" })).toThrow(WireError);
    expect(() => normalizeInvokeParams({ name: "n", phase: "request" })).toThrow(WireError);
    expect(() => normalizeInvokeParams({ name: "n", event: "e" })).toThrow(WireError);
  });
});

describe("matchesEvent", () => {
  it("matches exact events and the * wildcard, rejects others", () => {
    expect(matchesEvent(["tools/call"], "tools/call")).toBe(true);
    expect(matchesEvent(["*"], "anything/custom")).toBe(true);
    expect(matchesEvent(["tools/call"], "tools/list")).toBe(false);
    expect(matchesEvent([], "tools/call")).toBe(false);
  });
});
