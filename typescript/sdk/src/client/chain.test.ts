// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Exhaustive tests for the chain executor (FUNCTIONAL_PATTERNS RULE 15/16 +
 * Condition 1): every (mode × failOpen × outcome) combination is table-driven,
 * ordering/parallelism are observed rather than assumed, and every abort path
 * is exercised.
 */
import { describe, expect, it } from "vitest";
import {
  CHAIN_STATUS,
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "../protocol/constants.js";
import type { InterceptorMode } from "../protocol/constants.js";
import { normalizeResult } from "../protocol/wire.js";
import type {
  Interceptor,
  InterceptorResult,
  InvokeParams,
  MutationResult,
  ValidationResult,
} from "../protocol/types.js";
import { ABORT_KIND, executeChain } from "./chain.js";
import type { ChainParams, InterceptorInvoker } from "./chain.js";

// ── fixtures ─────────────────────────────────────────────────────────────────

const EVENT = "tools/call";

function descriptor(over: Partial<Interceptor> & { name: string }): Interceptor {
  return {
    version: null,
    description: null,
    type: INTERCEPTOR_TYPE.Validation,
    hooks: [
      { events: [EVENT], phase: INTERCEPTOR_PHASE.Request },
      { events: [EVENT], phase: INTERCEPTOR_PHASE.Response },
    ],
    mode: INTERCEPTOR_MODE.Enforce,
    failOpen: false,
    priorityHint: null,
    compat: null,
    configSchema: null,
    ...over,
  };
}

const validator = (name: string, over: Partial<Interceptor> = {}): Interceptor =>
  descriptor({ name, type: INTERCEPTOR_TYPE.Validation, ...over });
const mutator = (name: string, over: Partial<Interceptor> = {}): Interceptor =>
  descriptor({ name, type: INTERCEPTOR_TYPE.Mutation, ...over });

function vResult(over: Partial<ValidationResult> = {}): ValidationResult {
  return {
    type: INTERCEPTOR_TYPE.Validation,
    interceptor: null,
    phase: INTERCEPTOR_PHASE.Request,
    durationMs: null,
    info: null,
    valid: true,
    severity: null,
    messages: [],
    suggestions: [],
    ...over,
  };
}

function mResult(over: Partial<MutationResult> = {}): MutationResult {
  return {
    type: INTERCEPTOR_TYPE.Mutation,
    interceptor: null,
    phase: INTERCEPTOR_PHASE.Request,
    durationMs: null,
    info: null,
    modified: false,
    payload: null,
    ...over,
  };
}

const invalidError = (message: string): ValidationResult =>
  vResult({
    valid: false,
    severity: VALIDATION_SEVERITY.Error,
    messages: [{ path: null, message, severity: VALIDATION_SEVERITY.Error }],
  });

type Behavior = (params: InvokeParams) => InterceptorResult | Promise<InterceptorResult>;

function invokerFrom(table: Readonly<Record<string, Behavior>>): InterceptorInvoker {
  return async (params) => {
    const behavior = table[params.name];
    if (behavior === undefined) throw new Error(`no behavior for ${params.name}`);
    return behavior(params);
  };
}

function params(over: Partial<ChainParams> = {}): ChainParams {
  return {
    event: EVENT,
    phase: INTERCEPTOR_PHASE.Request,
    payload: { value: "x" },
    names: null,
    timeoutMs: null,
    context: null,
    ...over,
  };
}

// ── selection ────────────────────────────────────────────────────────────────

describe("selection", () => {
  it("skips interceptors whose hooks do not match the event", async () => {
    const chain = await executeChain(
      [validator("v", { hooks: [{ events: ["prompts/get"], phase: INTERCEPTOR_PHASE.Request }] })],
      invokerFrom({}),
      params(),
    );
    expect(chain.status).toBe(CHAIN_STATUS.Success);
    expect(chain.results).toHaveLength(0);
  });

  it("matches the * wildcard event", async () => {
    const chain = await executeChain(
      [validator("v", { hooks: [{ events: ["*"], phase: INTERCEPTOR_PHASE.Request }] })],
      invokerFrom({ v: () => vResult() }),
      params(),
    );
    expect(chain.results).toHaveLength(1);
  });

  it("skips interceptors hooked on the other phase", async () => {
    const chain = await executeChain(
      [validator("v", { hooks: [{ events: [EVENT], phase: INTERCEPTOR_PHASE.Response }] })],
      invokerFrom({}),
      params({ phase: INTERCEPTOR_PHASE.Request }),
    );
    expect(chain.results).toHaveLength(0);
  });

  it("honors the names filter", async () => {
    const calls: string[] = [];
    const spy =
      (name: string): Behavior =>
      () => {
        calls.push(name);
        return vResult();
      };
    await executeChain(
      [validator("a"), validator("b")],
      invokerFrom({ a: spy("a"), b: spy("b") }),
      params({ names: ["b"] }),
    );
    expect(calls).toEqual(["b"]);
  });

  it("an empty chain succeeds with the payload untouched", async () => {
    const chain = await executeChain([], invokerFrom({}), params());
    expect(chain.status).toBe(CHAIN_STATUS.Success);
    expect(chain.finalPayload).toEqual({ value: "x" });
    expect(chain.abortedAt).toBeNull();
  });
});

// ── trust-boundary ordering ──────────────────────────────────────────────────

describe("trust-boundary order", () => {
  const record = (calls: string[], name: string, result: InterceptorResult): Behavior =>
    () => {
      calls.push(name);
      return result;
    };

  it("request phase runs mutations before validations", async () => {
    const calls: string[] = [];
    await executeChain(
      [validator("v"), mutator("m")],
      invokerFrom({ v: record(calls, "v", vResult()), m: record(calls, "m", mResult()) }),
      params({ phase: INTERCEPTOR_PHASE.Request }),
    );
    expect(calls).toEqual(["m", "v"]);
  });

  it("response phase runs validations before mutations", async () => {
    const calls: string[] = [];
    await executeChain(
      [mutator("m"), validator("v")],
      invokerFrom({ v: record(calls, "v", vResult()), m: record(calls, "m", mResult()) }),
      params({ phase: INTERCEPTOR_PHASE.Response }),
    );
    expect(calls).toEqual(["v", "m"]);
  });

  it("a blocking validation in the response phase prevents mutations", async () => {
    const calls: string[] = [];
    const chain = await executeChain(
      [mutator("m"), validator("v")],
      invokerFrom({
        v: () => invalidError("nope"),
        m: record(calls, "m", mResult()),
      }),
      params({ phase: INTERCEPTOR_PHASE.Response }),
    );
    expect(chain.status).toBe(CHAIN_STATUS.ValidationFailed);
    expect(calls).toEqual([]);
  });

  it("a fail-closed mutation crash in the request phase prevents validations", async () => {
    const calls: string[] = [];
    const chain = await executeChain(
      [mutator("m"), validator("v")],
      invokerFrom({
        m: () => {
          throw new Error("boom");
        },
        v: record(calls, "v", vResult()),
      }),
      params({ phase: INTERCEPTOR_PHASE.Request }),
    );
    expect(chain.status).toBe(CHAIN_STATUS.MutationFailed);
    expect(chain.abortedAt).toEqual({
      interceptor: "m",
      reason: "boom",
      kind: ABORT_KIND.Mutation,
    });
    expect(calls).toEqual([]);
  });
});

// ── mutation ordering + folding ──────────────────────────────────────────────

describe("mutation ordering", () => {
  const appender =
    (suffix: string): Behavior =>
    (p) =>
      mResult({ modified: true, payload: `${p.payload as string}${suffix}` });

  it("orders by resolved priority ascending with alphabetical tie-break", async () => {
    const calls: string[] = [];
    const spy =
      (name: string): Behavior =>
      () => {
        calls.push(name);
        return mResult();
      };
    await executeChain(
      [
        mutator("b", { priorityHint: 10 }),
        mutator("a", { priorityHint: 10 }),
        mutator("c", { priorityHint: -5 }),
      ],
      invokerFrom({ a: spy("a"), b: spy("b"), c: spy("c") }),
      params(),
    );
    expect(calls).toEqual(["c", "a", "b"]);
  });

  it("resolves per-phase priority hints for the running phase", async () => {
    const calls: string[] = [];
    const spy =
      (name: string): Behavior =>
      () => {
        calls.push(name);
        return mResult();
      };
    const interceptors = [
      mutator("late-on-request", { priorityHint: { request: 5, response: -10 } }),
      mutator("zero", { priorityHint: 0 }),
    ];
    const table = { "late-on-request": spy("late-on-request"), zero: spy("zero") };

    await executeChain(interceptors, invokerFrom(table), params({ phase: INTERCEPTOR_PHASE.Request }));
    expect(calls).toEqual(["zero", "late-on-request"]);

    calls.length = 0;
    await executeChain(interceptors, invokerFrom(table), params({ phase: INTERCEPTOR_PHASE.Response }));
    expect(calls).toEqual(["late-on-request", "zero"]);
  });

  it("folds payloads sequentially through the mutation chain", async () => {
    const chain = await executeChain(
      [mutator("m1", { priorityHint: 1 }), mutator("m2", { priorityHint: 2 })],
      invokerFrom({ m1: appender("-a"), m2: appender("-b") }),
      params({ payload: "x" }),
    );
    expect(chain.finalPayload).toBe("x-a-b");
  });

  it("request-phase validators observe the mutated payload", async () => {
    let seen: unknown = null;
    await executeChain(
      [mutator("m"), validator("v")],
      invokerFrom({
        m: () => mResult({ modified: true, payload: "rewritten" }),
        v: (p) => {
          seen = p.payload;
          return vResult();
        },
      }),
      params({ payload: "original" }),
    );
    expect(seen).toBe("rewritten");
  });

  it("hands each mutator a snapshot: in-place edits are discarded", async () => {
    const payload = { arguments: { items: [1] } };
    const chain = await executeChain(
      [mutator("m")],
      invokerFrom({
        m: (p) => {
          (p.payload as typeof payload).arguments.items.push(2);
          return mResult({ modified: false });
        },
      }),
      params({ payload }),
    );
    expect(chain.finalPayload).toEqual({ arguments: { items: [1] } });
    expect(payload.arguments.items).toEqual([1]);
  });
});

// ── validations: parallelism + summary ───────────────────────────────────────

describe("validations", () => {
  it("runs validators in parallel", async () => {
    const started: string[] = [];
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => (release = resolve));
    const gated =
      (name: string): Behavior =>
      async () => {
        started.push(name);
        if (started.length === 2) release();
        await gate;
        return vResult();
      };
    const chain = await executeChain(
      [validator("a"), validator("b")],
      invokerFrom({ a: gated("a"), b: gated("b") }),
      params(),
    );
    // Both started before either finished — impossible under sequential execution.
    expect(started.sort()).toEqual(["a", "b"]);
    expect(chain.status).toBe(CHAIN_STATUS.Success);
  });

  it("aggregates message severities into the validation summary", async () => {
    const chain = await executeChain(
      [validator("v")],
      invokerFrom({
        v: () =>
          vResult({
            valid: false,
            severity: VALIDATION_SEVERITY.Warn,
            messages: [
              { path: null, message: "w", severity: VALIDATION_SEVERITY.Warn },
              { path: null, message: "i", severity: VALIDATION_SEVERITY.Info },
            ],
          }),
      }),
      params(),
    );
    expect(chain.validationSummary).toEqual({ errors: 0, warnings: 1, infos: 1 });
    expect(chain.status).toBe(CHAIN_STATUS.Success); // warn never blocks
  });

  it("the first blocking validator in declaration order wins the abort", async () => {
    const chain = await executeChain(
      [validator("first"), validator("second")],
      invokerFrom({
        first: () => invalidError("first says no"),
        second: () => invalidError("second says no"),
      }),
      params(),
    );
    expect(chain.abortedAt?.interceptor).toBe("first");
    expect(chain.abortedAt?.reason).toBe("first says no");
  });

  it("stamps interceptor name, phase and duration onto results", async () => {
    const chain = await executeChain(
      [validator("stamped")],
      invokerFrom({ stamped: () => vResult() }),
      params({ phase: INTERCEPTOR_PHASE.Request }),
    );
    const result = chain.results[0];
    expect(result?.interceptor).toBe("stamped");
    expect(result?.phase).toBe(INTERCEPTOR_PHASE.Request);
    expect(result?.durationMs).toBeGreaterThanOrEqual(0);
  });
});

