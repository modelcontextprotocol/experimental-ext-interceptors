// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

import { describe, expect, it } from "vitest";
import {
  CHAIN_STATUS,
  INTERCEPTION_EVENT,
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  RPC_METHOD,
  VALIDATION_SEVERITY,
} from "./constants.js";
import { resolvePriority } from "./resolve-priority.js";
import { normalizeInterceptor, normalizeResult, WireError } from "./wire.js";

/**
 * RULE 1 / Condition 1: every finite `const` set is pinned to an exact count,
 * has no value collisions, and (where the key is a PascalCase alias of a wire
 * value) is internally consistent. A silent addition or rename fails the count.
 */
const CONST_SETS = {
  INTERCEPTOR_TYPE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_MODE,
  VALIDATION_SEVERITY,
  CHAIN_STATUS,
  RPC_METHOD,
  INTERCEPTION_EVENT,
} as const;

const EXPECTED_COUNTS: Record<keyof typeof CONST_SETS, number> = {
  INTERCEPTOR_TYPE: 2,
  INTERCEPTOR_PHASE: 2,
  INTERCEPTOR_MODE: 2,
  VALIDATION_SEVERITY: 3,
  CHAIN_STATUS: 4,
  RPC_METHOD: 2,
  INTERCEPTION_EVENT: 12,
};

describe("constant sets (RULE 1)", () => {
  for (const [name, set] of Object.entries(CONST_SETS)) {
    it(`${name} has exactly ${
      EXPECTED_COUNTS[name as keyof typeof CONST_SETS]
    } members with no value collisions`, () => {
      const values = Object.values(set);
      expect(values).toHaveLength(
        EXPECTED_COUNTS[name as keyof typeof CONST_SETS],
      );
      expect(new Set(values).size).toBe(values.length);
      for (const v of values) expect(typeof v).toBe("string");
    });
  }

  it("SEP two-type invariant: validation + mutation only", () => {
    expect(Object.values(INTERCEPTOR_TYPE).sort()).toEqual([
      "mutation",
      "validation",
    ]);
  });

  it("mode uses the SEP canonical wire value `active` (enforce is legacy read-only)", () => {
    expect(INTERCEPTOR_MODE.Enforce).toBe("active");
    expect(Object.values(INTERCEPTOR_MODE)).toContain("active");
    expect(Object.values(INTERCEPTOR_MODE)).not.toContain("enforce");
    // Legacy `enforce` on the wire is accepted read-only and normalized to `active`.
    const legacy = normalizeInterceptor({
      name: "g",
      type: "validation",
      hooks: [{ events: ["tools/call"], phase: "request" }],
      mode: "enforce",
    });
    expect(legacy.mode).toBe(INTERCEPTOR_MODE.Enforce);
  });
});

/** RULE 7: the wire boundary applies defaults and collapses optionals to null. */
describe("normalizeInterceptor (RULE 7 boundary)", () => {
  it("applies active/failOpen=false defaults and nulls absent optionals", () => {
    const i = normalizeInterceptor({
      name: "guard",
      type: "validation",
      hooks: [{ events: ["tools/call"], phase: "request" }],
    });
    expect(i.mode).toBe(INTERCEPTOR_MODE.Enforce);
    expect(i.failOpen).toBe(false);
    expect(i.version).toBeNull();
    expect(i.description).toBeNull();
    expect(i.priorityHint).toBeNull();
    expect(i.compat).toBeNull();
    expect(i.configSchema).toBeNull();
    expect(i.hooks[0]?.events).toEqual(["tools/call"]);
  });

  it("passes through a custom namespace/operation event (open extension)", () => {
    const i = normalizeInterceptor({
      name: "x",
      type: "mutation",
      hooks: [{ events: ["custom/redactStuff"], phase: "response" }],
    });
    expect(i.hooks[0]?.events).toEqual(["custom/redactStuff"]);
  });

  it("throws WireError on an invalid closed-set value (type)", () => {
    expect(() =>
      normalizeInterceptor({ name: "x", type: "sink", hooks: [] }),
    ).toThrow(WireError);
  });

  it("throws WireError when the required name is missing", () => {
    expect(() => normalizeInterceptor({ type: "validation", hooks: [] })).toThrow(
      WireError,
    );
  });
});

/**
 * RULE 2 / Condition 1: the result dispatch table covers every interceptor
 * type. Exercised by round-tripping one result of each type through the wire.
 */
describe("normalizeResult (RULE 2 dispatch)", () => {
  it("normalizes a validation result and defaults messages to []", () => {
    const r = normalizeResult({ type: "validation", phase: "request", valid: false });
    expect(r.type).toBe(INTERCEPTOR_TYPE.Validation);
    if (r.type === INTERCEPTOR_TYPE.Validation) {
      expect(r.valid).toBe(false);
      expect(r.messages).toEqual([]);
      expect(r.suggestions).toEqual([]);
    }
  });

  it("normalizes a mutation result", () => {
    const r = normalizeResult({
      type: "mutation",
      phase: "response",
      modified: true,
      payload: { redacted: true },
    });
    expect(r.type).toBe(INTERCEPTOR_TYPE.Mutation);
    if (r.type === INTERCEPTOR_TYPE.Mutation) {
      expect(r.modified).toBe(true);
      expect(r.payload).toEqual({ redacted: true });
    }
  });

  it("covers every INTERCEPTOR_TYPE in the dispatch table", () => {
    for (const t of Object.values(INTERCEPTOR_TYPE)) {
      const r = normalizeResult({ type: t, phase: "request" });
      expect(r.type).toBe(t);
    }
  });

  it("throws WireError on an unknown result type", () => {
    expect(() => normalizeResult({ type: "observability", phase: "request" })).toThrow(
      WireError,
    );
  });
});

describe("resolvePriority", () => {
  it("defaults to 0 when the hint is null", () => {
    expect(resolvePriority(null, INTERCEPTOR_PHASE.Request)).toBe(0);
  });
  it("returns a scalar hint for both phases", () => {
    expect(resolvePriority(-500, INTERCEPTOR_PHASE.Request)).toBe(-500);
    expect(resolvePriority(-500, INTERCEPTOR_PHASE.Response)).toBe(-500);
  });
  it("selects per-phase and defaults the absent side to 0", () => {
    const hint = { request: -1000, response: null };
    expect(resolvePriority(hint, INTERCEPTOR_PHASE.Request)).toBe(-1000);
    expect(resolvePriority(hint, INTERCEPTOR_PHASE.Response)).toBe(0);
  });
});
