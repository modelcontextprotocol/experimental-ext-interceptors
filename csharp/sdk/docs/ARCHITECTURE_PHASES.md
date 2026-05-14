# Architecture Phases

This document captures the phased structural changes planned for the C# MCP interceptors SDK so we can reference them during implementation.

## Product Rule

The package should expose two distinct surfaces:

1. `WithInterceptors(...)` / interceptor server: pure SEP surface
2. `McpInterceptorGateway`: transparent proxy by default
3. Gateway SEP passthrough: opt-in advanced mode
4. Shared chain execution/runtime: reused by both surfaces

This lets the package support both:

1. SEP-aware clients that explicitly use `interceptors/list` and `interceptor/invoke` (chain execution is SDK-side orchestration over those two)
2. Transparent infrastructure deployments where clients do not need any code changes

## Phase 1: Split Gateway Modes

Goal: make the gateway's public behavior explicit.

Changes:

1. Add `ExposeInterceptorProtocol` to `McpInterceptorGatewayOptions`, defaulting to `false`
2. In transparent mode, do not advertise interceptor extension capability
3. In transparent mode, do not expose `interceptors/list` or `interceptor/invoke`
4. In opt-in SEP mode, preserve the current passthrough behavior

Expected result:

1. Default gateway looks like a backend proxy, not a backend-plus-admin endpoint
2. Advanced users can still enable interceptor protocol passthrough intentionally

Acceptance criteria:

1. Transparent mode does not advertise interceptor capability
2. Transparent mode does not handle `interceptor/*` requests
3. SEP mode preserves current passthrough behavior and tests

## Phase 2: Make Capability Mirroring Truly Transparent

Goal: align the gateway's advertised contract with the backend server more faithfully.

Changes:

1. Extract capability composition into a dedicated internal component or helper
2. Start from the backend `ServerCapabilities` rather than hand-mirroring only selected properties
3. Mirror standard and experimental capability surfaces, including `Experimental`, `Extensions`, and `Tasks` where present
4. Overlay interceptor capability only when `ExposeInterceptorProtocol = true`

Expected result:

1. The gateway remains aligned with the core SDK as new capabilities are added
2. The gateway can honestly describe itself as transparent by default

Acceptance criteria:

1. Transparent mode mirrors backend capabilities broadly
2. Interceptor capability is only present in SEP mode
3. Existing proxy behavior remains intact for supported primitives

## Phase 3: Separate Proxy Core from SEP Bridge

Goal: separate transparent proxy concerns from SEP passthrough concerns.

Changes:

1. Extract the built-in MCP proxying logic into a dedicated internal component
2. Extract interceptor protocol passthrough into a dedicated internal bridge component
3. Keep `InterceptorChainRunner` and related chain semantics shared
4. Centralize interceptor extension capability key handling behind one internal constant/provider

Current implementation note:

1. Transparent proxy wiring lives in `GatewayProxyConfigurator`
2. SEP bridge wiring lives in `GatewayInterceptorProtocolBridge`
3. The extension capability key is centralized in `InterceptorProtocolConstants`

Expected result:

1. Transparent proxy and SEP bridge can evolve independently
2. The gateway type becomes a coordinator rather than a monolith

Acceptance criteria:

1. Proxy wiring can be enabled without SEP bridge wiring
2. SEP bridge can be enabled or disabled explicitly
3. Internal tests can target the components independently

## Phase 4: Improve DI and Runtime Lifecycle Shape

Goal: align the builder path more closely with the core C# SDK's composition model.

Changes:

1. Keep the current `WithInterceptorGateway(options)` overload for convenience
2. Add a DI-native overload based on a factory or service-provider callback
3. Refactor notification forwarding into an internal connection-bound forwarder abstraction
4. Treat transport session identifiers as an implementation detail, not a public concept

Expected result:

1. The builder path stays declarative and SDK-native
2. Runtime ownership of backend/interceptor connections becomes easier to evolve
3. The implementation remains compatible with current session-bearing transports without depending on sessions semantically

Acceptance criteria:

1. Existing convenience builder API continues to work
2. A service-provider-based configuration path exists
3. Notification forwarding logic is isolated from proxy handler registration

Current implementation note:

1. Builder DI now supports both direct options and `Func<IServiceProvider, McpInterceptorGatewayOptions>`
2. Connection-bound forwarding registration lives in `GatewayConnectionForwardingRegistrar`
3. Transport session identifiers are only used as an internal deduplication mechanism where available

## Phase 5: Follow-Up Cleanup

Goal: tighten API roles after the bigger structural changes are done.

Changes:

1. Keep extension methods as the primary low-level SEP client API
2. Keep `InterceptingMcpClient` as a convenience/orchestration layer rather than the canonical abstraction
3. Consider clarifying or making explicit wrapped-client disposal ownership
4. Consolidate any duplicated transparent interception flow that remains after Phases 1-4

Expected result:

1. Public API roles are clearer
2. Future maintenance burden is lower

This phase is intentionally lower priority than Phases 1-4.

## Future Feature: External SEP-Exposing Servers

This comes after the phase work above.

Goal: allow transparent gateways to source interceptors from external SEP servers rather than only from in-process/directly-provided clients.

Possible outcomes:

1. Configuration-driven gateways (for example `mcp-interceptors.json`)
2. Auth-aware or per-user interceptor selection
3. Custom MCP middleware built on top of the extension's primitives

Likely direction:

1. Introduce an interceptor client/provider abstraction in addition to direct `InterceptorClients`
2. Support static, per-request, or per-identity interceptor resolution
3. Keep chain semantics unchanged regardless of interceptor source

Current implementation note:

1. `McpInterceptorGateway.CreateAsync(...)` now supports static external interceptor connections via `McpInterceptorServerConnectionOptions`
2. Those connections use the same transport-driven pattern as `McpClient.CreateAsync(...)`
3. Dynamic per-request interceptor resolution is now supported for the transparent proxy path via `InterceptorServerConnectionResolver`
4. SEP passthrough still requires statically configured interceptor clients

## Open Design Notes

1. The interceptor extension capability key should stay aligned with the SEP for now
2. If the SEP changes to a reverse-domain extension identifier, we should centralize the key and support a transition path
3. Transparent mode should be the default because it matches the expected operational use of the gateway
