use async_trait::async_trait;
use rmcp::{
    Peer, RoleClient, ServiceError,
    model::{ClientRequest, CustomRequest, ServerResult},
};
use serde_json::Value;
use thiserror::Error;

use crate::{
    InvokeInterceptorParams, ListInterceptorsParams, ListInterceptorsResult, METHOD_INVOKE,
    METHOD_LIST,
};

#[derive(Debug, Error)]
pub enum InterceptorClientError {
    #[error("MCP interceptor request failed: {0}")]
    Service(#[from] ServiceError),
    #[error("failed to encode interceptor request: {0}")]
    Encode(#[source] serde_json::Error),
    #[error("failed to decode interceptor response: {0}")]
    Decode(#[source] serde_json::Error),
    #[error("interceptor server returned an unexpected MCP result: {0}")]
    UnexpectedResult(String),
    #[error("interceptor timed out after {timeout_ms} ms")]
    Timeout { timeout_ms: u64 },
    #[error("{0}")]
    Other(String),
}

impl InterceptorClientError {
    pub fn other(message: impl Into<String>) -> Self {
        Self::Other(message.into())
    }
}

#[async_trait]
pub trait InterceptorClient: Send + Sync {
    async fn list(
        &self,
        event: Option<&str>,
    ) -> Result<ListInterceptorsResult, InterceptorClientError>;

    async fn invoke(
        &self,
        params: InvokeInterceptorParams,
    ) -> Result<Value, InterceptorClientError>;
}

#[derive(Debug, Clone)]
pub struct RmcpInterceptorClient {
    peer: Peer<RoleClient>,
}

impl RmcpInterceptorClient {
    pub fn new(peer: Peer<RoleClient>) -> Self {
        Self { peer }
    }

    pub fn peer(&self) -> &Peer<RoleClient> {
        &self.peer
    }

    async fn request(
        &self,
        method: &'static str,
        params: Value,
    ) -> Result<Value, InterceptorClientError> {
        let result = self
            .peer
            .send_request(ClientRequest::CustomRequest(CustomRequest::new(
                method,
                Some(params),
            )))
            .await?;

        match result {
            ServerResult::CustomResult(result) => Ok(result.0),
            other => Err(InterceptorClientError::UnexpectedResult(format!(
                "{other:?}"
            ))),
        }
    }
}

#[async_trait]
impl InterceptorClient for RmcpInterceptorClient {
    async fn list(
        &self,
        event: Option<&str>,
    ) -> Result<ListInterceptorsResult, InterceptorClientError> {
        let params = serde_json::to_value(ListInterceptorsParams {
            event: event.map(str::to_owned),
        })
        .map_err(InterceptorClientError::Encode)?;
        let result = self.request(METHOD_LIST, params).await?;
        serde_json::from_value(result).map_err(InterceptorClientError::Decode)
    }

    async fn invoke(
        &self,
        params: InvokeInterceptorParams,
    ) -> Result<Value, InterceptorClientError> {
        let params = serde_json::to_value(params).map_err(InterceptorClientError::Encode)?;
        self.request(METHOD_INVOKE, params).await
    }
}