// ── Condition 1: exhaustive mode × failOpen × outcome matrices ───────────────

const VALIDATOR_OUTCOME = {
  Valid: "valid",
  InvalidError: "invalid-error",
  InvalidWarn: "invalid-warn",
  Crash: "crash",
} as const;
type ValidatorOutcome = (typeof VALIDATOR_OUTCOME)[keyof typeof VALIDATOR_OUTCOME];

const VALIDATOR_BEHAVIOR: Record<ValidatorOutcome, Behavior> = {
  [VALIDATOR_OUTCOME.Valid]: () => vResult(),
  [VALIDATOR_OUTCOME.InvalidError]: () => invalidError("blocked"),
  [VALIDATOR_OUTCOME.InvalidWarn]: () =>
    vResult({
      valid: false,
      severity: VALIDATION_SEVERITY.Warn,
      messages: [{ path: null, message: "w", severity: VALIDATION_SEVERITY.Warn }],
    }),
  [VALIDATOR_OUTCOME.Crash]: () => {
    throw new Error("validator crashed");
  },
};

describe("Condition 1: validator blocking matrix", () => {
  for (const mode of Object.values(INTERCEPTOR_MODE)) {
    for (const failOpen of [false, true]) {
      for (const outcome of Object.values(VALIDATOR_OUTCOME)) {
        const blocks =
          mode === INTERCEPTOR_MODE.Enforce &&
          (outcome === VALIDATOR_OUTCOME.InvalidError ||
            (outcome === VALIDATOR_OUTCOME.Crash && !failOpen));
        it(`mode=${mode} failOpen=${String(failOpen)} outcome=${outcome} → ${blocks ? "blocks" : "passes"}`, async () => {
          const chain = await executeChain(
            [validator("v", { mode: mode as InterceptorMode, failOpen })],
            invokerFrom({ v: VALIDATOR_BEHAVIOR[outcome] }),
            params(),
          );
          expect(chain.status).toBe(
            blocks ? CHAIN_STATUS.ValidationFailed : CHAIN_STATUS.Success,
          );
        });
      }
    }
  }
});

