use std::{borrow::Cow, collections::BTreeSet, fmt, sync::Arc, time::Duration};

use async_trait::async_trait;
use rmcp::{
    ErrorData, RoleServer, Service,
    model::{
        ClientNotification, ClientRequest, CustomResult, ErrorCode, ProtocolVersion,
        ServerCapabilities, ServerConfig, ServerResult,
    },
    service::{NotificationContext, RequestContext},
};
use serde::Serialize;
use serde_json::{Map, Value, json};
use thiserror::Error;

use crate::{
    EXTENSION_ID, Hook, InterceptorDescriptor, InterceptorType, InvocationContext,
    InvocationResult, InvokeInterceptorParams, ListInterceptorsParams, ListInterceptorsResult,
    METHOD_INVOKE, METHOD_LIST, MutationResult, Phase, ValidationResult,
};

#[derive(Debug, Clone, PartialEq)]
/// The protocol payload and context passed to a hosted interceptor.
pub struct InterceptorInvocation {
    pub event: String,
    pub phase: Phase,
    pub payload: Value,
    pub config: Option<Map<String, Value>>,
    pub context: Option<InvocationContext>,
}

impl From<InvokeInterceptorParams> for InterceptorInvocation {
    fn from(params: InvokeInterceptorParams) -> Self {
        Self {
            event: params.event,
            phase: params.phase,
            payload: params.payload,
            config: params.config,
            context: params.context,
        }
    }
}

#[async_trait]
/// A mutation interceptor hosted by an MCP server.
pub trait MutationInterceptor: Send + Sync {
    fn descriptor(&self) -> &InterceptorDescriptor;

    async fn mutate(
        &self,
        invocation: InterceptorInvocation,
        context: RequestContext<RoleServer>,
    ) -> Result<MutationResult, ErrorData>;
}

#[async_trait]
/// A validation interceptor hosted by an MCP server.
pub trait ValidationInterceptor: Send + Sync {
    fn descriptor(&self) -> &InterceptorDescriptor;

    async fn validate(
        &self,
        invocation: InterceptorInvocation,
        context: RequestContext<RoleServer>,
    ) -> Result<ValidationResult, ErrorData>;
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum InterceptorRegistrationError {
    #[error("interceptor name cannot be empty")]
    EmptyName,
    #[error("interceptor {interceptor:?} must declare at least one hook")]
    MissingHooks { interceptor: String },
    #[error("interceptor {interceptor:?} has a hook without events")]
    MissingHookEvents { interceptor: String },
    #[error("interceptor {interceptor:?} is already registered")]
    DuplicateName { interceptor: String },
    #[error("interceptor {interceptor:?} must have type {expected:?}, not {actual:?}")]
    TypeMismatch {
        interceptor: String,
        expected: InterceptorType,
        actual: InterceptorType,
    },
}

#[async_trait]
trait HostedInterceptor: Send + Sync {
    fn descriptor(&self) -> &InterceptorDescriptor;

    async fn invoke(
        &self,
        invocation: InterceptorInvocation,
        context: RequestContext<RoleServer>,
    ) -> Result<InvocationResult, ErrorData>;
}

struct HostedMutator<T>(T);

#[async_trait]
impl<T> HostedInterceptor for HostedMutator<T>
where
    T: MutationInterceptor + 'static,
{
    fn descriptor(&self) -> &InterceptorDescriptor {
        self.0.descriptor()
    }

    async fn invoke(
        &self,
        invocation: InterceptorInvocation,
        context: RequestContext<RoleServer>,
    ) -> Result<InvocationResult, ErrorData> {
        self.0
            .mutate(invocation, context)
            .await
            .map(InvocationResult::Mutation)
    }
}

struct HostedValidator<T>(T);

#[async_trait]
impl<T> HostedInterceptor for HostedValidator<T>
where
    T: ValidationInterceptor + 'static,
{
    fn descriptor(&self) -> &InterceptorDescriptor {
        self.0.descriptor()
    }

    async fn invoke(
        &self,
        invocation: InterceptorInvocation,
        context: RequestContext<RoleServer>,
    ) -> Result<InvocationResult, ErrorData> {
        self.0
            .validate(invocation, context)
            .await
            .map(InvocationResult::Validation)
    }
}

#[derive(Clone, Default)]
/// A registry that exposes hosted interceptors through the extension methods.
pub struct InterceptorExtension {
    interceptors: Vec<Arc<dyn HostedInterceptor>>,
}

impl fmt::Debug for InterceptorExtension {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("InterceptorExtension")
            .field("interceptors", &self.descriptors())
            .finish()
    }
}

