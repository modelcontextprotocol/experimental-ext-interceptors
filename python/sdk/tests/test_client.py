# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Tests for the invoker-side helpers."""

import pytest
from mcp.client.client import Client
from mcp.server import MCPServer

from mcp_ext_interceptors.client import invoke_interceptor, list_interceptors, read_interceptors_capability
from mcp_ext_interceptors.interceptor import Invocation
from mcp_ext_interceptors.server import Interceptors
from mcp_ext_interceptors.types import EXTENSION_ID, InterceptorsCapability, ValidationResult

pytestmark = pytest.mark.anyio


def build_extension() -> Interceptors:
    interceptors = Interceptors()

    @interceptors.validator("ok", events=["tools/call"], phase="request")
    async def ok(inv: Invocation) -> ValidationResult:
        return ValidationResult(valid=True)

    return interceptors


async def test_helpers_accept_session_target() -> None:
    server = MCPServer("t", extensions=[build_extension()])
    async with Client(server) as client:
        listed = await list_interceptors(client.session)
        assert [i.name for i in listed.interceptors] == ["ok"]
        result = await invoke_interceptor(client.session, name="ok", event="tools/call", phase="request", payload=None)
        assert isinstance(result, ValidationResult)


class TestReadCapability:
    def test_absent_extensions(self) -> None:
        class Caps:
            extensions = None

        assert read_interceptors_capability(Caps()) is None
        assert read_interceptors_capability(object()) is None

    def test_present(self) -> None:
        class Caps:
            extensions = {EXTENSION_ID: {"supportedEvents": ["tools/call"]}}

        cap = read_interceptors_capability(Caps())
        assert cap == InterceptorsCapability(supported_events=["tools/call"])

    def test_other_extensions_only(self) -> None:
        class Caps:
            extensions = {"io.example/other": {}}

        assert read_interceptors_capability(Caps()) is None