const MUTATOR_OUTCOME = {
  Modified: "modified",
  Unmodified: "unmodified",
  Crash: "crash",
} as const;
type MutatorOutcome = (typeof MUTATOR_OUTCOME)[keyof typeof MUTATOR_OUTCOME];

const MUTATOR_BEHAVIOR: Record<MutatorOutcome, Behavior> = {
  [MUTATOR_OUTCOME.Modified]: () => mResult({ modified: true, payload: "mutated" }),
  [MUTATOR_OUTCOME.Unmodified]: () => mResult(),
  [MUTATOR_OUTCOME.Crash]: () => {
    throw new Error("mutator crashed");
  },
};

describe("Condition 1: mutator apply/abort matrix", () => {
  for (const mode of Object.values(INTERCEPTOR_MODE)) {
    for (const failOpen of [false, true]) {
      for (const outcome of Object.values(MUTATOR_OUTCOME)) {
        const aborts =
          mode === INTERCEPTOR_MODE.Enforce &&
          outcome === MUTATOR_OUTCOME.Crash &&
          !failOpen;
        const applies =
          mode === INTERCEPTOR_MODE.Enforce && outcome === MUTATOR_OUTCOME.Modified;
        it(`mode=${mode} failOpen=${String(failOpen)} outcome=${outcome} → ${aborts ? "aborts" : applies ? "applies" : "no-op"}`, async () => {
          const chain = await executeChain(
            [mutator("m", { mode: mode as InterceptorMode, failOpen })],
            invokerFrom({ m: MUTATOR_BEHAVIOR[outcome] }),
            params({ payload: "original" }),
          );
          expect(chain.status).toBe(
            aborts ? CHAIN_STATUS.MutationFailed : CHAIN_STATUS.Success,
          );
          expect(chain.finalPayload).toBe(applies ? "mutated" : "original");
        });
      }
    }
  }
});

