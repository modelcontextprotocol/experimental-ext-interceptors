//! Rust support for the experimental MCP interceptors extension.
//!
//! The crate provides four layers:
//! - protocol types for `interceptors/list` and `interceptor/invoke`;
//! - [`InterceptorChain`] for discovery and direction-aware execution;
//! - [`InterceptorMiddleware`] plus `rmcp` adapters for incoming and outgoing calls.
//! - [`InterceptorExtension`] for hosting interceptors on an `rmcp` server.

mod chain;
mod client;
mod middleware;
mod protocol;
mod server;

pub use async_trait::async_trait;
pub use chain::*;
pub use client::*;
pub use middleware::*;
pub use protocol::*;
pub use server::*;
