use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

pub const EXTENSION_ID: &str = "io.modelcontextprotocol/interceptors";
pub const METHOD_LIST: &str = "interceptors/list";
pub const METHOD_INVOKE: &str = "interceptor/invoke";

pub mod events {
    pub const TOOLS_LIST: &str = "tools/list";
    pub const TOOLS_CALL: &str = "tools/call";
    pub const PROMPTS_LIST: &str = "prompts/list";
    pub const PROMPTS_GET: &str = "prompts/get";
    pub const RESOURCES_LIST: &str = "resources/list";
    pub const RESOURCES_READ: &str = "resources/read";
    pub const RESOURCES_SUBSCRIBE: &str = "resources/subscribe";
    pub const SAMPLING_CREATE_MESSAGE: &str = "sampling/createMessage";
    pub const ELICITATION_CREATE: &str = "elicitation/create";
    pub const ROOTS_LIST: &str = "roots/list";
    pub const LLM_COMPLETION: &str = "llm/completion";
    pub const ALL: &str = "*";
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterceptorsCapability {
    #[serde(default)]
    pub supported_events: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Phase {
    Request,
    Response,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Direction {
    Sending,
    Receiving,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum InterceptorType {
    Validation,
    Mutation,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Mode {
    #[default]
    Active,
    Audit,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Severity {
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum PriorityHint {
    Shared(i32),
    ByPhase {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        request: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        response: Option<i32>,
    },
}

impl PriorityHint {
    pub fn resolve(self, phase: Phase) -> i32 {
        match self {
            Self::Shared(priority) => priority,
            Self::ByPhase { request, response } => match phase {
                Phase::Request => request.unwrap_or_default(),
                Phase::Response => response.unwrap_or_default(),
            },
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Hook {
    pub events: Vec<String>,
    pub phase: Phase,
}

impl Hook {
    pub fn new<I, S>(events: I, phase: Phase) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        Self {
            events: events.into_iter().map(Into::into).collect(),
            phase,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProtocolCompatibility {
    pub min_protocol: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_protocol: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterceptorDescriptor {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(rename = "type")]
    pub kind: InterceptorType,
    pub hooks: Vec<Hook>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mode: Option<Mode>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fail_open: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub priority_hint: Option<PriorityHint>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub compat: Option<ProtocolCompatibility>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub config_schema: Option<Value>,
    #[serde(rename = "_meta", default, skip_serializing_if = "Option::is_none")]
    pub meta: Option<Map<String, Value>>,
}

impl InterceptorDescriptor {
    pub fn new(name: impl Into<String>, kind: InterceptorType, hooks: Vec<Hook>) -> Self {
        Self {
            name: name.into(),
            version: None,
            description: None,
            kind,
            hooks,
            mode: None,
            fail_open: None,
            priority_hint: None,
            compat: None,
            config_schema: None,
            meta: None,
        }
    }

    pub fn mutation(name: impl Into<String>, hooks: Vec<Hook>) -> Self {
        Self::new(name, InterceptorType::Mutation, hooks)
    }

    pub fn validation(name: impl Into<String>, hooks: Vec<Hook>) -> Self {
        Self::new(name, InterceptorType::Validation, hooks)
    }

    pub fn resolved_mode(&self) -> Mode {
        self.mode.unwrap_or_default()
    }

    pub fn resolved_fail_open(&self) -> bool {
        self.fail_open.unwrap_or(false)
    }

    pub fn resolved_priority(&self, phase: Phase) -> i32 {
        self.priority_hint
            .map(|priority| priority.resolve(phase))
            .unwrap_or_default()
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListInterceptorsParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub event: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListInterceptorsResult {
    #[serde(default)]
    pub interceptors: Vec<InterceptorDescriptor>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InvokeInterceptorParams {
    pub name: String,
    pub event: String,
    pub phase: Phase,
    pub payload: Value,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub config: Option<Map<String, Value>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub context: Option<InvocationContext>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InvocationContext {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub principal: Option<Principal>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub trace_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub span_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timestamp: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Principal {
    #[serde(rename = "type")]
    pub kind: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub claims: Option<Map<String, Value>>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ValidationMessage {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    pub message: String,
    pub severity: Severity,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ValidationSuggestion {
    pub path: String,
    pub value: Value,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ValidationResult {
    pub valid: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub severity: Option<Severity>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub messages: Vec<ValidationMessage>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub suggestions: Vec<ValidationSuggestion>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub info: Option<Map<String, Value>>,
}

impl ValidationResult {
    pub fn valid() -> Self {
        Self {
            valid: true,
            severity: None,
            messages: Vec::new(),
            suggestions: Vec::new(),
            signature: None,
            info: None,
        }
    }

    pub fn invalid(message: impl Into<String>) -> Self {
        Self {
            valid: false,
            severity: Some(Severity::Error),
            messages: vec![ValidationMessage {
                path: None,
                message: message.into(),
                severity: Severity::Error,
            }],
            suggestions: Vec::new(),
            signature: None,
            info: None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MutationResult {
    pub modified: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub payload: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub info: Option<Map<String, Value>>,
}

impl MutationResult {
    pub fn modified(payload: Value) -> Self {
        Self {
            modified: true,
            payload: Some(payload),
            info: None,
        }
    }

    pub fn unchanged() -> Self {
        Self {
            modified: false,
            payload: None,
            info: None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum InvocationResult {
    Validation(ValidationResult),
    Mutation(MutationResult),
}

impl From<ValidationResult> for InvocationResult {
    fn from(result: ValidationResult) -> Self {
        Self::Validation(result)
    }
}

impl From<MutationResult> for InvocationResult {
    fn from(result: MutationResult) -> Self {
        Self::Mutation(result)
    }
}
