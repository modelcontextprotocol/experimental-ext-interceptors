use std::{env, error::Error};

use mcp_ext_interceptors::{
    Hook, InterceptorDescriptor, InterceptorExtension, InterceptorInvocation, MutationInterceptor,
    MutationResult, Phase, async_trait, events,
};
use rmcp::{
    ErrorData, RoleServer, ServerHandler, ServiceExt,
    model::{
        CallToolRequestParams, CallToolResponse, CallToolResult, ServerCapabilities, ServerConfig,
    },
    service::RequestContext,
};
use serde_json::{Value, json};
use tokio::net::TcpListener;

const DEFAULT_ADDRESS: &str = "127.0.0.1:8765";

#[derive(Debug, Clone)]
struct ExampleMutator {
    descriptor: InterceptorDescriptor,
}

impl ExampleMutator {
    fn new() -> Self {
        Self {
            descriptor: InterceptorDescriptor::mutation(
                "example-mutator",
                vec![
                    Hook::new([events::TOOLS_CALL], Phase::Request),
                    Hook::new([events::TOOLS_CALL], Phase::Response),
                ],
            ),
        }
    }
}

#[async_trait]
impl MutationInterceptor for ExampleMutator {
    fn descriptor(&self) -> &InterceptorDescriptor {
        &self.descriptor
    }

    async fn mutate(
        &self,
        mut invocation: InterceptorInvocation,
        _context: RequestContext<RoleServer>,
    ) -> Result<MutationResult, ErrorData> {
        match invocation.phase {
            Phase::Request => invocation
                .payload
                .get_mut("arguments")
                .and_then(Value::as_object_mut)
                .ok_or_else(|| ErrorData::invalid_params("tool arguments object required", None))?
                .insert("clientIntercepted".to_owned(), Value::Bool(true)),
            Phase::Response => invocation
                .payload
                .get_mut("structuredContent")
                .and_then(Value::as_object_mut)
                .ok_or_else(|| {
                    ErrorData::invalid_params("structured content object required", None)
                })?
                .insert("responseIntercepted".to_owned(), Value::Bool(true)),
        };
        Ok(MutationResult::modified(invocation.payload))
    }
}

#[derive(Debug, Clone)]
struct ExampleToolServer;

impl ServerHandler for ExampleToolServer {
    fn get_info(&self) -> ServerConfig {
        ServerConfig::new(ServerCapabilities::builder().enable_tools().build())
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResponse, ErrorData> {
        Ok(CallToolResult::structured(json!({
            "receivedArguments": request.arguments
        }))
        .into())
    }
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    let mut interceptors = InterceptorExtension::new();
    interceptors.register_mutator(ExampleMutator::new())?;

    let address = env::var("MCP_EXAMPLE_ADDR").unwrap_or_else(|_| DEFAULT_ADDRESS.to_owned());
    let listener = TcpListener::bind(&address).await?;
    eprintln!("listening on {address}");

    loop {
        let (transport, _) = listener.accept().await?;
        let server = interceptors.clone().wrap(ExampleToolServer);
        tokio::spawn(async move {
            match server.serve(transport).await {
                Ok(connection) => {
                    if let Err(error) = connection.waiting().await {
                        eprintln!("connection failed: {error}");
                    }
                }
                Err(error) => eprintln!("connection initialization failed: {error}"),
            }
        });
    }
}
