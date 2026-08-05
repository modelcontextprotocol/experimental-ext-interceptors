# MCP Interceptors Python SDK

Python implementation of the Model Context Protocol interceptor framework ([SEP-2624](../../docs/sep.md)): validators, mutators, and sinks that govern context operations, hosted on MCP servers and orchestrated through `interceptors/list` and `interceptor/invoke`.

Built on the [MCP Python SDK v2](https://github.com/modelcontextprotocol/python-sdk/releases/tag/v2.0.0), which serves both the 2025-11-25 (handshake) and 2026-07-28 (modern) protocol revisions from one package. The interceptor methods work on every connection mode; see the capability note below for what differs.

## Installation

```bash
pip install mcp-ext-interceptors
```

## Hosting interceptors on a server

`Interceptors` is an mcp v2 `Extension`. Register handlers with decorators, then hand it to `MCPServer`:

```python
from mcp.server import MCPServer
from mcp_ext_interceptors import Interceptors, Invocation, MutationResult, SinkResult, ValidationResult

interceptors = Interceptors()

@interceptors.validator("block-dangerous", events=["tools/call"], phase="request")
async def check(inv: Invocation) -> ValidationResult:
    return ValidationResult(valid="rm -rf" not in str(inv.payload))

@interceptors.mutator("pii-redactor", events=["tools/call"], phase="request", priority_hint=-1000)
async def redact(inv: Invocation) -> MutationResult:
    return MutationResult(modified=True, payload=redact_pii(inv.payload))

@interceptors.sink("audit-log", events=["tools/call"], phase="response")  # fire-and-forget, observe-only
async def record(inv: Invocation) -> SinkResult:
    log_to_bus(inv.payload)
    return SinkResult(recorded=True)

server = MCPServer("demo", extensions=[interceptors])
```

Register every interceptor before constructing the `MCPServer`: the capability settings (`supportedEvents`) are snapshotted at construction. Users of the lowlevel `Server` can call `interceptors.install_lowlevel(server)` instead.

## Invoking interceptors from a client

```python
from mcp.client.client import Client
from mcp_ext_interceptors import invoke_interceptor, list_interceptors

async with Client(server_or_url) as client:
    listed = await list_interceptors(client, event="tools/call")
    result = await invoke_interceptor(
        client, name="block-dangerous", event="tools/call", phase="request", payload=payload
    )
```

## Chain execution

`Chain` implements the SEP's orchestration convenience: discover across one or more servers, merge and sort by priority, and execute with the trust-boundary-aware model (sending: mutate sequentially, then validate in parallel; receiving: validate, then mutate). Only `severity: "error"` blocks, audit-mode interceptors never block, and mutations are atomic (all-or-none).

```python
from mcp_ext_interceptors import Chain, ChainExecutionParams, InterceptorOverrides

chain = Chain()
await chain.add_server(client_a)
await chain.add_server(client_b, overrides={"noisy-validator": InterceptorOverrides(mode="audit")})

outcome = await chain.execute(
    ChainExecutionParams(event="tools/call", phase="request", payload=payload, direction="sending")
)
if outcome.status == "success":
    payload = outcome.final_payload
```

`InterceptorOverrides` lets the invoker adjust `failOpen`, `priorityHint`, `mode`, and `timeoutMs`, and narrow (never widen) the hooks of a discovered interceptor without touching the server-side declaration.

`direction` tells the chain which side of the trust boundary it guards; when omitted it is derived from the phase (request → receiving, response → sending).

## Capability advertisement

Servers advertise `capabilities.extensions["io.modelcontextprotocol/interceptors"] = {"supportedEvents": [...]}` (SEP-2133 extensions format). On legacy-handshake connections (2025-11-25 and older) the wire schema has no `extensions` field, so the advertisement is silently dropped even though `interceptors/list` and `interceptor/invoke` keep working — treat an absent capability as "unknown" and probe `interceptors/list`. `read_interceptors_capability(client.server_capabilities)` handles both cases.

## Examples

- [`examples/validator_server.py`](examples/validator_server.py) — a guardrail validator blocking dangerous tool calls, driven through a chain.
- [`examples/mutator_server.py`](examples/mutator_server.py) — a PII redactor with phase-specific priorities.

## Development

```bash
uv sync          # creates .venv with mcp v2
uv run pytest    # exercises both protocol eras over in-memory transport
uv run ruff check && uv run mypy src
```

See [docs/CONFORMANCE.md](docs/CONFORMANCE.md) for the SEP-clause-to-code mapping and deliberate divergences from the other SDKs in this repo.

## License

Apache License 2.0 - See LICENSE file in the root directory for details.
