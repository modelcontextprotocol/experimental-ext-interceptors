use std::sync::Arc;

use mcp_ext_interceptors::{
    ChainExecution, ChainStatus, Direction, InterceptorChain, InvokeInterceptorParams,
    ListInterceptorsParams, Phase, RmcpInterceptorClient, events,
};
use rmcp::{
    ErrorData, RoleServer, ServerHandler, ServiceExt,
    model::{CustomRequest, CustomResult, ErrorCode},
    service::RequestContext,
};
use serde_json::json;

#[derive(Debug, Clone)]
struct TestInterceptorServer;

impl ServerHandler for TestInterceptorServer {
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
                        "name": "add-safe",
                        "type": "mutation",
                        "hooks": [{
                            "events": ["tools/call"],
                            "phase": "request"
                        }]
                    }]
                })))
            }
            "interceptor/invoke" => {
                let params: InvokeInterceptorParams = request
                    .params_as()
                    .map_err(|error| ErrorData::invalid_params(error.to_string(), None))?
                    .ok_or_else(|| ErrorData::invalid_params("params are required", None))?;
                assert_eq!(params.name, "add-safe");
                Ok(CustomResult::new(json!({
                    "modified": true,
                    "payload": {
                        "name": "echo",
                        "arguments": { "safe": true }
                    }
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

#[tokio::test]
async fn discovers_and_invokes_over_rmcp_custom_methods() {
    let (server_transport, client_transport) = tokio::io::duplex(16 * 1024);
    let server = tokio::spawn(async move {
        let server = TestInterceptorServer.serve(server_transport).await.unwrap();
        server.waiting().await.unwrap();
    });
    let client = ().serve(client_transport).await.unwrap();
    let interceptor_client = Arc::new(RmcpInterceptorClient::new(client.peer().clone()));
    let chain = InterceptorChain::discover(vec![interceptor_client], None)
        .await
        .unwrap();

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            json!({ "name": "echo", "arguments": {} }),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Success);
    assert_eq!(result.final_payload["arguments"]["safe"], true);

    client.cancel().await.unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(2), server)
        .await
        .unwrap()
        .unwrap();
}
