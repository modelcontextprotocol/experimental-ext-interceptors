# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by a Apache-2.0
# license that can be found in the LICENSE file.

"""Integration tests: Interceptors extension hosted on an in-memory MCP server."""

from typing import Any

import anyio
import pytest
from mcp.client.client import Client
from mcp.server import MCPServer
from mcp.server.lowlevel.server import Server
from mcp.shared.exceptions import MCPError
from pydantic import TypeAdapter

from mcp_ext_interceptors.client import invoke_interceptor, list_interceptors, read_interceptors_capability
from mcp_ext_interceptors.interceptor import Invocation
from mcp_ext_interceptors.server import Interceptors
from mcp_ext_interceptors.types import (
    EXTENSION_ID,
    INTERCEPTOR_MUTATION_FAILED,
    INTERCEPTOR_TIMEOUT,
    INTERCEPTOR_VALIDATION_FAILED,
    METHOD_INVOKE,
    Hook,
    InvokeInterceptorParams,
    MutationResult,
    ValidationMessage,
    ValidationResult,
)

pytestmark = pytest.mark.anyio


def build_extension() -> Interceptors:
    interceptors = Interceptors()

    @interceptors.validator("param-check", events=["tools/call"], phase="request", version="1.0.0")
    async def param_check(inv: Invocation) -> ValidationResult:
        bad = isinstance(inv.payload, dict) and bool(inv.payload.get("bad"))
        if bad:
            return ValidationResult(
                valid=False,
                severity="error",
                messages=[ValidationMessage(message="payload flagged as bad", severity="error", path="bad")],
            )
        return ValidationResult(valid=True)

    @interceptors.mutator("redactor", events=["tools/call", "llm/completion"], phase="request", priority_hint=-1000)
    async def redact(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload={"redacted": True}, info={"config": inv.config})

    @interceptors.validator(
        "audit-log",
        hooks=[Hook(events=["*"], phase="request"), Hook(events=["*"], phase="response")],
        mode="audit",
        fail_open=True,
    )
    async def audit(inv: Invocation) -> ValidationResult:
        return ValidationResult(valid=True, severity="info")

    @interceptors.validator("slow", events=["tools/call"], phase="request")
    async def slow(inv: Invocation) -> ValidationResult:
        await anyio.sleep(5)
        return ValidationResult(valid=True)

    @interceptors.validator("crasher", events=["tools/call"], phase="request")
    async def crash(inv: Invocation) -> ValidationResult:
        raise RuntimeError("boom")

    @interceptors.validator("wrong-type", events=["tools/call"], phase="request")
    async def wrong_type(inv: Invocation) -> ValidationResult:
        return MutationResult(modified=False, payload=None)  # type: ignore[return-value]

    return interceptors


@pytest.fixture
def server() -> MCPServer:
    return MCPServer("interceptors-test", extensions=[build_extension()])


ALL_NAMES = {"param-check", "redactor", "audit-log", "slow", "crasher", "wrong-type"}


class TestList:
    async def test_lists_all(self, server: MCPServer) -> None:
        async with Client(server) as client:
            result = await list_interceptors(client)
        assert {i.name for i in result.interceptors} == ALL_NAMES

    async def test_event_filter(self, server: MCPServer) -> None:
        async with Client(server) as client:
            result = await list_interceptors(client, event="llm/completion")
        # audit-log matches via its "*" wildcard hook.
        assert {i.name for i in result.interceptors} == {"redactor", "audit-log"}

    async def test_descriptor_fields_survive_wire(self, server: MCPServer) -> None:
        async with Client(server) as client:
            result = await list_interceptors(client, event="llm/completion")
        redactor = next(i for i in result.interceptors if i.name == "redactor")
        assert redactor.type == "mutation"
        assert redactor.priority_hint == -1000
        assert redactor.hooks == [Hook(events=["tools/call", "llm/completion"], phase="request")]


