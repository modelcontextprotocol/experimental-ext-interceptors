use std::{
    borrow::Cow,
    sync::{Arc, Mutex},
};

use mcp_ext_interceptors::{
    InterceptorChain, InterceptorMiddleware, InvokeInterceptorParams, ListInterceptorsParams,
    MiddlewareOptions, Phase, events,
};
use rmcp::{
    ClientHandler, ErrorData, RoleClient, RoleServer, ServerHandler, Service, ServiceExt,
    model::{
        CallToolRequest, CallToolRequestParams, CallToolResponse, CallToolResult,
        ClientCapabilities, ClientConfig, ClientRequest, ClientResult, CustomRequest, CustomResult,
        ElicitRequest, ElicitRequestParams, ElicitResult, ElicitationAction, ElicitationSchema,
        ErrorCode, Implementation, ProtocolVersion, ServerNotification, ServerRequest,
        ServerResult,
    },
    service::{NotificationContext, RequestContext, RunningService},
};
use serde_json::{Value, json};
use tokio::{sync::Notify, task::JoinHandle};

const OUTGOING_EVENT: &str = "test/outgoing";
const INCOMING_EVENT: &str = "test/incoming";
const EMPTY_OUTGOING_EVENT: &str = "test/empty-outgoing";
const EMPTY_INCOMING_EVENT: &str = "test/empty-incoming";

#[derive(Debug, Clone)]
struct MarkerInterceptorServer;

impl ServerHandler for MarkerInterceptorServer {
    async fn on_custom_request(
        &self,
        request: CustomRequest,
        _context: RequestContext<RoleServer>,
    ) -> Result<CustomResult, ErrorData> {
        match request.method.as_str() {
            "interceptors/list" => {
                let _: ListInterceptorsParams = request
                    .params_as()
                    .map_err(|error| ErrorData::invalid_params(error.to_string(), None))?
                    .unwrap_or_default();
                Ok(CustomResult::new(json!({
                    "interceptors": [{
                        "name": "marker",
                        "type": "mutation",
                        "hooks": [
                            { "events": [OUTGOING_EVENT], "phase": "request" },
                            { "events": [OUTGOING_EVENT], "phase": "response" },
                            { "events": [INCOMING_EVENT], "phase": "request" },
                            { "events": [INCOMING_EVENT], "phase": "response" },
                            { "events": [events::TOOLS_CALL], "phase": "request" },
                            { "events": [events::TOOLS_CALL], "phase": "response" },
                            { "events": [events::ELICITATION_CREATE], "phase": "request" },
                            { "events": [events::ELICITATION_CREATE], "phase": "response" }
                        ]
                    }]
                })))
            }
            "interceptor/invoke" => {
                let params: InvokeInterceptorParams = request
                    .params_as()
                    .map_err(|error| ErrorData::invalid_params(error.to_string(), None))?
                    .ok_or_else(|| ErrorData::invalid_params("params are required", None))?;
                let mut payload = params.payload;
                match (params.event.as_str(), params.phase) {
                    (OUTGOING_EVENT, Phase::Request) => {
                        insert_marker(&mut payload, "sendingRequest")?
                    }
                    (OUTGOING_EVENT, Phase::Response) => {
                        insert_marker(&mut payload, "receivingResponse")?
                    }
                    (INCOMING_EVENT, Phase::Request) => {
                        insert_marker(&mut payload, "receivingRequest")?
                    }
                    (INCOMING_EVENT, Phase::Response) => {
                        insert_marker(&mut payload, "sendingResponse")?
                    }
                    (events::TOOLS_CALL, Phase::Request) => {
                        payload
                            .get_mut("arguments")
                            .and_then(Value::as_object_mut)
                            .ok_or_else(|| {
                                ErrorData::invalid_params("tool arguments object required", None)
                            })?
                            .insert("intercepted".to_owned(), Value::Bool(true));
                    }
                    (events::TOOLS_CALL, Phase::Response) => {
                        payload
                            .get_mut("structuredContent")
                            .and_then(Value::as_object_mut)
                            .ok_or_else(|| {
                                ErrorData::invalid_params(
                                    "structured content object required",
                                    None,
                                )
                            })?
                            .insert("intercepted".to_owned(), Value::Bool(true));
                    }
                    (events::ELICITATION_CREATE, Phase::Request) => {
                        let message = payload
                            .get_mut("message")
                            .and_then(|value| value.as_str())
                            .ok_or_else(|| {
                                ErrorData::invalid_params("elicitation message required", None)
                            })?
                            .to_owned();
                        payload["message"] = Value::String(format!("{message} [intercepted]"));
                    }
                    (events::ELICITATION_CREATE, Phase::Response) => {
                        payload["action"] = Value::String("decline".to_owned());
                    }
                    _ => return Err(ErrorData::invalid_params("unexpected event or phase", None)),
                };
                Ok(CustomResult::new(json!({
                    "modified": true,
                    "payload": payload
                })))
            }
            method => Err(ErrorData::new(
                ErrorCode::METHOD_NOT_FOUND,
                format!("unknown method {method}"),
                None,
            )),
        }
    }
}

