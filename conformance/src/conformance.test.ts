/**
 * Conformance suite meta-tests.
 *
 * RULE 20/21: the tests are generated from the catalog — one runner pass per
 * fixture — plus structural meta-tests: generation is deterministic and
 * complete (fixture count == catalog size), the on-disk JSON is exactly what
 * the catalog generates (stale/hand-edited fixtures fail), every fixture
 * traces to a requirement, and the reference SDK scores 100% compliance.
 */
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { CATALOG } from "./catalog.ts";
import { FIXTURE_KIND, fixtureFile } from "./fixture-types.ts";
import { generateFiles, generateManifest } from "./generate.ts";
import { REFERENCE_ADAPTER } from "./reference-adapter.ts";
import { formatReport, runFixture, runSuite } from "./runner.ts";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "..");

// ── catalog integrity ────────────────────────────────────────────────────────

describe("catalog", () => {
  it("has globally unique fixture ids", () => {
    const ids = CATALOG.map((f) => f.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("prefixes every id with its kind and tags every fixture with a requirement", () => {
    for (const f of CATALOG) {
      expect(f.id.startsWith(`${f.kind}/`)).toBe(true);
      expect(f.requirement.length).toBeGreaterThan(0);
      expect(f.steps.length).toBeGreaterThan(0);
    }
  });

  it("covers both kinds, including the canonical relaybleed fixture", () => {
    const kinds = new Set(CATALOG.map((f) => f.kind));
    expect(kinds).toEqual(new Set([FIXTURE_KIND.Protocol, FIXTURE_KIND.Behavior]));
    expect(CATALOG.some((f) => f.id.startsWith("behavior/relaybleed-"))).toBe(true);
  });
});

// ── generator (RULE 20: programmatic, complete, deterministic) ───────────────

describe("generator", () => {
  it("emits exactly one fixture file per catalog entry plus the manifest", () => {
    const files = generateFiles();
    expect(files.length).toBe(CATALOG.length + 1);
    const paths = files.map((f) => f.path);
    for (const fixture of CATALOG) expect(paths).toContain(fixtureFile(fixture));
    expect(paths).toContain("manifest.json");
  });

  it("is deterministic: two runs produce identical bytes", () => {
    expect(generateFiles()).toEqual(generateFiles());
  });

  it("traces every fixture to its requirement in the manifest", () => {
    const manifest = generateManifest();
    expect(manifest.fixtures.length).toBe(CATALOG.length);
    for (const [i, entry] of manifest.fixtures.entries()) {
      expect(entry.id).toBe(CATALOG[i]?.id);
      expect(entry.requirement).toBe(CATALOG[i]?.requirement);
      expect(entry.file).toBe(fixtureFile(CATALOG[i]!));
    }
  });

  it("enriches every exact expectation with a JCS canonical string", () => {
    for (const file of generateFiles()) {
      if (file.path === "manifest.json") continue;
      const fixture = JSON.parse(file.contents) as {
        steps: readonly {
          op: string;
          expect: Record<string, unknown>;
        }[];
      };
      for (const step of fixture.steps) {
        if (step.op === "chain") {
          if ("finalPayload" in step.expect) {
            expect(typeof step.expect.finalPayloadCanonical).toBe("string");
          }
        } else if (!("errorCode" in step.expect)) {
          expect(typeof step.expect.canonical).toBe("string");
        }
      }
    }
  });

  it("matches the fixtures on disk byte-for-byte (regenerate if this fails)", async () => {
    for (const file of generateFiles()) {
      const onDisk = await readFile(join(ROOT, file.path), "utf8");
      expect(onDisk, `${file.path} is stale — run scripts/generate.ts`).toBe(
        file.contents,
      );
    }
  });
});

// ── the suite against the reference SDK (proves fixtures are satisfiable) ────

describe("reference adapter", () => {
  it.each(CATALOG.map((f) => [f.id, f] as const))(
    "%s passes against the TypeScript reference SDK",
    async (_id, fixture) => {
      const report = await runFixture(fixture, REFERENCE_ADAPTER);
      const failures = report.steps
        .filter((s) => !s.passed)
        .map((s) => `step ${String(s.index)} (${s.op}): ${s.detail}`)
        .join("\n");
      expect(report.passed, failures).toBe(true);
    },
  );

  it("reports 100% compliance for the reference SDK", async () => {
    const report = await runSuite(CATALOG, REFERENCE_ADAPTER);
    expect(report.compliancePercent).toBe(100);
    expect(report.passed).toBe(CATALOG.length);
    const text = formatReport(report);
    expect(text).toContain("compliance 100%");
    expect(text).toContain("typescript-reference-sdk");
  });

  it("scores a broken implementation below 100% and names the failure", async () => {
    // An adapter that denies nothing: relaybleed fixtures MUST fail against it.
    const permissive = {
      name: "permissive-strawman",
      createSession: async (interceptors: Parameters<typeof REFERENCE_ADAPTER.createSession>[0]) => {
        const session = await REFERENCE_ADAPTER.createSession(
          interceptors.filter(
            (i) => i.behavior !== "cross-boundary-guard" && i.behavior !== "secretless-redactor",
          ),
        );
        return session;
      },
    };
    const relaybleed = CATALOG.filter((f) => f.id.startsWith("behavior/relaybleed-"));
    const report = await runSuite(relaybleed, permissive);
    expect(report.passed).toBe(0);
    expect(report.compliancePercent).toBe(0);
    expect(formatReport(report)).toContain("expected decision 'deny', got 'allow'");
  });
});
