use std::{borrow::Cow, fmt, future::Future, sync::Arc};

use rmcp::{
    ErrorData, Peer, RoleClient, Service, ServiceError,
    model::{
        CallToolRequest, CallToolRequestParams, CallToolResponse, ClientRequest, ClientResult,
        CustomResult, ProtocolVersion, ServerNotification, ServerRequest, ServerResult,
    },
    service::{NotificationContext, RequestContext},
};
use serde_json::{Map, Value};
use thiserror::Error;

use crate::{
    ChainBuildError, ChainExecution, ChainExecutionResult, ChainStatus, Direction,
    InterceptorChain, InvocationContext, METHOD_INVOKE, METHOD_LIST, Phase,
};

#[derive(Debug, Clone, Default)]
pub struct MiddlewareOptions {
    pub interceptors: Vec<String>,
    pub config: std::collections::HashMap<String, Map<String, Value>>,
    pub timeout: Option<std::time::Duration>,
    pub context: Option<InvocationContext>,
}

#[derive(Debug, Error)]
pub enum MiddlewareError<E> {
    #[error("interceptor chain rejected {event} during the {phase:?} phase")]
    Rejected {
        event: String,
        phase: Phase,
        result: Box<ChainExecutionResult>,
    },
    #[error("wrapped operation failed: {0}")]
    Inner(E),
}

#[derive(Debug, Clone)]
pub struct InterceptorMiddleware {
    chain: Arc<InterceptorChain>,
}

impl InterceptorMiddleware {
    pub fn new(chain: Arc<InterceptorChain>) -> Self {
        Self { chain }
    }

    pub fn chain(&self) -> &Arc<InterceptorChain> {
        &self.chain
    }

    pub async fn outgoing<F, Fut, E>(
        &self,
        event: impl Into<String>,
        payload: Value,
        options: &MiddlewareOptions,
        next: F,
    ) -> Result<Value, MiddlewareError<E>>
    where
        F: FnOnce(Value) -> Fut,
        Fut: Future<Output = Result<Value, E>>,
    {
        let event = event.into();
        if is_control_event(&event) {
            return next(payload).await.map_err(MiddlewareError::Inner);
        }

        let request = self
            .process(&event, Phase::Request, Direction::Sending, payload, options)
            .await
            .map_err(|result| rejected(&event, Phase::Request, result))?;
        let response = next(request).await.map_err(MiddlewareError::Inner)?;
        self.process(
            &event,
            Phase::Response,
            Direction::Receiving,
            response,
            options,
        )
        .await
        .map_err(|result| rejected(&event, Phase::Response, result))
    }

    pub async fn incoming<F, Fut, E>(
        &self,
        event: impl Into<String>,
        payload: Value,
        options: &MiddlewareOptions,
        next: F,
    ) -> Result<Value, MiddlewareError<E>>
    where
        F: FnOnce(Value) -> Fut,
        Fut: Future<Output = Result<Value, E>>,
    {
        let event = event.into();
        if is_control_event(&event) {
            return next(payload).await.map_err(MiddlewareError::Inner);
        }

        let request = self
            .process(
                &event,
                Phase::Request,
                Direction::Receiving,
                payload,
                options,
            )
            .await
            .map_err(|result| rejected(&event, Phase::Request, result))?;
        let response = next(request).await.map_err(MiddlewareError::Inner)?;
        self.process(
            &event,
            Phase::Response,
            Direction::Sending,
            response,
            options,
        )
        .await
        .map_err(|result| rejected(&event, Phase::Response, result))
    }

    async fn process(
        &self,
        event: &str,
        phase: Phase,
        direction: Direction,
        payload: Value,
        options: &MiddlewareOptions,
    ) -> Result<Value, Box<ChainExecutionResult>> {
        let mut execution = ChainExecution::new(event, phase, direction, payload);
        execution.interceptors.clone_from(&options.interceptors);
        execution.config.clone_from(&options.config);
        execution.timeout = options.timeout;
        execution.context.clone_from(&options.context);
        let result = self.chain.execute(execution).await;
        if result.is_success() {
            Ok(result.final_payload)
        } else {
            Err(Box::new(result))
        }
    }

    pub fn wrap_client_service<S>(
        &self,
        service: S,
        options: MiddlewareOptions,
    ) -> InterceptingClientService<S> {
        InterceptingClientService::new(service, self.clone(), options)
    }

    pub fn wrap_peer(
        &self,
        peer: Peer<RoleClient>,
        options: MiddlewareOptions,
    ) -> InterceptingPeer {
        InterceptingPeer::new(peer, self.clone(), options)
    }
}

fn rejected<E>(event: &str, phase: Phase, result: Box<ChainExecutionResult>) -> MiddlewareError<E> {
    MiddlewareError::Rejected {
        event: event.to_owned(),
        phase,
        result,
    }
}

