# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Server-side hosting of interceptors as an MCP extension (SEP-2624).

`Interceptors` is an mcp v2 `Extension`: pass it to `MCPServer(extensions=[...])`
and it serves `interceptors/list` and `interceptor/invoke` and advertises the
`io.modelcontextprotocol/interceptors` capability with the supported events.

Register every interceptor *before* constructing the `MCPServer`: the server
snapshots the capability settings at construction time. Interceptors added
later are still served by list/invoke but will not appear in `supportedEvents`.

Note that on legacy-handshake connections (protocol 2025-11-25 and older) the
wire schema has no `capabilities.extensions`, so the capability advertisement
is dropped even though both methods keep working; clients should treat an
absent capability as "unknown" and probe `interceptors/list`.
"""

from __future__ import annotations

import time
from collections.abc import Callable, Sequence
from typing import Any

import anyio
from mcp.server.context import ServerRequestContext
from mcp.server.extension import Extension, MethodBinding
from mcp.server.lowlevel.server import Server
from mcp.shared.exceptions import MCPError

from mcp_ext_interceptors.interceptor import (
    Invocation,
    Mutator,
    MutatorHandler,
    RegisteredInterceptor,
    Validator,
    ValidatorHandler,
    build_hooks,
)
from mcp_ext_interceptors.types import (
    EXTENSION_ID,
    INTERCEPTOR_MUTATION_FAILED,
    INTERCEPTOR_TIMEOUT,
    INTERCEPTOR_VALIDATION_FAILED,
    METHOD_INVOKE,
    METHOD_LIST,
    TYPE_MUTATION,
    TYPE_VALIDATION,
    Compat,
    Hook,
    InterceptorInfo,
    InvokeInterceptorParams,
    ListInterceptorsParams,
    ListInterceptorsResult,
    Mode,
    MutationResult,
    Phase,
    PriorityHint,
    ValidationResult,
    hooks_contain_event,
    matches_hooks,
)


class Interceptors(Extension):
    """Hosts registered interceptors on an MCP server.

    Usage::

        interceptors = Interceptors()

        @interceptors.validator("block-dangerous", events=["tools/call"], phase="request")
        async def check(inv: Invocation) -> ValidationResult:
            return ValidationResult(valid="rm -rf" not in str(inv.payload))

        server = MCPServer("demo", extensions=[interceptors])
    """

    identifier = EXTENSION_ID

    def __init__(self, interceptors: Sequence[RegisteredInterceptor] = ()) -> None:
        self._by_name: dict[str, RegisteredInterceptor] = {}
        for interceptor in interceptors:
            self.add(interceptor)

    def add(self, interceptor: RegisteredInterceptor) -> Interceptors:
        """Register an interceptor; returns self for chaining."""
        name = interceptor.info.name
        if name in self._by_name:
            raise ValueError(f"interceptor {name!r} is already registered")
        self._by_name[name] = interceptor
        return self

    def validator(
        self,
        name: str,
        *,
        events: Sequence[str] | None = None,
        phase: Phase | None = None,
        hooks: Sequence[Hook] | None = None,
        version: str | None = None,
        description: str | None = None,
        mode: Mode = "active",
        fail_open: bool = False,
        compat: Compat | None = None,
        config_schema: dict[str, Any] | None = None,
    ) -> Callable[[ValidatorHandler], ValidatorHandler]:
        """Decorator registering an async validator handler.

        Pass either `events` and `phase` (one hook entry) or explicit `hooks`.
        Validators have no `priority_hint`: the SEP ignores it for validation.
        """

        def decorator(fn: ValidatorHandler) -> ValidatorHandler:
            info = InterceptorInfo(
                name=name,
                version=version,
                description=description,
                type=TYPE_VALIDATION,
                hooks=build_hooks(events=events, phase=phase, hooks=hooks),
                mode=mode,
                fail_open=fail_open,
                compat=compat,
                config_schema=config_schema,
            )
            self.add(Validator(info=info, handler=fn))
            return fn

        return decorator

    def mutator(
        self,
        name: str,
        *,
        events: Sequence[str] | None = None,
        phase: Phase | None = None,
        hooks: Sequence[Hook] | None = None,
        version: str | None = None,
        description: str | None = None,
        mode: Mode = "active",
        fail_open: bool = False,
        priority_hint: PriorityHint | None = None,
        compat: Compat | None = None,
        config_schema: dict[str, Any] | None = None,
    ) -> Callable[[MutatorHandler], MutatorHandler]:
        """Decorator registering an async mutator handler."""

        def decorator(fn: MutatorHandler) -> MutatorHandler:
            info = InterceptorInfo(
                name=name,
                version=version,
                description=description,
                type=TYPE_MUTATION,
                hooks=build_hooks(events=events, phase=phase, hooks=hooks),
                mode=mode,
                fail_open=fail_open,
                priority_hint=priority_hint,
                compat=compat,
                config_schema=config_schema,
            )
            self.add(Mutator(info=info, handler=fn))
            return fn

        return decorator

    # -- Extension contract ------------------------------------------------

    def settings(self) -> dict[str, Any]:
        """Capability payload: the union of all hooked events."""
        events = {event for i in self._by_name.values() for hook in i.info.hooks for event in hook.events}
        return {"supportedEvents": sorted(events)}

    def methods(self) -> Sequence[MethodBinding]:
        return (
            MethodBinding(METHOD_LIST, ListInterceptorsParams, self._handle_list),
            MethodBinding(METHOD_INVOKE, InvokeInterceptorParams, self._handle_invoke),
        )

    def install_lowlevel(self, server: Server[Any]) -> None:
        """Escape hatch for lowlevel `Server` users not going through `MCPServer`."""
        for binding in self.methods():
            server.add_request_handler(binding.method, binding.params_type, binding.handler)
        server.extensions[EXTENSION_ID] = self.settings()

    # -- Method handlers ----------------------------------------------------

    async def _handle_list(
        self, ctx: ServerRequestContext[Any, Any], params: ListInterceptorsParams
    ) -> ListInterceptorsResult:
        infos = [i.info for i in self._by_name.values()]
        if params.event is not None:
            infos = [info for info in infos if hooks_contain_event(info.hooks, params.event)]
        return ListInterceptorsResult(interceptors=infos)

    async def _handle_invoke(
        self, ctx: ServerRequestContext[Any, Any], params: InvokeInterceptorParams
    ) -> ValidationResult | MutationResult:
        interceptor = self._by_name.get(params.name)
        if interceptor is None:
            raise MCPError(
                code=INTERCEPTOR_VALIDATION_FAILED,
                message=f"Interceptor {params.name!r} not found",
                data={"interceptor": params.name},
            )
        if not matches_hooks(interceptor.info.hooks, params.event, params.phase):
            raise MCPError(
                code=INTERCEPTOR_VALIDATION_FAILED,
                message=f"Interceptor {params.name!r} does not hook event {params.event!r} in phase {params.phase!r}",
                data={"interceptor": params.name, "event": params.event, "phase": params.phase},
            )

        invocation = Invocation(
            event=params.event,
            phase=params.phase,
            payload=params.payload,
            config=params.config,
            context=params.context,
        )
        start = time.monotonic()
        try:
            if params.timeout_ms is not None:
                # move_on_after rather than fail_after: only a deadline expiry
                # becomes INTERCEPTOR_TIMEOUT; a TimeoutError raised by the
                # handler itself is an ordinary execution failure below.
                with anyio.move_on_after(params.timeout_ms / 1000) as scope:
                    result = await interceptor.handler(invocation)
                if scope.cancelled_caught:
                    raise MCPError(
                        code=INTERCEPTOR_TIMEOUT,
                        message="Interceptor execution timeout",
                        data={"interceptor": params.name, "timeoutMs": params.timeout_ms, "phase": params.phase},
                    )
            else:
                result = await interceptor.handler(invocation)
        except MCPError:
            raise
        except Exception as exc:
            raise MCPError(
                code=INTERCEPTOR_MUTATION_FAILED,
                message="Interceptor execution failed",
                data={"interceptor": params.name, "reason": str(exc)},
            ) from exc

        expected_type = interceptor.info.type
        if result.type != expected_type:
            raise MCPError(
                code=INTERCEPTOR_MUTATION_FAILED,
                message="Interceptor execution failed",
                data={
                    "interceptor": params.name,
                    "reason": f"handler returned a {result.type!r} result for a {expected_type!r} interceptor",
                },
            )

        duration_ms = (time.monotonic() - start) * 1000
        return result.model_copy(update={"interceptor": params.name, "phase": params.phase, "duration_ms": duration_ms})
