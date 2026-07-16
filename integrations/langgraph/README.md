# @formalcore/mcp-interceptors-langgraph

A one-line LangGraph binding for the MCP interceptors reference security stack
(SEP-2624): causal cross-boundary exfiltration blocking, outbound secret
redaction, and Ed25519-attested, offline-verifiable decision receipts.

Why: an LLM tool loop that reads a secret from one server and then calls a tool
on another server can exfiltrate that secret even though every single call is
individually authorized. This binds the reference cross-boundary guard and
secretless redactor to LangGraph so that flow is blocked (or defused) and every
decision produces a signed receipt an auditor can verify later, offline, with no
access to your systems.

## The one-liner

Drop-in tools node:

```ts
import { InterceptingToolNode, generateSigningKeyPair } from "@formalcore/mcp-interceptors-langgraph";

const graph = new StateGraph(MessagesAnnotation)
  .addNode("agent", callModel)
  .addNode("tools", new InterceptingToolNode(tools, { issuerKeyPair: await generateSigningKeyPair() }))
  .addEdge("tools", "agent");
```

Or wrap the tools in place (for `createReactAgent` or any existing `ToolNode`):

```ts
import { withInterceptors, generateSigningKeyPair } from "@formalcore/mcp-interceptors-langgraph";

const safeTools = withInterceptors(tools, { issuerKeyPair: await generateSigningKeyPair() });
```

That is the whole change. Cross-server secret flows are denied (or, with
redaction on by default, defused to an opaque handle), and each tool call yields
a signed receipt.

## Options

```ts
{
  issuerKeyPair,        // Ed25519 signer for receipts (required; attestation is the point)
  onReceipt?: (r) => …, // called once per tool call, on allow and on deny
  mode?: "enforce" | "audit",  // default "enforce"; "audit" observes without blocking or changing
  redact?: boolean,     // default true; false makes a residual cross-boundary secret a hard deny
}
```

With `redact: true` (default), an outbound secret is replaced by an opaque
handle before it leaves, so the write succeeds safely. With `redact: false`, the
guard denies the cross-boundary write outright. Either way, the verbatim secret
never crosses the boundary.

## Verify a receipt offline

```ts
import { verifyReceipt } from "@formalcore/mcp-interceptors-langgraph";

const v = await verifyReceipt(receipt, pinnedIssuerPublicKeyBase64);
// v.ok === true against the real issuer key; { ok: false, reason } against any other.
```

## Demo (no network, no API key, no LLM)

```bash
node --experimental-strip-types examples/relaybleed-denied.ts
```

It runs three passes over a scripted read-then-send sequence: baseline (secret
reaches the sink), guard-only (the cross-server write is DENIED and the receipt
is verified offline), and guard-plus-redactor (the write is allowed but the sink
receives a handle, secret defused).

## What it binds (it does not reimplement)

- `cross-boundary-guard` and `secretless-redactor` reference interceptors and
  the SEP-2624 chain executor from `@ext-modelcontextprotocol/interceptors`.
- The Ed25519 attest/verify and RFC 8785 canonicalization from
  `@formalcore/mcp-attested-validation`.

Session isolation is by `config.configurable.thread_id`, so concurrent graph
threads never share taint.
