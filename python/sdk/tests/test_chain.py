# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by a Apache-2.0
# license that can be found in the LICENSE file.

"""Tests for the chain orchestrator's execution model."""

from typing import Any

import anyio
import pytest
from mcp.client.client import Client
from mcp.server import MCPServer

from mcp_ext_interceptors.chain import (
    Chain,
    ChainEntry,
    ChainExecutionParams,
    ChainExecutionResult,
    InterceptorOverrides,
)
from mcp_ext_interceptors.interceptor import Invocation
from mcp_ext_interceptors.server import Interceptors
from mcp_ext_interceptors.types import (
    Hook,
    InterceptorInfo,
    MutationResult,
    PhasePriority,
    ValidationMessage,
    ValidationResult,
)

pytestmark = pytest.mark.anyio


def appender(events: list[Any], name: str, payload: Any) -> Any:
    """Mutator payload transform that records execution order."""
    events.append(name)
    if isinstance(payload, list):
        return [*payload, name]
    return [name]


async def test_mutator_priority_and_tiebreak_across_servers() -> None:
    """Mutators from two servers merge into one chain sorted by priority, then name."""
    order: list[str] = []

    ext_a = Interceptors()
    ext_b = Interceptors()

    @ext_a.mutator("bravo", events=["tools/call"], phase="request")  # priority 0
    async def bravo(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload=appender(order, "bravo", inv.payload))

    @ext_a.mutator("early", events=["tools/call"], phase="request", priority_hint=-1000)
    async def early(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload=appender(order, "early", inv.payload))

    @ext_b.mutator("alpha", events=["tools/call"], phase="request")  # priority 0, ties with bravo
    async def alpha(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload=appender(order, "alpha", inv.payload))

    @ext_b.mutator("late", events=["tools/call"], phase="request", priority_hint=1000)
    async def late(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload=appender(order, "late", inv.payload))

    async with Client(MCPServer("a", extensions=[ext_a])) as client_a:
        async with Client(MCPServer("b", extensions=[ext_b])) as client_b:
            chain = Chain()
            await chain.add_server(client_a)
            await chain.add_server(client_b)
            result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload=[]))

    assert result.status == "success"
    assert order == ["early", "alpha", "bravo", "late"]
    # The payload threaded through every mutator sequentially, across servers.
    assert result.final_payload == ["early", "alpha", "bravo", "late"]


async def test_phase_specific_priority() -> None:
    order: list[str] = []
    ext = Interceptors()

    @ext.mutator(
        "flipper",
        hooks=[Hook(events=["tools/call"], phase="request"), Hook(events=["tools/call"], phase="response")],
        priority_hint=PhasePriority(request=-1000, response=1000),
    )
    async def flipper(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload=appender(order, "flipper", inv.payload))

    @ext.mutator(
        "middle",
        hooks=[Hook(events=["tools/call"], phase="request"), Hook(events=["tools/call"], phase="response")],
    )
    async def middle(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload=appender(order, "middle", inv.payload))

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload=[]))
        request_order = list(order)
        order.clear()
        await chain.execute(ChainExecutionParams(event="tools/call", phase="response", payload=[]))
        response_order = list(order)

    assert request_order == ["flipper", "middle"]
    assert response_order == ["middle", "flipper"]


async def test_trust_boundary_ordering() -> None:
    """Receiving (request): validators before mutators. Sending (response): mutators first."""
    order: list[str] = []
    ext = Interceptors()

    both = [Hook(events=["*"], phase="request"), Hook(events=["*"], phase="response")]

    @ext.validator("val", hooks=both)
    async def val(inv: Invocation) -> ValidationResult:
        order.append("val")
        return ValidationResult(valid=True)

    @ext.mutator("mut", hooks=both)
    async def mut(inv: Invocation) -> MutationResult:
        order.append("mut")
        return MutationResult(modified=False, payload=inv.payload)

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)

        await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))
        assert order == ["val", "mut"], "request phase defaults to receiving: validate then mutate"

        order.clear()
        await chain.execute(ChainExecutionParams(event="tools/call", phase="response", payload={}))
        assert order == ["mut", "val"], "response phase defaults to sending: mutate then validate"

        order.clear()
        await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}, direction="sending"))
        assert order == ["mut", "val"], "explicit direction overrides the phase-derived default"


async def test_validators_run_in_parallel() -> None:
    """Each validator waits for the other to start; only parallel execution passes."""
    ext = Interceptors()
    started_1 = anyio.Event()
    started_2 = anyio.Event()

    @ext.validator("v1", events=["tools/call"], phase="request")
    async def v1(inv: Invocation) -> ValidationResult:
        started_1.set()
        with anyio.fail_after(2):
            await started_2.wait()
        return ValidationResult(valid=True)

    @ext.validator("v2", events=["tools/call"], phase="request")
    async def v2(inv: Invocation) -> ValidationResult:
        started_2.set()
        with anyio.fail_after(2):
            await started_1.wait()
        return ValidationResult(valid=True)

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))

    assert result.status == "success"
    assert len(result.results) == 2


