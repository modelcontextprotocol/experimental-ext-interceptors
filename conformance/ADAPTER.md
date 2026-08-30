# The Adapter Contract

To run this conformance suite against ANY SEP-2624 interceptors implementation
(Python, C#, Go, TypeScript, …) you implement one small adapter - four
functions - and replay the JSON fixtures under `fixtures/` with the algorithm
below. You do not port the tests; the fixtures ARE the tests.

## What you implement

```
createSession(interceptors) -> session     # fresh state, fixtures never share state
session.list(event | null)  -> wire JSON   # interceptors/list result
session.invoke(params)      -> { ok: true, result: wire JSON }
                             | { ok: false, errorCode: int }   # JSON-RPC error code
session.chain(event, phase, payload, sessionId | null)
                            -> { decision: "allow" | "deny", finalPayload }
```

- `createSession` registers each fixture interceptor: bind its `behavior`
  (table below) and honor `name`, `events`, `phases` (`both` = request AND
  response hooks), `mode` (default `active`), `failOpen` (default `false`).
- `list` returns exactly what your `interceptors/list` puts on the wire.
- `invoke` returns exactly what your `interceptor/invoke` puts on the wire;
  an RPC failure becomes `{ ok: false, errorCode }`.
- `chain` runs ONE lifecycle event through your chain executor over ALL of the
  session's interceptors: `allow` iff the chain completed, `deny` iff it was
  blocked. `finalPayload` is the payload after applied mutations. Pass
  `sessionId` through to interceptor invocation context - the security
  behaviors are stateful per session.

## The behavior vocabulary

| behavior | binds to |
| --- | --- |
| `allow-all` | validator; always valid |
| `deny-all` | validator; always invalid, severity `error`, message exactly `denied by policy` |
| `require-note` | validator; valid iff `arguments.note` is a non-empty string, else error `note is required` |
| `require-uppercase-note` | validator; valid iff `arguments.note` equals its own uppercase, else error `note must be uppercase` |
| `uppercase-note` | mutator; uppercases string `arguments.note` (`modified: true`), else `modified: false` |
| `crash` | handler throws/raises (message free-form); exercises `failOpen` |
| `cross-boundary-guard` | your security VALIDATOR: a verbatim secret read from server A in a prior response must be denied in a later request to server B (per session) |
| `secretless-redactor` | your security MUTATOR: verbatim secrets in outbound requests are replaced (handle format is yours; the fixture only asserts the secret is gone) |

The two security behaviors are how a non-reference security interceptor
certifies against the `behavior/` fixtures (including `relaybleed-*`) without
this suite prescribing its implementation.

## The replay algorithm (what any runner does)

For each fixture JSON:

1. `session = createSession(fixture.interceptors)`.
2. Replay `steps` IN ORDER (state carries across steps within a fixture).
3. Per step, compare:
   - scrub keys in `fixture.volatile` recursively from the ACTUAL result;
   - canonicalize the scrubbed actual with RFC 8785 (JCS: sorted keys, no
     whitespace, minimal escapes);
   - string-compare against the step's precomputed `expect.canonical`.
   - `chain` steps instead check `decision` equality; then, if present:
     `finalPayloadCanonical` (same string comparison), `forbids` (substrings
     that must be ABSENT from the canonical final payload), `requires`
     (substrings that must be PRESENT).
   - `invoke` steps whose `expect` is `{ errorCode }` require the RPC error.
4. A fixture passes iff every step passes. Report
   `passed / total → compliance %`, per `requirement` tag via `manifest.json`.

Fixture files validate against `schema/fixture.schema.json`; `manifest.json`
maps every fixture to the SEP-2624 / security-profile requirement it
certifies. Fixtures are generated from `src/catalog.ts` - regenerate with
`node --experimental-strip-types scripts/generate.ts`; never edit JSON by hand.

The reference TypeScript adapter (`src/reference-adapter.ts`) is the porting
template: the language-specific surface is the behavior table plus the four
session calls.
