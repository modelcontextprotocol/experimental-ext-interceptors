use std::{
    collections::{HashMap, HashSet},
    fmt,
    sync::Arc,
    time::Duration,
};

use futures::future::join_all;
use rmcp::{Peer, RoleClient};
use serde_json::{Map, Value};
use thiserror::Error;
use tokio::time::Instant;

use crate::{
    Direction, Hook, InterceptorClient, InterceptorClientError, InterceptorDescriptor,
    InterceptorType, InvocationContext, InvocationResult, InvokeInterceptorParams, Mode, Phase,
    PriorityHint, RmcpInterceptorClient, Severity, ValidationResult,
};

#[derive(Debug, Clone, Default)]
pub struct InterceptorOverrides {
    pub fail_open: Option<bool>,
    pub priority_hint: Option<PriorityHint>,
    pub mode: Option<Mode>,
    pub timeout: Option<Duration>,
    pub hooks: Option<Vec<Hook>>,
}

#[derive(Clone)]
pub struct ChainEntry {
    pub interceptor: InterceptorDescriptor,
    overrides: InterceptorOverrides,
    client: Arc<dyn InterceptorClient>,
}

impl fmt::Debug for ChainEntry {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ChainEntry")
            .field("interceptor", &self.interceptor)
            .field("overrides", &self.overrides)
            .finish_non_exhaustive()
    }
}

impl ChainEntry {
    pub fn new(interceptor: InterceptorDescriptor, client: Arc<dyn InterceptorClient>) -> Self {
        Self {
            interceptor,
            overrides: InterceptorOverrides::default(),
            client,
        }
    }

    pub fn with_overrides(
        mut self,
        overrides: InterceptorOverrides,
    ) -> Result<Self, ChainBuildError> {
        if let Some(hooks) = &overrides.hooks {
            validate_hook_narrowing(&self.interceptor, hooks)?;
        }
        self.overrides = overrides;
        Ok(self)
    }

    pub fn overrides(&self) -> &InterceptorOverrides {
        &self.overrides
    }

    fn mode(&self) -> Mode {
        self.overrides
            .mode
            .unwrap_or_else(|| self.interceptor.resolved_mode())
    }

    fn fail_open(&self) -> bool {
        self.overrides
            .fail_open
            .unwrap_or_else(|| self.interceptor.resolved_fail_open())
    }

    fn priority(&self, phase: Phase) -> i32 {
        self.overrides
            .priority_hint
            .map(|priority| priority.resolve(phase))
            .unwrap_or_else(|| self.interceptor.resolved_priority(phase))
    }

    fn hooks(&self) -> &[Hook] {
        self.overrides
            .hooks
            .as_deref()
            .unwrap_or(&self.interceptor.hooks)
    }
}

#[derive(Debug, Error)]
pub enum ChainBuildError {
    #[error("failed to discover interceptors from server {server_index}: {source}")]
    Discovery {
        server_index: usize,
        #[source]
        source: InterceptorClientError,
    },
    #[error("hook override for interceptor {interceptor:?} widens its declared hooks")]
    HookOverrideWidens { interceptor: String },
}

#[derive(Debug, Clone)]
pub struct ChainExecution {
    pub event: String,
    pub phase: Phase,
    pub direction: Direction,
    pub payload: Value,
    pub interceptors: Vec<String>,
    pub config: HashMap<String, Map<String, Value>>,
    pub timeout: Option<Duration>,
    pub context: Option<InvocationContext>,
}