impl InterceptorExtension {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register_mutator<T>(
        &mut self,
        interceptor: T,
    ) -> Result<&mut Self, InterceptorRegistrationError>
    where
        T: MutationInterceptor + 'static,
    {
        self.validate_registration(interceptor.descriptor(), InterceptorType::Mutation)?;
        self.interceptors.push(Arc::new(HostedMutator(interceptor)));
        Ok(self)
    }

    pub fn register_validator<T>(
        &mut self,
        interceptor: T,
    ) -> Result<&mut Self, InterceptorRegistrationError>
    where
        T: ValidationInterceptor + 'static,
    {
        self.validate_registration(interceptor.descriptor(), InterceptorType::Validation)?;
        self.interceptors
            .push(Arc::new(HostedValidator(interceptor)));
        Ok(self)
    }

    pub fn descriptors(&self) -> Vec<InterceptorDescriptor> {
        self.interceptors
            .iter()
            .map(|interceptor| interceptor.descriptor().clone())
            .collect()
    }

    pub fn supported_events(&self) -> Vec<String> {
        self.interceptors
            .iter()
            .flat_map(|interceptor| interceptor.descriptor().hooks.iter())
            .flat_map(|hook| hook.events.iter().cloned())
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect()
    }

    pub fn wrap<S>(self, service: S) -> InterceptorServer<S> {
        InterceptorServer::new(service, self)
    }

    fn validate_registration(
        &self,
        descriptor: &InterceptorDescriptor,
        expected: InterceptorType,
    ) -> Result<(), InterceptorRegistrationError> {
        if descriptor.name.is_empty() {
            return Err(InterceptorRegistrationError::EmptyName);
        }
        if descriptor.kind != expected {
            return Err(InterceptorRegistrationError::TypeMismatch {
                interceptor: descriptor.name.clone(),
                expected,
                actual: descriptor.kind,
            });
        }
        if descriptor.hooks.is_empty() {
            return Err(InterceptorRegistrationError::MissingHooks {
                interceptor: descriptor.name.clone(),
            });
        }
        if descriptor.hooks.iter().any(|hook| hook.events.is_empty()) {
            return Err(InterceptorRegistrationError::MissingHookEvents {
                interceptor: descriptor.name.clone(),
            });
        }
        if self
            .interceptors
            .iter()
            .any(|interceptor| interceptor.descriptor().name == descriptor.name)
        {
            return Err(InterceptorRegistrationError::DuplicateName {
                interceptor: descriptor.name.clone(),
            });
        }
        Ok(())
    }

    fn list(&self, params: ListInterceptorsParams) -> ListInterceptorsResult {
        let interceptors = self
            .interceptors
            .iter()
            .map(|interceptor| interceptor.descriptor())
            .filter(|descriptor| {
                params
                    .event
                    .as_deref()
                    .is_none_or(|event| descriptor_matches_event(descriptor, event))
            })
            .cloned()
            .collect();
        ListInterceptorsResult { interceptors }
    }

    async fn invoke(
        &self,
        params: InvokeInterceptorParams,
        context: RequestContext<RoleServer>,
    ) -> Result<InvocationResult, ErrorData> {
        let interceptor = self
            .interceptors
            .iter()
            .find(|interceptor| interceptor.descriptor().name == params.name)
            .cloned()
            .ok_or_else(|| {
                ErrorData::invalid_params(format!("interceptor {:?} not found", params.name), None)
            })?;
        let name = params.name.clone();
        let phase = params.phase;
        let timeout_ms = params.timeout_ms;
        let invocation = InterceptorInvocation::from(params);
        let invoke = interceptor.invoke(invocation, context);

        match timeout_ms {
            Some(timeout_ms) => {
                match tokio::time::timeout(Duration::from_millis(timeout_ms), invoke).await {
                    Ok(result) => result,
                    Err(_) => Err(ErrorData::new(
                        ErrorCode(-32000),
                        format!("interceptor {name:?} timed out after {timeout_ms} ms"),
                        Some(json!({
                            "interceptor": name,
                            "timeoutMs": timeout_ms,
                            "phase": phase
                        })),
                    )),
                }
            }
            None => invoke.await,
        }
    }

