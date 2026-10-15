use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use mcp_ext_interceptors::{
    AbortType, ChainBuildError, ChainEntry, ChainExecution, ChainStatus, Direction, Hook,
    InterceptorChain, InterceptorClient, InterceptorClientError, InterceptorDescriptor,
    InterceptorOverrides, InterceptorType, InvokeInterceptorParams, ListInterceptorsResult,
    MiddlewareOptions, Mode, Phase, PriorityHint, Severity, ValidationMessage, events,
};
use rmcp::{ErrorData, ServiceError, model::ErrorCode};
use serde_json::{Value, json};

type InvokeHandler =
    dyn Fn(InvokeInterceptorParams) -> Result<Value, InterceptorClientError> + Send + Sync;

struct MockClient {
    interceptors: Vec<InterceptorDescriptor>,
    handler: Arc<InvokeHandler>,
}

#[async_trait]
impl InterceptorClient for MockClient {
    async fn list(
        &self,
        event: Option<&str>,
    ) -> Result<ListInterceptorsResult, InterceptorClientError> {
        let interceptors = self
            .interceptors
            .iter()
            .filter(|interceptor| {
                event.is_none_or(|event| {
                    interceptor
                        .hooks
                        .iter()
                        .any(|hook| hook.events.iter().any(|candidate| candidate == event))
                })
            })
            .cloned()
            .collect();
        Ok(ListInterceptorsResult { interceptors })
    }

    async fn invoke(
        &self,
        params: InvokeInterceptorParams,
    ) -> Result<Value, InterceptorClientError> {
        (self.handler)(params)
    }
}

fn descriptor(name: &str, kind: InterceptorType, phases: &[Phase]) -> InterceptorDescriptor {
    InterceptorDescriptor {
        name: name.to_owned(),
        version: None,
        description: None,
        kind,
        hooks: phases
            .iter()
            .map(|phase| Hook {
                events: vec![events::TOOLS_CALL.to_owned()],
                phase: *phase,
            })
            .collect(),
        mode: None,
        fail_open: None,
        priority_hint: None,
        compat: None,
        config_schema: None,
        meta: None,
    }
}

async fn chain(
    interceptors: Vec<InterceptorDescriptor>,
    handler: impl Fn(InvokeInterceptorParams) -> Result<Value, InterceptorClientError>
    + Send
    + Sync
    + 'static,
) -> InterceptorChain {
    let client = Arc::new(MockClient {
        interceptors,
        handler: Arc::new(handler),
    }) as Arc<dyn InterceptorClient>;
    InterceptorChain::discover(vec![client], None)
        .await
        .unwrap()
}

#[test]
fn hook_overrides_may_narrow_but_not_widen_declared_hooks() {
    let client = Arc::new(MockClient {
        interceptors: Vec::new(),
        handler: Arc::new(|_| unreachable!()),
    }) as Arc<dyn InterceptorClient>;
    let entry = ChainEntry::new(
        descriptor("guard", InterceptorType::Validation, &[Phase::Request]),
        Arc::clone(&client),
    );
    let matching = InterceptorOverrides {
        hooks: Some(vec![Hook {
            events: vec![events::TOOLS_CALL.to_owned()],
            phase: Phase::Request,
        }]),
        ..InterceptorOverrides::default()
    };

    let entry = entry.with_overrides(matching).unwrap();
    assert!(entry.overrides().hooks.is_some());

    let widening = InterceptorOverrides {
        hooks: Some(vec![Hook {
            events: vec![events::PROMPTS_GET.to_owned()],
            phase: Phase::Request,
        }]),
        ..InterceptorOverrides::default()
    };
    let error = ChainEntry::new(
        descriptor("guard", InterceptorType::Validation, &[Phase::Request]),
        client,
    )
    .with_overrides(widening)
    .unwrap_err();

    assert!(matches!(error, ChainBuildError::HookOverrideWidens { .. }));
}

#[tokio::test]
async fn sending_runs_mutation_before_validation() {
    let calls = Arc::new(Mutex::new(Vec::new()));
    let mut mutator = descriptor("sanitize", InterceptorType::Mutation, &[Phase::Request]);
    mutator.priority_hint = Some(PriorityHint::Shared(-100));
    let validator = descriptor("guard", InterceptorType::Validation, &[Phase::Request]);

    let chain = chain(vec![validator, mutator], {
        let calls = Arc::clone(&calls);
        move |params| {
            calls.lock().unwrap().push(params.name.clone());
            match params.name.as_str() {
                "sanitize" => Ok(json!({
                    "modified": true,
                    "payload": { "safe": true }
                })),
                "guard" => Ok(json!({
                    "valid": params.payload == json!({ "safe": true }),
                    "severity": "error",
                    "messages": []
                })),
                _ => unreachable!(),
            }
        }
    })
    .await;

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            json!({ "unsafe": true }),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Success);
    assert_eq!(result.final_payload, json!({ "safe": true }));
    assert_eq!(*calls.lock().unwrap(), ["sanitize", "guard"]);
}

#[tokio::test]
async fn receiving_runs_validation_before_mutation() {
    let calls = Arc::new(Mutex::new(Vec::new()));
    let validator = descriptor("guard", InterceptorType::Validation, &[Phase::Response]);
    let mutator = descriptor("redact", InterceptorType::Mutation, &[Phase::Response]);

    let chain = chain(vec![mutator, validator], {
        let calls = Arc::clone(&calls);
        move |params| {
            calls.lock().unwrap().push(params.name.clone());
            match params.name.as_str() {
                "guard" => Ok(json!({
                    "valid": params.payload == json!({ "secret": "value" })
                })),
                "redact" => Ok(json!({
                    "modified": true,
                    "payload": { "secret": "[redacted]" }
                })),
                _ => unreachable!(),
            }
        }
    })
    .await;

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Response,
            Direction::Receiving,
            json!({ "secret": "value" }),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Success);
    assert_eq!(result.final_payload, json!({ "secret": "[redacted]" }));
    assert_eq!(*calls.lock().unwrap(), ["guard", "redact"]);
}

