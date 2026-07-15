# Python conformance adapter

Certifies a Python SEP-2624 interceptors implementation against the SHARED
language-neutral fixtures in `conformance/fixtures/` — the same JSON bytes the
TypeScript reference adapter replays, consumed unchanged. The fixtures are the
oracle: no expectation logic is ported from TypeScript; this package only
implements the ADAPTER.md contract (`createSession` / `list` / `invoke` /
`chain`), the eight-entry behavior vocabulary, and the comparison model
(scrub volatile keys → JCS-canonicalize → string-compare).

## What is bound

- `list` / `invoke` / `chain` bind to the **WG `feature/python-sdk`**
  implementation (`mcp_ext_interceptors`), vendored read-only at a pinned
  commit by `scripts/vendor.sh` into `_vendor/` (gitignored, never modified,
  never committed). Sessions run over a real in-memory MCP client/server
  connection — a fixture pass certifies the wire round-trip.
- The `cross-boundary-guard` / `secretless-redactor` behaviors bind to the
  **Python port of the open reference security interceptors**
  (`src/conformance_adapter/security/`), behavior-equivalent to
  `typescript/sdk/src/samples/security/`: same seven public credential
  formats, causal cross-boundary taint per session, deterministic FNV-1a
  handle redaction (pinned byte-equal to the TS handles).

## Run it

```bash
./scripts/vendor.sh        # vendor the pinned WG SDK (requires the fetched ref)
uv sync --dev
uv run python -m conformance_adapter                      # conformant posture
uv run python -m conformance_adapter --adapter raw        # WG SDK defaults
uv run python -m conformance_adapter --adapter strawman   # permissive strawman
uv run pytest
```

Output shape matches the TypeScript reference runner:
`N/M fixtures passed — compliance P%`.

## Adapter postures

| adapter | scores | meaning |
| --- | --- | --- |
| `conformant` (`python-wg-sdk`) | 100% | WG SDK + documented SEP bridges + Python security interceptors |
| `raw` (`python-wg-sdk-raw`) | <100% | WG SDK out-of-the-box; its failures ARE the interop findings |
| `strawman` (`permissive-strawman`) | 0% on `behavior/` | security behaviors unbound; proves the fixtures reject a permissive implementation |

## Interop findings (why `raw` fails)

Divergences between `feature/python-sdk` and the SEP-2624 wire shape the
fixtures pin, each bridged in exactly one marked place in
`src/conformance_adapter/adapter.py` and pinned executable in
`tests/test_conformance.py`:

1. **Mode vocabulary** — the WG SDK spells the enforcing mode `active`; the
   SEP (as amended) spells it `enforce`.
2. **Default emission** — the WG SDK serializes `mode` and `failOpen` on
   every `interceptors/list` descriptor; the SEP wire shape omits defaults.
3. **Chain direction** — the WG `Chain` derives request→receiving (validate
   before mutate) like the Go SDK's server posture; the SEP client-side
   trust-boundary order is request→sending (mutate before validate), which is
   what the fixtures pin. Bridged by passing `direction` explicitly.

These are exactly the cross-language interop bugs the conformance suite
exists to catch.

## JCS parity

`src/conformance_adapter/canonical.py` is pinned byte-identical to
`typescript/attested-validation/src/canonicalize.ts` by golden tests
(`tests/test_canonical_parity.py`) whose expected strings were produced by
executing the TS canonicalizer, covering UTF-16 key order, control-character
escapes, lone surrogates, astral keys, and integral floats.