fn is_control_event(event: &str) -> bool {
    matches!(
        event,
        "initialize" | "notifications/initialized" | "notifications/cancelled"
    ) || event == METHOD_LIST
        || event == METHOD_INVOKE
}

#[derive(Debug, Error)]
pub enum RmcpMiddlewareError {
    #[error("failed to serialize an MCP payload: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("MCP operation failed: {0}")]
    Service(#[from] ServiceError),
    #[error("interceptor chain rejected {event} during the {phase:?} phase")]
    Rejected {
        event: String,
        phase: Phase,
        result: Box<ChainExecutionResult>,
    },
}

#[derive(Debug, Clone)]
pub struct InterceptingPeer {
    peer: Peer<RoleClient>,
    middleware: InterceptorMiddleware,
    options: MiddlewareOptions,
}

impl InterceptingPeer {
    pub fn new(
        peer: Peer<RoleClient>,
        middleware: InterceptorMiddleware,
        options: MiddlewareOptions,
    ) -> Self {
        Self {
            peer,
            middleware,
            options,
        }
    }

    pub fn inner(&self) -> &Peer<RoleClient> {
        &self.peer
    }

    pub async fn discover(
        peer: Peer<RoleClient>,
        interceptor_peers: impl IntoIterator<Item = Peer<RoleClient>>,
        event: Option<&str>,
        options: MiddlewareOptions,
    ) -> Result<Self, ChainBuildError> {
        let chain = InterceptorChain::discover_rmcp(interceptor_peers, event).await?;
        Ok(Self::new(
            peer,
            InterceptorMiddleware::new(Arc::new(chain)),
            options,
        ))
    }

    pub async fn send_request(
        &self,
        request: ClientRequest,
    ) -> Result<ServerResult, RmcpMiddlewareError> {
        let event = request.method().to_owned();
        if is_control_event(&event) {
            return self.peer.send_request(request).await.map_err(Into::into);
        }
        let expects_custom_result = matches!(&request, ClientRequest::CustomRequest(_));

        let payload = request_params(&request)?;
        let request_payload = self
            .middleware
            .process(
                &event,
                Phase::Request,
                Direction::Sending,
                payload,
                &self.options,
            )
            .await
            .map_err(|result| RmcpMiddlewareError::Rejected {
                event: event.clone(),
                phase: Phase::Request,
                result,
            })?;
        let request = replace_request_params(request, request_payload)?;
        let response = self.peer.send_request(request).await?;
        let response_payload = serde_json::to_value(response)?;
        let response_payload = self
            .middleware
            .process(
                &event,
                Phase::Response,
                Direction::Receiving,
                response_payload,
                &self.options,
            )
            .await
            .map_err(|result| RmcpMiddlewareError::Rejected {
                event: event.clone(),
                phase: Phase::Response,
                result,
            })?;
        if expects_custom_result {
            Ok(ServerResult::CustomResult(CustomResult::new(
                response_payload,
            )))
        } else {
            serde_json::from_value(response_payload).map_err(Into::into)
        }
    }

    pub async fn call_tool_once(
        &self,
        params: CallToolRequestParams,
    ) -> Result<CallToolResponse, RmcpMiddlewareError> {
        let result = self
            .send_request(ClientRequest::CallToolRequest(CallToolRequest::new(params)))
            .await?;
        match result {
            ServerResult::CallToolResult(result) => Ok(CallToolResponse::Complete(result)),
            ServerResult::InputRequiredResult(result) => {
                Ok(CallToolResponse::InputRequired(result))
            }
            ServerResult::CreateTaskResult(result) => Ok(CallToolResponse::Task(result)),
            _ => Err(ServiceError::UnexpectedResponse.into()),
        }
    }
}

pub trait InterceptorPeerExt {
    fn with_interceptors(
        &self,
        interceptor_peers: impl IntoIterator<Item = Peer<RoleClient>>,
    ) -> impl Future<Output = Result<InterceptingPeer, ChainBuildError>> + Send;
}

impl InterceptorPeerExt for Peer<RoleClient> {
    fn with_interceptors(
        &self,
        interceptor_peers: impl IntoIterator<Item = Peer<RoleClient>>,
    ) -> impl Future<Output = Result<InterceptingPeer, ChainBuildError>> + Send {
        let peer = self.clone();
        let interceptor_peers = interceptor_peers.into_iter().collect::<Vec<_>>();
        async move {
            InterceptingPeer::discover(peer, interceptor_peers, None, MiddlewareOptions::default())
                .await
        }
    }
}

pub struct InterceptingClientService<S> {
    inner: S,
    middleware: InterceptorMiddleware,
    options: MiddlewareOptions,
}

impl<S> InterceptingClientService<S> {
    pub fn new(inner: S, middleware: InterceptorMiddleware, options: MiddlewareOptions) -> Self {
        Self {
            inner,
            middleware,
            options,
        }
    }

    pub fn inner(&self) -> &S {
        &self.inner
    }