class TestInvoke:
    async def test_validator_pass(self, server: MCPServer) -> None:
        async with Client(server) as client:
            result = await invoke_interceptor(
                client, name="param-check", event="tools/call", phase="request", payload={"ok": 1}
            )
        assert isinstance(result, ValidationResult)
        assert result.valid is True
        assert result.interceptor == "param-check"
        assert result.phase == "request"
        assert result.duration_ms is not None

    async def test_validator_fail(self, server: MCPServer) -> None:
        async with Client(server) as client:
            result = await invoke_interceptor(
                client, name="param-check", event="tools/call", phase="request", payload={"bad": True}
            )
        assert isinstance(result, ValidationResult)
        assert result.valid is False
        assert result.severity == "error"
        assert result.messages is not None and result.messages[0].path == "bad"

    async def test_mutator_receives_config(self, server: MCPServer) -> None:
        async with Client(server) as client:
            result = await invoke_interceptor(
                client,
                name="redactor",
                event="tools/call",
                phase="request",
                payload={"secret": "x"},
                config={"level": "high"},
            )
        assert isinstance(result, MutationResult)
        assert result.modified is True
        assert result.payload == {"redacted": True}
        assert result.info == {"config": {"level": "high"}}

    async def test_wire_shape_is_flat(self, server: MCPServer) -> None:
        """The invoke result body is the flat SEP shape, not nested under a type key."""
        from mcp_ext_interceptors.client import _InvokeInterceptorRequest

        async with Client(server) as client:
            raw = await client.session.send_request(
                _InvokeInterceptorRequest(
                    params=InvokeInterceptorParams(
                        name="param-check", event="tools/call", phase="request", payload={"bad": True}
                    )
                ),
                TypeAdapter(dict[str, Any]),
            )
        assert raw["type"] == "validation"
        assert raw["valid"] is False
        assert raw["interceptor"] == "param-check"
        assert raw["phase"] == "request"
        assert "durationMs" in raw
        assert "validation" not in raw and "mutation" not in raw

    async def test_unknown_name(self, server: MCPServer) -> None:
        async with Client(server) as client:
            with pytest.raises(MCPError) as exc_info:
                await invoke_interceptor(client, name="nope", event="tools/call", phase="request", payload={})
        assert exc_info.value.code == INTERCEPTOR_VALIDATION_FAILED

    async def test_hook_mismatch(self, server: MCPServer) -> None:
        async with Client(server) as client:
            with pytest.raises(MCPError) as exc_info:
                await invoke_interceptor(
                    client, name="param-check", event="resources/read", phase="request", payload={}
                )
        assert exc_info.value.code == INTERCEPTOR_VALIDATION_FAILED

    async def test_phase_mismatch(self, server: MCPServer) -> None:
        async with Client(server) as client:
            with pytest.raises(MCPError) as exc_info:
                await invoke_interceptor(client, name="param-check", event="tools/call", phase="response", payload={})
        assert exc_info.value.code == INTERCEPTOR_VALIDATION_FAILED

    async def test_handler_crash(self, server: MCPServer) -> None:
        async with Client(server) as client:
            with pytest.raises(MCPError) as exc_info:
                await invoke_interceptor(client, name="crasher", event="tools/call", phase="request", payload={})
        assert exc_info.value.code == INTERCEPTOR_MUTATION_FAILED

    async def test_timeout(self, server: MCPServer) -> None:
        async with Client(server) as client:
            with pytest.raises(MCPError) as exc_info:
                await invoke_interceptor(
                    client, name="slow", event="tools/call", phase="request", payload={}, timeout_ms=50
                )
        assert exc_info.value.code == INTERCEPTOR_TIMEOUT

    async def test_wrong_result_type(self, server: MCPServer) -> None:
        async with Client(server) as client:
            with pytest.raises(MCPError) as exc_info:
                await invoke_interceptor(client, name="wrong-type", event="tools/call", phase="request", payload={})
        assert exc_info.value.code == INTERCEPTOR_MUTATION_FAILED


class TestCapability:
    async def test_advertised_on_modern_discover(self, server: MCPServer) -> None:
        async with Client(server, mode="auto") as client:
            cap = read_interceptors_capability(client.server_capabilities)
        assert cap is not None
        assert cap.supported_events == ["*", "llm/completion", "tools/call"]

    async def test_dropped_on_legacy_handshake_but_methods_work(self, server: MCPServer) -> None:
        async with Client(server, mode="legacy") as client:
            assert client.protocol_version == "2025-11-25"
            assert read_interceptors_capability(client.server_capabilities) is None
            result = await list_interceptors(client)
            assert {i.name for i in result.interceptors} == ALL_NAMES
            invoked = await invoke_interceptor(
                client, name="param-check", event="tools/call", phase="request", payload={"ok": 1}
            )
            assert isinstance(invoked, ValidationResult) and invoked.valid

    @pytest.mark.parametrize("mode", ["legacy", "auto", "2026-07-28"])
    async def test_methods_work_on_every_mode(self, server: MCPServer, mode: str) -> None:
        async with Client(server, mode=mode) as client:  # type: ignore[arg-type]
            result = await invoke_interceptor(
                client, name="redactor", event="tools/call", phase="request", payload={"a": 1}
            )
        assert isinstance(result, MutationResult)
        assert result.payload == {"redacted": True}


class TestRegistration:
    def test_duplicate_name_rejected(self) -> None:
        interceptors = build_extension()
        with pytest.raises(ValueError, match="already registered"):

            @interceptors.validator("param-check", events=["tools/call"], phase="request")
            async def dup(inv: Invocation) -> ValidationResult:
                return ValidationResult(valid=True)

    def test_settings_shape(self) -> None:
        assert build_extension().settings() == {"supportedEvents": ["*", "llm/completion", "tools/call"]}

    async def test_install_lowlevel(self) -> None:
        lowlevel = Server("lowlevel-test")
        extension = build_extension()
        extension.install_lowlevel(lowlevel)
        assert lowlevel.extensions[EXTENSION_ID] == extension.settings()
        async with Client(lowlevel) as client:
            result = await list_interceptors(client)
            assert {i.name for i in result.interceptors} == ALL_NAMES

    def test_invoke_method_is_singular(self) -> None:
        # Guard against the easy mistake: list is plural, invoke is singular.
        methods = [binding.method for binding in build_extension().methods()]
        assert methods == ["interceptors/list", METHOD_INVOKE]
        assert METHOD_INVOKE == "interceptor/invoke"
