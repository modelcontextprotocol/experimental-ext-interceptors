# @formalcore/langgraph-interceptors

A drop-in for LangChain / LangGraph agents. Wrap your tools once and every tool
call runs through the reference MCP interceptors (SEP-2624): outputs carry a
provenance label, cross-boundary secret sends are denied with an
offline-verifiable attested receipt, and an optional redactor defuses a would-be
leak instead of blocking it.

It reuses the reference interceptors unchanged. The security logic lives in
`typescript/sdk/src/samples/security` (the `cross-boundary-guard` validator and
`secretless-redactor` mutator) and `typescript/attested-validation` (Ed25519 over
RFC 8785 JCS); this package only adapts them to the LangChain tool surface.

## The one-liner

```ts
import { createInterceptorShield } from "@formalcore/langgraph-interceptors";
import { createReactAgent } from "@langchain/langgraph/prebuilt";

const shield = await createInterceptorShield({ sessionId: "user-42" });
const agent = createReactAgent({ llm, tools: shield.wrap(myTools) }); // wrap: the whole adoption
```

`shield.wrap(tools)` returns ordinary `StructuredToolInterface`s, so they also
drop straight into a `ToolNode`, a legacy agent executor, or any LangChain
runnable. Nothing else in your graph changes.

## What a wrapped tool emits

Every result carries a typed artifact on `ToolMessage.artifact`, read it with
`readInterceptorArtifact(message)`:

- Allowed: `{ outcome: "allowed", provenance, redacted }` where `provenance` is
  the `mcp-provenance-label` envelope (origin server, whether the output was
  tainted, which secret formats were seen).
- Denied: `{ outcome: "denied", server, whyNot, receipt }` where `receipt` is an
  attested SEP-2624 `ValidationResult`. Verify it offline against the pinned
  issuer key, with no callback to us and no shared state:

```ts
const check = await shield.verifyReceipt(artifact.receipt); // { ok: true } or { ok: false, reason }
```

Set `onDeny: "throw"` to raise `CrossBoundaryDenied` (carrying the same receipt)
instead of returning a denial message.

## The relaybleed demo

```bash
npm install
npm run demo   # hermetic: no API key, no network
```

Three runs, all deterministic:

1. A vanilla LangGraph `ToolNode` over the raw tools ALLOWS a read-then-send
   exfiltration; the secret reaches the sqlite sink.
2. The shielded tools DENY the same flow and hand back a receipt that verifies
   against the pinned key and is correctly rejected against a wrong key; nothing
   reaches the sink.
3. The shielded tools with `redact: true` ALLOW the send but the sink receives an
   opaque handle (`<mcp:secret-ref:...>`), never the verbatim secret.

An optional live model runs behind `LANGGRAPH_DEMO_LIVE=1` (needs
`@langchain/openai` and `OPENAI_API_KEY`); the default path needs neither.

## Behavior is pinned to conformance

The tests pin this integration to the language-neutral conformance behavior
fixtures (`conformance/fixtures/behavior/relaybleed-*`,
`redaction-defuses-relaybleed`, `same-origin-writeback-allowed`,
`no-prior-read-allowed`, `session-isolation`), so it cannot drift from the
certified security semantics.

```bash
npm test
```

## Design

Written in the spirit of the project's FUNCTIONAL_PATTERNS: constants as the
source of their own union types, dispatch tables over switches, `readonly`
data, `null` for absence (never `undefined`), and sanitization only at the
boundary (the tool arguments and the tool output). The security decision is not
re-implemented here; it is the reference chain executor run in SEP-2624
trust-boundary order.

## Dependencies

- Peer: `@langchain/core` (the tool and message types).
- Demo also uses `@langchain/langgraph` (for `ToolNode` / `createReactAgent`).

## License

Apache-2.0.