fn insert_marker(payload: &mut Value, marker: &str) -> Result<(), ErrorData> {
    payload
        .as_object_mut()
        .ok_or_else(|| ErrorData::invalid_params("object payload required", None))?
        .insert(marker.to_owned(), Value::Bool(true));
    Ok(())
}

async fn start_middleware() -> (
    InterceptorMiddleware,
    RunningService<RoleClient, ()>,
    JoinHandle<()>,
) {
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let server = tokio::spawn(async move {
        let server = MarkerInterceptorServer
            .serve(server_transport)
            .await
            .unwrap();
        server.waiting().await.unwrap();
    });
    let client = ().serve(client_transport).await.unwrap();
    let chain = InterceptorChain::discover_rmcp([client.peer().clone()], None)
        .await
        .unwrap();
    (InterceptorMiddleware::new(Arc::new(chain)), client, server)
}

#[derive(Debug, Clone)]
struct OutgoingTargetServer {
    request: Arc<Mutex<Option<Value>>>,
    response: Value,
}

impl ServerHandler for OutgoingTargetServer {
    async fn on_custom_request(
        &self,
        request: CustomRequest,
        _context: RequestContext<RoleServer>,
    ) -> Result<CustomResult, ErrorData> {
        *self.request.lock().unwrap() = request.params;
        Ok(CustomResult::new(self.response.clone()))
    }
}

#[tokio::test]
async fn intercepting_peer_processes_outgoing_requests_and_responses() {
    let (middleware, interceptor_client, interceptor_server) = start_middleware().await;
    let request = Arc::new(Mutex::new(None));
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let target_server = tokio::spawn({
        let request = Arc::clone(&request);
        async move {
            let server = OutgoingTargetServer {
                request,
                response: json!({ "target": true }),
            }
            .serve(server_transport)
            .await
            .unwrap();
            server.waiting().await.unwrap();
        }
    });
    let target_client = ().serve(client_transport).await.unwrap();
    let peer = middleware.wrap_peer(target_client.peer().clone(), MiddlewareOptions::default());

    let response = peer
        .send_request(ClientRequest::CustomRequest(CustomRequest::new(
            OUTGOING_EVENT,
            Some(json!({ "original": true })),
        )))
        .await
        .unwrap();

    assert_eq!(
        *request.lock().unwrap(),
        Some(json!({ "original": true, "sendingRequest": true }))
    );
    match response {
        ServerResult::CustomResult(result) => assert_eq!(
            result.0,
            json!({ "target": true, "receivingResponse": true })
        ),
        other => panic!("expected custom result, got {other:?}"),
    }

    target_client.cancel().await.unwrap();
    interceptor_client.cancel().await.unwrap();
    target_server.await.unwrap();
    interceptor_server.await.unwrap();
}

