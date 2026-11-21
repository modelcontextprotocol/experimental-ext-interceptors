// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/** Tests for the authoring surface + registry (pure server core). */
import { McpError } from "@modelcontextprotocol/sdk/types.js";
import { describe, expect, it } from "vitest";
import {
  INTERCEPTOR_MODE,
  INTERCEPTOR_PHASE,
  INTERCEPTOR_TYPE,
  VALIDATION_SEVERITY,
} from "../protocol/constants.js";
import type { InvokeParams } from "../protocol/types.js";
import {
  HOOK_PHASES,
  apply,
  block,
  defineMutator,
  defineValidator,
  keep,
  pass,
} from "./define-interceptor.js";
import { createRegistry } from "./registry.js";

function invokeParams(over: Partial<InvokeParams> & { name: string }): InvokeParams {
  return {
    event: "tools/call",
    phase: INTERCEPTOR_PHASE.Request,
    payload: null,
    config: null,
    timeoutMs: null,
    context: null,
    ...over,
  };
}

describe("defineValidator", () => {
  const spec = {
    name: "no-secrets",
    events: ["tools/call"],
    validate: () => pass(),
  };

  it("normalizes defaults: enforce, fail-closed, both phases hooked", () => {
    const { descriptor } = defineValidator(spec);
    expect(descriptor.type).toBe(INTERCEPTOR_TYPE.Validation);
    expect(descriptor.mode).toBe(INTERCEPTOR_MODE.Enforce);
    expect(descriptor.failOpen).toBe(false);
    expect(descriptor.hooks.map((h) => h.phase)).toEqual([
      INTERCEPTOR_PHASE.Request,
      INTERCEPTOR_PHASE.Response,
    ]);
  });

  // Condition 1: every HOOK_PHASES member expands to its exact hook set.
  const EXPECTED_HOOKS = {
    [HOOK_PHASES.Request]: [INTERCEPTOR_PHASE.Request],
    [HOOK_PHASES.Response]: [INTERCEPTOR_PHASE.Response],
    [HOOK_PHASES.Both]: [INTERCEPTOR_PHASE.Request, INTERCEPTOR_PHASE.Response],
  } as const;

  for (const phases of Object.values(HOOK_PHASES)) {
    it(`phases=${phases} expands to hooks [${EXPECTED_HOOKS[phases].join(", ")}]`, () => {
      const { descriptor } = defineValidator({ ...spec, phases });
      expect(descriptor.hooks.map((h) => h.phase)).toEqual([...EXPECTED_HOOKS[phases]]);
    });
  }

  it("wraps a passing verdict into a full ValidationResult", async () => {
    const { handler } = defineValidator({
      ...spec,
      validate: () => pass({ checked: 3 }),
    });
    const result = await handler(invokeParams({ name: "no-secrets" }), null);
    expect(result).toMatchObject({
      type: INTERCEPTOR_TYPE.Validation,
      interceptor: "no-secrets",
      valid: true,
      severity: null,
      messages: [],
      info: { checked: 3 },
    });
  });

  it("wraps a blocking verdict with defaulted error severity and string message", async () => {
    const { handler } = defineValidator({
      ...spec,
      validate: () => block("credential detected"),
    });
    const result = await handler(invokeParams({ name: "no-secrets" }), null);
    expect(result).toMatchObject({
      valid: false,
      severity: VALIDATION_SEVERITY.Error,
      messages: [
        {
          path: null,
          message: "credential detected",
          severity: VALIDATION_SEVERITY.Error,
        },
      ],
    });
  });

  it("an invalid verdict without explicit severity defaults to error", async () => {
    const { handler } = defineValidator({
      ...spec,
      validate: () => ({ valid: false }),
    });
    const result = await handler(invokeParams({ name: "no-secrets" }), null);
    expect(result).toMatchObject({ valid: false, severity: VALIDATION_SEVERITY.Error });
  });
});

describe("defineMutator", () => {
  it("apply() marks modified and carries the payload", async () => {
    const { descriptor, handler } = defineMutator({
      name: "redactor",
      events: ["*"],
      mutate: (p) => apply({ ...(p.payload as object), redacted: true }),
    });
    expect(descriptor.type).toBe(INTERCEPTOR_TYPE.Mutation);
    const result = await handler(
      invokeParams({ name: "redactor", payload: { a: 1 } }),
      null,
    );
    expect(result).toMatchObject({
      modified: true,
      payload: { a: 1, redacted: true },
    });
  });

  it("keep() echoes the original payload unmodified", async () => {
    const { handler } = defineMutator({
      name: "noop",
      events: ["*"],
      mutate: () => keep(),
    });
    const result = await handler(
      invokeParams({ name: "noop", payload: "original" }),
      null,
    );
    expect(result).toMatchObject({ modified: false, payload: "original" });
  });
});

describe("createRegistry", () => {
  const entries = [
    defineValidator({
      name: "v-tools",
      events: ["tools/call"],
      validate: (p) =>
        (p.payload as { forbidden?: boolean } | null)?.forbidden === true
          ? block("forbidden payload")
          : pass(),
    }),
    defineMutator({
      name: "m-prompts",
      events: ["prompts/get"],
      mutate: () => keep(),
    }),
  ];

  it("rejects duplicate names at construction", () => {
    expect(() => createRegistry([...entries, entries[0]])).toThrow(
      /duplicate interceptor name: 'v-tools'/,
    );
  });

  it("lists everything without a filter and filters by event", () => {
    const registry = createRegistry(entries);
    expect(registry.list(null).map((d) => d.name)).toEqual(["v-tools", "m-prompts"]);
    expect(registry.list("tools/call").map((d) => d.name)).toEqual(["v-tools"]);
    expect(registry.list("resources/read")).toHaveLength(0);
  });

  it("exposes the union of hook events as supportedEvents", () => {
    const registry = createRegistry(entries);
    expect([...registry.supportedEvents].sort()).toEqual(["prompts/get", "tools/call"]);
  });

  it("invoke routes by name, runs the handler, stamps duration", async () => {
    const registry = createRegistry(entries);
    const result = await registry.invoke(
      invokeParams({ name: "v-tools", payload: { forbidden: true } }),
    );
    expect(result).toMatchObject({ interceptor: "v-tools", valid: false });
    expect(result.durationMs).toBeGreaterThanOrEqual(0);
  });

  it("invoke throws MCP invalid-params for an unknown name", async () => {
    const registry = createRegistry(entries);
    await expect(registry.invoke(invokeParams({ name: "ghost" }))).rejects.toThrow(
      McpError,
    );
  });

  it("invoke converts a timed-out handler into a timeout error", async () => {
    const registry = createRegistry([
      defineValidator({
        name: "sleeper",
        events: ["*"],
        validate: (_p, signal) =>
          new Promise((_resolve, reject) => {
            signal?.addEventListener("abort", () => {
              reject(new Error("aborted"));
            });
          }),
      }),
    ]);
    await expect(
      registry.invoke(invokeParams({ name: "sleeper", timeoutMs: 20 })),
    ).rejects.toThrow(/timed out after 20ms/);
  });
});
