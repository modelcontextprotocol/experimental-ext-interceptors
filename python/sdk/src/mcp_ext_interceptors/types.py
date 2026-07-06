# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Wire types and constants for MCP Interceptors (SEP-2624).

Models follow the SEP text as amended by the pending capability-key change
(SEP-2133 extensions format) and the InterceptorOverrides chain extension.
All models serialize camelCase on the wire and accept snake_case in Python,
matching the mcp-types conventions.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from typing import Any, Final, Literal

import mcp_types
from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel

# JSON-RPC methods defined by the SEP. Note: list is plural, invoke is singular.
METHOD_LIST: Final = "interceptors/list"
METHOD_INVOKE: Final = "interceptor/invoke"

# Extension identifier used as the capability key under `capabilities.extensions`
# (SEP-2133 format, per the capability-alignment amendment to SEP-2624).
EXTENSION_ID: Final = "io.modelcontextprotocol/interceptors"

# Interceptor types. The SEP defines exactly these two; the `type` fields below
# are typed as open strings so a future third type is not a breaking change.
TYPE_VALIDATION: Final = "validation"
TYPE_MUTATION: Final = "mutation"

# Lifecycle events defined by the SEP. The set is open: implementations may
# define additional `namespace/operation` events.
EVENT_TOOLS_LIST: Final = "tools/list"
EVENT_TOOLS_CALL: Final = "tools/call"
EVENT_PROMPTS_LIST: Final = "prompts/list"
EVENT_PROMPTS_GET: Final = "prompts/get"
EVENT_RESOURCES_LIST: Final = "resources/list"
EVENT_RESOURCES_READ: Final = "resources/read"
EVENT_RESOURCES_SUBSCRIBE: Final = "resources/subscribe"
EVENT_SAMPLING_CREATE_MESSAGE: Final = "sampling/createMessage"
EVENT_ELICITATION_CREATE: Final = "elicitation/create"
EVENT_ROOTS_LIST: Final = "roots/list"
EVENT_LLM_COMPLETION: Final = "llm/completion"
EVENT_WILDCARD: Final = "*"

# JSON-RPC error codes assigned by the SEP.
INTERCEPTOR_TIMEOUT: Final = -32000
INTERCEPTOR_VALIDATION_FAILED: Final = mcp_types.INVALID_PARAMS  # -32602
INTERCEPTOR_MUTATION_FAILED: Final = mcp_types.INTERNAL_ERROR  # -32603

Phase = Literal["request", "response"]
Mode = Literal["active", "audit"]
Severity = Literal["info", "warn", "error"]


