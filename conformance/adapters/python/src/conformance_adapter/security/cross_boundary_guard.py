# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""``cross-boundary-guard`` — the reference security VALIDATOR (open tier),
ported clean-room from
`typescript/sdk/src/samples/security/cross-boundary-guard.ts` onto the WG
``feature/python-sdk`` author API.

Enforces causal cross-boundary non-interference over verbatim secrets: a
secret value that appeared in a strictly-prior response from server A may not
appear in a later request to a different server B. That composed
read-then-send flow is the exfiltration class where every per-call check
legitimately passes — only the cross-call, cross-server view denies it.

Attribution: a request payload names its server (tool name / resource URI,
via ``server_of``); a response payload does not. The guard therefore hooks
BOTH phases and correlates: the request phase records the session's in-flight
server, and the response phase attributes any ingested secret to it. This
models a proxy/host that runs request → operation → response sequentially per
session; concurrent in-flight operations within one session would need
host-supplied attribution (a closed-tier concern).

Open tier boundaries (exact, by design):
  - verbatim (exact-match) secrets only — no fragment, paraphrase, or
    semantic detection;
  - the public secret-FORMAT catalog in ``secret_formats.py`` only;
  - in-memory per-session taint, isolated per guard instance.

Causality is by construction: taint is only ever recorded from responses, so
a request-phase check can only see taint from strictly-prior reads. A secret
sent with no prior cross-boundary read passes — the guard tracks flows, it
does not moralize about values.
"""

from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

from mcp_ext_interceptors.interceptor import Invocation, Validator
from mcp_ext_interceptors.types import (
    EVENT_ELICITATION_CREATE,
    EVENT_RESOURCES_READ,
    EVENT_SAMPLING_CREATE_MESSAGE,
    EVENT_TOOLS_CALL,
    TYPE_VALIDATION,
    Hook,
    InterceptorInfo,
    InvocationContext,
    Phase,
    ValidationMessage,
    ValidationResult,
)

from conformance_adapter.security.secret_formats import find_secrets
from conformance_adapter.security.server_of import server_of

CROSS_BOUNDARY_GUARD_NAME = "formalcore/cross-boundary-guard"

_GUARD_EVENTS: tuple[str, ...] = (
    EVENT_TOOLS_CALL,
    EVENT_RESOURCES_READ,
    EVENT_SAMPLING_CREATE_MESSAGE,
    EVENT_ELICITATION_CREATE,
)

_DEFAULT_SESSION = "default"


@dataclass(frozen=True, kw_only=True)
class TaintOrigin:
    format_id: str
    server: str


@dataclass(kw_only=True)
class _SessionState:
    #: Secret value → where it was FIRST read (first origin wins).
    taint: dict[str, TaintOrigin] = field(default_factory=dict)
    #: Server of the session's in-flight request, for response attribution.
    in_flight_server: str | None = None


def _session_key(context: InvocationContext | None) -> str:
    if context is None:
        return _DEFAULT_SESSION
    if context.session_id is not None:
        return context.session_id
    if context.trace_id is not None:
        return context.trace_id
    return _DEFAULT_SESSION


def _payload_text(payload: Any) -> str:
    return "" if payload is None else json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


def _pass(info: dict[str, Any]) -> ValidationResult:
    return ValidationResult(valid=True, info=info)


def _block(message: str) -> ValidationResult:
    return ValidationResult(
        valid=False,
        severity="error",
        messages=[ValidationMessage(message=message, severity="error")],
    )


def create_cross_boundary_guard() -> Validator:
    """A fresh guard instance: isolated per-session taint state."""
    sessions: dict[str, _SessionState] = {}

    def _session_for(inv: Invocation) -> _SessionState:
        return sessions.setdefault(_session_key(inv.context), _SessionState())

    def _enforce(inv: Invocation) -> ValidationResult:
        """Request phase: enforce, then note the in-flight server for the response."""
        session = _session_for(inv)
        target = server_of(inv.event, inv.payload)
        for hit in find_secrets(_payload_text(inv.payload)):
            origin = session.taint.get(hit.value)
            if origin is not None and origin.server != target:
                return _block(
                    f"cross-boundary secret flow: {origin.format_id} read from "
                    f"'{origin.server}' may not be sent to '{target}'"
                )
        session.in_flight_server = target
        return _pass({"target": target, "tainted": len(session.taint)})

    def _ingest(inv: Invocation) -> ValidationResult:
        """Response phase: ingest, attributing to the correlated in-flight server."""
        session = _session_for(inv)
        server = session.in_flight_server if session.in_flight_server is not None else server_of(inv.event, inv.payload)
        ingested = 0
        for hit in find_secrets(_payload_text(inv.payload)):
            if hit.value not in session.taint:
                session.taint[hit.value] = TaintOrigin(format_id=hit.format_id, server=server)
                ingested += 1
        return _pass({"origin": server, "ingested": ingested, "tainted": len(session.taint)})

    by_phase: dict[Phase, Callable[[Invocation], ValidationResult]] = {
        "request": _enforce,
        "response": _ingest,
    }

    async def handler(inv: Invocation) -> ValidationResult:
        return by_phase[inv.phase](inv)

    info = InterceptorInfo(
        name=CROSS_BOUNDARY_GUARD_NAME,
        version="0.1.0",
        description=(
            "Blocks verbatim secrets read from one server from being sent to "
            "another (causal cross-boundary taint, open tier)."
        ),
        type=TYPE_VALIDATION,
        hooks=[
            Hook(events=list(_GUARD_EVENTS), phase="request"),
            Hook(events=list(_GUARD_EVENTS), phase="response"),
        ],
    )
    return Validator(info=info, handler=handler)