#[tokio::test]
async fn intercepting_peer_preserves_empty_custom_results() {
    let (middleware, interceptor_client, interceptor_server) = start_middleware().await;
    let request = Arc::new(Mutex::new(None));
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let target_server = tokio::spawn({
        let request = Arc::clone(&request);
        async move {
            let server = OutgoingTargetServer {
                request,
                response: json!({}),
            }
            .serve(server_transport)
            .await
            .unwrap();
            server.waiting().await.unwrap();
        }
    });
    let target_client = ().serve(client_transport).await.unwrap();
    let peer = middleware.wrap_peer(target_client.peer().clone(), MiddlewareOptions::default());

    let response = peer
        .send_request(ClientRequest::CustomRequest(CustomRequest::new(
            EMPTY_OUTGOING_EVENT,
            None,
        )))
        .await
        .unwrap();

    match response {
        ServerResult::CustomResult(result) => assert_eq!(result.0, json!({})),
        other => panic!("expected custom result, got {other:?}"),
    }

    target_client.cancel().await.unwrap();
    interceptor_client.cancel().await.unwrap();
    target_server.await.unwrap();
    interceptor_server.await.unwrap();
}

#[derive(Debug, Clone)]
struct ToolTargetServer {
    request: Arc<Mutex<Option<CallToolRequestParams>>>,
}

impl ServerHandler for ToolTargetServer {
    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        *self.request.lock().unwrap() = Some(request);
        Ok(CallToolResult::structured(json!({ "target": true })).into())
    }
}

#[tokio::test]
async fn intercepting_peer_processes_typed_tool_calls() {
    let (middleware, interceptor_client, interceptor_server) = start_middleware().await;
    let request = Arc::new(Mutex::new(None));
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let target_server = tokio::spawn({
        let request = Arc::clone(&request);
        async move {
            let server = ToolTargetServer { request }
                .serve(server_transport)
                .await
                .unwrap();
            server.waiting().await.unwrap();
        }
    });
    let target_client = ().serve(client_transport).await.unwrap();
    let peer = middleware.wrap_peer(target_client.peer().clone(), MiddlewareOptions::default());
    let arguments = json!({ "original": true }).as_object().unwrap().clone();

    let response = peer
        .send_request(ClientRequest::CallToolRequest(CallToolRequest::new(
            CallToolRequestParams::new("echo").with_arguments(arguments),
        )))
        .await
        .unwrap();

    {
        let request = request.lock().unwrap();
        let request = request.as_ref().unwrap();
        assert_eq!(request.name, "echo");
        assert_eq!(
            request.arguments,
            Some(
                json!({ "original": true, "intercepted": true })
                    .as_object()
                    .unwrap()
                    .clone()
            )
        );
    }
    match response {
        ServerResult::CallToolResult(result) => assert_eq!(
            result.structured_content,
            Some(json!({ "target": true, "intercepted": true }))
        ),
        other => panic!("expected tool result, got {other:?}"),
    }
    target_client.cancel().await.unwrap();
    interceptor_client.cancel().await.unwrap();
    target_server.await.unwrap();
    interceptor_server.await.unwrap();
}

#[derive(Debug, Clone)]
struct IncomingClient {
    request: Arc<Mutex<Option<Value>>>,
    response: Value,
}

struct RecordingClientService<S> {
    inner: S,
    returned_custom_result: Arc<Mutex<Option<bool>>>,
}

impl<S> Service<RoleClient> for RecordingClientService<S>
where
    S: Service<RoleClient>,
{
    async fn handle_request(
        &self,
        request: ServerRequest,
        context: RequestContext<RoleClient>,
    ) -> Result<ClientResult, ErrorData> {
        let result = self.inner.handle_request(request, context).await;
        if let Ok(result) = &result {
            *self.returned_custom_result.lock().unwrap() =
                Some(matches!(result, ClientResult::CustomResult(_)));
        }
        result
    }

    async fn handle_notification(
        &self,
        notification: ServerNotification,
        context: NotificationContext<RoleClient>,
    ) -> Result<(), ErrorData> {
        self.inner.handle_notification(notification, context).await
    }

    fn get_info(&self) -> ClientConfig {
        self.inner.get_info()
    }

    fn supported_protocol_versions(&self) -> Cow<'static, [ProtocolVersion]> {
        self.inner.supported_protocol_versions()
    }
}

