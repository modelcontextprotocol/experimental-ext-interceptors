use mcp_ext_interceptors::{
    Hook, InterceptorClient, InterceptorClientError, InterceptorDescriptor, InterceptorExtension,
    InterceptorInvocation, InterceptorPeerExt, InterceptorRegistrationError, InterceptorType,
    InvokeInterceptorParams, MutationInterceptor, MutationResult, Phase, RmcpInterceptorClient,
    ValidationInterceptor, ValidationResult, async_trait, events,
};
use rmcp::{
    ErrorData, RoleClient, RoleServer, ServerHandler, ServiceError, ServiceExt,
    model::{
        CallToolRequestParams, CallToolResponse, CallToolResult, ServerCapabilities, ServerConfig,
    },
    service::{RequestContext, RunningService},
};
use serde_json::{Value, json};
use tokio::task::JoinHandle;

fn descriptor(name: &str, kind: InterceptorType, phases: &[Phase]) -> InterceptorDescriptor {
    InterceptorDescriptor::new(
        name,
        kind,
        phases
            .iter()
            .map(|phase| Hook::new([events::TOOLS_CALL], *phase))
            .collect(),
    )
}

#[derive(Debug, Clone)]
struct MarkerMutator {
    descriptor: InterceptorDescriptor,
}

#[async_trait]
impl MutationInterceptor for MarkerMutator {
    fn descriptor(&self) -> &InterceptorDescriptor {
        &self.descriptor
    }

    async fn mutate(
        &self,
        mut invocation: InterceptorInvocation,
        _context: RequestContext<RoleServer>,
    ) -> Result<MutationResult, ErrorData> {
        let field = match invocation.phase {
            Phase::Request => "requestIntercepted",
            Phase::Response => "responseIntercepted",
        };
        let target = match invocation.phase {
            Phase::Request => invocation
                .payload
                .get_mut("arguments")
                .and_then(Value::as_object_mut),
            Phase::Response => invocation
                .payload
                .get_mut("structuredContent")
                .and_then(Value::as_object_mut),
        }
        .ok_or_else(|| ErrorData::invalid_params("object payload required", None))?;
        target.insert(field.to_owned(), Value::Bool(true));
        Ok(MutationResult::modified(invocation.payload))
    }
}

#[derive(Debug, Clone)]
struct AllowValidator {
    descriptor: InterceptorDescriptor,
}

#[async_trait]
impl ValidationInterceptor for AllowValidator {
    fn descriptor(&self) -> &InterceptorDescriptor {
        &self.descriptor
    }

    async fn validate(
        &self,
        _invocation: InterceptorInvocation,
        _context: RequestContext<RoleServer>,
    ) -> Result<ValidationResult, ErrorData> {
        Ok(ValidationResult::valid())
    }
}

#[test]
fn validates_interceptor_registration() {
    let mut extension = InterceptorExtension::new();
    let wrong_type = extension
        .register_mutator(MarkerMutator {
            descriptor: descriptor("wrong", InterceptorType::Validation, &[Phase::Request]),
        })
        .unwrap_err();
    assert!(matches!(
        wrong_type,
        InterceptorRegistrationError::TypeMismatch { .. }
    ));

    let marker = MarkerMutator {
        descriptor: descriptor("marker", InterceptorType::Mutation, &[Phase::Request]),
    };
    extension.register_mutator(marker.clone()).unwrap();
    let duplicate = extension.register_mutator(marker).unwrap_err();
    assert!(matches!(
        duplicate,
        InterceptorRegistrationError::DuplicateName { .. }
    ));
}

#[derive(Debug, Clone)]
struct ToolServer;

impl ServerHandler for ToolServer {
    fn get_info(&self) -> ServerConfig {
        ServerConfig::new(ServerCapabilities::builder().enable_tools().build())
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        Ok(CallToolResult::structured(json!({
            "arguments": request.arguments
        }))
        .into())
    }
}

async fn start_server(
    extension: InterceptorExtension,
) -> (RunningService<RoleClient, ()>, JoinHandle<()>) {
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let server = tokio::spawn(async move {
        let server = extension
            .wrap(ToolServer)
            .serve(server_transport)
            .await
            .unwrap();
        server.waiting().await.unwrap();
    });
    let client = ().serve(client_transport).await.unwrap();
    (client, server)
}

#[tokio::test]
async fn hosts_interceptors_alongside_an_rmcp_server() {
    let mut extension = InterceptorExtension::new();
    extension
        .register_mutator(MarkerMutator {
            descriptor: descriptor(
                "marker",
                InterceptorType::Mutation,
                &[Phase::Request, Phase::Response],
            ),
        })
        .unwrap();
    extension
        .register_validator(AllowValidator {
            descriptor: descriptor(
                "allow",
                InterceptorType::Validation,
                &[Phase::Request, Phase::Response],
            ),
        })
        .unwrap();
    let (connection, server) = start_server(extension).await;
    let peer = connection.peer().clone();

    let capabilities = &peer.peer_info().unwrap().capabilities;
    assert_eq!(
        capabilities
            .extensions
            .as_ref()
            .unwrap()
            .get(mcp_ext_interceptors::EXTENSION_ID)
            .unwrap()["supportedEvents"],
        json!([events::TOOLS_CALL])
    );

    let interceptor_client = RmcpInterceptorClient::new(peer.clone());
    assert!(
        interceptor_client
            .list(Some(events::PROMPTS_GET))
            .await
            .unwrap()
            .interceptors
            .is_empty()
    );

    let client = peer.with_interceptors([peer.clone()]).await.unwrap();
    let arguments = json!({ "original": true }).as_object().unwrap().clone();
    let response = client
        .call_tool_once(CallToolRequestParams::new("echo").with_arguments(arguments))
        .await
        .unwrap();

    match response {
        CallToolResponse::Complete(result) => assert_eq!(
            result.structured_content,
            Some(json!({
                "arguments": {
                    "original": true,
                    "requestIntercepted": true
                },
                "responseIntercepted": true
            }))
        ),
        other => panic!("expected tool result, got {other:?}"),
    }

    connection.cancel().await.unwrap();
    server.await.unwrap();
}

#[derive(Debug, Clone)]
struct SlowValidator {
    descriptor: InterceptorDescriptor,
}

#[async_trait]
impl ValidationInterceptor for SlowValidator {
    fn descriptor(&self) -> &InterceptorDescriptor {
        &self.descriptor
    }

    async fn validate(
        &self,
        _invocation: InterceptorInvocation,
        _context: RequestContext<RoleServer>,
    ) -> Result<ValidationResult, ErrorData> {
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        Ok(ValidationResult::valid())
    }
}

#[tokio::test]
async fn interceptor_server_enforces_requested_timeout() {
    let mut extension = InterceptorExtension::new();
    extension
        .register_validator(SlowValidator {
            descriptor: descriptor("slow", InterceptorType::Validation, &[Phase::Request]),
        })
        .unwrap();
    let (connection, server) = start_server(extension).await;
    let client = RmcpInterceptorClient::new(connection.peer().clone());

    let error = client
        .invoke(InvokeInterceptorParams {
            name: "slow".to_owned(),
            event: events::TOOLS_CALL.to_owned(),
            phase: Phase::Request,
            payload: json!({}),
            config: None,
            timeout_ms: Some(1),
            context: None,
        })
        .await
        .unwrap_err();

    match error {
        InterceptorClientError::Service(ServiceError::McpError(error)) => {
            assert_eq!(error.code.0, -32000)
        }
        other => panic!("expected MCP timeout error, got {other:?}"),
    }

    connection.cancel().await.unwrap();
    server.await.unwrap();
}
