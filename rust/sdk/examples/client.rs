use std::{env, error::Error};

use mcp_ext_interceptors::InterceptorPeerExt;
use rmcp::{
    ServiceExt,
    model::{CallToolRequestParams, CallToolResponse},
};
use serde_json::{Value, json};
use tokio::net::TcpStream;

const DEFAULT_ADDRESS: &str = "127.0.0.1:8765";

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    let address = env::var("MCP_EXAMPLE_ADDR").unwrap_or_else(|_| DEFAULT_ADDRESS.to_owned());
    let transport = TcpStream::connect(&address).await?;
    let connection = ().serve(transport).await?;

    let peer = connection.peer().clone();
    let client = peer.with_interceptors([peer.clone()]).await?;

    let arguments = json!({ "city": "London" }).as_object().unwrap().clone();
    let response = client
        .call_tool_once(CallToolRequestParams::new("weather").with_arguments(arguments))
        .await?;

    match response {
        CallToolResponse::Complete(result) => {
            println!("{}", result.structured_content.unwrap_or(Value::Null));
        }
        other => return Err(format!("unexpected response: {other:?}").into()),
    }

    connection.cancel().await?;
    Ok(())
}