class TestBlocking:
    @staticmethod
    def build(severity: str | None, *, mode: str = "active") -> Interceptors:
        ext = Interceptors()

        @ext.validator("judge", events=["tools/call"], phase="request", mode=mode)  # type: ignore[arg-type]
        async def judge(inv: Invocation) -> ValidationResult:
            return ValidationResult(
                valid=False,
                severity=severity,  # type: ignore[arg-type]
                messages=[ValidationMessage(message="nope", severity=severity or "error")],  # type: ignore[arg-type]
            )

        @ext.mutator("after", events=["tools/call"], phase="request")
        async def after(inv: Invocation) -> MutationResult:
            return MutationResult(modified=True, payload="mutated")

        return ext

    async def run(self, ext: Interceptors) -> ChainExecutionResult:
        async with Client(MCPServer("s", extensions=[ext])) as client:
            chain = Chain()
            await chain.add_server(client)
            return await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload="original"))

    async def test_error_blocks(self) -> None:
        result = await self.run(self.build("error"))
        assert result.status == "validation_failed"
        assert result.aborted_at is not None
        assert result.aborted_at.interceptor == "judge"
        assert result.aborted_at.reason == "nope"
        assert result.final_payload is None, "mutators must not run after a blocking validation"
        assert result.validation_summary.errors == 1

    @pytest.mark.parametrize("severity", ["warn", "info"])
    async def test_warn_and_info_do_not_block(self, severity: str) -> None:
        result = await self.run(self.build(severity))
        assert result.status == "success"
        assert result.final_payload == "mutated"

    async def test_invalid_without_severity_fails_secure(self) -> None:
        ext = Interceptors()

        @ext.validator("judge", events=["tools/call"], phase="request")
        async def judge(inv: Invocation) -> ValidationResult:
            return ValidationResult(valid=False)

        async with Client(MCPServer("s", extensions=[ext])) as client:
            chain = Chain()
            await chain.add_server(client)
            result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))
        assert result.status == "validation_failed"

    async def test_audit_validator_never_blocks(self) -> None:
        result = await self.run(self.build("error", mode="audit"))
        assert result.status == "success"
        assert result.final_payload == "mutated"
        # Violations are still visible in the summary.
        assert result.validation_summary.errors == 1


async def test_audit_mutator_is_shadow() -> None:
    ext = Interceptors()

    @ext.mutator("shadow", events=["tools/call"], phase="request", mode="audit")
    async def shadow(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload="shadow-payload")

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload="original"))

    assert result.status == "success"
    assert result.final_payload == "original", "audit mutations are computed but never applied"
    assert len(result.results) == 1, "the shadow result is still recorded"


class TestFailOpen:
    @staticmethod
    def build(*, fail_open: bool) -> Interceptors:
        ext = Interceptors()

        @ext.validator("crasher", events=["tools/call"], phase="request", fail_open=fail_open)
        async def crasher(inv: Invocation) -> ValidationResult:
            raise RuntimeError("boom")

        return ext

    async def run(self, ext: Interceptors, **kwargs: Any) -> ChainExecutionResult:
        async with Client(MCPServer("s", extensions=[ext])) as client:
            chain = Chain()
            await chain.add_server(client, **kwargs)
            return await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))

    async def test_fail_closed_blocks(self) -> None:
        result = await self.run(self.build(fail_open=False))
        assert result.status == "validation_failed"
        assert result.aborted_at is not None and result.aborted_at.interceptor == "crasher"

    async def test_fail_open_proceeds(self) -> None:
        result = await self.run(self.build(fail_open=True))
        assert result.status == "success"

    async def test_fail_open_override_unblocks(self) -> None:
        result = await self.run(
            self.build(fail_open=False),
            overrides={"crasher": InterceptorOverrides(fail_open=True)},
        )
        assert result.status == "success"


async def test_mutation_atomicity() -> None:
    """A failing mutator aborts the chain; earlier mutations are not exposed."""
    ext = Interceptors()

    @ext.mutator("first", events=["tools/call"], phase="request", priority_hint=-1)
    async def first(inv: Invocation) -> MutationResult:
        return MutationResult(modified=True, payload="half-done")

    @ext.mutator("second", events=["tools/call"], phase="request")
    async def second(inv: Invocation) -> MutationResult:
        raise RuntimeError("boom")

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload="original"))

    assert result.status == "mutation_failed"
    assert result.aborted_at is not None and result.aborted_at.interceptor == "second"
    assert result.final_payload is None, "all-or-none: partial mutations must not leak"


async def test_unmodified_mutator_keeps_payload() -> None:
    ext = Interceptors()

    @ext.mutator("noop", events=["tools/call"], phase="request")
    async def noop(inv: Invocation) -> MutationResult:
        return MutationResult(modified=False, payload=None)

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload="original"))

    assert result.status == "success"
    assert result.final_payload == "original"


