# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""The Python ADAPTER: binds the conformance behavior vocabulary (ADAPTER.md)
to the WG ``feature/python-sdk`` implementation (``mcp_ext_interceptors``).

Every fixture interceptor is registered on a real in-memory MCP server via the
WG ``Interceptors`` extension; ``list``/``invoke`` return the RAW bytes the WG
SDK puts on the wire (captured through an untyped ``send_request``), and
``chain`` runs the WG ``Chain`` orchestrator over a live client connection -
so a fixture pass certifies the full wire round-trip, not an in-process stub.

Three adapter configurations, differing ONLY in two documented switches:

- ``CONFORMANT_ADAPTER`` (``python-wg-sdk``): SEP wire posture. Must score
  100% against the shared fixtures.
- ``RAW_ADAPTER`` (``python-wg-sdk-raw``): the WG SDK's out-of-the-box
  defaults. Its failing fixtures are the executable list of interop
  divergences between ``feature/python-sdk`` and the SEP wire shape the
  fixtures pin (see FINDINGS below; pinned in ``tests/test_conformance.py``).
- ``STRAWMAN_ADAPTER`` (``permissive-strawman``): the conformant posture with
  the security behaviors NOT bound (mirrors the TS meta-test's strawman).
  Must score 0% on the ``behavior/relaybleed-*`` fixtures.

FINDINGS - divergences of feature/python-sdk from the SEP-2624 wire shape the
fixtures (and the TS SDK's serializer) pin. Each is bridged in exactly one
place below, marked ``FINDING n``:

1. Mode vocabulary (ALIGNED): SEP-2624 as amended (PR #17) and the WG Python
   SDK (``types.Mode = Literal["active", "audit"]``) agree on ``active`` as
   the enforcing mode; the conformance fixtures, once corrected, pin the same.
   ``_MODE_TO_WG`` is therefore an identity map that additionally accepts the
   legacy ``enforce`` spelling read-only (normalizing it to ``active``). The
   wire divergence that remains is default EMISSION, folded into finding 2.
2. Default emission on ``interceptors/list``: the WG SDK serializes
   ``mode: "active"`` and ``failOpen: false`` on every descriptor; the SEP
   wire shape OMITS defaults. Bridged by ``_sep_descriptor``.
3. Chain direction: the WG ``Chain`` derives direction from phase as
   request→receiving / response→sending (server posture, "the way the Go SDK
   does"), which runs validators BEFORE mutators on requests. The SEP
   trust-boundary order for a client-side chain (the posture the fixtures
   pin, and the TS ``executeChain`` hardcodes) is request→sending
   (mutate→validate) / response→receiving (validate→mutate). Bridged by
   passing ``direction`` explicitly (``_DIRECTION_BY_PHASE``).
"""

from __future__ import annotations

from collections.abc import AsyncIterator, Callable
from contextlib import asynccontextmanager
from dataclasses import dataclass
from typing import Any, Literal

import mcp_types
from mcp.client.client import Client
from mcp.server import MCPServer
from mcp.shared.exceptions import MCPError
from pydantic import TypeAdapter

from mcp_ext_interceptors.chain import Chain, ChainExecutionParams
from mcp_ext_interceptors.interceptor import Invocation, Mutator, RegisteredInterceptor, Validator
from mcp_ext_interceptors.server import Interceptors
from mcp_ext_interceptors.types import (
    METHOD_INVOKE,
    METHOD_LIST,
    TYPE_MUTATION,
    TYPE_VALIDATION,
    Hook,
    InterceptorInfo,
    InvocationContext,
    InvokeInterceptorParams,
    ListInterceptorsParams,
    MutationResult,
    ValidationMessage,
    ValidationResult,
)

from conformance_adapter.fixtures import FixtureInterceptor, InvokeParams, Phase
from conformance_adapter.runner import ChainOutcome, InvokeOutcome
from conformance_adapter.security.cross_boundary_guard import create_cross_boundary_guard
from conformance_adapter.security.secretless_redactor import create_secretless_redactor

# ── wire plumbing (raw bytes in, raw bytes out) ──────────────────────────────

_RAW: TypeAdapter[Any] = TypeAdapter(dict[str, Any])


class _ListRequest(mcp_types.Request[ListInterceptorsParams, Literal["interceptors/list"]]):
    method: Literal["interceptors/list"] = METHOD_LIST
    params: ListInterceptorsParams


class _InvokeRequest(mcp_types.Request[InvokeInterceptorParams, Literal["interceptor/invoke"]]):
    method: Literal["interceptor/invoke"] = METHOD_INVOKE
    params: InvokeInterceptorParams


# FINDING 1 (ALIGNED): SEP + WG SDK both use `active`; this is an identity map
# that also accepts the legacy `enforce` spelling read-only → `active`.
_MODE_TO_WG: dict[str | None, str] = {
    None: "active",
    "active": "active",
    "audit": "audit",
    "enforce": "active",  # legacy read-only (pre-amendment SEP / Go SDK)
}

# FINDING 3: the SEP trust-boundary order for a client-side chain, stated as
# the WG Chain's explicit `direction` parameter.
_DIRECTION_BY_PHASE: dict[Phase, Literal["sending", "receiving"]] = {
    "request": "sending",
    "response": "receiving",
}


def _sep_descriptor(descriptor: dict[str, Any]) -> dict[str, Any]:
    """FINDING 2: compact a WG-emitted descriptor to the SEP wire shape by
    dropping the two default fields the WG SDK always emits. Non-default
    values (``mode: "audit"``, ``failOpen: true``) pass through untouched."""
    out = dict(descriptor)
    if out.get("mode") == "active":
        del out["mode"]
    if out.get("failOpen") is False:
        del out["failOpen"]
    return out


# ── behavior bindings (the whole ADAPTER.md vocabulary, exhaustively) ────────


def _hooks_of(i: FixtureInterceptor) -> list[Hook]:
    if i.phases == "both":
        return [Hook(events=list(i.events), phase="request"), Hook(events=list(i.events), phase="response")]
    return [Hook(events=list(i.events), phase=i.phases)]


def _info_of(i: FixtureInterceptor, type_: str) -> InterceptorInfo:
    return InterceptorInfo(
        name=i.name,
        type=type_,
        hooks=_hooks_of(i),
        mode=_MODE_TO_WG[i.mode],
        fail_open=False if i.fail_open is None else i.fail_open,
    )


def _note_of(payload: Any) -> str | None:
    if not isinstance(payload, dict):
        return None
    arguments = payload.get("arguments")
    if not isinstance(arguments, dict):
        return None
    note = arguments.get("note")
    return note if isinstance(note, str) else None


def _valid() -> ValidationResult:
    return ValidationResult(valid=True)


def _invalid(message: str) -> ValidationResult:
    return ValidationResult(
        valid=False,
        severity="error",
        messages=[ValidationMessage(message=message, severity="error")],
    )


def _bind_allow_all(i: FixtureInterceptor) -> RegisteredInterceptor:
    async def handler(inv: Invocation) -> ValidationResult:
        return _valid()

    return Validator(info=_info_of(i, TYPE_VALIDATION), handler=handler)


def _bind_deny_all(i: FixtureInterceptor) -> RegisteredInterceptor:
    async def handler(inv: Invocation) -> ValidationResult:
        return _invalid("denied by policy")

    return Validator(info=_info_of(i, TYPE_VALIDATION), handler=handler)


def _bind_require_note(i: FixtureInterceptor) -> RegisteredInterceptor:
    async def handler(inv: Invocation) -> ValidationResult:
        note = _note_of(inv.payload)
        return _valid() if note is not None and note != "" else _invalid("note is required")

    return Validator(info=_info_of(i, TYPE_VALIDATION), handler=handler)


def _bind_require_uppercase_note(i: FixtureInterceptor) -> RegisteredInterceptor:
    async def handler(inv: Invocation) -> ValidationResult:
        note = _note_of(inv.payload)
        return (
            _valid()
            if note is not None and note == note.upper()
            else _invalid("note must be uppercase")
        )

    return Validator(info=_info_of(i, TYPE_VALIDATION), handler=handler)


def _bind_uppercase_note(i: FixtureInterceptor) -> RegisteredInterceptor:
    async def handler(inv: Invocation) -> MutationResult:
        note = _note_of(inv.payload)
        if note is None:
            return MutationResult(modified=False, payload=inv.payload)
        payload = {**inv.payload, "arguments": {**inv.payload["arguments"], "note": note.upper()}}
        return MutationResult(modified=True, payload=payload)

    return Mutator(info=_info_of(i, TYPE_MUTATION), handler=handler)


def _bind_crash(i: FixtureInterceptor) -> RegisteredInterceptor:
    async def handler(inv: Invocation) -> ValidationResult:
        raise RuntimeError("interceptor crashed")

    return Validator(info=_info_of(i, TYPE_VALIDATION), handler=handler)


def _bind_cross_boundary_guard(i: FixtureInterceptor) -> RegisteredInterceptor:
    # A FRESH guard per session: fixtures never share taint state. Re-hooked
    # to the fixture's name/events (chain identity is by name), like the TS
    # reference adapter's `rebind`.
    guard = create_cross_boundary_guard()
    return Validator(info=_info_of(i, TYPE_VALIDATION), handler=guard.handler)


def _bind_secretless_redactor(i: FixtureInterceptor) -> RegisteredInterceptor:
    redactor = create_secretless_redactor()
    return Mutator(info=_info_of(i, TYPE_MUTATION), handler=redactor.handler)


_SECURITY_BEHAVIORS = frozenset({"cross-boundary-guard", "secretless-redactor"})

_BIND: dict[str, Callable[[FixtureInterceptor], RegisteredInterceptor]] = {
    "allow-all": _bind_allow_all,
    "deny-all": _bind_deny_all,
    "require-note": _bind_require_note,
    "require-uppercase-note": _bind_require_uppercase_note,
    "uppercase-note": _bind_uppercase_note,
    "crash": _bind_crash,
    "cross-boundary-guard": _bind_cross_boundary_guard,
    "secretless-redactor": _bind_secretless_redactor,
}


# ── the session (four calls over a live in-memory connection) ────────────────


@dataclass(frozen=True, kw_only=True)
class _Session:
    client: Client
    chain_: Chain
    sep_wire_shape: bool
    client_posture: bool

    async def list(self, event: str | None) -> Any:
        raw = await self.client.session.send_request(
            _ListRequest(params=ListInterceptorsParams(event=event)), _RAW
        )
        if not self.sep_wire_shape:
            return raw
        return {**raw, "interceptors": [_sep_descriptor(d) for d in raw["interceptors"]]}

    async def invoke(self, params: InvokeParams) -> InvokeOutcome:
        request = _InvokeRequest(
            params=InvokeInterceptorParams(
                name=params.name, event=params.event, phase=params.phase, payload=params.payload
            )
        )
        try:
            result = await self.client.session.send_request(request, _RAW)
        except MCPError as err:
            code = err.code
            return InvokeOutcome(ok=False, error_code=code if isinstance(code, int) else -32603)
        return InvokeOutcome(ok=True, result=result)

    async def chain(self, event: str, phase: Phase, payload: Any, session_id: str | None) -> ChainOutcome:
        params = ChainExecutionParams(
            event=event,
            phase=phase,
            payload=payload,
            context=None if session_id is None else InvocationContext(session_id=session_id),
            direction=_DIRECTION_BY_PHASE[phase] if self.client_posture else None,
        )
        result = await self.chain_.execute(params)
        return ChainOutcome(
            decision="allow" if result.status == "success" else "deny",
            final_payload=result.final_payload,
        )


# ── the adapter ──────────────────────────────────────────────────────────────


@dataclass(frozen=True, kw_only=True)
class WGPythonAdapter:
    """ADAPTER.md contract over the WG feature/python-sdk. The two boolean
    switches are the documented FINDING bridges; ``bind_security`` toggles the
    strawman."""

    name: str
    sep_wire_shape: bool
    client_posture: bool
    bind_security: bool

    @asynccontextmanager
    async def create_session(self, interceptors: tuple[FixtureInterceptor, ...]) -> AsyncIterator[_Session]:
        bound = [
            _BIND[i.behavior](i)
            for i in interceptors
            if self.bind_security or i.behavior not in _SECURITY_BEHAVIORS
        ]
        server = MCPServer("conformance-session", extensions=[Interceptors(bound)])
        async with Client(server) as client:
            chain = Chain()
            await chain.add_server(client)
            yield _Session(
                client=client,
                chain_=chain,
                sep_wire_shape=self.sep_wire_shape,
                client_posture=self.client_posture,
            )


CONFORMANT_ADAPTER = WGPythonAdapter(
    name="python-wg-sdk", sep_wire_shape=True, client_posture=True, bind_security=True
)

RAW_ADAPTER = WGPythonAdapter(
    name="python-wg-sdk-raw", sep_wire_shape=False, client_posture=False, bind_security=True
)

STRAWMAN_ADAPTER = WGPythonAdapter(
    name="permissive-strawman", sep_wire_shape=True, client_posture=True, bind_security=False
)