#[tokio::test]
async fn failed_sending_validation_rolls_back_mutations() {
    let mutator = descriptor("mutate", InterceptorType::Mutation, &[Phase::Request]);
    let validator = descriptor("reject", InterceptorType::Validation, &[Phase::Request]);
    let chain = chain(vec![mutator, validator], |params| {
        match params.name.as_str() {
            "mutate" => Ok(json!({ "modified": true, "payload": { "changed": true } })),
            "reject" => Ok(
                serde_json::to_value(mcp_ext_interceptors::ValidationResult {
                    valid: false,
                    severity: Some(Severity::Error),
                    messages: vec![ValidationMessage {
                        path: None,
                        message: "blocked".to_owned(),
                        severity: Severity::Error,
                    }],
                    suggestions: Vec::new(),
                    signature: None,
                    info: None,
                })
                .unwrap(),
            ),
            _ => unreachable!(),
        }
    })
    .await;
    let original = json!({ "original": true });

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            original.clone(),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::ValidationFailed);
    assert_eq!(result.final_payload, original);
    assert_eq!(result.validation_summary.errors, 1);
}

#[tokio::test]
async fn audit_mutations_and_fail_open_errors_do_not_block() {
    let mut audit = descriptor("audit", InterceptorType::Mutation, &[Phase::Request]);
    audit.mode = Some(Mode::Audit);
    let mut fail_open = descriptor("optional", InterceptorType::Validation, &[Phase::Request]);
    fail_open.fail_open = Some(true);
    let chain = chain(vec![audit, fail_open], |params| {
        match params.name.as_str() {
            "audit" => Ok(json!({ "modified": true, "payload": { "shadow": true } })),
            "optional" => Err(InterceptorClientError::other("unavailable")),
            _ => unreachable!(),
        }
    })
    .await;
    let original = json!({ "original": true });

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            original.clone(),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Success);
    assert_eq!(result.final_payload, original);
    assert_eq!(result.results.len(), 2);
}

#[tokio::test]
async fn classifies_rmcp_service_timeout_as_chain_timeout() {
    let validator = descriptor("slow", InterceptorType::Validation, &[Phase::Request]);
    let chain = chain(vec![validator], |_| {
        Err(InterceptorClientError::Service(ServiceError::Timeout {
            timeout: std::time::Duration::from_millis(25),
        }))
    })
    .await;

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            json!({}),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Timeout);
    assert_eq!(result.aborted_at[0].kind, AbortType::Timeout);
}

#[tokio::test]
async fn classifies_sep_timeout_error_code_as_chain_timeout() {
    let mutator = descriptor("remote-slow", InterceptorType::Mutation, &[Phase::Request]);
    let chain = chain(vec![mutator], |_| {
        Err(InterceptorClientError::Service(ServiceError::McpError(
            ErrorData::new(ErrorCode(-32000), "Interceptor execution timeout", None),
        )))
    })
    .await;

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            json!({}),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Timeout);
    assert_eq!(result.aborted_at[0].kind, AbortType::Timeout);
}

#[tokio::test]
async fn accepts_go_sdk_nested_invoke_results() {
    let mutator = descriptor("go-mutator", InterceptorType::Mutation, &[Phase::Request]);
    let chain = chain(vec![mutator], |_| {
        Ok(json!({
            "interceptor": "go-mutator",
            "type": "mutation",
            "phase": "request",
            "mutation": {
                "modified": true,
                "payload": { "compatible": true }
            }
        }))
    })
    .await;

    let result = chain
        .execute(ChainExecution::new(
            events::TOOLS_CALL,
            Phase::Request,
            Direction::Sending,
            json!({}),
        ))
        .await;

    assert_eq!(result.status, ChainStatus::Success);
    assert_eq!(result.final_payload, json!({ "compatible": true }));
}

#[tokio::test]
async fn middleware_applies_client_side_directions() {
    let calls = Arc::new(Mutex::new(Vec::new()));
    let validator = descriptor(
        "validator",
        InterceptorType::Validation,
        &[Phase::Request, Phase::Response],
    );
    let mutator = descriptor(
        "mutator",
        InterceptorType::Mutation,
        &[Phase::Request, Phase::Response],
    );
    let chain = chain(vec![validator, mutator], {
        let calls = Arc::clone(&calls);
        move |params| {
            calls
                .lock()
                .unwrap()
                .push((params.name.clone(), params.phase));
            if params.name == "validator" {
                Ok(json!({ "valid": true }))
            } else {
                Ok(json!({ "modified": false }))
            }
        }
    })
    .await;
    let middleware = mcp_ext_interceptors::InterceptorMiddleware::new(Arc::new(chain));

    let output = middleware
        .outgoing(
            events::TOOLS_CALL,
            json!({ "request": true }),
            &MiddlewareOptions::default(),
            |request| async move {
                assert_eq!(request, json!({ "request": true }));
                Ok::<_, ()>(json!({ "response": true }))
            },
        )
        .await
        .unwrap();

    assert_eq!(output, json!({ "response": true }));
    assert_eq!(
        *calls.lock().unwrap(),
        [
            ("mutator".to_owned(), Phase::Request),
            ("validator".to_owned(), Phase::Request),
            ("validator".to_owned(), Phase::Response),
            ("mutator".to_owned(), Phase::Response),
        ]
    );
}
