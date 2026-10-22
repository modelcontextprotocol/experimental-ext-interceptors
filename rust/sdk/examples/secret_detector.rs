use std::error::Error;

use mcp_ext_interceptors::{
    Hook, InterceptorDescriptor, InterceptorExtension, InterceptorInvocation, Phase,
    ValidationInterceptor, ValidationResult, async_trait, events,
};
use regex::Regex;
use rmcp::{ErrorData, RoleServer, ServerHandler, ServiceExt, service::RequestContext};

#[derive(Debug, Clone)]
struct GitHubTokenDetector {
    descriptor: InterceptorDescriptor,
    pattern: Regex,
}

impl GitHubTokenDetector {
    fn new() -> Result<Self, regex::Error> {
        Ok(Self {
            descriptor: InterceptorDescriptor::validation(
                "github-token-detector",
                vec![
                    Hook::new([events::TOOLS_CALL, events::LLM_COMPLETION], Phase::Request),
                    Hook::new(
                        [events::TOOLS_CALL, events::LLM_COMPLETION],
                        Phase::Response,
                    ),
                ],
            ),
            pattern: Regex::new(r"\bghp_[A-Za-z0-9]{36}\b")?,
        })
    }

    fn contains_token(&self, payload: &serde_json::Value) -> Result<bool, serde_json::Error> {
        serde_json::to_string(payload).map(|payload| self.pattern.is_match(&payload))
    }
}

#[async_trait]
impl ValidationInterceptor for GitHubTokenDetector {
    fn descriptor(&self) -> &InterceptorDescriptor {
        &self.descriptor
    }

    async fn validate(
        &self,
        invocation: InterceptorInvocation,
        _context: RequestContext<RoleServer>,
    ) -> Result<ValidationResult, ErrorData> {
        let contains_token = self.contains_token(&invocation.payload).map_err(|error| {
            ErrorData::internal_error(format!("failed to inspect payload: {error}"), None)
        })?;
        if contains_token {
            Ok(ValidationResult::invalid(
                "GitHub personal access token detected",
            ))
        } else {
            Ok(ValidationResult::valid())
        }
    }
}

#[derive(Debug, Clone)]
struct InterceptorOnlyServer;

impl ServerHandler for InterceptorOnlyServer {}

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    let mut interceptors = InterceptorExtension::new();
    interceptors.register_validator(GitHubTokenDetector::new()?)?;

    let server = interceptors
        .wrap(InterceptorOnlyServer)
        .serve(rmcp::transport::stdio())
        .await?;
    server.waiting().await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_only_classic_github_tokens_in_payloads() {
        let detector = GitHubTokenDetector::new().unwrap();
        let token = format!("ghp_{}", "a".repeat(36));

        assert!(
            detector
                .contains_token(&serde_json::json!({ "arguments": { "token": token } }))
                .unwrap()
        );
        assert!(
            !detector
                .contains_token(&serde_json::json!({
                    "messages": [{ "content": "gho_not-a-classic-token" }]
                }))
                .unwrap()
        );
        assert!(
            !detector
                .contains_token(&serde_json::json!({ "value": "ghp_too-short" }))
                .unwrap()
        );
    }
}
