# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""The Python conformance RUNNER: replays the SHARED fixtures against an
adapter and diffs canonical JSON — the Python realization of the algorithm in
`conformance/ADAPTER.md`, structurally mirroring `conformance/src/runner.ts`.

No expectation logic is ported from TypeScript: the fixtures are the oracle.
The runner only implements the comparison model (identical in every language):

  1. scrub volatile keys (``fixture.volatile``) recursively from the ACTUAL;
  2. JCS-canonicalize the scrubbed actual (``canonical.py``, byte-equal to TS);
  3. string-compare against the fixture's precomputed ``canonical``.

Behavior expectations additionally check decision equality and
forbids/requires substrings over the canonical final payload.
"""

from __future__ import annotations

import math
from collections.abc import Awaitable, Callable
from contextlib import AbstractAsyncContextManager
from dataclasses import dataclass
from typing import Any, Literal, Protocol

from conformance_adapter.canonical import canonicalize
from conformance_adapter.fixtures import (
    ChainStep,
    DecisionExpectation,
    ErrorExpectation,
    Fixture,
    FixtureInterceptor,
    FixtureStep,
    InvokeParams,
    InvokeStep,
    ListStep,
    Phase,
)

# ── the adapter contract (ADAPTER.md, four functions) ────────────────────────


@dataclass(frozen=True, kw_only=True)
class InvokeOutcome:
    """``ok`` with the wire result, or the JSON-RPC error code."""

    ok: bool
    result: Any = None
    error_code: int | None = None


@dataclass(frozen=True, kw_only=True)
class ChainOutcome:
    #: ``allow`` iff the chain completed; ``deny`` iff it was blocked/aborted.
    decision: Literal["allow", "deny"]
    #: The payload after all applied mutations (meaningful when allowed).
    final_payload: Any


class AdapterSession(Protocol):
    def list(self, event: str | None) -> Awaitable[Any]:
        """Wire JSON of ``interceptors/list`` (event filter or None)."""
        ...

    def invoke(self, params: InvokeParams) -> Awaitable[InvokeOutcome]:
        """Wire JSON of ``interceptor/invoke``, or the JSON-RPC error code."""
        ...

    def chain(
        self, event: str, phase: Phase, payload: Any, session_id: str | None
    ) -> Awaitable[ChainOutcome]:
        """One chain execution across ALL of the session's interceptors."""
        ...


class Adapter(Protocol):
    @property
    def name(self) -> str: ...

    def create_session(
        self, interceptors: tuple[FixtureInterceptor, ...]
    ) -> AbstractAsyncContextManager[AdapterSession]:
        """Fresh implementation state with these interceptors registered."""
        ...


# ── scrubbing + canonical comparison ─────────────────────────────────────────


def scrub(value: Any, volatile: tuple[str, ...]) -> Any:
    """Recursively drop volatile keys (timings, impl-specific info) from actuals."""
    if isinstance(value, list):
        return [scrub(v, volatile) for v in value]
    if isinstance(value, dict):
        return {k: scrub(v, volatile) for k, v in value.items() if k not in volatile}
    return value


@dataclass(frozen=True, kw_only=True)
class _Comparison:
    passed: bool
    detail: str


def _compare_canonical(actual: Any, expected_canonical: str, volatile: tuple[str, ...]) -> _Comparison:
    actual_canonical = canonicalize(scrub(actual, volatile))
    if actual_canonical == expected_canonical:
        return _Comparison(passed=True, detail="canonical match")
    return _Comparison(
        passed=False,
        detail=f"expected {expected_canonical}\n       actual {actual_canonical}",
    )


# ── per-op step execution (dispatch dict, no if-chain) ───────────────────────


@dataclass(frozen=True, kw_only=True)
class StepReport:
    index: int
    op: str
    passed: bool
    detail: str


async def _run_list_step(step: ListStep, session: AdapterSession, fixture: Fixture, index: int) -> StepReport:
    actual = await session.list(step.event)
    cmp = _compare_canonical(actual, step.expect.canonical, fixture.volatile)
    return StepReport(index=index, op=step.op, passed=cmp.passed, detail=cmp.detail)


async def _run_invoke_step(step: InvokeStep, session: AdapterSession, fixture: Fixture, index: int) -> StepReport:
    outcome = await session.invoke(step.params)
    if isinstance(step.expect, ErrorExpectation):
        passed = not outcome.ok and outcome.error_code == step.expect.error_code
        detail = (
            f"error {step.expect.error_code} as required"
            if passed
            else f"expected error {step.expect.error_code}, got "
            + ("a result" if outcome.ok else str(outcome.error_code))
        )
        return StepReport(index=index, op=step.op, passed=passed, detail=detail)
    if not outcome.ok:
        return StepReport(
            index=index,
            op=step.op,
            passed=False,
            detail=f"expected a result, got error {outcome.error_code}",
        )
    cmp = _compare_canonical(outcome.result, step.expect.canonical, fixture.volatile)
    return StepReport(index=index, op=step.op, passed=cmp.passed, detail=cmp.detail)