describe("audit mode shadow behavior", () => {
  it("records the audit mutator's result without applying its payload", async () => {
    const chain = await executeChain(
      [mutator("shadow", { mode: INTERCEPTOR_MODE.Audit })],
      invokerFrom({ shadow: () => mResult({ modified: true, payload: "shadow-payload" }) }),
      params({ payload: "original" }),
    );
    expect(chain.finalPayload).toBe("original");
    const result = chain.results[0];
    expect(result?.type).toBe(INTERCEPTOR_TYPE.Mutation);
    expect((result as MutationResult).payload).toBe("shadow-payload");
  });

  it("records the audit validator's failure without blocking, still counting it", async () => {
    const chain = await executeChain(
      [validator("audit-v", { mode: INTERCEPTOR_MODE.Audit })],
      invokerFrom({ "audit-v": () => invalidError("would block") }),
      params(),
    );
    expect(chain.status).toBe(CHAIN_STATUS.Success);
    expect(chain.validationSummary.errors).toBe(1);
    expect(chain.results).toHaveLength(1);
  });
});

// ── wire failures + timeout ──────────────────────────────────────────────────

describe("wire failures", () => {
  // A remote SDK answering with a type this SDK does not know (e.g. `sink`,
  // issue #16). normalizeResult throws WireError → interceptor failure.
  const unknownTypeInvoker: InterceptorInvoker = (p) =>
    Promise.resolve(normalizeResult({ type: "sink", phase: p.phase }));

  it("an unknown result type fails closed by default", async () => {
    const chain = await executeChain(
      [validator("v")],
      unknownTypeInvoker,
      params(),
    );
    expect(chain.status).toBe(CHAIN_STATUS.ValidationFailed);
    expect(chain.abortedAt?.reason).toContain("invalid interceptor type");
  });

  it("an unknown result type is skipped under failOpen — never a crash", async () => {
    const chain = await executeChain(
      [validator("v", { failOpen: true }), mutator("m", { failOpen: true })],
      unknownTypeInvoker,
      params({ payload: "p" }),
    );
    expect(chain.status).toBe(CHAIN_STATUS.Success);
    expect(chain.finalPayload).toBe("p");
  });
});

