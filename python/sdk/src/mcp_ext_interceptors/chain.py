# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Chain orchestrator: the SDK-side convenience utility from SEP-2624.

Discovers interceptors from one or more MCP servers, merges them into a single
chain, and executes them with the SEP's trust-boundary-aware execution model:

- Sending:   Mutate (sequential, by priority) -> Validate (parallel) -> Send
- Receiving: Validate (parallel) -> Mutate (sequential, by priority) -> Process

Per-entry invoker policy (`InterceptorOverrides`) can adjust `failOpen`,
`priorityHint`, `mode`, `timeoutMs`, and narrow (never widen) `hooks` without
touching the server-declared descriptor.
"""

from __future__ import annotations

import logging
import time
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass, field
from typing import Any, Literal

import anyio

from mcp_ext_interceptors.client import InvokerTarget, invoke_interceptor, list_interceptors
from mcp_ext_interceptors.types import (
    TYPE_MUTATION,
    TYPE_VALIDATION,
    AnyInvokeResult,
    Hook,
    InterceptorInfo,
    InterceptorModel,
    InvocationContext,
    InvokeInterceptorParams,
    Mode,
    MutationResult,
    Phase,
    PriorityHint,
    ValidationResult,
    is_hook_subset,
    matches_hooks,
    resolve_priority,
)

logger = logging.getLogger(__name__)

Direction = Literal["sending", "receiving"]
ChainStatus = Literal["success", "validation_failed", "mutation_failed", "timeout"]


class InterceptorOverrides(InterceptorModel):
    """Invoker-side policy overrides for one chain entry.

    Overrides take precedence over the server-declared descriptor. `hooks` may
    only narrow the declared hooks; widening is rejected when the entry is added.
    """

    fail_open: bool | None = None
    priority_hint: PriorityHint | None = None
    mode: Mode | None = None
    timeout_ms: float | None = None
    hooks: list[Hook] | None = None


@dataclass(kw_only=True)
class ChainEntry:
    """One interceptor in the chain and the server connection that hosts it."""

    interceptor: InterceptorInfo
    target: InvokerTarget
    overrides: InterceptorOverrides | None = None

    def effective_mode(self) -> Mode:
        if self.overrides is not None and self.overrides.mode is not None:
            return self.overrides.mode
        return self.interceptor.mode

    def effective_fail_open(self) -> bool:
        if self.overrides is not None and self.overrides.fail_open is not None:
            return self.overrides.fail_open
        return self.interceptor.fail_open

    def effective_priority(self, phase: Phase) -> int:
        if self.overrides is not None and self.overrides.priority_hint is not None:
            return resolve_priority(self.overrides.priority_hint, phase)
        return resolve_priority(self.interceptor.priority_hint, phase)

    def effective_hooks(self) -> list[Hook]:
        if self.overrides is not None and self.overrides.hooks is not None:
            return self.overrides.hooks
        return self.interceptor.hooks

    def effective_timeout_ms(self) -> float | None:
        if self.overrides is not None and self.overrides.timeout_ms is not None:
            return self.overrides.timeout_ms
        return None


@dataclass(kw_only=True)
class ChainExecutionParams:
    """Parameters for one chain execution."""

    event: str
    phase: Phase
    payload: Any
    interceptors: list[str] | None = None
    config: dict[str, dict[str, Any]] | None = None
    timeout_ms: float | None = None
    context: InvocationContext | None = None
    direction: Direction | None = None
    """Which side of the trust boundary this chain guards.

    The SEP orders execution by data-flow direction. When omitted it is
    derived from the phase the way the Go SDK does: request -> receiving,
    response -> sending. An invoker sitting on the sending side of a request
    (e.g. a client about to send tools/call) should pass "sending" explicitly.
    """


class ValidationSummary(InterceptorModel):
    errors: int = 0
    warnings: int = 0
    infos: int = 0


class AbortInfo(InterceptorModel):
    interceptor: str
    reason: str
    type: Literal["validation", "mutation", "timeout"]


@dataclass(kw_only=True)
class ChainExecutionResult:
    """Aggregated outcome of one chain execution.

    `final_payload` is only set when `status` is "success"; on any abort the
    caller's original payload stands (mutations are atomic, all-or-none).
    `results` contains the results actually returned by interceptors; entries
    that crashed or timed out appear in `aborted_at`/logs instead.
    """

    status: ChainStatus
    event: str
    phase: Phase
    results: list[AnyInvokeResult] = field(default_factory=list)
    final_payload: Any = None
    validation_summary: ValidationSummary = field(default_factory=ValidationSummary)
    total_duration_ms: float = 0.0
    aborted_at: AbortInfo | None = None


_Sender = Callable[["ChainEntry", InvokeInterceptorParams], Awaitable[AnyInvokeResult]]

ExecutionHandler = Callable[["ChainEntry", InvokeInterceptorParams, _Sender], Awaitable[AnyInvokeResult]]
"""Wraps every `interceptor/invoke` RPC the chain makes (logging, overrides, stubs in tests)."""


class Chain:
    """Merges interceptors from multiple servers and executes them per the SEP."""

    def __init__(self, *, execution_handler: ExecutionHandler | None = None) -> None:
        self.entries: list[ChainEntry] = []
        self._execution_handler = execution_handler

    async def add_server(
        self,
        target: InvokerTarget,
        *,
        overrides: Mapping[str, InterceptorOverrides] | None = None,
    ) -> list[ChainEntry]:
        """Discover interceptors on `target` via `interceptors/list` and add them."""
        listed = await list_interceptors(target)
        added: list[ChainEntry] = []
        for info in listed.interceptors:
            entry = ChainEntry(
                interceptor=info,
                target=target,
                overrides=overrides.get(info.name) if overrides else None,
            )
            self.add_entry(entry)
            added.append(entry)
        return added

    def add_entry(self, entry: ChainEntry) -> None:
        """Add one entry, validating that hook overrides only narrow."""
        overrides = entry.overrides
        if overrides is not None and overrides.hooks is not None:
            if not is_hook_subset(overrides.hooks, entry.interceptor.hooks):
                raise ValueError(
                    f"hook override for {entry.interceptor.name!r} widens the declared hooks; overrides may only narrow"
                )
        self.entries.append(entry)

    async def execute(self, params: ChainExecutionParams) -> ChainExecutionResult:
        """Execute the chain for one event/phase, enforcing the SEP execution model."""
        start = time.monotonic()
        result = ChainExecutionResult(status="success", event=params.event, phase=params.phase)

        selected = [
            entry
            for entry in self.entries
            if (params.interceptors is None or entry.interceptor.name in params.interceptors)
            and matches_hooks(entry.effective_hooks(), params.event, params.phase)
        ]
        validators = [e for e in selected if e.interceptor.type == TYPE_VALIDATION]
        mutators = sorted(
            (e for e in selected if e.interceptor.type == TYPE_MUTATION),
            key=lambda e: (e.effective_priority(params.phase), e.interceptor.name),
        )
        for entry in selected:
            if entry.interceptor.type not in (TYPE_VALIDATION, TYPE_MUTATION):
                logger.warning(
                    "skipping interceptor %r with unknown type %r", entry.interceptor.name, entry.interceptor.type
                )

        direction: Direction = params.direction or ("receiving" if params.phase == "request" else "sending")
        state = _ExecutionState(payload=params.payload)

        async def body() -> None:
            if direction == "receiving":
                await self._run_validators(validators, params, state, result)
                if result.aborted_at is None:
                    await self._run_mutators(mutators, params, state, result)
            else:
                await self._run_mutators(mutators, params, state, result)
                if result.aborted_at is None:
                    await self._run_validators(validators, params, state, result)

        if params.timeout_ms is not None:
            with anyio.move_on_after(params.timeout_ms / 1000) as scope:
                await body()
            if scope.cancelled_caught:
                result.status = "timeout"
                result.aborted_at = AbortInfo(
                    interceptor=state.in_flight or "",
                    reason=f"chain timeout of {params.timeout_ms}ms exceeded",
                    type="timeout",
                )
        else:
            await body()

        if result.status == "success" and result.aborted_at is None:
            result.final_payload = state.payload
        result.total_duration_ms = (time.monotonic() - start) * 1000
        return result

    # -- Stages --------------------------------------------------------------

    async def _run_validators(
        self,
        validators: list[ChainEntry],
        params: ChainExecutionParams,
        state: _ExecutionState,
        result: ChainExecutionResult,
    ) -> None:
        if not validators:
            return
        outcomes: list[tuple[AnyInvokeResult | None, Exception | None]] = [(None, None)] * len(validators)

        async def invoke_one(index: int, entry: ChainEntry) -> None:
            try:
                outcomes[index] = (await self._invoke(entry, params, state.payload), None)
            except Exception as exc:  # recorded per fail-open policy; cancellation propagates
                outcomes[index] = (None, exc)

        async with anyio.create_task_group() as tg:
            for index, entry in enumerate(validators):
                tg.start_soon(invoke_one, index, entry)

        # Record in entry order so aborts are deterministic.
        for entry, (invoke_result, error) in zip(validators, outcomes, strict=True):
            audit = entry.effective_mode() == "audit"
            if error is not None:
                if audit or entry.effective_fail_open():
                    logger.warning("validator %r failed open: %s", entry.interceptor.name, error)
                    continue
                self._abort(result, entry, f"validator failed: {error}", "validation", "validation_failed")
                continue
            if not isinstance(invoke_result, ValidationResult):
                if audit or entry.effective_fail_open():
                    logger.warning("validator %r returned a non-validation result; ignoring", entry.interceptor.name)
                    continue
                self._abort(
                    result, entry, "validator returned a non-validation result", "validation", "validation_failed"
                )
                continue

            result.results.append(invoke_result)
            self._tally(result.validation_summary, invoke_result)
            if audit:
                continue
            if not invoke_result.valid and _blocking_severity(invoke_result):
                reason = "validation failed"
                if invoke_result.messages:
                    reason = invoke_result.messages[0].message
                self._abort(result, entry, reason, "validation", "validation_failed")

    async def _run_mutators(
        self,
        mutators: list[ChainEntry],
        params: ChainExecutionParams,
        state: _ExecutionState,
        result: ChainExecutionResult,
    ) -> None:
        for entry in mutators:
            if result.aborted_at is not None:
                return
            audit = entry.effective_mode() == "audit"
            state.in_flight = entry.interceptor.name
            try:
                invoke_result = await self._invoke(entry, params, state.payload)
            except Exception as exc:  # recorded per fail-open policy; cancellation propagates
                if audit or entry.effective_fail_open():
                    logger.warning("mutator %r failed open: %s", entry.interceptor.name, exc)
                    continue
                self._abort(result, entry, f"mutator failed: {exc}", "mutation", "mutation_failed")
                return
            # Cleared here (not in a finally) so a chain-timeout cancellation
            # still sees which mutator was in flight.
            state.in_flight = None

            if not isinstance(invoke_result, MutationResult):
                if audit or entry.effective_fail_open():
                    logger.warning("mutator %r returned a non-mutation result; ignoring", entry.interceptor.name)
                    continue
                self._abort(result, entry, "mutator returned a non-mutation result", "mutation", "mutation_failed")
                return

            result.results.append(invoke_result)
            # Audit mutators compute but never apply (shadow mutations).
            if not audit and invoke_result.modified:
                state.payload = invoke_result.payload

    # -- Plumbing ------------------------------------------------------------

    async def _invoke(self, entry: ChainEntry, params: ChainExecutionParams, payload: Any) -> AnyInvokeResult:
        invoke_params = InvokeInterceptorParams(
            name=entry.interceptor.name,
            event=params.event,
            phase=params.phase,
            payload=payload,
            config=(params.config or {}).get(entry.interceptor.name),
            timeout_ms=entry.effective_timeout_ms(),
            context=params.context,
        )
        if self._execution_handler is not None:
            return await self._execution_handler(entry, invoke_params, self._send)
        return await self._send(entry, invoke_params)

    @staticmethod
    async def _send(entry: ChainEntry, invoke_params: InvokeInterceptorParams) -> AnyInvokeResult:
        timeout_s = None if invoke_params.timeout_ms is None else invoke_params.timeout_ms / 1000

        async def call() -> AnyInvokeResult:
            return await invoke_interceptor(
                entry.target,
                name=invoke_params.name,
                event=invoke_params.event,
                phase=invoke_params.phase,
                payload=invoke_params.payload,
                config=invoke_params.config,
                timeout_ms=invoke_params.timeout_ms,
                context=invoke_params.context,
            )

        if timeout_s is None:
            return await call()
        # The server enforces timeoutMs too; this guards against servers that don't.
        with anyio.fail_after(timeout_s):
            return await call()

    @staticmethod
    def _abort(
        result: ChainExecutionResult,
        entry: ChainEntry,
        reason: str,
        abort_type: Literal["validation", "mutation"],
        status: ChainStatus,
    ) -> None:
        if result.aborted_at is not None:
            return
        result.aborted_at = AbortInfo(interceptor=entry.interceptor.name, reason=reason, type=abort_type)
        result.status = status

    @staticmethod
    def _tally(summary: ValidationSummary, validation: ValidationResult) -> None:
        severities: list[str]
        if validation.messages:
            severities = [message.severity for message in validation.messages]
        elif validation.severity is not None:
            severities = [validation.severity]
        elif not validation.valid:
            severities = ["error"]
        else:
            severities = []
        for severity in severities:
            if severity == "error":
                summary.errors += 1
            elif severity == "warn":
                summary.warnings += 1
            else:
                summary.infos += 1


def _blocking_severity(validation: ValidationResult) -> bool:
    """Only severity "error" blocks; an invalid result without severity fails secure."""
    if validation.severity is not None:
        return validation.severity == "error"
    if validation.messages:
        return any(message.severity == "error" for message in validation.messages)
    return True


@dataclass
class _ExecutionState:
    payload: Any
    in_flight: str | None = None
