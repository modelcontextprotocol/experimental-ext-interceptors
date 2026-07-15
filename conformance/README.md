# MCP Interceptors Conformance Suite

Language-neutral conformance for SEP-2624 interceptors. The tests are **pure
JSON golden fixtures** - no test framework, no language runtime assumptions -
generated programmatically from one typed catalog. Any SDK (TypeScript,
Python, C#, Go, …) certifies by implementing a four-function adapter
([ADAPTER.md](./ADAPTER.md)) and replaying the fixtures.

## Layout

```
conformance/
├── ADAPTER.md                  the ~50-line adapter contract (start here to port)
├── manifest.json               traceability: fixture → SEP requirement   [generated]
├── schema/fixture.schema.json  JSON Schema for the fixture format
├── fixtures/
│   ├── protocol/               exact wire agreement: list / invoke / chain [generated]
│   └── behavior/               security behaviors incl. relaybleed-*      [generated]
└── src/
    ├── fixture-types.ts        the fixture format, typed
    ├── catalog.ts              THE ORACLE: every scenario as typed literal data
    ├── generate.ts             catalog → JSON fixtures + manifest
    ├── runner.ts               replays fixtures against an adapter, emits compliance %
    ├── reference-adapter.ts    TypeScript SDK adapter (the porting template)
    └── conformance.test.ts     meta-tests + full suite vs the reference SDK
```

## The two fixture kinds

- **`protocol/`** - exact `interceptors/list`, `interceptor/invoke`, and chain
  semantics (trust-boundary ordering, enforce vs audit, fail-open vs
  fail-closed, unknown-name errors). Expectations are exact SEP wire JSON with
  a precomputed RFC 8785 (JCS) canonical string, so comparison in any language
  is: scrub volatile keys → canonicalize → compare one string.
- **`behavior/`** - flows a conformant **security interceptor** MUST handle,
  stated declaratively (the catalog is the oracle; no reference implementation
  output is trusted). The canonical `relaybleed-*` fixtures certify denial of
  the composed read-then-send cross-server exfiltration; sibling fixtures pin
  the false-positive boundary (same-origin writeback, no-prior-read causality,
  session isolation) and outbound redaction.

## Running

```sh
npm install
npm run generate    # regenerate fixtures/ + manifest.json from the catalog
npm test            # meta-tests + the full suite against the TypeScript reference SDK
```

The meta-tests enforce: fixture count == catalog size, deterministic
generation, on-disk JSON byte-identical to the catalog (stale or hand-edited
fixtures fail), every fixture requirement-tagged, and 100% compliance for the
reference SDK.

## Certifying another SDK

1. Implement the adapter contract in your language (see ADAPTER.md; the
   reference adapter is the template - the language-specific surface is one
   behavior table plus four calls).
2. Replay every fixture under `fixtures/` with the documented algorithm.
3. Report `passed / total` as the compliance percentage, grouped by the
   `requirement` tags in `manifest.json`.

Fixtures are versioned artifacts: pin a suite version, and regenerate only via
`npm run generate`.
