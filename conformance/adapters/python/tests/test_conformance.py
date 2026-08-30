# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""The proof: the SHARED TypeScript-generated fixtures certify (and reject)
Python implementations without modification.

- The conformant posture (WG feature/python-sdk + documented SEP bridges +
  the Python security interceptors) scores 100% - all 24 fixtures, including
  every ``behavior/relaybleed-*`` fixture.
- The permissive strawman scores 0% on the relaybleed fixtures (the same
  negative control as the TS meta-test) - the suite cannot be satisfied by
  an implementation that denies nothing.
- The raw WG-SDK posture fails EXACTLY the fixtures that encode its
  divergences from the SEP wire shape (the interop findings), pinned here so
  an upstream fix flips a visible assertion.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from conformance_adapter.adapter import CONFORMANT_ADAPTER, RAW_ADAPTER, STRAWMAN_ADAPTER
from conformance_adapter.fixtures import Fixture, load_suite
from conformance_adapter.runner import format_report, run_fixture, run_suite

pytestmark = pytest.mark.anyio

EXPECTED_FIXTURE_COUNT = 24
RELAYBLEED_PREFIX = "behavior/relaybleed-"


@pytest.fixture(scope="module")
def suite(conformance_root: Path) -> tuple[Fixture, ...]:
    return load_suite(conformance_root)


# ── the shared artifact is consumed unchanged ────────────────────────────────


def test_suite_is_the_shared_typescript_generated_artifact(
    suite: tuple[Fixture, ...], conformance_root: Path
) -> None:
    manifest = json.loads((conformance_root / "manifest.json").read_text(encoding="utf-8"))
    assert manifest["generatedBy"] == "conformance/src/generate.ts (from src/catalog.ts)"
    assert manifest["sep"] == "https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624"
    assert len(suite) == EXPECTED_FIXTURE_COUNT
    assert [f.id for f in suite] == [entry["id"] for entry in manifest["fixtures"]]
    relaybleed = [f.id for f in suite if f.id.startswith(RELAYBLEED_PREFIX)]
    assert relaybleed == [
        "behavior/relaybleed-stripe_secret_live",
        "behavior/relaybleed-github_pat",
        "behavior/relaybleed-aws_access_key",
    ]


# ── 100% for the correct implementation ──────────────────────────────────────


async def test_conformant_adapter_passes_every_fixture(suite: tuple[Fixture, ...]) -> None:
    report = await run_suite(suite, CONFORMANT_ADAPTER)
    failures = "\n".join(
        f"{f.id}: " + "; ".join(s.detail for s in f.steps if not s.passed)
        for f in report.fixtures
        if not f.passed
    )
    assert report.passed == EXPECTED_FIXTURE_COUNT, failures
    assert report.total == EXPECTED_FIXTURE_COUNT
    assert report.compliance_percent == 100
    text = format_report(report)
    assert "compliance 100%" in text
    assert "python-wg-sdk" in text


@pytest.mark.parametrize(
    "fixture_id",
    [
        "behavior/relaybleed-stripe_secret_live",
        "behavior/relaybleed-github_pat",
        "behavior/relaybleed-aws_access_key",
    ],
)
async def test_every_relaybleed_fixture_passes_individually(
    suite: tuple[Fixture, ...], fixture_id: str
) -> None:
    fixture = next(f for f in suite if f.id == fixture_id)
    report = await run_fixture(fixture, CONFORMANT_ADAPTER)
    assert report.passed, [s.detail for s in report.steps if not s.passed]


# ── 0% for the permissive strawman ───────────────────────────────────────────


async def test_strawman_scores_zero_on_relaybleed(suite: tuple[Fixture, ...]) -> None:
    relaybleed = tuple(f for f in suite if f.id.startswith(RELAYBLEED_PREFIX))
    report = await run_suite(relaybleed, STRAWMAN_ADAPTER)
    assert report.total == 3
    assert report.passed == 0
    assert report.compliance_percent == 0
    assert "expected decision 'deny', got 'allow'" in format_report(report)


async def test_strawman_leaks_the_verbatim_secret_on_redaction_fixtures(
    suite: tuple[Fixture, ...],
) -> None:
    redaction = tuple(f for f in suite if f.id.startswith("behavior/redaction-"))
    report = await run_suite(redaction, STRAWMAN_ADAPTER)
    assert report.passed == 0
    assert "forbidden content present" in format_report(report)


# ── the raw WG SDK posture fails exactly the finding fixtures ────────────────

#: The executable statement of the interop findings (adapter.py FINDINGS 1-3):
#: what feature/python-sdk out-of-the-box fails against the SEP wire shape.
RAW_EXPECTED_FAILURES = frozenset(
    {
        # FINDINGS 1+2: `mode: "active"` + defaults emitted on list descriptors.
        "protocol/list-all",
        "protocol/list-filtered-by-event",
        # FINDING 3: request phase derived as receiving (validate-then-mutate).
        "protocol/chain-request-order-mutate-then-validate",
        "protocol/chain-response-order-validate-then-mutate",
        "behavior/redaction-defuses-relaybleed",
    }
)


async def test_raw_wg_sdk_fails_exactly_the_finding_fixtures(suite: tuple[Fixture, ...]) -> None:
    report = await run_suite(suite, RAW_ADAPTER)
    failed = {f.id for f in report.fixtures if not f.passed}
    assert failed == RAW_EXPECTED_FAILURES
    assert report.passed == EXPECTED_FIXTURE_COUNT - len(RAW_EXPECTED_FAILURES)
    assert report.compliance_percent == 79.2  # 19/24, JS-rounded to one decimal
