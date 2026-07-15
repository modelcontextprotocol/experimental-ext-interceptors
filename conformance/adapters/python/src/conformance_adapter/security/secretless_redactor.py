# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""``secretless-redactor`` — the reference security MUTATOR (open tier),
ported clean-room from
`typescript/sdk/src/samples/security/secretless-redactor.ts` onto the WG
``feature/python-sdk`` author API.

Replaces every verbatim secret value in an outbound payload with an opaque
handle token, so downstream servers receive a stable reference instead of the
credential. The handle is deterministic (same secret → same handle within and
across payloads, and ACROSS LANGUAGES: the FNV-1a hash below is pinned
byte-equal to the TypeScript implementation) but non-invertible from the
token alone.

Composes with ``cross-boundary-guard``: on the request phase mutations run
BEFORE validations (SEP-2624 trust-boundary order), so a payload redacted
here no longer carries the verbatim secret when the guard checks it —
redaction is the remediation, blocking is the backstop.
"""

from __future__ import annotations

import json
from typing import Any

from mcp_ext_interceptors.interceptor import Invocation, Mutator
from mcp_ext_interceptors.types import (
    EVENT_ELICITATION_CREATE,
    EVENT_SAMPLING_CREATE_MESSAGE,
    EVENT_TOOLS_CALL,
    TYPE_MUTATION,
    Hook,
    InterceptorInfo,
    MutationResult,
)

from conformance_adapter.security.secret_formats import find_secrets

SECRETLESS_REDACTOR_NAME = "formalcore/secretless-redactor"

_REDACTOR_EVENTS: tuple[str, ...] = (
    EVENT_TOOLS_CALL,
    EVENT_SAMPLING_CREATE_MESSAGE,
    EVENT_ELICITATION_CREATE,
)


def fnv1a(text: str) -> str:
    """FNV-1a 32-bit over the secret value — deterministic, dependency-free,
    and enough to disambiguate handles. Iterates UTF-16 code units and wraps
    multiplication to 32 bits, matching the TS ``charCodeAt``/``Math.imul``
    implementation bit-for-bit. This is an OPAQUE REFERENCE, not a
    commitment: unforgeability is the attestation layer's job, not the
    token's."""
    hash_ = 0x811C9DC5
    for unit in _utf16_units(text):
        hash_ ^= unit
        hash_ = (hash_ * 0x01000193) & 0xFFFFFFFF
    return f"{hash_:08x}"


def _utf16_units(text: str) -> list[int]:
    units: list[int] = []
    for ch in text:
        cp = ord(ch)
        if cp <= 0xFFFF:
            units.append(cp)
        else:
            cp -= 0x10000
            units.append(0xD800 | (cp >> 10))
            units.append(0xDC00 | (cp & 0x3FF))
    return units


def handle_for(format_id: str, value: str) -> str:
    return f"<mcp:secret-ref:{format_id}:{fnv1a(value)}>"


def _redact_text(text: str) -> str:
    redacted = text
    for hit in find_secrets(text):
        redacted = redacted.replace(hit.value, handle_for(hit.format_id, hit.value))
    return redacted


def redact_value(value: Any) -> Any:
    """Deep, pure JSON transform: strings are redacted, structure is preserved."""
    if isinstance(value, str):
        return _redact_text(value)
    if isinstance(value, list):
        return [redact_value(v) for v in value]
    if isinstance(value, dict):
        return {k: redact_value(v) for k, v in value.items()}
    return value


def create_secretless_redactor() -> Mutator:
    async def handler(inv: Invocation) -> MutationResult:
        text = "" if inv.payload is None else json.dumps(inv.payload, ensure_ascii=False, separators=(",", ":"))
        hits = find_secrets(text)
        if not hits:
            return MutationResult(modified=False, payload=inv.payload)
        formats = list(dict.fromkeys(hit.format_id for hit in hits))
        return MutationResult(
            modified=True,
            payload=redact_value(inv.payload),
            info={"redacted": len(hits), "formats": formats},
        )

    info = InterceptorInfo(
        name=SECRETLESS_REDACTOR_NAME,
        version="0.1.0",
        description=(
            "Replaces verbatim secret values in outbound payloads with opaque, "
            "deterministic handle tokens (open tier)."
        ),
        type=TYPE_MUTATION,
        hooks=[Hook(events=list(_REDACTOR_EVENTS), phase="request")],
    )
    return Mutator(info=info, handler=handler)
