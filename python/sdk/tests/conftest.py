# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by a Apache-2.0
# license that can be found in the LICENSE file.

import pytest


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"
