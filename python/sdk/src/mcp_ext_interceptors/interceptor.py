# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by a Apache-2.0
# license that can be found in the LICENSE file.

"""Author-side API for writing interceptors (SEP-2624).

Handlers receive an :class:`Invocation` and return the type-specific result
fields; the hosting side stamps the common wire fields (`interceptor`,
`phase`, `durationMs`) before returning them to the invoker.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable, Sequence
from dataclasses import dataclass
from typing import Any

from mcp_ext_interceptors.types import (
    TYPE_MUTATION,
    TYPE_VALIDATION,
    Hook,
    InterceptorInfo,
    InvocationContext,
    MutationResult,
    Phase,
    ValidationResult,
)


@dataclass(kw_only=True, frozen=True)
class Invocation:
    """Read-only view of one interceptor invocation, passed to handlers.

    Treat `payload` as immutable: validators for the same event run
    concurrently and share it. Mutators return a new payload in their result
    instead of modifying this one in place.
    """

    event: str
    phase: Phase
    payload: Any
    config: dict[str, Any] | None = None
    context: InvocationContext | None = None


ValidatorHandler = Callable[[Invocation], Awaitable[ValidationResult]]
MutatorHandler = Callable[[Invocation], Awaitable[MutationResult]]


@dataclass(kw_only=True)
class Validator:
    """A validation interceptor: descriptor plus its handler."""

    info: InterceptorInfo
    handler: ValidatorHandler

    def __post_init__(self) -> None:
        if self.info.type != TYPE_VALIDATION:
            raise ValueError(f"Validator {self.info.name!r} must have type {TYPE_VALIDATION!r}, got {self.info.type!r}")


@dataclass(kw_only=True)
class Mutator:
    """A mutation interceptor: descriptor plus its handler."""

    info: InterceptorInfo
    handler: MutatorHandler

    def __post_init__(self) -> None:
        if self.info.type != TYPE_MUTATION:
            raise ValueError(f"Mutator {self.info.name!r} must have type {TYPE_MUTATION!r}, got {self.info.type!r}")


RegisteredInterceptor = Validator | Mutator


def build_hooks(
    *,
    events: Sequence[str] | None,
    phase: Phase | None,
    hooks: Sequence[Hook] | None,
) -> list[Hook]:
    """Build the hooks list from either `events`+`phase` or explicit `hooks`."""
    if hooks is not None:
        if events is not None or phase is not None:
            raise ValueError("pass either hooks= or events=/phase=, not both")
        if not hooks:
            raise ValueError("hooks must not be empty")
        return list(hooks)
    if events is None or phase is None:
        raise ValueError("events= and phase= are required when hooks= is not given")
    if not events:
        raise ValueError("events must not be empty")
    return [Hook(events=list(events), phase=phase)]
