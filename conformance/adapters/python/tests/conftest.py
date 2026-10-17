# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

from pathlib import Path

import pytest

#: tests/ → python/ → adapters/ → conformance/ (the shared suite root).
CONFORMANCE_ROOT = Path(__file__).resolve().parents[3]


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.fixture(scope="session")
def conformance_root() -> Path:
    return CONFORMANCE_ROOT
