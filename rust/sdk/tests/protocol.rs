use mcp_ext_interceptors::{
    InterceptorDescriptor, InterceptorsCapability, Mode, Phase, PriorityHint,
};
use serde_json::json;

#[test]
fn interceptor_descriptor_matches_the_sep_wire_shape() {
    let descriptor: InterceptorDescriptor = serde_json::from_value(json!({
        "name": "redactor",
        "type": "mutation",
        "hooks": [{ "events": ["tools/call"], "phase": "request" }],
        "priorityHint": { "request": -1000 },
        "failOpen": true
    }))
    .unwrap();

    assert_eq!(descriptor.resolved_mode(), Mode::Active);
    assert!(descriptor.resolved_fail_open());
    assert_eq!(descriptor.resolved_priority(Phase::Request), -1000);
    assert_eq!(descriptor.resolved_priority(Phase::Response), 0);
    assert_eq!(
        descriptor.priority_hint,
        Some(PriorityHint::ByPhase {
            request: Some(-1000),
            response: None,
        })
    );

    let value = serde_json::to_value(descriptor).unwrap();
    assert_eq!(value["type"], "mutation");
    assert_eq!(value["priorityHint"], json!({ "request": -1000 }));
    assert!(value.get("mode").is_none());
}

#[test]
fn interceptor_capability_uses_supported_events() {
    let capability = InterceptorsCapability {
        supported_events: vec!["tools/call".to_owned(), "roots/list".to_owned()],
    };

    assert_eq!(
        serde_json::to_value(capability).unwrap(),
        json!({ "supportedEvents": ["tools/call", "roots/list"] })
    );
}
