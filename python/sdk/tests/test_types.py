# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by a Apache-2.0
# license that can be found in the LICENSE file.

"""Unit tests for the wire types (SEP-2624)."""

import pytest

from mcp_ext_interceptors.interceptor import Invocation, Mutator, Validator, build_hooks
from mcp_ext_interceptors.types import (
    Compat,
    Hook,
    InterceptorInfo,
    InvokeInterceptorParams,
    MutationResult,
    PhasePriority,
    UnknownInterceptorResult,
    ValidationResult,
    hooks_contain_event,
    is_hook_subset,
    matches_hooks,
    parse_invoke_result,
    resolve_priority,
)


class TestWireSerialization:
    def test_descriptor_camel_case_round_trip(self) -> None:
        info = InterceptorInfo(
            name="pii-redactor",
            type="mutation",
            hooks=[Hook(events=["tools/call"], phase="request")],
            fail_open=True,
            priority_hint=PhasePriority(request=-1000, response=1000),
            compat=Compat(min_protocol="2025-06-18", max_protocol="2026-07-28"),
        )
        wire = info.model_dump(by_alias=True, mode="json", exclude_none=True)
        assert wire == {
            "name": "pii-redactor",
            "type": "mutation",
            "hooks": [{"events": ["tools/call"], "phase": "request"}],
            "mode": "active",
            "failOpen": True,
            "priorityHint": {"request": -1000, "response": 1000},
            "compat": {"minProtocol": "2025-06-18", "maxProtocol": "2026-07-28"},
        }
        assert InterceptorInfo.model_validate(wire) == info

    def test_priority_hint_accepts_bare_number(self) -> None:
        info = InterceptorInfo.model_validate({"name": "a", "type": "validation", "hooks": [], "priorityHint": -500})
        assert info.priority_hint == -500
        wire = info.model_dump(by_alias=True, exclude_none=True)
        assert wire["priorityHint"] == -500

    def test_invoke_params_camel_case(self) -> None:
        params = InvokeInterceptorParams(
            name="v", event="tools/call", phase="request", payload={"x": 1}, timeout_ms=5000
        )
        wire = params.model_dump(by_alias=True, mode="json", exclude_none=True)
        assert wire == {
            "name": "v",
            "event": "tools/call",
            "phase": "request",
            "payload": {"x": 1},
            "timeoutMs": 5000.0,
        }

    def test_defaults_omitted_fields(self) -> None:
        info = InterceptorInfo(name="v", type="validation", hooks=[])
        assert info.mode == "active"
        assert info.fail_open is False
        assert info.priority_hint is None

    def test_validation_result_wire_shape(self) -> None:
        result = ValidationResult(
            interceptor="v",
            phase="request",
            valid=False,
            severity="error",
            messages=[{"message": "bad", "severity": "error", "path": "params.x"}],  # type: ignore[list-item]
            duration_ms=1.5,
        )
        wire = result.model_dump(by_alias=True, mode="json", exclude_none=True)
        assert wire == {
            "interceptor": "v",
            "type": "validation",
            "phase": "request",
            "durationMs": 1.5,
            "valid": False,
            "severity": "error",
            "messages": [{"message": "bad", "severity": "error", "path": "params.x"}],
        }


class TestParseInvokeResult:
    def test_validation(self) -> None:
        parsed = parse_invoke_result({"type": "validation", "interceptor": "v", "phase": "request", "valid": True})
        assert isinstance(parsed, ValidationResult)
        assert parsed.valid is True

    def test_mutation(self) -> None:
        parsed = parse_invoke_result(
            {"type": "mutation", "interceptor": "m", "phase": "response", "modified": True, "payload": {"a": 1}}
        )
        assert isinstance(parsed, MutationResult)
        assert parsed.payload == {"a": 1}

    def test_unknown_type_degrades_gracefully(self) -> None:
        parsed = parse_invoke_result(
            {"type": "sink", "interceptor": "s", "phase": "request", "accepted": True, "durationMs": 2}
        )
        assert isinstance(parsed, UnknownInterceptorResult)
        assert parsed.type == "sink"
        assert parsed.duration_ms == 2

    def test_common_field_defaults_for_authors(self) -> None:
        # Handler authors construct results without the common fields;
        # the host stamps them afterwards.
        result = ValidationResult(valid=True)
        stamped = result.model_copy(update={"interceptor": "v", "phase": "response", "duration_ms": 3.0})
        assert stamped.interceptor == "v"
        assert stamped.phase == "response"


