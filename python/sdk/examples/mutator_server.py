# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by a Apache-2.0
# license that can be found in the LICENSE file.

"""A server hosting a PII-redacting mutation interceptor with phase-specific priority.

The mutator redacts email addresses and SSNs: early when sending (before other
transforms), late when receiving. Run with: `uv run python examples/mutator_server.py`
"""

import json
import re

import anyio
from mcp.client.client import Client
from mcp.server import MCPServer

from mcp_ext_interceptors import (
    Chain,
    ChainExecutionParams,
    Hook,
    Interceptors,
    Invocation,
    MutationResult,
    PhasePriority,
)

EMAIL = re.compile(r"[\w.+-]+@[\w-]+\.[\w.]+")
SSN = re.compile(r"\b\d{3}-\d{2}-\d{4}\b")

interceptors = Interceptors()


@interceptors.mutator(
    "pii-redactor",
    hooks=[Hook(events=["tools/call"], phase="request"), Hook(events=["tools/call"], phase="response")],
    version="1.0.0",
    description="Redacts email addresses and SSNs from tool call payloads",
    priority_hint=PhasePriority(request=-50000, response=50000),
)
async def redact_pii(inv: Invocation) -> MutationResult:
    text = json.dumps(inv.payload)
    redacted = EMAIL.sub("[EMAIL]", text)
    redacted = SSN.sub("[SSN]", redacted)
    if redacted == text:
        return MutationResult(modified=False, payload=inv.payload)
    return MutationResult(
        modified=True,
        payload=json.loads(redacted),
        info={"redactions": len(EMAIL.findall(text)) + len(SSN.findall(text))},
    )


async def demo() -> None:
    server = MCPServer("mutator-example", extensions=[interceptors])
    async with Client(server) as client:
        chain = Chain()
        await chain.add_server(client)

        payload = {"query": "look up john.doe@example.com with SSN 123-45-6789"}
        outcome = await chain.execute(
            ChainExecutionParams(event="tools/call", phase="request", payload=payload, direction="sending")
        )
        print(f"status:  {outcome.status}")
        print(f"before:  {payload}")
        print(f"after:   {outcome.final_payload}")
        print(f"results: {[r.model_dump(by_alias=True, exclude_none=True) for r in outcome.results]}")


if __name__ == "__main__":
    anyio.run(demo)