    fn enrich_capabilities(&self, capabilities: &mut ServerCapabilities) {
        let supported_events = self
            .supported_events()
            .into_iter()
            .map(Value::String)
            .collect();
        capabilities.extensions.get_or_insert_default().insert(
            EXTENSION_ID.to_owned(),
            Map::from_iter([("supportedEvents".to_owned(), Value::Array(supported_events))]),
        );
    }

    fn enrich_config(&self, mut config: ServerConfig) -> ServerConfig {
        self.enrich_capabilities(&mut config.capabilities);
        config
    }

    fn enrich_result(&self, result: &mut ServerResult) {
        match result {
            ServerResult::InitializeResult(result) => {
                self.enrich_capabilities(&mut result.capabilities)
            }
            ServerResult::DiscoverResult(result) => {
                self.enrich_capabilities(&mut result.capabilities)
            }
            _ => {}
        }
    }
}

pub struct InterceptorServer<S> {
    inner: S,
    extension: InterceptorExtension,
}

impl<S> InterceptorServer<S> {
    pub fn new(inner: S, extension: InterceptorExtension) -> Self {
        Self { inner, extension }
    }

    pub fn inner(&self) -> &S {
        &self.inner
    }

    pub fn extension(&self) -> &InterceptorExtension {
        &self.extension
    }

    pub fn into_inner(self) -> S {
        self.inner
    }
}

impl<S: fmt::Debug> fmt::Debug for InterceptorServer<S> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("InterceptorServer")
            .field("inner", &self.inner)
            .field("extension", &self.extension)
            .finish()
    }
}

impl<S> Service<RoleServer> for InterceptorServer<S>
where
    S: Service<RoleServer>,
{
    async fn handle_request(
        &self,
        request: ClientRequest,
        context: RequestContext<RoleServer>,
    ) -> Result<ServerResult, ErrorData> {
        match request {
            ClientRequest::CustomRequest(request) if request.method == METHOD_LIST => {
                let params = request
                    .params_as::<ListInterceptorsParams>()
                    .map_err(invalid_params)?
                    .unwrap_or_default();
                custom_result(self.extension.list(params))
            }
            ClientRequest::CustomRequest(request) if request.method == METHOD_INVOKE => {
                let params = request
                    .params_as::<InvokeInterceptorParams>()
                    .map_err(invalid_params)?
                    .ok_or_else(|| ErrorData::invalid_params("params are required", None))?;
                let result = self.extension.invoke(params, context).await?;
                custom_result(result)
            }
            request => {
                let mut result = self.inner.handle_request(request, context).await?;
                self.extension.enrich_result(&mut result);
                Ok(result)
            }
        }
    }

    async fn handle_notification(
        &self,
        notification: ClientNotification,
        context: NotificationContext<RoleServer>,
    ) -> Result<(), ErrorData> {
        self.inner.handle_notification(notification, context).await
    }

    fn get_info(&self) -> ServerConfig {
        self.extension.enrich_config(self.inner.get_info())
    }

    fn supported_protocol_versions(&self) -> Cow<'static, [ProtocolVersion]> {
        self.inner.supported_protocol_versions()
    }
}

fn custom_result(result: impl Serialize) -> Result<ServerResult, ErrorData> {
    serde_json::to_value(result)
        .map(CustomResult::new)
        .map(ServerResult::CustomResult)
        .map_err(|error| {
            ErrorData::internal_error(
                format!("failed to serialize interceptor response: {error}"),
                None,
            )
        })
}

fn invalid_params(error: serde_json::Error) -> ErrorData {
    ErrorData::invalid_params(error.to_string(), None)
}

fn descriptor_matches_event(descriptor: &InterceptorDescriptor, event: &str) -> bool {
    descriptor
        .hooks
        .iter()
        .any(|hook| hook_matches_event(hook, event))
}

fn hook_matches_event(hook: &Hook, event: &str) -> bool {
    hook.events.iter().any(|pattern| {
        pattern == "*"
            || pattern == event
            || pattern.strip_suffix("/*").is_some_and(|prefix| {
                event.starts_with(prefix) && event[prefix.len()..].starts_with('/')
            })
    })
}