class TestResolvePriority:
    @pytest.mark.parametrize(
        ("hint", "phase", "expected"),
        [
            (None, "request", 0),
            (42, "request", 42),
            (42, "response", 42),
            (PhasePriority(request=-1000), "request", -1000),
            (PhasePriority(request=-1000), "response", 0),
            (PhasePriority(response=1000), "request", 0),
            (PhasePriority(request=-1000, response=1000), "response", 1000),
            (PhasePriority(request=0), "request", 0),
        ],
    )
    def test_matrix(self, hint, phase, expected) -> None:  # type: ignore[no-untyped-def]
        assert resolve_priority(hint, phase) == expected


class TestHookMatching:
    HOOKS = [
        Hook(events=["tools/call", "prompts/get"], phase="request"),
        Hook(events=["*"], phase="response"),
    ]

    def test_literal_match(self) -> None:
        assert matches_hooks(self.HOOKS, "tools/call", "request")
        assert matches_hooks(self.HOOKS, "tools/call", "response")  # wildcard covers response
        assert matches_hooks(self.HOOKS, "anything/else", "response")

    def test_phase_mismatch(self) -> None:
        assert not matches_hooks([Hook(events=["tools/call"], phase="request")], "tools/call", "response")

    def test_no_match(self) -> None:
        assert not matches_hooks(self.HOOKS, "resources/read", "request")

    def test_contains_event_wildcard_matches_any_filter(self) -> None:
        assert hooks_contain_event(self.HOOKS, "tools/call")
        assert hooks_contain_event(self.HOOKS, "resources/read")  # via "*" response hook
        assert not hooks_contain_event([Hook(events=["tools/call"], phase="request")], "resources/read")


class TestHookSubset:
    def test_narrowing_accepted(self) -> None:
        declared = [Hook(events=["tools/call", "prompts/get"], phase="request")]
        narrow = [Hook(events=["tools/call"], phase="request")]
        assert is_hook_subset(narrow, declared)

    def test_widening_event_rejected(self) -> None:
        declared = [Hook(events=["tools/call"], phase="request")]
        narrow = [Hook(events=["tools/call", "resources/read"], phase="request")]
        assert not is_hook_subset(narrow, declared)

    def test_widening_phase_rejected(self) -> None:
        declared = [Hook(events=["tools/call"], phase="request")]
        narrow = [Hook(events=["tools/call"], phase="response")]
        assert not is_hook_subset(narrow, declared)

    def test_wildcard_narrow_needs_wildcard_declared(self) -> None:
        declared = [Hook(events=["tools/call"], phase="request")]
        assert not is_hook_subset([Hook(events=["*"], phase="request")], declared)
        assert is_hook_subset([Hook(events=["*"], phase="request")], [Hook(events=["*"], phase="request")])

    def test_narrow_under_declared_wildcard(self) -> None:
        declared = [Hook(events=["*"], phase="request")]
        assert is_hook_subset([Hook(events=["tools/call"], phase="request")], declared)


class TestAuthorApi:
    async def _ok(self, inv: Invocation) -> ValidationResult:
        return ValidationResult(valid=True)

    async def _mutate(self, inv: Invocation) -> MutationResult:
        return MutationResult(modified=False, payload=inv.payload)

    def _info(self, type_: str) -> InterceptorInfo:
        return InterceptorInfo(name="x", type=type_, hooks=[Hook(events=["tools/call"], phase="request")])

    def test_validator_type_enforced(self) -> None:
        Validator(info=self._info("validation"), handler=self._ok)
        with pytest.raises(ValueError, match="must have type 'validation'"):
            Validator(info=self._info("mutation"), handler=self._ok)

    def test_mutator_type_enforced(self) -> None:
        Mutator(info=self._info("mutation"), handler=self._mutate)
        with pytest.raises(ValueError, match="must have type 'mutation'"):
            Mutator(info=self._info("validation"), handler=self._mutate)

    def test_build_hooks_single(self) -> None:
        assert build_hooks(events=["tools/call"], phase="request", hooks=None) == [
            Hook(events=["tools/call"], phase="request")
        ]

    def test_build_hooks_explicit(self) -> None:
        hooks = [Hook(events=["*"], phase="request"), Hook(events=["*"], phase="response")]
        assert build_hooks(events=None, phase=None, hooks=hooks) == hooks

    def test_build_hooks_rejects_ambiguity_and_emptiness(self) -> None:
        with pytest.raises(ValueError, match="not both"):
            build_hooks(events=["a"], phase="request", hooks=[Hook(events=["a"], phase="request")])
        with pytest.raises(ValueError, match="required"):
            build_hooks(events=["a"], phase=None, hooks=None)
        with pytest.raises(ValueError, match="must not be empty"):
            build_hooks(events=[], phase="request", hooks=None)
        with pytest.raises(ValueError, match="must not be empty"):
            build_hooks(events=None, phase=None, hooks=[])
