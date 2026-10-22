# MCP Interceptors - Rust Implementation

Rust implementation of the MCP Interceptor Extension described in
[SEP-2624](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624).

The crate provides:

- protocol types for `interceptors/list` and `interceptor/invoke`;
- automatic discovery across one or more interceptor servers;
- `InterceptorChain::execute` with SEP ordering, audit mode, fail-open, timeouts,
  hook filtering, and atomic mutations;
- incoming and outgoing `InterceptorMiddleware`;
- adapters for [`rmcp`](https://crates.io/crates/rmcp) clients;
- typed interceptor hosting for `rmcp` servers.

Chain execution remains client-side. Server hosting only exposes interceptor discovery
and invocation. Sink interceptors are not implemented.

## Requirements

- Rust 1.88 or newer
- `rmcp` 3.4.1 or a semver-compatible 3.x release

## Installation

Until the crate is published, Cargo can load it directly from this repository and will
find the package under `rust/sdk/`. Pin a commit for reproducible builds:

```toml
[dependencies]
mcp-ext-interceptors = { git = "https://github.com/modelcontextprotocol/experimental-ext-interceptors", rev = "<commit-sha>" }
```

## Chain Execution

Discovery calls `interceptors/list` on every supplied interceptor server. Execution
then selects matching hooks automatically; `ChainExecution::interceptors` is only an
optional allowlist, not a required manual selection step.

```rust,ignore
use mcp_ext_interceptors::{
    ChainExecution, Direction, InterceptorChain, Phase, events,
};
use serde_json::json;

let chain = InterceptorChain::discover_rmcp(interceptor_peers, None).await?;
let result = chain
    .execute(ChainExecution::new(
        events::TOOLS_CALL,
        Phase::Request,
        Direction::Sending,
        json!({ "name": "get_weather", "arguments": {} }),
    ))
    .await;

if result.is_success() {
    send_to_mcp_server(result.final_payload).await?;
}
```

`Direction` is explicit because a phase alone does not identify the trust-boundary
order. The chain uses these orders:

| Direction | Order |
|-----------|-------|
| `Sending` | mutate, then validate |
| `Receiving` | validate, then mutate |

Request payloads are MCP `params` objects. Response payloads are MCP result objects,
matching the existing C# and Go implementations.

## Server Authoring

Implement `MutationInterceptor` or `ValidationInterceptor`, register it, and wrap an
existing `rmcp` server. The wrapper advertises the extension capability and handles
`interceptors/list` and `interceptor/invoke`; all other MCP methods are delegated.

```rust,ignore
use mcp_ext_interceptors::{InterceptorExtension, MutationInterceptor};

let mut interceptors = InterceptorExtension::new();
interceptors.register_mutator(MyMutator::new())?;

let server = interceptors.wrap(my_rmcp_server);
server.serve(transport).await?;
```

The complete implementation is in [`examples/server.rs`](examples/server.rs).

## Middleware

Use `outgoing` for client-to-server operations and `incoming` for server-to-client
operations. Each method applies both request and response phases around `next`.

```rust,ignore
use mcp_ext_interceptors::{InterceptorMiddleware, InterceptorPeerExt, MiddlewareOptions};

let target = target_peer.with_interceptors(interceptor_peers).await?;
let response = target.call_tool_once(params).await?;

let middleware = InterceptorMiddleware::new(chain.into());
let client_service = middleware.wrap_client_service(
    client_handler,
    MiddlewareOptions::default(),
);
```

`InterceptingPeer` covers requests sent through its `send_request` method.
It also provides the typed `call_tool_once` convenience method; use
`InterceptingPeer::discover` when discovery filters or middleware options are needed.
`InterceptingClientService` implements `rmcp::Service<RoleClient>` and covers incoming
sampling, elicitation, roots, and custom requests. Calls made directly through the
original `rmcp::Peer` bypass the wrapper.

Run the end-to-end typed `tools/call` client/server example in two terminals. The
server accepts multiple clients and runs until interrupted.

```sh
# Terminal 1
cargo run --example server

# Terminal 2
cargo run --example client
```

Set `MCP_EXAMPLE_ADDR` in both terminals to use an address other than
`127.0.0.1:8765`. The implementation is in [`examples/client.rs`](examples/client.rs)
and [`examples/server.rs`](examples/server.rs).