impl ChainExecution {
    pub fn new(
        event: impl Into<String>,
        phase: Phase,
        direction: Direction,
        payload: Value,
    ) -> Self {
        Self {
            event: event.into(),
            phase,
            direction,
            payload,
            interceptors: Vec::new(),
            config: HashMap::new(),
            timeout: None,
            context: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChainStatus {
    Success,
    ValidationFailed,
    MutationFailed,
    Timeout,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AbortType {
    Validation,
    Mutation,
    Timeout,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AbortInfo {
    pub interceptor: Option<String>,
    pub reason: String,
    pub kind: AbortType,
    pub phase: Phase,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ValidationSummary {
    pub errors: usize,
    pub warnings: usize,
    pub infos: usize,
}

#[derive(Debug, Clone, PartialEq)]
pub struct InterceptorExecution {
    pub interceptor: String,
    pub kind: InterceptorType,
    pub phase: Phase,
    pub mode: Mode,
    pub duration: Duration,
    pub result: Option<InvocationResult>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ChainExecutionResult {
    pub status: ChainStatus,
    pub event: String,
    pub phase: Phase,
    pub direction: Direction,
    pub results: Vec<InterceptorExecution>,
    pub final_payload: Value,
    pub validation_summary: ValidationSummary,
    pub total_duration: Duration,
    pub aborted_at: Vec<AbortInfo>,
}

impl ChainExecutionResult {
    pub fn is_success(&self) -> bool {
        self.status == ChainStatus::Success
    }
}

#[derive(Debug, Clone, Default)]
pub struct InterceptorChain {
    entries: Vec<ChainEntry>,
}

impl InterceptorChain {
    pub fn from_entries(entries: Vec<ChainEntry>) -> Self {
        Self { entries }
    }

    pub fn entries(&self) -> &[ChainEntry] {
        &self.entries
    }

    pub async fn discover(
        clients: Vec<Arc<dyn InterceptorClient>>,
        event: Option<&str>,
    ) -> Result<Self, ChainBuildError> {
        let event = event.map(str::to_owned);
        let discoveries = clients.iter().enumerate().map(|(server_index, client)| {
            let client = Arc::clone(client);
            let event = event.clone();
            async move {
                client
                    .list(event.as_deref())
                    .await
                    .map(|result| (client, result))
                    .map_err(|source| ChainBuildError::Discovery {
                        server_index,
                        source,
                    })
            }
        });

        let mut entries = Vec::new();
        for discovered in join_all(discoveries).await {
            let (client, result) = discovered?;
            entries.extend(
                result
                    .interceptors
                    .into_iter()
                    .map(|interceptor| ChainEntry::new(interceptor, Arc::clone(&client))),
            );
        }
        Ok(Self { entries })
    }

    pub async fn discover_rmcp(
        peers: impl IntoIterator<Item = Peer<RoleClient>>,
        event: Option<&str>,
    ) -> Result<Self, ChainBuildError> {
        let clients = peers
            .into_iter()
            .map(|peer| Arc::new(RmcpInterceptorClient::new(peer)) as Arc<dyn InterceptorClient>)
            .collect();
        Self::discover(clients, event).await
    }

    pub async fn execute(&self, execution: ChainExecution) -> ChainExecutionResult {
        let started = Instant::now();
        if let Some(timeout) = execution.timeout {
            match tokio::time::timeout(timeout, self.execute_inner(&execution)).await {
                Ok(mut result) => {
                    result.total_duration = started.elapsed();
                    result
                }
                Err(_) => ChainExecutionResult {
                    status: ChainStatus::Timeout,
                    event: execution.event,
                    phase: execution.phase,
                    direction: execution.direction,
                    results: Vec::new(),
                    final_payload: execution.payload,
                    validation_summary: ValidationSummary::default(),
                    total_duration: started.elapsed(),
                    aborted_at: vec![AbortInfo {
                        interceptor: None,
                        reason: "interceptor chain timeout exceeded".to_owned(),
                        kind: AbortType::Timeout,
                        phase: execution.phase,
                    }],
                },
            }
        } else {
            let mut result = self.execute_inner(&execution).await;
            result.total_duration = started.elapsed();
            result
        }
    }

    async fn execute_inner(&self, execution: &ChainExecution) -> ChainExecutionResult {
        let selected: HashSet<&str> = execution.interceptors.iter().map(String::as_str).collect();
        let applicable: Vec<_> = self
            .entries
            .iter()
            .filter(|entry| {
                (selected.is_empty() || selected.contains(entry.interceptor.name.as_str()))
                    && hooks_match(entry.hooks(), &execution.event, execution.phase)
            })
            .collect();

        let mut validators = Vec::new();
        let mut mutators = Vec::new();
        for entry in applicable {
            match entry.interceptor.kind {
                InterceptorType::Validation => validators.push(entry),
                InterceptorType::Mutation => mutators.push(entry),
            }
        }
        mutators.sort_by(|left, right| {
            left.priority(execution.phase)
                .cmp(&right.priority(execution.phase))
                .then_with(|| left.interceptor.name.cmp(&right.interceptor.name))
        });

        let original_payload = execution.payload.clone();
        let mut state = ExecutionState::new(original_payload.clone());

        match execution.direction {
            Direction::Sending => {
                self.run_mutators(execution, &mutators, &mut state).await;
                if state.aborted_at.is_empty() {
                    self.run_validators(execution, &validators, &mut state)
                        .await;
                }
            }
            Direction::Receiving => {
                self.run_validators(execution, &validators, &mut state)
                    .await;
                if state.aborted_at.is_empty() {
                    self.run_mutators(execution, &mutators, &mut state).await;
                }
            }
        }

        let status = state.status();
        let final_payload = if status == ChainStatus::Success {
            state.current_payload
        } else {
            original_payload
        };

        ChainExecutionResult {
            status,
            event: execution.event.clone(),
            phase: execution.phase,
            direction: execution.direction,
            results: state.results,
            final_payload,
            validation_summary: state.validation_summary,
            total_duration: Duration::ZERO,
            aborted_at: state.aborted_at,
        }
    }

    async fn run_validators(
        &self,
        execution: &ChainExecution,
        validators: &[&ChainEntry],
        state: &mut ExecutionState,
    ) {
        let payload = state.current_payload.clone();
        let attempts = validators.iter().map(|entry| async {
            (
                *entry,
                invoke_entry(entry, execution, payload.clone()).await,
            )
        });

        for (entry, attempt) in join_all(attempts).await {
            let mode = entry.mode();
            match attempt.result.and_then(decode_validation_result) {
                Ok(validation) => {
                    tally_validation(&validation, &mut state.validation_summary);
                    let blocks = mode == Mode::Active && validation_blocks(&validation);
                    let reason = validation_failure_reason(&validation);
                    state.results.push(InterceptorExecution {
                        interceptor: entry.interceptor.name.clone(),
                        kind: InterceptorType::Validation,
                        phase: execution.phase,
                        mode,
                        duration: attempt.duration,
                        result: Some(InvocationResult::Validation(validation)),
                        error: None,
                    });
                    if blocks {
                        state.aborted_at.push(AbortInfo {
                            interceptor: Some(entry.interceptor.name.clone()),
                            reason,
                            kind: AbortType::Validation,
                            phase: execution.phase,
                        });
                    }
                }
                Err(error) => {
                    let kind = abort_type(&error, AbortType::Validation);
                    let reason = error.to_string();
                    state.results.push(InterceptorExecution {
                        interceptor: entry.interceptor.name.clone(),
                        kind: InterceptorType::Validation,
                        phase: execution.phase,
                        mode,
                        duration: attempt.duration,
                        result: None,
                        error: Some(reason.clone()),
                    });
                    if mode == Mode::Active && !entry.fail_open() {
                        state.aborted_at.push(AbortInfo {
                            interceptor: Some(entry.interceptor.name.clone()),
                            reason,
                            kind,
                            phase: execution.phase,
                        });
                    }
                }
            }
        }
    }

    async fn run_mutators(
        &self,
        execution: &ChainExecution,
        mutators: &[&ChainEntry],
        state: &mut ExecutionState,
    ) {
        for entry in mutators {
            let mode = entry.mode();
            let attempt = invoke_entry(entry, execution, state.current_payload.clone()).await;
            match attempt.result.and_then(decode_mutation_result) {
                Ok(mutation) => {
                    let modified = mutation.modified;
                    let next_payload = mutation.payload.clone();
                    state.results.push(InterceptorExecution {
                        interceptor: entry.interceptor.name.clone(),
                        kind: InterceptorType::Mutation,
                        phase: execution.phase,
                        mode,
                        duration: attempt.duration,
                        result: Some(InvocationResult::Mutation(mutation)),
                        error: None,
                    });

                    if mode == Mode::Active
                        && modified
                        && let Some(payload) = next_payload
                    {
                        state.current_payload = payload;
                    }
                }
                Err(error) => {
                    let kind = abort_type(&error, AbortType::Mutation);
                    let reason = error.to_string();
                    state.results.push(InterceptorExecution {
                        interceptor: entry.interceptor.name.clone(),
                        kind: InterceptorType::Mutation,
                        phase: execution.phase,
                        mode,
                        duration: attempt.duration,
                        result: None,
                        error: Some(reason.clone()),
                    });
                    if mode == Mode::Active && !entry.fail_open() {
                        state.aborted_at.push(AbortInfo {
                            interceptor: Some(entry.interceptor.name.clone()),
                            reason,
                            kind,
                            phase: execution.phase,
                        });
                        break;
                    }
                }
            }
        }
    }
}

struct ExecutionState {
    current_payload: Value,
    results: Vec<InterceptorExecution>,
    validation_summary: ValidationSummary,
    aborted_at: Vec<AbortInfo>,
}

impl ExecutionState {
    fn new(payload: Value) -> Self {
        Self {
            current_payload: payload,
            results: Vec::new(),
            validation_summary: ValidationSummary::default(),
            aborted_at: Vec::new(),
        }
    }

    fn status(&self) -> ChainStatus {
        if self
            .aborted_at
            .iter()
            .any(|abort| abort.kind == AbortType::Timeout)
        {
            ChainStatus::Timeout
        } else if self
            .aborted_at
            .iter()
            .any(|abort| abort.kind == AbortType::Validation)
        {
            ChainStatus::ValidationFailed
        } else if self
            .aborted_at
            .iter()
            .any(|abort| abort.kind == AbortType::Mutation)
        {
            ChainStatus::MutationFailed
        } else {
            ChainStatus::Success
        }
    }
}

struct InvocationAttempt {
    result: Result<Value, InterceptorClientError>,
    duration: Duration,
}

async fn invoke_entry(
    entry: &ChainEntry,
    execution: &ChainExecution,
    payload: Value,
) -> InvocationAttempt {
    let timeout = entry.overrides.timeout;
    let timeout_ms = timeout.map(duration_millis);
    let params = InvokeInterceptorParams {
        name: entry.interceptor.name.clone(),
        event: execution.event.clone(),
        phase: execution.phase,
        payload,
        config: execution.config.get(&entry.interceptor.name).cloned(),
        timeout_ms,
        context: execution.context.clone(),
    };
    let started = Instant::now();
    let result = match timeout {
        Some(timeout) => match tokio::time::timeout(timeout, entry.client.invoke(params)).await {
            Ok(result) => result,
            Err(_) => Err(InterceptorClientError::Timeout {
                timeout_ms: duration_millis(timeout),
            }),
        },
        None => entry.client.invoke(params).await,
    };
    InvocationAttempt {
        result,
        duration: started.elapsed(),
    }
}

fn decode_validation_result(value: Value) -> Result<ValidationResult, InterceptorClientError> {
    let value = nested_result(value, "validation");
    serde_json::from_value(value).map_err(InterceptorClientError::Decode)
}

fn decode_mutation_result(value: Value) -> Result<crate::MutationResult, InterceptorClientError> {
    let value = nested_result(value, "mutation");
    let result: crate::MutationResult =
        serde_json::from_value(value).map_err(InterceptorClientError::Decode)?;
    if result.modified && result.payload.is_none() {
        return Err(InterceptorClientError::other(
            "mutator reported modified=true without a payload",
        ));
    }
    Ok(result)
}

fn nested_result(value: Value, field: &str) -> Value {
    value
        .as_object()
        .and_then(|object| object.get(field))
        .cloned()
        .unwrap_or(value)
}

fn duration_millis(duration: Duration) -> u64 {
    duration.as_millis().min(u64::MAX as u128) as u64
}

fn abort_type(error: &InterceptorClientError, fallback: AbortType) -> AbortType {
    let timed_out = match error {
        InterceptorClientError::Timeout { .. }
        | InterceptorClientError::Service(rmcp::ServiceError::Timeout { .. }) => true,
        InterceptorClientError::Service(rmcp::ServiceError::McpError(error)) => {
            error.code.0 == -32000
        }
        _ => false,
    };
    if timed_out {
        AbortType::Timeout
    } else {
        fallback
    }
}

fn hooks_match(hooks: &[Hook], event: &str, phase: Phase) -> bool {
    hooks.iter().any(|hook| {
        hook.phase == phase
            && hook
                .events
                .iter()
                .any(|pattern| event_matches(pattern, event))
    })
}

fn event_matches(pattern: &str, event: &str) -> bool {
    pattern == "*"
        || pattern == event
        || pattern.strip_suffix("/*").is_some_and(|prefix| {
            event.starts_with(prefix) && event[prefix.len()..].starts_with('/')
        })
}

fn validate_hook_narrowing(
    interceptor: &InterceptorDescriptor,
    overrides: &[Hook],
) -> Result<(), ChainBuildError> {
    let widens = overrides.iter().any(|override_hook| {
        override_hook.events.iter().any(|override_event| {
            !interceptor.hooks.iter().any(|declared| {
                declared.phase == override_hook.phase
                    && declared
                        .events
                        .iter()
                        .any(|event| event_matches(event, override_event))
            })
        })
    });
    if widens {
        Err(ChainBuildError::HookOverrideWidens {
            interceptor: interceptor.name.clone(),
        })
    } else {
        Ok(())
    }
}

fn tally_validation(validation: &ValidationResult, summary: &mut ValidationSummary) {
    if validation.messages.is_empty() {
        if let Some(severity) = validation.severity {
            tally_severity(severity, summary);
        }
    } else {
        for message in &validation.messages {
            tally_severity(message.severity, summary);
        }
    }
}

fn tally_severity(severity: Severity, summary: &mut ValidationSummary) {
    match severity {
        Severity::Error => summary.errors += 1,
        Severity::Warn => summary.warnings += 1,
        Severity::Info => summary.infos += 1,
    }
}

fn validation_blocks(validation: &ValidationResult) -> bool {
    !validation.valid
        && (validation.severity == Some(Severity::Error)
            || validation
                .messages
                .iter()
                .any(|message| message.severity == Severity::Error))
}

fn validation_failure_reason(validation: &ValidationResult) -> String {
    validation
        .messages
        .iter()
        .find(|message| message.severity == Severity::Error)
        .or_else(|| validation.messages.first())
        .map(|message| message.message.clone())
        .unwrap_or_else(|| "validation failed".to_owned())
}
