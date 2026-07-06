# SEP-2624 Conformance Notes — Python SDK

This maps the SEP's normative requirements to their implementation, records the pending SEP amendments this SDK anticipates, and lists deliberate divergences from the sibling SDKs in this repo. The SEP baseline is [`docs/sep.md`](../../../docs/sep.md) as amended by PR #24 (InterceptorOverrides) and PR #25 (SEP-2133 extensions capability key), both approved by the WG at the time of writing.

## Wire protocol

| SEP clause | Implementation |
|---|---|
| Methods `interceptors/list` (plural) and `interceptor/invoke` (singular) | `types.METHOD_LIST`, `types.METHOD_INVOKE`; bound in `server.Interceptors.methods()` |
| Interceptor descriptor fields (`name`, `type`, `hooks`, `mode`, `failOpen`, `priorityHint`, `compat`, `configSchema`) | `types.InterceptorInfo`, camelCase on the wire via `InterceptorModel` |
| `priorityHint: number \| {request?, response?}` | `types.PriorityHint = int \| PhasePriority`; resolution in `types.resolve_priority` per the SEP's `resolvePriority` |
| Invoke result is the flat `ValidationResult` / `MutationResult` body | `server.Interceptors._handle_invoke` returns the flat model; asserted byte-for-byte in `tests/test_server_extension.py::TestInvoke::test_wire_shape_is_flat` |
| Timeout error `-32000`, validation failure `-32602`, execution failure `-32603` | `types.INTERCEPTOR_TIMEOUT` / `INTERCEPTOR_VALIDATION_FAILED` / `INTERCEPTOR_MUTATION_FAILED`; raised in `_handle_invoke` |
| `timeoutMs` MUST cancel execution | `anyio.fail_after` in `_handle_invoke` (server side) and `chain.Chain._send` (invoker side, guarding against servers that don't enforce it) |
| Capability `capabilities.extensions["io.modelcontextprotocol/interceptors"] = {supportedEvents}` (PR #25 shape) | `Interceptors.settings()`; delivered by the mcp v2 extension mechanism |

## Execution model (chain)

| SEP clause | Implementation |
|---|---|
| Sending: Mutate → Validate; Receiving: Validate → Mutate | `chain.Chain.execute` orders stages by `direction` |
| Mutations sequential by priority, alphabetical tie-break | mutator sort key `(effective_priority(phase), name)` |
| Mutations atomic (all-or-none) | `final_payload` is only set on `status == "success"`; a mid-chain failure leaves the caller's original payload standing |
| Validations parallel; all complete before rejecting | `anyio.create_task_group` in `_run_validators`; outcomes recorded in entry order after the group completes |
| Only `severity: "error"` blocks; `warn`/`info` never block | `chain._blocking_severity`; an invalid result with no severity fails secure (treated as error) |
| Audit mode never blocks; mutators in audit compute but don't apply | audit checks in `_run_validators` / `_run_mutators` (shadow mutations recorded in `results`, payload untouched) |
| `failOpen: true` lets a crashed/timed-out interceptor pass; default fail-closed | `effective_fail_open()` consulted on every error path |
| InterceptorOverrides checked before descriptor values; hooks may only narrow (PR #24) | `chain.InterceptorOverrides`, `ChainEntry.effective_*()`; widening rejected with `ValueError` in `Chain.add_entry` |

## Open-union posture (future `sink` type)

PR #28 (reintroducing a third `sink` interceptor type) is unreviewed, so this SDK implements only `validation` and `mutation` — but the type fields are open strings, invoke results are parsed through `types.parse_invoke_result` (unknown types degrade to `UnknownInterceptorResult` instead of failing validation), and the chain logs-and-skips entries with unknown types. Adding sink later is one new model plus a dispatch arm, not a breaking change.

## Deliberate divergences from sibling SDKs

- **Mode string**: this SDK uses `"active"` per the revised SEP. The Go SDK still uses `"enforce"` (tracked in issue #15 / PR #17 for C#).
- **Invoke result shape**: this SDK returns the SEP's flat result body. The Go SDK nests it under `validation`/`mutation` keys in `InvokeResult` (wire.go) — that nesting is not SEP text.
- **Capability key**: this SDK uses `extensions["io.modelcontextprotocol/interceptors"]` per PR #25. Go used `experimental["io.modelcontextprotocol/interceptors"]`, C# used `extensions["interceptors"]` before that PR.
- **`interceptors/list` event filter**: a `"*"`-hooked interceptor matches every event filter here (SEP-truer reading); the Go SDK only matches `"*"` against a literal `"*"` filter.
- **No `sink` type**: C# ships one; the current SEP deliberately replaced it with audit mode.
- **No `"both"` phase value**: Go accepts `PhaseBoth` as an SDK convenience; here an interceptor on both phases declares two hook entries (pass `hooks=[...]` to the decorators), keeping the wire vocabulary exactly the SEP's.

## Flags for WG discussion

- **Direction vs phase**: the SEP orders execution by data-flow direction (sending/receiving), but the chain APIs in this repo key it off phase (request → receiving, response → sending), which encodes the server-side perspective. A client guarding a request it is about to send is on the *sending* side. This SDK follows the Go mapping by default but exposes `ChainExecutionParams.direction` to override; the SEP could make this explicit.
- **`abortedAt` with parallel validators**: the SEP field is singular; when several validators fail in parallel this SDK reports the first blocking failure in chain-entry order (all results still appear in `results` and the summary).
- **Era gap for capability advertisement**: on 2025-11-25 handshake connections `capabilities.extensions` does not exist on the wire, so discovery of interceptor support silently degrades to probing `interceptors/list`. Worth a note in the SEP's backward-compatibility section.
- **`-32602` overloaded for routing errors**: the SEP assigns `-32602` the specific meaning "Interceptor validation failed", with a `data.validationErrors[]` payload. This SDK never emits `-32602` for an actual validation *verdict* (a failing validator returns a normal `ValidationResult(valid=false)` body and the chain decides to abort); instead `_handle_invoke` reuses `-32602` for the routing/bad-request cases "interceptor not found" and "does not hook this event/phase", whose `data` is `{interceptor, event, phase}` — not `validationErrors`. A client that special-cases `-32602` to parse `validationErrors` would misread these. The SEP should either carve out a distinct code for not-found/hook-mismatch or acknowledge the raw JSON-RPC "invalid params" meaning alongside the interceptor-specific one.
- **SEP internal inconsistencies this SDK resolves silently**:
  - *Method names* — §"Design Rationale → Method Names" writes `interceptor/list`; §"Backward Compatibility" writes `interceptors/list` and `interceptors/invoke`; the normative request bodies use `interceptors/list` + `interceptor/invoke`. This SDK implements the normative-request-body pair (`METHOD_LIST = "interceptors/list"`, `METHOD_INVOKE = "interceptor/invoke"`).
  - *Error `data` keys* — the mutation-failure example uses `failedInterceptor`/`lastValidPayload`; the generic execution-failure example uses `interceptor`/`reason`. This SDK uses `interceptor`/`reason` uniformly.

## Test coverage

`uv run pytest` — 79 tests: wire-shape round-trips (`test_types.py`), server hosting incl. error codes, timeouts, and both protocol eras via `Client(mode=...)` parametrization (`test_server_extension.py`, `test_client.py`), and the full chain matrix — ordering, parallelism, blocking, audit, failOpen, atomicity, timeouts, overrides (`test_chain.py`).