impl ClientHandler for IncomingClient {
    async fn on_custom_request(
        &self,
        request: CustomRequest,
        _context: RequestContext<RoleClient>,
    ) -> Result<CustomResult, ErrorData> {
        *self.request.lock().unwrap() = request.params;
        Ok(CustomResult::new(self.response.clone()))
    }
}

#[derive(Debug, Clone)]
struct IncomingTargetServer {
    event: &'static str,
    response: Arc<Mutex<Option<Result<Value, String>>>>,
    response_ready: Arc<Notify>,
}

impl ServerHandler for IncomingTargetServer {
    async fn on_initialized(&self, context: NotificationContext<RoleServer>) {
        let peer = context.peer;
        let response = Arc::clone(&self.response);
        let response_ready = Arc::clone(&self.response_ready);
        let event = self.event;
        tokio::spawn(async move {
            let result = peer
                .send_request(ServerRequest::CustomRequest(CustomRequest::new(
                    event,
                    Some(json!({ "server": true })),
                )))
                .await;
            let result = match result {
                Ok(result) => serde_json::to_value(result).map_err(|error| error.to_string()),
                Err(error) => Err(error.to_string()),
            };
            *response.lock().unwrap() = Some(result);
            response_ready.notify_one();
        });
    }
}

#[tokio::test]
async fn intercepting_client_service_processes_incoming_requests_and_responses() {
    let (middleware, interceptor_client, interceptor_server) = start_middleware().await;
    let request = Arc::new(Mutex::new(None));
    let response = Arc::new(Mutex::new(None));
    let response_ready = Arc::new(Notify::new());
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let target_server = tokio::spawn({
        let response = Arc::clone(&response);
        let response_ready = Arc::clone(&response_ready);
        async move {
            let server = IncomingTargetServer {
                event: INCOMING_EVENT,
                response,
                response_ready,
            }
            .serve(server_transport)
            .await
            .unwrap();
            server.waiting().await.unwrap();
        }
    });
    let client = middleware
        .wrap_client_service(
            IncomingClient {
                request: Arc::clone(&request),
                response: json!({ "client": true }),
            },
            MiddlewareOptions::default(),
        )
        .serve(client_transport)
        .await
        .unwrap();

    tokio::time::timeout(std::time::Duration::from_secs(2), response_ready.notified())
        .await
        .unwrap();

    assert_eq!(
        *request.lock().unwrap(),
        Some(json!({ "server": true, "receivingRequest": true }))
    );
    assert_eq!(
        response.lock().unwrap().take().unwrap().unwrap(),
        json!({ "client": true, "sendingResponse": true })
    );

    client.cancel().await.unwrap();
    interceptor_client.cancel().await.unwrap();
    target_server.await.unwrap();
    interceptor_server.await.unwrap();
}

#[tokio::test]
async fn intercepting_client_service_preserves_empty_custom_results() {
    let (middleware, interceptor_client, interceptor_server) = start_middleware().await;
    let request = Arc::new(Mutex::new(None));
    let response = Arc::new(Mutex::new(None));
    let response_ready = Arc::new(Notify::new());
    let returned_custom_result = Arc::new(Mutex::new(None));
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let target_server = tokio::spawn({
        let response = Arc::clone(&response);
        let response_ready = Arc::clone(&response_ready);
        async move {
            let server = IncomingTargetServer {
                event: EMPTY_INCOMING_EVENT,
                response,
                response_ready,
            }
            .serve(server_transport)
            .await
            .unwrap();
            server.waiting().await.unwrap();
        }
    });
    let service = middleware.wrap_client_service(
        IncomingClient {
            request: Arc::clone(&request),
            response: json!({}),
        },
        MiddlewareOptions::default(),
    );
    let client = RecordingClientService {
        inner: service,
        returned_custom_result: Arc::clone(&returned_custom_result),
    }
    .serve(client_transport)
    .await
    .unwrap();

    tokio::time::timeout(std::time::Duration::from_secs(2), response_ready.notified())
        .await
        .unwrap();

    assert_eq!(*request.lock().unwrap(), Some(json!({ "server": true })));
    assert_eq!(response.lock().unwrap().take().unwrap().unwrap(), json!({}));
    assert_eq!(*returned_custom_result.lock().unwrap(), Some(true));

    client.cancel().await.unwrap();
    interceptor_client.cancel().await.unwrap();
    target_server.await.unwrap();
    interceptor_server.await.unwrap();
}

