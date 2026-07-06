# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""A server hosting a validation interceptor, plus a client driving it through a chain.

The validator guards `tools/call` requests and rejects payloads containing
dangerous shell patterns. Run with: `uv run python examples/validator_server.py`
"""

from typing import Any

import anyio
from mcp.client.client import Client
from mcp.server import MCPServer

from mcp_ext_interceptors import (
    Chain,
    ChainExecutionParams,
    Interceptors,
    Invocation,
    ValidationMessage,
    ValidationResult,
    read_interceptors_capability,
)

DANGEROUS_PATTERNS = ["rm -rf", "sudo", "DROP TABLE"]

interceptors = Interceptors()


@interceptors.validator(
    "block-dangerous-input",
    events=["tools/call"],
    phase="request",
    version="1.0.0",
    description="Rejects tool calls whose arguments contain dangerous shell patterns",
)
async def block_dangerous(inv: Invocation) -> ValidationResult:
    text = str(inv.payload)
    found = [pattern for pattern in DANGEROUS_PATTERNS if pattern in text]
    if found:
        return ValidationResult(
            valid=False,
            severity="error",
            messages=[
                ValidationMessage(message=f"dangerous pattern {pattern!r} detected", severity="error")
                for pattern in found
            ],
        )
    return ValidationResult(valid=True)


def build_server() -> MCPServer:
    server = MCPServer("validator-example", extensions=[interceptors])

    @server.tool()
    def echo(text: str) -> str:
        """Echo the input back."""
        return text

    return server


async def demo() -> None:
    async with Client(build_server()) as client:
        capability = read_interceptors_capability(client.server_capabilities)
        print(f"advertised capability: {capability}")

        chain = Chain()
        await chain.add_server(client)

        async def guarded_call(arguments: dict[str, Any]) -> None:
            payload = {"method": "tools/call", "params": {"name": "echo", "arguments": arguments}}
            outcome = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload=payload))
            if outcome.status != "success":
                print(f"BLOCKED: {outcome.aborted_at}")
                return
            result = await client.call_tool("echo", arguments)
            print(f"allowed -> {result.content[0].text}")  # type: ignore[union-attr]

        await guarded_call({"text": "hello world"})
        await guarded_call({"text": "please run rm -rf /"})


if __name__ == "__main__":
    anyio.run(demo)
