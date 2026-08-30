# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Behavior-equivalence tests for the Python security interceptor port.

Two layers of parity with `typescript/sdk/src/samples/security/`:

- structural: same seven public credential formats (ids AND regex sources),
  same calibration examples, same tool→server roster;
- byte-level: FNV-1a handle values pinned to the exact strings the TS
  implementation emits (verified by executing it under Node 24), so the
  "deterministic handle" property holds ACROSS LANGUAGES, not just runs.
"""

from __future__ import annotations

import pytest

from conformance_adapter.security import (
    SECRET_FORMATS,
    TOOL_SERVER,
    create_cross_boundary_guard,
    create_secretless_redactor,
    find_secrets,
    fnv1a,
    handle_for,
    redact_value,
    server_of,
)
from mcp_ext_interceptors.interceptor import Invocation
from mcp_ext_interceptors.types import InvocationContext, MutationResult, ValidationResult

pytestmark = pytest.mark.anyio


def _ctx(session_id: str) -> InvocationContext:
    return InvocationContext(session_id=session_id)


def _inv(phase: str, payload: object, session_id: str | None = "s") -> Invocation:
    return Invocation(
        event="tools/call",
        phase=phase,  # type: ignore[arg-type]
        payload=payload,
        context=None if session_id is None else _ctx(session_id),
    )


# ── secret formats (structural parity with secret-formats.ts) ────────────────


def test_catalog_is_exactly_the_seven_public_formats() -> None:
    assert [f.id for f in SECRET_FORMATS] == [
        "stripe_secret_live",
        "stripe_pub_live",
        "github_pat",
        "github_oauth",
        "aws_access_key",
        "slack_bot",
        "slack_refresh",
    ]


def test_every_calibration_example_is_found_as_its_own_format() -> None:
    for fmt in SECRET_FORMATS:
        hits = find_secrets(f"prefix {fmt.example} suffix")
        assert [(h.format_id, h.value) for h in hits] == [(fmt.id, fmt.example)]


def test_find_secrets_enumerates_catalog_order_then_position() -> None:
    text = "ghp_Xa2bC3dEf4gH5iJk6Lm7nN8oP then sk_live_4eC7aRm9Kx2bNw5pQj8sYd"
    assert [h.format_id for h in find_secrets(text)] == ["stripe_secret_live", "github_pat"]


def test_near_misses_are_not_secrets() -> None:
    assert find_secrets("sk_live_short AKIA123 xoxq-17345628901 ghp_short") == ()


# ── FNV-1a handles (byte parity with secretless-redactor.ts) ─────────────────

#: (value, expected) pinned by executing the TS fnv1a under Node 24.
FNV1A_PINS: tuple[tuple[str, str], ...] = (
    ("", "811c9dc5"),
    ("a", "e40c292c"),
    ("sk_live_4eC7aRm9Kx2bNw5pQj8sYd", "f11f593d"),
    # Non-ASCII + astral: exercises the UTF-16 code-unit iteration.
    ("héllo→世界", "bf0c8b2d"),
    ("😀🔑", "a3b5a2ca"),
)


@pytest.mark.parametrize(("value", "expected"), FNV1A_PINS, ids=[v or "empty" for v, _ in FNV1A_PINS])
def test_fnv1a_matches_typescript(value: str, expected: str) -> None:
    assert fnv1a(value) == expected


#: (format_id, value, handle) pinned by executing the TS handleFor under Node 24.
HANDLE_PINS: tuple[tuple[str, str, str], ...] = (
    ("stripe_secret_live", "sk_live_4eC7aRm9Kx2bNw5pQj8sYd", "<mcp:secret-ref:stripe_secret_live:f11f593d>"),
    ("stripe_pub_live", "pk_live_51HGf0KxLPq3NmRs7TvW9y", "<mcp:secret-ref:stripe_pub_live:247b266e>"),
    ("github_pat", "ghp_Xa2bC3dEf4gH5iJk6Lm7nN8oP", "<mcp:secret-ref:github_pat:0f0fb3ca>"),
    ("github_oauth", "gho_Bc4dEf5gHi6jKl7mNo8pQr9sT", "<mcp:secret-ref:github_oauth:fbc8a244>"),
    ("aws_access_key", "AKIA5MZXN8QRF3WBY6OE", "<mcp:secret-ref:aws_access_key:bc2c3233>"),
    ("slack_bot", "xoxb-17345628901-AbCdEfGhIjKlMnOp", "<mcp:secret-ref:slack_bot:f0f4119c>"),
    ("slack_refresh", "xoxr-98127345602-QrStUvWxYzAbCdEf", "<mcp:secret-ref:slack_refresh:9392e58d>"),
)


@pytest.mark.parametrize(("format_id", "value", "handle"), HANDLE_PINS, ids=[f for f, _, _ in HANDLE_PINS])
def test_handles_match_typescript(format_id: str, value: str, handle: str) -> None:
    assert handle_for(format_id, value) == handle


def test_redact_value_preserves_structure() -> None:
    payload = {
        "text": "key sk_live_4eC7aRm9Kx2bNw5pQj8sYd here",
        "nested": ["AKIA5MZXN8QRF3WBY6OE", {"n": 1, "b": True, "z": None}],
    }
    redacted = redact_value(payload)
    assert redacted == {
        "text": "key <mcp:secret-ref:stripe_secret_live:f11f593d> here",
        "nested": ["<mcp:secret-ref:aws_access_key:bc2c3233>", {"n": 1, "b": True, "z": None}],
    }


# ── server attribution (structural parity with server-of.ts) ─────────────────


def test_tool_roster_matches_typescript() -> None:
    assert TOOL_SERVER["read_file"] == "filesystem"
    assert TOOL_SERVER["write_query"] == "sqlite"
    assert len(TOOL_SERVER) == 20


@pytest.mark.parametrize(
    ("event", "payload", "expected"),
    [
        ("tools/call", {"name": "read_file", "arguments": {}}, "filesystem"),
        ("tools/call", {"params": {"name": "write_query"}}, "sqlite"),
        ("tools/call", {"name": "custom_tool"}, "custom_tool"),
        ("resources/read", {"uri": "https://api.example.com/x"}, "api.example.com"),
        ("resources/read", {"uri": "file:///etc/passwd"}, "file"),
        ("sampling/createMessage", {"messages": []}, "sampling"),
    ],
)
def test_server_of(event: str, payload: object, expected: str) -> None:
    assert server_of(event, payload) == expected


# ── cross-boundary guard (causal taint semantics) ────────────────────────────


async def test_relaybleed_denied_same_origin_allowed_and_causality() -> None:
    guard = create_cross_boundary_guard()
    secret = "ghp_Xa2bC3dEf4gH5iJk6Lm7nN8oP"

    # No prior read: a secret-shaped value passes (flows, not values).
    fresh = await guard.handler(_inv("request", {"name": "write_query", "arguments": {"query": secret}}))
    assert isinstance(fresh, ValidationResult) and fresh.valid

    # read_file request → response carrying the secret (taints origin=filesystem).
    await guard.handler(_inv("request", {"name": "read_file", "arguments": {"path": "/x"}}))
    ingest = await guard.handler(_inv("response", {"content": [{"type": "text", "text": secret}]}))
    assert isinstance(ingest, ValidationResult) and ingest.valid
    assert ingest.info == {"origin": "filesystem", "ingested": 1, "tainted": 1}

    # Same-origin writeback allowed.
    same = await guard.handler(_inv("request", {"name": "write_file", "arguments": {"content": secret}}))
    assert isinstance(same, ValidationResult) and same.valid

    # Cross-boundary send denied, with the exact message shape.
    crossed = await guard.handler(_inv("request", {"name": "write_query", "arguments": {"query": secret}}))
    assert isinstance(crossed, ValidationResult) and not crossed.valid
    assert crossed.severity == "error"
    assert crossed.messages is not None
    assert crossed.messages[0].message == (
        "cross-boundary secret flow: github_pat read from 'filesystem' may not be sent to 'sqlite'"
    )


async def test_sessions_are_isolated() -> None:
    guard = create_cross_boundary_guard()
    secret = "AKIA5MZXN8QRF3WBY6OE"
    await guard.handler(_inv("request", {"name": "read_file", "arguments": {}}, session_id="a"))
    await guard.handler(_inv("response", {"content": [{"type": "text", "text": secret}]}, session_id="a"))

    denied = await guard.handler(_inv("request", {"name": "write_query", "arguments": {"query": secret}}, session_id="a"))
    assert isinstance(denied, ValidationResult) and not denied.valid

    other = await guard.handler(_inv("request", {"name": "write_query", "arguments": {"query": secret}}, session_id="b"))
    assert isinstance(other, ValidationResult) and other.valid


# ── secretless redactor (mutation semantics) ─────────────────────────────────


async def test_redactor_replaces_and_reports() -> None:
    redactor = create_secretless_redactor()
    result = await redactor.handler(
        _inv("request", {"name": "write_query", "arguments": {"query": "x sk_live_4eC7aRm9Kx2bNw5pQj8sYd y"}})
    )
    assert isinstance(result, MutationResult) and result.modified
    assert result.payload == {
        "name": "write_query",
        "arguments": {"query": "x <mcp:secret-ref:stripe_secret_live:f11f593d> y"},
    }
    assert result.info == {"redacted": 1, "formats": ["stripe_secret_live"]}


async def test_redactor_keeps_clean_payloads() -> None:
    redactor = create_secretless_redactor()
    payload = {"name": "echo", "arguments": {"note": "no credentials here"}}
    result = await redactor.handler(_inv("request", payload))
    assert isinstance(result, MutationResult) and not result.modified
    assert result.payload == payload