#[derive(Debug, Clone)]
struct ElicitationClient {
    message: Arc<Mutex<Option<String>>>,
}

impl ClientHandler for ElicitationClient {
    async fn create_elicitation(
        &self,
        request: ElicitRequestParams,
        _context: RequestContext<RoleClient>,
    ) -> Result<ElicitResult, ErrorData> {
        let message = match request {
            ElicitRequestParams::FormElicitationParams { message, .. } => message,
            _ => return Err(ErrorData::invalid_params("expected form elicitation", None)),
        };
        *self.message.lock().unwrap() = Some(message);
        Ok(ElicitResult::new(ElicitationAction::Accept).with_content(json!({ "confirmed": true })))
    }

    fn get_info(&self) -> ClientConfig {
        ClientConfig::new(
            ClientCapabilities::builder().enable_elicitation().build(),
            Implementation::new("interceptor-test-client", "0.1.0"),
        )
    }
}

#[derive(Debug, Clone)]
struct ElicitingToolServer;

impl ServerHandler for ElicitingToolServer {
    async fn call_tool(
        &self,
        _request: CallToolRequestParams,
        context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        let requested_schema = ElicitationSchema::builder()
            .required_bool("confirmed")
            .build()
            .map_err(|error| ErrorData::internal_error(error, None))?;
        let response = context
            .peer
            .send_request(ServerRequest::ElicitRequest(ElicitRequest::new(
                ElicitRequestParams::FormElicitationParams {
                    meta: None,
                    message: "Confirm this tool call".to_owned(),
                    requested_schema,
                },
            )))
            .await
            .map_err(|error| ErrorData::internal_error(error.to_string(), None))?;
        let ClientResult::ElicitResult(response) = response else {
            return Err(ErrorData::internal_error(
                "client returned an unexpected elicitation result",
                None,
            ));
        };

        Ok(CallToolResult::structured(json!({ "action": response.action })).into())
    }
}

#[tokio::test]
async fn intercepting_client_service_processes_typed_elicitation() {
    let (middleware, interceptor_client, interceptor_server) = start_middleware().await;
    let message = Arc::new(Mutex::new(None));
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let target_server = tokio::spawn(async move {
        let server = ElicitingToolServer.serve(server_transport).await.unwrap();
        server.waiting().await.unwrap();
    });
    let client = middleware
        .wrap_client_service(
            ElicitationClient {
                message: Arc::clone(&message),
            },
            MiddlewareOptions::default(),
        )
        .serve(client_transport)
        .await
        .unwrap();

    let response = client
        .peer()
        .call_tool_once(CallToolRequestParams::new("confirm"))
        .await
        .unwrap();

    assert_eq!(
        *message.lock().unwrap(),
        Some("Confirm this tool call [intercepted]".to_owned())
    );
    match response {
        CallToolResponse::Complete(result) => {
            assert_eq!(
                result.structured_content,
                Some(json!({ "action": "decline" }))
            )
        }
        other => panic!("expected complete tool result, got {other:?}"),
    }

    client.cancel().await.unwrap();
    interceptor_client.cancel().await.unwrap();
    target_server.await.unwrap();
    interceptor_server.await.unwrap();
}
