# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Run the shared conformance suite against a Python adapter posture.

Usage::

    python -m conformance_adapter [--adapter conformant|raw|strawman] [--only PREFIX]

Prints the same report shape as the TypeScript reference runner
(`per-fixture PASS/FAIL, then "passed/total fixtures passed - compliance N%"`).
Exits 0 iff every selected fixture passed.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import anyio

from conformance_adapter.adapter import CONFORMANT_ADAPTER, RAW_ADAPTER, STRAWMAN_ADAPTER
from conformance_adapter.fixtures import load_suite
from conformance_adapter.runner import Adapter, format_report, run_suite

#: This file lives at conformance/adapters/python/src/conformance_adapter/;
#: the shared fixtures + manifest live at conformance/.
CONFORMANCE_ROOT = Path(__file__).resolve().parents[4]

ADAPTERS: dict[str, Adapter] = {
    "conformant": CONFORMANT_ADAPTER,
    "raw": RAW_ADAPTER,
    "strawman": STRAWMAN_ADAPTER,
}


def main() -> int:
    parser = argparse.ArgumentParser(prog="conformance_adapter")
    parser.add_argument("--adapter", choices=sorted(ADAPTERS), default="conformant")
    parser.add_argument("--only", default=None, help="run only fixtures whose id starts with this prefix")
    args = parser.parse_args()

    fixtures = load_suite(CONFORMANCE_ROOT)
    if args.only is not None:
        fixtures = tuple(f for f in fixtures if f.id.startswith(args.only))

    report = anyio.run(run_suite, fixtures, ADAPTERS[args.adapter])
    print(format_report(report))
    return 0 if report.passed == report.total else 1


if __name__ == "__main__":
    sys.exit(main())