async def test_chain_timeout() -> None:
    ext = Interceptors()

    @ext.mutator("slow", events=["tools/call"], phase="request")
    async def slow(inv: Invocation) -> MutationResult:
        await anyio.sleep(5)
        return MutationResult(modified=False, payload=inv.payload)

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        result = await chain.execute(
            ChainExecutionParams(event="tools/call", phase="request", payload={}, timeout_ms=200)
        )

    assert result.status == "timeout"
    assert result.aborted_at is not None
    assert result.aborted_at.type == "timeout"
    assert result.aborted_at.interceptor == "slow"
    assert result.final_payload is None


async def test_per_entry_timeout_override() -> None:
    ext = Interceptors()

    @ext.validator("slow", events=["tools/call"], phase="request")
    async def slow(inv: Invocation) -> ValidationResult:
        await anyio.sleep(5)
        return ValidationResult(valid=True)

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client, overrides={"slow": InterceptorOverrides(timeout_ms=100)})
        result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))

    assert result.status == "validation_failed"
    assert result.aborted_at is not None and result.aborted_at.interceptor == "slow"


class TestOverrides:
    async def test_priority_override_reorders(self) -> None:
        order: list[str] = []
        ext = Interceptors()

        @ext.mutator("a", events=["tools/call"], phase="request", priority_hint=-100)
        async def a(inv: Invocation) -> MutationResult:
            return MutationResult(modified=True, payload=appender(order, "a", inv.payload))

        @ext.mutator("b", events=["tools/call"], phase="request", priority_hint=100)
        async def b(inv: Invocation) -> MutationResult:
            return MutationResult(modified=True, payload=appender(order, "b", inv.payload))

        async with Client(MCPServer("s", extensions=[ext])) as client:
            chain = Chain()
            await chain.add_server(client, overrides={"b": InterceptorOverrides(priority_hint=-200)})
            await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload=[]))

        assert order == ["b", "a"]

    async def test_mode_override_promotes_audit_to_active(self) -> None:
        ext = Interceptors()

        @ext.validator("auditor", events=["tools/call"], phase="request", mode="audit")
        async def auditor(inv: Invocation) -> ValidationResult:
            return ValidationResult(valid=False, severity="error")

        async with Client(MCPServer("s", extensions=[ext])) as client:
            chain = Chain()
            await chain.add_server(client, overrides={"auditor": InterceptorOverrides(mode="active")})
            result = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))

        assert result.status == "validation_failed"

    async def test_hook_narrowing_filters_entry(self) -> None:
        ext = Interceptors()

        @ext.validator("wide", events=["tools/call", "prompts/get"], phase="request")
        async def wide(inv: Invocation) -> ValidationResult:
            return ValidationResult(valid=False, severity="error")

        narrowed = InterceptorOverrides(hooks=[Hook(events=["prompts/get"], phase="request")])
        async with Client(MCPServer("s", extensions=[ext])) as client:
            chain = Chain()
            await chain.add_server(client, overrides={"wide": narrowed})
            skipped = await chain.execute(ChainExecutionParams(event="tools/call", phase="request", payload={}))
            still_hooked = await chain.execute(ChainExecutionParams(event="prompts/get", phase="request", payload={}))

        assert skipped.status == "success", "narrowed hooks exclude tools/call"
        assert still_hooked.status == "validation_failed"

    async def test_hook_widening_rejected(self) -> None:
        info = InterceptorInfo(name="narrow", type="validation", hooks=[Hook(events=["tools/call"], phase="request")])
        widened = InterceptorOverrides(hooks=[Hook(events=["tools/call", "resources/read"], phase="request")])
        chain = Chain()
        with pytest.raises(ValueError, match="widens"):
            chain.add_entry(ChainEntry(interceptor=info, target=None, overrides=widened))  # type: ignore[arg-type]


async def test_name_filter_and_config_delivery() -> None:
    seen_config: dict[str, Any] = {}
    ext = Interceptors()

    @ext.validator("checked", events=["tools/call"], phase="request")
    async def checked(inv: Invocation) -> ValidationResult:
        seen_config["checked"] = inv.config
        return ValidationResult(valid=True)

    @ext.validator("excluded", events=["tools/call"], phase="request")
    async def excluded(inv: Invocation) -> ValidationResult:
        seen_config["excluded"] = inv.config
        return ValidationResult(valid=True)

    async with Client(MCPServer("s", extensions=[ext])) as client:
        chain = Chain()
        await chain.add_server(client)
        result = await chain.execute(
            ChainExecutionParams(
                event="tools/call",
                phase="request",
                payload={},
                interceptors=["checked"],
                config={"checked": {"strict": True}},
            )
        )

    assert result.status == "success"
    assert len(result.results) == 1
    assert seen_config == {"checked": {"strict": True}}