def _check_decision(expect: DecisionExpectation, outcome: ChainOutcome, volatile: tuple[str, ...]) -> _Comparison:
    if outcome.decision != expect.decision:
        return _Comparison(
            passed=False,
            detail=f"expected decision '{expect.decision}', got '{outcome.decision}'",
        )
    final_canonical = canonicalize(scrub(outcome.final_payload, volatile))
    if expect.final_payload_canonical is not None and final_canonical != expect.final_payload_canonical:
        return _Comparison(
            passed=False,
            detail=(
                "final payload mismatch:\n"
                f"       expected {expect.final_payload_canonical}\n"
                f"       actual {final_canonical}"
            ),
        )
    for forbidden in expect.forbids:
        if forbidden in final_canonical:
            return _Comparison(passed=False, detail=f"forbidden content present: {forbidden}")
    for required in expect.requires:
        if required not in final_canonical:
            return _Comparison(passed=False, detail=f"required content absent: {required}")
    return _Comparison(passed=True, detail=f"decision '{expect.decision}' as required")


async def _run_chain_step(step: ChainStep, session: AdapterSession, fixture: Fixture, index: int) -> StepReport:
    outcome = await session.chain(step.event, step.phase, step.payload, step.session_id)
    cmp = _check_decision(step.expect, outcome, fixture.volatile)
    return StepReport(index=index, op=step.op, passed=cmp.passed, detail=cmp.detail)


_StepRunner = Callable[[Any, AdapterSession, Fixture, int], Awaitable[StepReport]]

_RUN_BY_OP: dict[str, _StepRunner] = {
    "list": _run_list_step,
    "invoke": _run_invoke_step,
    "chain": _run_chain_step,
}


# ── fixture + suite execution ────────────────────────────────────────────────


@dataclass(frozen=True, kw_only=True)
class FixtureReport:
    id: str
    requirement: str
    passed: bool
    steps: tuple[StepReport, ...]


@dataclass(frozen=True, kw_only=True)
class ComplianceReport:
    adapter: str
    total: int
    passed: int
    #: passed / total as a percentage, rounded half-up to one decimal place —
    #: the same rounding as the TS reference (`Math.round(x * 1000) / 10`).
    compliance_percent: float
    fixtures: tuple[FixtureReport, ...]


def _round_half_up(value: float) -> int:
    """JS ``Math.round``: half-up toward positive infinity (Python's built-in
    ``round`` is banker's rounding and disagrees on exact .5 ties)."""
    return math.floor(value + 0.5)


def compliance_percent(passed: int, total: int) -> float:
    return 100.0 if total == 0 else _round_half_up((passed / total) * 1000) / 10


async def run_fixture(fixture: Fixture, adapter: Adapter) -> FixtureReport:
    steps: list[StepReport] = []
    async with adapter.create_session(fixture.interceptors) as session:
        for index, step in enumerate(fixture.steps):
            try:
                steps.append(await _RUN_BY_OP[step.op](step, session, fixture, index))
            except Exception as err:  # an adapter crash is a step failure, not a runner crash
                steps.append(
                    StepReport(index=index, op=step.op, passed=False, detail=f"adapter raised: {err}")
                )
    return FixtureReport(
        id=fixture.id,
        requirement=fixture.requirement,
        passed=all(s.passed for s in steps),
        steps=tuple(steps),
    )


async def run_suite(fixtures: tuple[Fixture, ...], adapter: Adapter) -> ComplianceReport:
    reports = [await run_fixture(fixture, adapter) for fixture in fixtures]
    passed = sum(1 for r in reports if r.passed)
    return ComplianceReport(
        adapter=adapter.name,
        total=len(reports),
        passed=passed,
        compliance_percent=compliance_percent(passed, len(reports)),
        fixtures=tuple(reports),
    )


def _js_number(value: float) -> str:
    """Format like JS ``String(n)``: integral floats print without ``.0``."""
    return str(int(value)) if value == int(value) else str(value)


def format_report(report: ComplianceReport) -> str:
    """Human-readable report, line-compatible with the TS ``formatReport``."""
    lines = []
    for f in report.fixtures:
        mark = "PASS" if f.passed else "FAIL"
        failures = "".join(
            f"\n       step {s.index} ({s.op}): {s.detail}" for s in f.steps if not s.passed
        )
        lines.append(f"  {mark}  {f.id} [{f.requirement}]{failures}")
    return "\n".join(
        [
            f"MCP interceptors conformance — adapter: {report.adapter}",
            *lines,
            f"  {report.passed}/{report.total} fixtures passed — "
            f"compliance {_js_number(report.compliance_percent)}%",
        ]
    )
