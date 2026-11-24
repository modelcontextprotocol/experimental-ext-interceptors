# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Python conformance adapter for the SEP-2624 interceptors suite.

Consumes the SHARED language-neutral fixtures under ``conformance/fixtures/``
unchanged and certifies the WG ``feature/python-sdk`` implementation plus the
Python port of the reference security interceptors against them.
"""

from conformance_adapter.adapter import (
    CONFORMANT_ADAPTER,
    RAW_ADAPTER,
    STRAWMAN_ADAPTER,
    WGPythonAdapter,
)
from conformance_adapter.canonical import canonicalize
from conformance_adapter.fixtures import Fixture, load_fixture, load_suite, parse_fixture
from conformance_adapter.runner import (
    Adapter,
    AdapterSession,
    ChainOutcome,
    ComplianceReport,
    FixtureReport,
    InvokeOutcome,
    StepReport,
    compliance_percent,
    format_report,
    run_fixture,
    run_suite,
    scrub,
)

__all__ = [
    "CONFORMANT_ADAPTER",
    "RAW_ADAPTER",
    "STRAWMAN_ADAPTER",
    "Adapter",
    "AdapterSession",
    "ChainOutcome",
    "ComplianceReport",
    "Fixture",
    "FixtureReport",
    "InvokeOutcome",
    "StepReport",
    "WGPythonAdapter",
    "canonicalize",
    "compliance_percent",
    "format_report",
    "load_fixture",
    "load_suite",
    "parse_fixture",
    "run_fixture",
    "run_suite",
    "scrub",
]
