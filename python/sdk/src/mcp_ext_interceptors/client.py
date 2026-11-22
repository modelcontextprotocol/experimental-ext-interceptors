# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Invoker-side helpers for calling interceptors hosted on MCP servers."""

from __future__ import annotations

from typing import Any, Literal

import mcp.types as mcp_types
from mcp.client.client import Client
from mcp.client.session import ClientSession
from pydantic import TypeAdapter

from mcp_ext_interceptors.types import (
    EXTENSION_ID,
    METHOD_INVOKE,
    METHOD_LIST,
    AnyInvokeResult,
    InterceptorsCapability,
    InvocationContext,
    InvokeInterceptorParams,
    ListInterceptorsParams,
    ListInterceptorsResult,
    Phase,
    parse_invoke_result,
)

InvokerTarget = Client | ClientSession


class _ListInterceptorsRequest(mcp_types.Request[ListInterceptorsParams, Literal["interceptors/list"]]):
    method: Literal["interceptors/list"] = METHOD_LIST
    params: ListInterceptorsParams


class _InvokeInterceptorRequest(mcp_types.Request[InvokeInterceptorParams, Literal["interceptor/invoke"]]):
    method: Literal["interceptor/invoke"] = METHOD_INVOKE
    params: InvokeInterceptorParams


# Raw dict adapter: invoke results are parsed with `parse_invoke_result` so an
# unknown result `type` from a newer server degrades instead of failing validation.
# Typed as Any because send_request's result TypeVar is bound to BaseModel.
_RAW_RESULT: TypeAdapter[Any] = TypeAdapter(dict[str, Any])


def _session(target: InvokerTarget) -> ClientSession:
    return target if isinstance(target, ClientSession) else target.session


async def list_interceptors(target: InvokerTarget, *, event: str | None = None) -> ListInterceptorsResult:
    """Call `interceptors/list`, optionally filtered by lifecycle event."""
    request = _ListInterceptorsRequest(params=ListInterceptorsParams(event=event))
    return await _session(target).send_request(request, ListInterceptorsResult)


async def invoke_interceptor(
    target: InvokerTarget,
    *,
    name: str,
    event: str,
    phase: Phase,
    payload: Any,
    config: dict[str, Any] | None = None,
    timeout_ms: float | None = None,
    context: InvocationContext | None = None,
) -> AnyInvokeResult:
    """Call `interceptor/invoke` and parse the flat result body."""
    request = _InvokeInterceptorRequest(
        params=InvokeInterceptorParams(
            name=name,
            event=event,
            phase=phase,
            payload=payload,
            config=config,
            timeout_ms=timeout_ms,
            context=context,
        )
    )
    raw = await _session(target).send_request(request, _RAW_RESULT)
    return parse_invoke_result(raw)


def read_interceptors_capability(capabilities: Any) -> InterceptorsCapability | None:
    """Read the interceptors capability from negotiated `ServerCapabilities`.

    Returns None when absent. Note: on legacy-handshake connections
    (2025-11-25 and older) `capabilities.extensions` does not exist on the
    wire, so None means "unknown", not "unsupported" — probe
    `interceptors/list` to find out.
    """
    extensions = getattr(capabilities, "extensions", None)
    if not extensions:
        return None
    raw = extensions.get(EXTENSION_ID)
    if raw is None:
        return None
    return InterceptorsCapability.model_validate(raw)