class InterceptorModel(BaseModel):
    """Base class for all interceptor wire types: camelCase on the wire."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class Hook(InterceptorModel):
    """One hook entry: a set of lifecycle events and a single phase."""

    events: list[str]
    phase: Phase


class PhasePriority(InterceptorModel):
    """Per-phase priority values; a missing phase falls back to the default 0."""

    request: int | None = None
    response: int | None = None


PriorityHint = int | PhasePriority


class Compat(InterceptorModel):
    """Protocol version compatibility range declared by an interceptor."""

    min_protocol: str
    max_protocol: str | None = None


class Principal(InterceptorModel):
    """Identity on whose behalf an interceptor invocation runs."""

    type: Literal["user", "service", "anonymous"]
    id: str | None = None
    claims: dict[str, Any] | None = None


class InvocationContext(InterceptorModel):
    """Optional context information passed along with an invocation."""

    principal: Principal | None = None
    trace_id: str | None = None
    span_id: str | None = None
    timestamp: str | None = None
    session_id: str | None = None


class InterceptorInfo(InterceptorModel):
    """The interceptor descriptor, as listed by `interceptors/list`."""

    name: str
    version: str | None = None
    description: str | None = None
    type: str
    hooks: list[Hook]
    mode: Mode = "active"
    fail_open: bool = False
    priority_hint: PriorityHint | None = None
    compat: Compat | None = None
    config_schema: dict[str, Any] | None = None


class ValidationMessage(InterceptorModel):
    message: str
    severity: Severity
    path: str | None = None


class ValidationSuggestion(InterceptorModel):
    path: str
    value: Any = None


class _ResultCommon(InterceptorModel):
    # `interceptor` and `phase` default to placeholder values so handler
    # authors can construct results from the type-specific fields alone; the
    # serving side stamps the real values before returning them on the wire.
    interceptor: str = ""
    phase: Phase = "request"
    duration_ms: float | None = None
    info: dict[str, Any] | None = None


class ValidationResult(_ResultCommon):
    """Result of a validation interceptor. Validators never modify the payload."""

    type: Literal["validation"] = "validation"
    valid: bool
    severity: Severity | None = None
    messages: list[ValidationMessage] | None = None
    suggestions: list[ValidationSuggestion] | None = None


class MutationResult(_ResultCommon):
    """Result of a mutation interceptor: the (possibly) transformed payload."""

    type: Literal["mutation"] = "mutation"
    modified: bool
    payload: Any = None


class UnknownInterceptorResult(_ResultCommon):
    """Fallback for invoke results whose `type` this SDK version does not know.

    Keeps the result union open: a server speaking a newer SEP revision with an
    additional interceptor type degrades to this instead of a validation error.
    """

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="allow")

    type: str


AnyInvokeResult = ValidationResult | MutationResult | UnknownInterceptorResult


def parse_invoke_result(raw: Mapping[str, Any]) -> AnyInvokeResult:
    """Parse a raw `interceptor/invoke` result body, tolerating unknown types."""
    result_type = raw.get("type")
    if result_type == TYPE_VALIDATION:
        return ValidationResult.model_validate(raw)
    if result_type == TYPE_MUTATION:
        return MutationResult.model_validate(raw)
    return UnknownInterceptorResult.model_validate(raw)


class InterceptorsCapability(InterceptorModel):
    """Value advertised at `capabilities.extensions["io.modelcontextprotocol/interceptors"]`."""

    supported_events: list[str]


class ListInterceptorsParams(mcp_types.RequestParams):
    """Params for `interceptors/list`."""

    event: str | None = None


class ListInterceptorsResult(mcp_types.Result):
    """Result of `interceptors/list`."""

    interceptors: list[InterceptorInfo]


class InvokeInterceptorParams(mcp_types.RequestParams):
    """Params for `interceptor/invoke`."""

    name: str
    event: str
    phase: Phase
    payload: Any = None
    config: dict[str, Any] | None = None
    timeout_ms: float | None = None
    context: InvocationContext | None = None


def resolve_priority(hint: PriorityHint | None, phase: Phase) -> int:
    """Resolve the effective priority for a phase, per the SEP's resolvePriority."""
    if hint is None:
        return 0
    if isinstance(hint, int):
        return hint
    value = hint.request if phase == "request" else hint.response
    return 0 if value is None else value


def matches_hooks(hooks: Sequence[Hook], event: str, phase: Phase) -> bool:
    """Whether any hook entry covers the given event and phase.

    An event is covered by a literal match or by the `"*"` wildcard.
    """
    return any(hook.phase == phase and (event in hook.events or EVENT_WILDCARD in hook.events) for hook in hooks)


def hooks_contain_event(hooks: Sequence[Hook], event: str) -> bool:
    """Whether any hook entry (in either phase) covers the given event.

    Used for the `interceptors/list` event filter. Wildcard-hooked
    interceptors match every event filter.
    """
    return any(event in hook.events or EVENT_WILDCARD in hook.events for hook in hooks)


def is_hook_subset(narrow: Sequence[Hook], declared: Sequence[Hook]) -> bool:
    """Whether every event/phase pair in `narrow` is covered by `declared`.

    Chain-entry hook overrides may only narrow the declared hooks, never widen
    them. A wildcard in `narrow` is only covered by a wildcard in `declared`.
    """
    return all(matches_hooks(declared, event, hook.phase) for hook in narrow for event in hook.events)