describe("timeout and cancellation", () => {
  const never: Behavior = () => new Promise<InterceptorResult>(() => {});

  it("a chain timeout aborts with status=timeout", async () => {
    const chain = await executeChain(
      [validator("slow")],
      invokerFrom({ slow: never }),
      params({ timeoutMs: 25 }),
    );
    expect(chain.status).toBe(CHAIN_STATUS.Timeout);
    expect(chain.abortedAt?.kind).toBe(ABORT_KIND.Timeout);
  });

  it("timeout in the mutation step also reports timeout, not mutation_failed", async () => {
    const chain = await executeChain(
      [mutator("slow")],
      invokerFrom({ slow: never }),
      params({ timeoutMs: 25 }),
    );
    expect(chain.status).toBe(CHAIN_STATUS.Timeout);
  });

  it("an outer AbortSignal aborts the chain", async () => {
    const controller = new AbortController();
    const invoker: InterceptorInvoker = (_p, signal) =>
      new Promise((_resolve, reject) => {
        signal?.addEventListener("abort", () => {
          reject(new DOMException("aborted", "AbortError"));
        });
      });
    const pending = executeChain([validator("v")], invoker, params(), controller.signal);
    controller.abort();
    const chain = await pending;
    expect(chain.status).toBe(CHAIN_STATUS.Timeout);
  });

  it("a non-abort invoker rejection propagates failOpen semantics instead", async () => {
    const chain = await executeChain(
      [validator("v", { failOpen: true })],
      invokerFrom({
        v: () => {
          throw new Error("plain crash");
        },
      }),
      params({ timeoutMs: 5_000 }),
    );
    expect(chain.status).toBe(CHAIN_STATUS.Success);
  });
});

describe("invoke params passed to interceptors", () => {
  it("carries event, phase, per-chain timeout and context through", async () => {
    let seen: InvokeParams | null = null;
    const context = {
      principal: { type: "user", id: "u-1", claims: null },
      traceId: "t-1",
      spanId: null,
      timestamp: null,
      sessionId: "s-1",
    };
    await executeChain(
      [validator("v")],
      invokerFrom({
        v: (p) => {
          seen = p;
          return vResult();
        },
      }),
      params({ context, timeoutMs: 500 }),
    );
    expect(seen).toMatchObject({
      name: "v",
      event: EVENT,
      phase: INTERCEPTOR_PHASE.Request,
      timeoutMs: 500,
      context,
      config: null,
    });
  });
});