    pub fn into_inner(self) -> S {
        self.inner
    }
}

impl<S: fmt::Debug> fmt::Debug for InterceptingClientService<S> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("InterceptingClientService")
            .field("inner", &self.inner)
            .field("middleware", &self.middleware)
            .field("options", &self.options)
            .finish()
    }
}

impl<S> Service<RoleClient> for InterceptingClientService<S>
where
    S: Service<RoleClient>,
{
    async fn handle_request(
        &self,
        request: ServerRequest,
        context: RequestContext<RoleClient>,
    ) -> Result<ClientResult, ErrorData> {
        let event = server_request_event(&request);
        if is_control_event(&event) {
            return self.inner.handle_request(request, context).await;
        }
        let expects_custom_result = matches!(&request, ServerRequest::CustomRequest(_));

        let request_payload = request_params(&request).map_err(serialization_error)?;
        let request_payload = self
            .middleware
            .process(
                &event,
                Phase::Request,
                Direction::Receiving,
                request_payload,
                &self.options,
            )
            .await
            .map_err(|result| chain_error(&result))?;
        let request =
            replace_request_params(request, request_payload).map_err(serialization_error)?;
        let response = self.inner.handle_request(request, context).await?;
        let response_payload = serde_json::to_value(response).map_err(serialization_error)?;
        let response_payload = self
            .middleware
            .process(
                &event,
                Phase::Response,
                Direction::Sending,
                response_payload,
                &self.options,
            )
            .await
            .map_err(|result| chain_error(&result))?;

        if expects_custom_result {
            Ok(ClientResult::CustomResult(CustomResult::new(
                response_payload,
            )))
        } else {
            serde_json::from_value(response_payload).map_err(serialization_error)
        }
    }

    async fn handle_notification(
        &self,
        notification: ServerNotification,
        context: NotificationContext<RoleClient>,
    ) -> Result<(), ErrorData> {
        self.inner.handle_notification(notification, context).await
    }

    fn get_info(&self) -> rmcp::model::ClientConfig {
        self.inner.get_info()
    }

    fn supported_protocol_versions(&self) -> Cow<'static, [ProtocolVersion]> {
        self.inner.supported_protocol_versions()
    }
}

fn server_request_event(request: &ServerRequest) -> String {
    match request {
        ServerRequest::PingRequest(_) => "ping".to_owned(),
        ServerRequest::CreateMessageRequest(_) => crate::events::SAMPLING_CREATE_MESSAGE.to_owned(),
        ServerRequest::ListRootsRequest(_) => crate::events::ROOTS_LIST.to_owned(),
        ServerRequest::ElicitRequest(_) => crate::events::ELICITATION_CREATE.to_owned(),
        ServerRequest::CustomRequest(request) => request.method.clone(),
        _ => serde_json::to_value(request)
            .ok()
            .and_then(|request| request.get("method")?.as_str().map(str::to_owned))
            .unwrap_or_else(|| "unknown".to_owned()),
    }
}

fn request_params<T: serde::Serialize>(request: &T) -> Result<Value, serde_json::Error> {
    let request = serde_json::to_value(request)?;
    Ok(request
        .get("params")
        .cloned()
        .filter(|params| !params.is_null())
        .unwrap_or_else(|| Value::Object(Map::new())))
}

fn replace_request_params<T>(request: T, params: Value) -> Result<T, serde_json::Error>
where
    T: serde::Serialize + serde::de::DeserializeOwned,
{
    let mut request = serde_json::to_value(request)?;
    if let Some(object) = request.as_object_mut() {
        let has_params = object.contains_key("params");
        if has_params || !is_empty_params(&params) {
            object.insert("params".to_owned(), params.clone());
        }
    }
    let request = serde_json::from_value(request)?;
    if request_params(&request)? == params || is_empty_params(&params) {
        Ok(request)
    } else {
        Err(serde_json::Error::io(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "mutated params cannot be represented by the MCP request type",
        )))
    }
}

fn is_empty_params(params: &Value) -> bool {
    params.is_null() || params.as_object().is_some_and(Map::is_empty)
}

fn serialization_error(error: serde_json::Error) -> ErrorData {
    ErrorData::internal_error(
        format!("interceptor middleware serialization failed: {error}"),
        None,
    )
}

fn chain_error(result: &ChainExecutionResult) -> ErrorData {
    let message = result
        .aborted_at
        .first()
        .map(|abort| abort.reason.clone())
        .unwrap_or_else(|| "interceptor chain rejected the operation".to_owned());
    match result.status {
        ChainStatus::ValidationFailed => ErrorData::invalid_params(message, None),
        ChainStatus::MutationFailed | ChainStatus::Timeout => {
            ErrorData::internal_error(message, None)
        }
        ChainStatus::Success => ErrorData::internal_error(
            "interceptor middleware received an unexpected successful rejection",
            None,
        ),
    }
}
