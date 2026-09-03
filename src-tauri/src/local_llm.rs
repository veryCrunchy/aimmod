use crate::{AppState, hub_api};
use anyhow::{Context, anyhow};
use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use serde_json::{Value as JsonValue, json};
use std::{
    collections::HashMap,
    fs,
    io::{Read, Write},
    net::TcpListener,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{Arc, Mutex},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tauri::{AppHandle, Emitter, Manager, State};

const DEFAULT_HOST: &str = "127.0.0.1";
const DEFAULT_PORT: u16 = 38371;
const DEFAULT_CONTEXT_SIZE: u32 = 12_288;
const MIN_CONTEXT_SIZE: u32 = 8_192;
const DEFAULT_TEMPERATURE: f64 = 0.35;
const STARTUP_TIMEOUT_MS: u64 = 90_000;
const POLL_INTERVAL_MS: u64 = 250;
const DEFAULT_REMOTE_MANIFEST_URL: &str = "https://aimmod.app/llm/manifest.json";
pub const LOCAL_COACH_STREAM_EVENT: &str = "local-coach-stream";

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachInputCard {
    pub title: String,
    pub badge: String,
    pub body: String,
    pub tip: String,
    pub signals: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachChatRequest {
    pub scenario_name: String,
    pub scenario_type: String,
    pub question: String,
    pub signal_keys: Vec<String>,
    pub context_tags: Vec<String>,
    pub focus_area: String,
    pub challenge_preference: String,
    pub time_preference: String,
    pub scenario_summary: String,
    pub global_summary: String,
    pub coaching_cards: Vec<LocalCoachInputCard>,
    /// When true, the KB query ignores scenario-compatibility filtering so
    /// entries that don't match the current scenario are still considered.
    pub general: bool,
    /// Prior turns in the conversation: [(user_question, coach_answer), ...].
    /// Passed so the LLM can answer follow-up questions with full context.
    pub conversation_history: Vec<LocalCoachTurn>,
    pub coach_facts: Vec<LocalCoachFact>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachTurn {
    pub question: String,
    pub answer: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachKnowledgePreview {
    pub id: String,
    pub title: String,
    pub summary: String,
    pub why: Vec<String>,
    pub actions: Vec<String>,
    pub drills: Vec<LocalCoachKnowledgeDrillPreview>,
    pub avoid: Vec<String>,
    pub sources: Vec<LocalCoachKnowledgeSourcePreview>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachKnowledgeDrillPreview {
    pub label: String,
    pub query: String,
    pub reason: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachKnowledgeSourcePreview {
    pub id: String,
    pub title: String,
    pub author: String,
    pub url: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachVisualPoint {
    pub label: String,
    pub value: f64,
    pub secondary_value: Option<f64>,
    pub note: String,
    pub values: HashMap<String, f64>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachVisualSeries {
    pub key: String,
    pub label: String,
    pub kind: String,
    pub color: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachVisual {
    pub id: String,
    pub kind: String,
    pub title: String,
    pub subtitle: String,
    pub primary_label: String,
    pub secondary_label: String,
    pub points: Vec<LocalCoachVisualPoint>,
    pub series: Vec<LocalCoachVisualSeries>,
    pub detail_lines: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct LocalCoachVisualDataset {
    pub id: String,
    pub source: String,
    pub default_x_key: String,
    pub rows: Vec<JsonValue>,
    pub available_metrics: Vec<String>,
    pub available_dimensions: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct VisualCreateRequest {
    pub id: String,
    pub dataset_id: String,
    pub x_key: String,
    pub kind: String,
    pub title: String,
    pub subtitle: String,
    pub primary_label: String,
    pub secondary_label: String,
    pub points: Vec<LocalCoachVisualPoint>,
    pub series: Vec<LocalCoachVisualSeries>,
    pub detail_lines: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachFact {
    pub key: String,
    pub label: String,
    pub value_text: String,
    pub numeric_value: Option<f64>,
    pub bool_value: Option<bool>,
    pub direction: String,
    pub confidence: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachAnswerPlan {
    pub intent: String,
    pub response_shape: String,
    pub must_answer_directly: bool,
    pub primary_findings: Vec<String>,
    pub suggested_actions: Vec<String>,
    pub clarifying_question: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalLlmRuntimeStatus {
    pub state: String,
    pub detail: String,
    pub can_start: bool,
    pub asset_root: String,
    pub manifest_path: String,
    pub runner_path: String,
    pub model_path: String,
    pub endpoint: String,
    pub model_id: String,
    pub pid: Option<u32>,
    pub launched_at_unix_ms: Option<u64>,
    pub active_gpu_layers: i32,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachChatResponse {
    pub message: String,
    pub model: String,
    pub runtime_status: LocalLlmRuntimeStatus,
    pub knowledge_items: Vec<LocalCoachKnowledgePreview>,
    pub answer_plan: LocalCoachAnswerPlan,
    pub visuals: Vec<LocalCoachVisual>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LocalCoachStreamEvent {
    pub stream_id: String,
    pub kind: String,
    pub delta: String,
    pub content: String,
    pub done: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct RuntimeManifest {
    runtime_path: String,
    model_path: String,
    host: String,
    port: u16,
    context_size: u32,
    gpu_layers: i32,
    threads: Option<u32>,
    model_id: String,
    extra_args: Vec<String>,
}

impl Default for RuntimeManifest {
    fn default() -> Self {
        Self {
            runtime_path: "runner/llama-server".to_string(),
            model_path: "models/aimmod-coach.gguf".to_string(),
            host: DEFAULT_HOST.to_string(),
            port: DEFAULT_PORT,
            context_size: DEFAULT_CONTEXT_SIZE,
            gpu_layers: 0,
            threads: None,
            model_id: String::new(),
            extra_args: vec![
                "--parallel".to_string(),
                "1".to_string(),
                "--jinja".to_string(),
            ],
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct RemoteAssetManifest {
    version: String,
    runtime: std::collections::HashMap<String, RemoteRuntimePackage>,
    model: RemoteModelPackage,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct RemoteRuntimePackage {
    url: String,
    sha256: String,
    archive_type: String,
    extras: Vec<RemoteRuntimePackageExtra>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct RemoteRuntimePackageExtra {
    url: String,
    sha256: String,
    archive_type: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct RemoteModelPackage {
    url: String,
    sha256: String,
    filename: String,
}

#[derive(Debug, Default)]
pub struct LocalLlmRuntimeState {
    child: Option<Child>,
    launched_at_unix_ms: Option<u64>,
    model_id: Option<String>,
    endpoint: Option<String>,
    last_error: Option<String>,
    /// The --n-gpu-layers value used when this server instance was launched.
    active_gpu_layers: i32,
}

#[derive(Debug, Clone)]
struct ResolvedRuntimeAssets {
    asset_root: PathBuf,
    manifest_path: PathBuf,
    runner_path: PathBuf,
    runner_dir: PathBuf,
    model_path: PathBuf,
    stdout_log_path: PathBuf,
    stderr_log_path: PathBuf,
    endpoint: String,
    manifest: RuntimeManifest,
}

#[derive(Debug, Deserialize)]
struct OpenAiModelsResponse {
    data: Vec<OpenAiModelEntry>,
}

#[derive(Debug, Deserialize)]
struct OpenAiModelEntry {
    id: String,
}

#[derive(Debug, Serialize)]
struct OpenAiChatRequest {
    model: String,
    temperature: f64,
    #[serde(skip_serializing_if = "is_false")]
    stream: bool,
    messages: Vec<OpenAiChatMessage>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    tools: Vec<ToolDefinition>,
    #[serde(skip_serializing_if = "Option::is_none")]
    tool_choice: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
struct OpenAiChatMessage {
    role: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    content: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    tool_calls: Option<Vec<ToolCallItem>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    tool_call_id: Option<String>,
}

impl OpenAiChatMessage {
    fn system(content: impl Into<String>) -> Self {
        Self {
            role: "system".into(),
            content: Some(content.into()),
            tool_calls: None,
            tool_call_id: None,
        }
    }
    fn user(content: impl Into<String>) -> Self {
        Self {
            role: "user".into(),
            content: Some(content.into()),
            tool_calls: None,
            tool_call_id: None,
        }
    }
    fn assistant_tool_calls(tool_calls: Vec<ToolCallItem>) -> Self {
        Self {
            role: "assistant".into(),
            content: None,
            tool_calls: Some(tool_calls),
            tool_call_id: None,
        }
    }
    fn tool_result(id: impl Into<String>, content: impl Into<String>) -> Self {
        Self {
            role: "tool".into(),
            content: Some(content.into()),
            tool_calls: None,
            tool_call_id: Some(id.into()),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ToolCallItem {
    id: String,
    #[serde(rename = "type")]
    kind: String,
    function: ToolCallFunction,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ToolCallFunction {
    name: String,
    arguments: String,
}

#[derive(Debug, Clone, Serialize)]
struct ToolDefinition {
    #[serde(rename = "type")]
    kind: String,
    function: ToolFunctionDef,
}

#[derive(Debug, Clone, Serialize)]
struct ToolFunctionDef {
    name: String,
    description: String,
    parameters: JsonValue,
}

#[derive(Debug, Deserialize)]
struct OpenAiChatResponse {
    choices: Vec<OpenAiChatChoice>,
    model: Option<String>,
}

#[derive(Debug, Deserialize)]
struct OpenAiChatChoice {
    message: OpenAiChatChoiceMessage,
    finish_reason: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(default)]
struct OpenAiChatChoiceMessage {
    content: Option<String>,
    tool_calls: Option<Vec<ToolCallItem>>,
}

fn is_false(value: &bool) -> bool {
    !*value
}

#[derive(Debug, Deserialize)]
struct OpenAiChatStreamResponse {
    choices: Vec<OpenAiChatStreamChoice>,
    model: Option<String>,
}

#[derive(Debug, Deserialize)]
struct OpenAiChatStreamChoice {
    #[serde(default)]
    delta: OpenAiChatStreamDelta,
}

#[derive(Debug, Default, Deserialize)]
struct OpenAiChatStreamDelta {
    content: Option<String>,
}

#[derive(Debug, Clone)]
struct RetrievedKnowledge {
    tool_instruction: String,
    items: Vec<LocalCoachKnowledgePreview>,
    answer_plan: LocalCoachAnswerPlan,
    visuals: Vec<LocalCoachVisual>,
    datasets: Vec<LocalCoachVisualDataset>,
}

pub fn default_runtime_state() -> Arc<Mutex<LocalLlmRuntimeState>> {
    Arc::new(Mutex::new(LocalLlmRuntimeState::default()))
}

/// Kill the llama-server child if it is running.  Called on app exit so the
/// process doesn't become an orphan that holds the port across restarts.
pub fn kill_child_if_running(app: &AppHandle) {
    let state: tauri::State<'_, crate::AppState> = app.state();
    if let Ok(mut runtime) = state.local_llm.lock() {
        if let Some(mut child) = runtime.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

pub async fn get_runtime_status(
    app: &AppHandle,
    state: &State<'_, AppState>,
) -> anyhow::Result<LocalLlmRuntimeStatus> {
    build_runtime_status(app, &state.local_llm)
}

pub async fn install_assets(
    app: &AppHandle,
    state: &State<'_, AppState>,
) -> anyhow::Result<LocalLlmRuntimeStatus> {
    {
        let mut runtime = state
            .local_llm
            .lock()
            .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
        if let Some(mut child) = runtime.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        runtime.model_id = None;
        runtime.endpoint = None;
        runtime.last_error = None;
        runtime.launched_at_unix_ms = None;
    }

    let _ = ensure_assets_available(app).await?;
    build_runtime_status(app, &state.local_llm)
}

pub async fn uninstall_assets(
    app: &AppHandle,
    state: &State<'_, AppState>,
) -> anyhow::Result<LocalLlmRuntimeStatus> {
    // Kill child if running.
    {
        let mut runtime = state
            .local_llm
            .lock()
            .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
        if let Some(mut child) = runtime.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        runtime.model_id = None;
        runtime.endpoint = None;
        runtime.last_error = None;
        runtime.launched_at_unix_ms = None;
    }
    // Delete the install root directory entirely.
    let install_root = installed_asset_root(app)?;
    if install_root.exists() {
        fs::remove_dir_all(&install_root).with_context(|| {
            format!(
                "could not remove local coach install directory {}",
                install_root.display()
            )
        })?;
    }
    build_runtime_status(app, &state.local_llm)
}

pub async fn stop_runtime(
    app: &AppHandle,
    state: &State<'_, AppState>,
) -> anyhow::Result<LocalLlmRuntimeStatus> {
    let mut runtime = state
        .local_llm
        .lock()
        .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
    if let Some(mut child) = runtime.child.take() {
        let _ = child.kill();
        let _ = child.wait();
    }
    runtime.model_id = None;
    runtime.endpoint = None;
    runtime.last_error = None;
    runtime.launched_at_unix_ms = None;
    drop(runtime);
    build_runtime_status(app, &state.local_llm)
}

pub async fn generate_coaching_reply(
    app: &AppHandle,
    state: &State<'_, AppState>,
    request: LocalCoachChatRequest,
) -> anyhow::Result<LocalCoachChatResponse> {
    generate_coaching_reply_inner(app, state, request, None).await
}

pub async fn stream_coaching_reply(
    app: &AppHandle,
    state: &State<'_, AppState>,
    request: LocalCoachChatRequest,
    stream_id: String,
) -> anyhow::Result<LocalCoachChatResponse> {
    generate_coaching_reply_inner(app, state, request, Some(stream_id)).await
}

async fn generate_coaching_reply_inner(
    app: &AppHandle,
    state: &State<'_, AppState>,
    request: LocalCoachChatRequest,
    stream_id: Option<String>,
) -> anyhow::Result<LocalCoachChatResponse> {
    match generate_coaching_reply_once(app, state, &request, stream_id.clone()).await {
        Ok(response) => Ok(response),
        Err(first_error) => {
            emit_status_event(
                app,
                stream_id.as_deref(),
                "Coach hit an internal error; retrying once automatically",
            );
            match generate_coaching_reply_once(app, state, &request, stream_id.clone()).await {
                Ok(response) => Ok(response),
                Err(second_error) => {
                    let error_message = format!(
                        "local coach failed after automatic retry: first attempt: {first_error}; second attempt: {second_error}"
                    );
                    if let Some(sid) = stream_id.as_deref() {
                        emit_stream_event(
                            app,
                            LocalCoachStreamEvent {
                                stream_id: sid.to_string(),
                                kind: "error".to_string(),
                                delta: String::new(),
                                content: String::new(),
                                done: true,
                                error: Some(error_message.clone()),
                            },
                        );
                    }
                    Err(anyhow!(error_message))
                }
            }
        }
    }
}

async fn generate_coaching_reply_once(
    app: &AppHandle,
    state: &State<'_, AppState>,
    request: &LocalCoachChatRequest,
    stream_id: Option<String>,
) -> anyhow::Result<LocalCoachChatResponse> {
    emit_status_event(app, stream_id.as_deref(), "Reading local context");
    let mut retrieved_knowledge = RetrievedKnowledge {
        tool_instruction: String::new(),
        items: vec![],
        answer_plan: LocalCoachAnswerPlan::default(),
        visuals: vec![],
        datasets: vec![],
    };

    let gpu_layers = state
        .settings
        .lock()
        .map(|s| s.local_llm_gpu_layers)
        .unwrap_or(0);
    let runtime = ensure_runtime_started(app, &state.local_llm, gpu_layers).await?;
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(300))
        .build()
        .context("could not build local LLM HTTP client")?;

    let tools = build_tools();
    let mut messages: Vec<OpenAiChatMessage> = vec![
        OpenAiChatMessage::system(build_system_prompt()),
        OpenAiChatMessage::user(build_user_prompt(request)),
    ];
    let mut response_model: Option<String> = None;
    let mut final_message = String::new();
    let mut require_tool_call = false;
    let mut tool_roleplay_retries = 0usize;
    let mut visual_output_retries = 0usize;
    let mut tool_error_retries = 0usize;
    const MAX_ROUNDS: usize = 5;

    for round in 0..MAX_ROUNDS {
        let last_round = round == MAX_ROUNDS - 1;
        let chat = OpenAiChatRequest {
            model: runtime.model_id.clone(),
            temperature: DEFAULT_TEMPERATURE,
            stream: false,
            messages: messages.clone(),
            tools: if last_round { vec![] } else { tools.clone() },
            tool_choice: if last_round {
                None
            } else if require_tool_call {
                Some("required".to_string())
            } else {
                Some("auto".to_string())
            },
        };

        let resp = send_local_coach_chat_request(&client, &runtime.endpoint, &chat).await?;

        let parsed = resp
            .json::<OpenAiChatResponse>()
            .await
            .context("could not decode local coach response")?;

        if response_model.is_none() {
            response_model = parsed.model.clone();
        }

        let choice = parsed
            .choices
            .into_iter()
            .next()
            .ok_or_else(|| anyhow!("local coach returned no choices"))?;

        // If the model wants to call tools, execute them and continue.
        if let Some(tool_calls) = choice.message.tool_calls.filter(|tc| !tc.is_empty()) {
            require_tool_call = false;
            messages.push(OpenAiChatMessage::assistant_tool_calls(tool_calls.clone()));
            let mut saw_tool_error = None::<String>;
            for call in &tool_calls {
                let args: JsonValue = serde_json::from_str(&call.function.arguments)
                    .unwrap_or(JsonValue::Object(Default::default()));
                let result = execute_tool_call(
                    app,
                    request,
                    &mut retrieved_knowledge,
                    stream_id.as_deref(),
                    &call.function.name,
                    &args,
                )
                .await;
                if saw_tool_error.is_none() {
                    saw_tool_error = parse_tool_error_message(&result)
                        .map(|error| format!("{} failed: {}", call.function.name, error));
                }
                messages.push(OpenAiChatMessage::tool_result(&call.id, result));
            }
            if let Some(tool_error) = saw_tool_error {
                tool_error_retries += 1;
                require_tool_call = true;
                emit_status_event(
                    app,
                    stream_id.as_deref(),
                    "A tool call failed; retrying with corrective guidance",
                );
                messages.push(OpenAiChatMessage::system(format!(
                    "Your previous tool call failed: {}. \
Fix the request and call the needed tool again before answering. \
For visuals, always fetch a dataset tool first and then call create_visual with datasetId, xKey, and series.",
                    tool_error
                )));
                if !last_round && tool_error_retries < 3 {
                    continue;
                }
                ensure_visual_fallback(request, &mut retrieved_knowledge);
                final_message = build_visual_fallback_response(request, &retrieved_knowledge)
                    .unwrap_or_else(|| build_knowledge_gap_response(request, &retrieved_knowledge));
                break;
            }
            continue;
        }

        // No tool calls — this is the final answer.
        let candidate_message = choice
            .message
            .content
            .unwrap_or_default()
            .trim()
            .to_string();

        if !last_round && response_roleplays_tool_use(&candidate_message) {
            tool_roleplay_retries += 1;
            require_tool_call = true;
            emit_status_event(
                app,
                stream_id.as_deref(),
                "Coach narrated tool use without calling a tool; retrying with real tool usage",
            );
            messages.push(OpenAiChatMessage::system(
                "Your previous reply narrated planned tool use instead of actually calling a tool. \
If you need data, call the appropriate tool now. If you do not need a tool, answer directly right now. \
Do not say that you will fetch, read, query, analyze, or create a chart later. Either call a tool or give the final answer.",
            ));
            if tool_roleplay_retries < 2 {
                continue;
            }
        }

        if !last_round && response_prints_pseudo_tool_call(&candidate_message) {
            tool_roleplay_retries += 1;
            require_tool_call = true;
            emit_status_event(
                app,
                stream_id.as_deref(),
                "Coach printed a fake tool call instead of calling the tool; retrying with a real tool invocation",
            );
            messages.push(OpenAiChatMessage::system(
                "Your previous reply printed function-call syntax instead of making a real tool call. \
Do not print [tool(...)] or create_visual(...) in the answer. \
If you need a tool, call it through the tool interface now. If you do not need a tool, answer directly with no pseudo-call syntax.",
            ));
            if tool_roleplay_retries < 3 {
                continue;
            }
        }

        if let Some(issue) =
            describe_invalid_visual_output(&candidate_message, &retrieved_knowledge)
        {
            visual_output_retries += 1;
            emit_status_event(
                app,
                stream_id.as_deref(),
                "Coach produced invalid visual output; retrying with corrected chart formatting",
            );
            if !last_round && visual_output_retries < 3 {
                require_tool_call = true;
                messages.push(OpenAiChatMessage::system(format!(
                    "Your previous reply had invalid visual output: {}. \
If you want to include a chart, make sure the tool call succeeded and then reference the created chart inline with exactly [[visual:visual-id]]. \
Prefer the dataset-backed path: fetch a raw dataset tool, then call create_visual with datasetId, xKey, and series so the app can build the chart from that known data. \
Do not use markdown image syntax like ![...](...), do not use generic embeds like ![[...]], and do not claim a chart exists unless there is matching created visual data.",
                    issue
                )));
                continue;
            }

            ensure_visual_fallback(request, &mut retrieved_knowledge);
            final_message = build_visual_fallback_response(request, &retrieved_knowledge)
                .unwrap_or_else(|| build_knowledge_gap_response(request, &retrieved_knowledge));
            break;
        }

        final_message = candidate_message;
        break;
    }

    if final_message.is_empty() {
        ensure_visual_fallback(request, &mut retrieved_knowledge);
        final_message = build_visual_fallback_response(request, &retrieved_knowledge)
            .unwrap_or_else(|| build_knowledge_gap_response(request, &retrieved_knowledge));
    }
    let final_message = finalize_grounded_response(request, &final_message, &retrieved_knowledge);

    if question_requests_visual(&request.question) && retrieved_knowledge.visuals.is_empty() {
        ensure_visual_fallback(request, &mut retrieved_knowledge);
    }

    // Emit the answer as a stream if a stream_id was provided.
    if let Some(sid) = stream_id.as_deref() {
        emit_stream_event(
            app,
            LocalCoachStreamEvent {
                stream_id: sid.to_string(),
                kind: "chunk".to_string(),
                delta: final_message.clone(),
                content: final_message.clone(),
                done: false,
                error: None,
            },
        );
        emit_stream_event(
            app,
            LocalCoachStreamEvent {
                stream_id: sid.to_string(),
                kind: "done".to_string(),
                delta: String::new(),
                content: final_message.clone(),
                done: true,
                error: None,
            },
        );
    }

    let runtime_status = build_runtime_status(app, &state.local_llm)?;
    Ok(LocalCoachChatResponse {
        message: final_message,
        model: response_model.unwrap_or(runtime.model_id),
        runtime_status,
        knowledge_items: retrieved_knowledge.items,
        answer_plan: retrieved_knowledge.answer_plan,
        visuals: retrieved_knowledge.visuals,
    })
}

async fn stream_chat_response(
    app: &AppHandle,
    response: reqwest::Response,
    stream_id: &str,
) -> anyhow::Result<(String, Option<String>)> {
    let mut stream = response.bytes_stream();
    let mut buffered = String::new();
    let mut content = String::new();
    let mut model = None;

    while let Some(chunk) = stream.next().await {
        let chunk: bytes::Bytes = chunk.context("could not read local coach stream chunk")?;
        buffered.push_str(&String::from_utf8_lossy(&chunk));

        while let Some(line_end) = buffered.find('\n') {
            let line = buffered[..line_end].trim_end_matches('\r').to_string();
            buffered.drain(..=line_end);
            if let Some(done) = handle_stream_line(app, &line, stream_id, &mut content, &mut model)?
            {
                if done {
                    emit_stream_event(
                        app,
                        LocalCoachStreamEvent {
                            stream_id: stream_id.to_string(),
                            kind: "done".to_string(),
                            delta: String::new(),
                            content: content.clone(),
                            done: true,
                            error: None,
                        },
                    );
                    let final_content = content.trim().to_string();
                    if final_content.is_empty() {
                        return Err(anyhow!("local coach returned an empty reply"));
                    }
                    return Ok((final_content, model));
                }
            }
        }
    }

    if !buffered.trim().is_empty() {
        let _ = handle_stream_line(app, buffered.trim(), stream_id, &mut content, &mut model)?;
    }

    emit_stream_event(
        app,
        LocalCoachStreamEvent {
            stream_id: stream_id.to_string(),
            kind: "done".to_string(),
            delta: String::new(),
            content: content.clone(),
            done: true,
            error: None,
        },
    );

    let final_content = content.trim().to_string();
    if final_content.is_empty() {
        return Err(anyhow!("local coach returned an empty reply"));
    }
    Ok((final_content, model))
}

async fn send_local_coach_chat_request(
    client: &reqwest::Client,
    endpoint: &str,
    chat: &OpenAiChatRequest,
) -> anyhow::Result<reqwest::Response> {
    let response = client
        .post(format!("{}/chat/completions", endpoint))
        .json(chat)
        .send()
        .await
        .context("could not reach local coach runtime")?;

    let status = response.status();
    if status.is_success() {
        return Ok(response);
    }

    let body = response
        .text()
        .await
        .unwrap_or_else(|_| "<could not read response body>".to_string());
    let body = body.trim();
    let trimmed_body = if body.is_empty() {
        "<empty response body>".to_string()
    } else if body.len() > 1200 {
        format!("{}…", &body[..1200])
    } else {
        body.to_string()
    };

    Err(anyhow!(
        "local coach runtime returned HTTP {}: {}",
        status.as_u16(),
        trimmed_body
    ))
}

async fn send_chat_request(
    app: &AppHandle,
    client: &reqwest::Client,
    endpoint: &str,
    chat: &OpenAiChatRequest,
    stream_id: Option<&str>,
) -> anyhow::Result<(String, Option<String>)> {
    let response = send_local_coach_chat_request(client, endpoint, chat).await?;

    if let Some(stream_id) = stream_id {
        stream_chat_response(app, response, stream_id).await
    } else {
        let response = response
            .json::<OpenAiChatResponse>()
            .await
            .context("could not decode local coach response")?;
        let message = response
            .choices
            .first()
            .and_then(|choice| choice.message.content.as_deref())
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(String::from)
            .ok_or_else(|| anyhow!("local coach returned an empty reply"))?;
        Ok((message, response.model))
    }
}

fn handle_stream_line(
    app: &AppHandle,
    line: &str,
    stream_id: &str,
    content: &mut String,
    model: &mut Option<String>,
) -> anyhow::Result<Option<bool>> {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return Ok(None);
    }
    let Some(data) = trimmed.strip_prefix("data:") else {
        return Ok(None);
    };
    let data = data.trim();
    if data == "[DONE]" {
        return Ok(Some(true));
    }

    let chunk = match serde_json::from_str::<OpenAiChatStreamResponse>(data) {
        Ok(c) => c,
        Err(_) => return Ok(None), // skip unrecognised/non-JSON SSE lines
    };
    if model.is_none() {
        *model = chunk.model.clone();
    }

    let delta = chunk
        .choices
        .iter()
        .filter_map(|choice| choice.delta.content.as_deref())
        .collect::<String>();

    if !delta.is_empty() {
        content.push_str(&delta);
        emit_stream_event(
            app,
            LocalCoachStreamEvent {
                stream_id: stream_id.to_string(),
                kind: "chunk".to_string(),
                delta,
                content: content.clone(),
                done: false,
                error: None,
            },
        );
    }

    Ok(Some(false))
}

fn emit_stream_event(app: &AppHandle, payload: LocalCoachStreamEvent) {
    let _ = app.emit(LOCAL_COACH_STREAM_EVENT, payload);
}

fn emit_status_event(app: &AppHandle, stream_id: Option<&str>, message: &str) {
    let Some(stream_id) = stream_id else {
        return;
    };
    emit_stream_event(
        app,
        LocalCoachStreamEvent {
            stream_id: stream_id.to_string(),
            kind: "status".to_string(),
            delta: message.to_string(),
            content: String::new(),
            done: false,
            error: None,
        },
    );
}

async fn query_retrieved_knowledge(
    app: &AppHandle,
    request: &LocalCoachChatRequest,
    question: String,
    signal_keys: Vec<String>,
    context_tags: Vec<String>,
    limit: Option<u32>,
) -> RetrievedKnowledge {
    let knowledge_query = hub_api::HubCoachingKnowledgeQuery {
        scenario_name: request.scenario_name.clone(),
        scenario_type: request.scenario_type.clone(),
        signal_keys,
        context_tags,
        focus_area: request.focus_area.clone(),
        challenge_preference: request.challenge_preference.clone(),
        time_preference: request.time_preference.clone(),
        question,
        limit,
        coach_facts: request
            .coach_facts
            .iter()
            .map(|fact| hub_api::HubCoachFact {
                key: fact.key.clone(),
                label: fact.label.clone(),
                value_text: fact.value_text.clone(),
                numeric_value: fact.numeric_value,
                bool_value: fact.bool_value,
                direction: fact.direction.clone(),
                confidence: fact.confidence.clone(),
            })
            .collect::<Vec<_>>(),
        general: request.general,
    };

    let knowledge = hub_api::query_coaching_knowledge(app, knowledge_query)
        .await
        .unwrap_or_default();

    RetrievedKnowledge {
        tool_instruction: knowledge.tool_instruction.trim().to_string(),
        answer_plan: LocalCoachAnswerPlan {
            intent: knowledge.answer_plan.intent,
            response_shape: knowledge.answer_plan.response_shape,
            must_answer_directly: knowledge.answer_plan.must_answer_directly,
            primary_findings: knowledge.answer_plan.primary_findings,
            suggested_actions: knowledge.answer_plan.suggested_actions,
            clarifying_question: knowledge.answer_plan.clarifying_question,
        },
        items: knowledge
            .items
            .iter()
            .map(|item| LocalCoachKnowledgePreview {
                id: item.id.clone(),
                title: item.title.clone(),
                summary: item.summary.clone(),
                why: item.why.clone(),
                actions: item.actions.clone(),
                drills: item
                    .drills
                    .iter()
                    .map(|drill| LocalCoachKnowledgeDrillPreview {
                        label: drill.label.clone(),
                        query: drill.query.clone(),
                        reason: drill.reason.clone(),
                    })
                    .collect::<Vec<_>>(),
                avoid: item.avoid.clone(),
                sources: item
                    .sources
                    .iter()
                    .map(|source| LocalCoachKnowledgeSourcePreview {
                        id: source.id.clone(),
                        title: source.title.clone(),
                        author: source.author.clone(),
                        url: source.url.clone(),
                    })
                    .collect::<Vec<_>>(),
            })
            .collect::<Vec<_>>(),
        visuals: vec![],
        datasets: vec![],
    }
}

fn merge_unique_strings(base: &[String], extra: &[String]) -> Vec<String> {
    let mut merged = Vec::new();
    for value in base.iter().chain(extra.iter()) {
        let trimmed = value.trim();
        if trimmed.is_empty() {
            continue;
        }
        if !merged
            .iter()
            .any(|existing: &String| existing.eq_ignore_ascii_case(trimmed))
        {
            merged.push(trimmed.to_string());
        }
    }
    merged
}

fn merge_retrieved_knowledge(target: &mut RetrievedKnowledge, extra: RetrievedKnowledge) {
    if target.tool_instruction.trim().is_empty() && !extra.tool_instruction.trim().is_empty() {
        target.tool_instruction = extra.tool_instruction;
    }
    if target.answer_plan.intent.trim().is_empty() && !extra.answer_plan.intent.trim().is_empty() {
        target.answer_plan = extra.answer_plan.clone();
    } else {
        for finding in extra.answer_plan.primary_findings {
            if !target
                .answer_plan
                .primary_findings
                .iter()
                .any(|existing| existing.eq_ignore_ascii_case(&finding))
            {
                target.answer_plan.primary_findings.push(finding);
            }
        }
        for action in extra.answer_plan.suggested_actions {
            if !target
                .answer_plan
                .suggested_actions
                .iter()
                .any(|existing| existing.eq_ignore_ascii_case(&action))
            {
                target.answer_plan.suggested_actions.push(action);
            }
        }
        if target.answer_plan.clarifying_question.trim().is_empty()
            && !extra.answer_plan.clarifying_question.trim().is_empty()
        {
            target.answer_plan.clarifying_question = extra.answer_plan.clarifying_question;
        }
        if target.answer_plan.response_shape.trim().is_empty()
            && !extra.answer_plan.response_shape.trim().is_empty()
        {
            target.answer_plan.response_shape = extra.answer_plan.response_shape;
        }
        target.answer_plan.must_answer_directly |= extra.answer_plan.must_answer_directly;
    }
    for item in extra.items {
        if target
            .items
            .iter()
            .any(|existing| existing.id.eq_ignore_ascii_case(&item.id))
        {
            continue;
        }
        target.items.push(item);
    }
    for visual in extra.visuals {
        if target
            .visuals
            .iter()
            .any(|existing| existing.id.eq_ignore_ascii_case(&visual.id))
        {
            continue;
        }
        target.visuals.push(visual);
    }
    for dataset in extra.datasets {
        if target
            .datasets
            .iter()
            .any(|existing| existing.id.eq_ignore_ascii_case(&dataset.id))
        {
            continue;
        }
        target.datasets.push(dataset);
    }
}

fn build_tools() -> Vec<ToolDefinition> {
    vec![
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_scenario_context".to_string(),
                description: "Get the current scenario name, scenario type, local summaries, focus preferences, and current coaching cards.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {},
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_coach_facts".to_string(),
                description: "Get deterministic local coach facts such as variance, slope, plateau, warmup drop, recent averages, and practice totals.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {},
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "query_coaching_knowledge".to_string(),
                description: "Query the AimMod hub coaching knowledge base. Use this before answering if you need grounded coaching facts, scenario recommendations, transfer guidance, or setup information. You may call it multiple times with narrower follow-up lookups.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "question": {
                            "type": "string",
                            "description": "The exact coaching question or narrower follow-up lookup to run against the knowledge base."
                        },
                        "signalKeys": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "Optional extra signal keys to bias the lookup."
                        },
                        "contextTags": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "Optional extra game or context tags such as valorant, tactical_shooter, transfer, or sensitivity."
                        },
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 8,
                            "description": "Maximum number of KB entries to return."
                        }
                    },
                    "required": ["question"]
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_player_stats".to_string(),
                description: "Get deterministic performance stats for a specific scenario name, including average score, slope, variance, plateau state, and warmup tax if available.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "scenarioName": {
                            "type": "string",
                            "description": "Exact KovaaK's scenario name."
                        }
                    },
                    "required": ["scenarioName"]
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_recent_scenarios_played".to_string(),
                description: "Get the most recent scenarios the user actually played locally, with timestamps. Useful for warmup, routine, and recent-history questions.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 20,
                            "description": "How many recent scenarios to return."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_local_most_played_scenarios".to_string(),
                description: "Get the user's most-played scenarios from local session history, including play counts and average score. Useful for routine and familiarity questions.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 20,
                            "description": "How many scenarios to return."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_local_warmup_candidates".to_string(),
                description: "Get warmup candidate scenarios based on the user's own local history. Prefers scenarios with enough repetitions, steadier variance, and lighter warmup tax when available.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 10,
                            "description": "How many warmup candidates to return."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "query_local_sessions".to_string(),
                description: "Query raw local session rows with filtering and sorting already applied. Use this when you need actual session-level data instead of computing the grouping manually in the model.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "scenarioName": { "type": "string", "description": "Optional exact scenario name filter." },
                        "scenarioType": { "type": "string", "description": "Optional scenario type filter such as Tracking or DynamicClicking." },
                        "scenarioSubtype": { "type": "string", "description": "Optional scenario subtype filter." },
                        "day": { "type": "string", "description": "Optional exact day in YYYY.MM.DD format." },
                        "days": { "type": "integer", "minimum": 1, "maximum": 180, "description": "Optional trailing-day window to include." },
                        "sortBy": {
                            "type": "string",
                            "enum": ["timestamp", "score", "accuracyPct", "avgKps", "avgTtkMs", "smoothnessComposite", "correctionRatio", "avgFireToHitMs"],
                            "description": "How to sort the returned session rows."
                        },
                        "sortOrder": {
                            "type": "string",
                            "enum": ["asc", "desc"],
                            "description": "Sort direction."
                        },
                        "limit": { "type": "integer", "minimum": 1, "maximum": 60, "description": "Maximum rows to return." }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "query_local_grouped_history".to_string(),
                description: "Query grouped local history with filtering, aggregation, ranking, and sorting already done by the app. Use this when you want grouped answers like top scenario types, daily patterns, or scenario leaderboards without manually aggregating rows in the model.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "groupBy": {
                            "type": "string",
                            "enum": ["day", "scenarioName", "scenarioType", "scenarioSubtype"],
                            "description": "Which dimension to group by."
                        },
                        "scenarioName": { "type": "string", "description": "Optional exact scenario name filter." },
                        "scenarioType": { "type": "string", "description": "Optional scenario type filter." },
                        "scenarioSubtype": { "type": "string", "description": "Optional scenario subtype filter." },
                        "day": { "type": "string", "description": "Optional exact day in YYYY.MM.DD format." },
                        "days": { "type": "integer", "minimum": 1, "maximum": 180, "description": "Optional trailing-day window to include." },
                        "metrics": {
                            "type": "array",
                            "description": "Optional metrics to emphasize in the grouped result and recommended chart.",
                            "items": {
                                "type": "string",
                                "enum": [
                                    "sessionCount", "uniqueScenarioCount", "avgScore", "bestScore",
                                    "avgAccuracyPct", "bestAccuracyPct", "avgKps", "avgTtkMs",
                                    "avgAccuracyTrend", "avgSmoothnessComposite", "avgJitter",
                                    "avgCorrectionRatio", "avgDirectionalBias", "avgShotsToHit",
                                    "avgFireToHitMs"
                                ]
                            }
                        },
                        "sortBy": {
                            "type": "string",
                            "description": "Optional sort field. Can be the group key or one of the supported metrics."
                        },
                        "sortOrder": {
                            "type": "string",
                            "enum": ["asc", "desc"],
                            "description": "Sort direction."
                        },
                        "limit": { "type": "integer", "minimum": 1, "maximum": 50, "description": "Maximum grouped rows to return." }
                    },
                    "required": ["groupBy"]
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_scenario_runs".to_string(),
                description: "Get raw local run-by-run data for a scenario. This also returns a datasetId you can pass to create_visual so the app can build a validated chart from fetched data.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "scenarioName": {
                            "type": "string",
                            "description": "Scenario name. Leave empty to use the current scenario from the UI."
                        },
                        "limit": {
                            "type": "integer",
                            "minimum": 5,
                            "maximum": 60,
                            "description": "How many recent runs to return."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_local_scenario_aggregates".to_string(),
                description: "Get raw per-scenario aggregate data from local history, including play count, average score, best score, and average accuracy. This also returns a datasetId for validated comparison charts.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 30,
                            "description": "How many scenario aggregates to return."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_local_scenario_type_aggregates".to_string(),
                description: "Get raw aggregates grouped by scenario type from local history, including plays, average score, average accuracy, and unique scenario count. This also returns a datasetId for validated charts.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 20,
                            "description": "How many scenario types to return."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_local_activity_timeline".to_string(),
                description: "Get raw daily practice timeline data from local history, including session count, average score, average accuracy, and unique scenarios per day. This also returns a datasetId for validated time-series charts.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "days": {
                            "type": "integer",
                            "minimum": 7,
                            "maximum": 120,
                            "description": "How many recent days to include."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "get_local_day_scenario_breakdown".to_string(),
                description: "Get a per-scenario breakdown for one local practice day. If day is omitted, the tool picks the strongest day using the chosen ranking metric. Returns a datasetId and a recommendedCreateVisual payload for charting scenario performance inside that day.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "day": {
                            "type": "string",
                            "description": "Optional day in YYYY.MM.DD format. If omitted, the tool picks the best day from local history."
                        },
                        "rankingMetric": {
                            "type": "string",
                            "enum": ["avgScore", "avgAccuracyPct", "sessionCount"],
                            "description": "How to choose the strongest day when day is omitted."
                        },
                        "limit": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 20,
                            "description": "How many scenario rows to include for the chosen day."
                        }
                    },
                    "required": []
                }),
            },
        },
        ToolDefinition {
            kind: "function".to_string(),
            function: ToolFunctionDef {
                name: "create_visual".to_string(),
                description: "Create a visual from data you already fetched. Prefer using datasetId plus xKey and series so the app builds validated points from a fetched dataset instead of relying on handcrafted point arrays. If the dataset tool returned recommendedCreateVisual, reuse that shape instead of inventing your own. Supported chart kinds are line, bar, and combo. After creating one, reference it inline in your answer with [[visual:visual-id]] exactly where it should appear.".to_string(),
                parameters: json!({
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "string",
                            "description": "Short stable visual id such as current-scenario-accuracy."
                        },
                        "datasetId": {
                            "type": "string",
                            "description": "Preferred. A datasetId returned by a raw data tool such as get_local_activity_timeline, get_scenario_runs, get_local_scenario_aggregates, or get_local_scenario_type_aggregates."
                        },
                        "xKey": {
                            "type": "string",
                            "description": "When using datasetId, which field from that dataset should become the x-axis labels, such as day, timestamp, index, scenarioName, or scenarioType."
                        },
                        "kind": {
                            "type": "string",
                            "enum": ["line", "bar", "combo"],
                            "description": "Use line for trends over ordered runs/time, bar for comparisons, and combo when you want mixed line+bar series in one chart."
                        },
                        "title": {
                            "type": "string",
                            "description": "Chart title shown to the user."
                        },
                        "subtitle": {
                            "type": "string",
                            "description": "Short chart subtitle."
                        },
                        "primaryLabel": {
                            "type": "string",
                            "description": "Primary metric label, such as Accuracy % or Score."
                        },
                        "secondaryLabel": {
                            "type": "string",
                            "description": "Optional secondary metric label for a second line."
                        },
                        "series": {
                            "type": "array",
                            "description": "Optional richer series definitions. When using datasetId, key must match a numeric field in that dataset, such as sessionCount, uniqueScenarioCount, avgScore, avgAccuracyPct, plays, or bestScore. kind can be line or bar. color is optional but recommended.",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "key": { "type": "string" },
                                    "label": { "type": "string" },
                                    "kind": { "type": "string", "enum": ["line", "bar"] },
                                    "color": { "type": "string" }
                                },
                                "required": ["key", "label"]
                            }
                        },
                        "points": {
                            "type": "array",
                            "description": "Not recommended for local AimMod data. Prefer datasetId plus xKey and series so charts are built from fetched data instead of hand-authored points.",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "label": { "type": "string" },
                                    "value": { "type": "number" },
                                    "secondaryValue": { "type": "number" },
                                    "note": { "type": "string" },
                                    "values": {
                                        "type": "object",
                                        "additionalProperties": { "type": "number" }
                                    }
                                },
                                "required": ["label"]
                            }
                        },
                        "detailLines": {
                            "type": "array",
                            "description": "Optional explanatory bullet lines rendered under the chart.",
                            "items": { "type": "string" }
                        }
                    },
                    "required": ["kind", "title"]
                }),
            },
        },
    ]
}

async fn execute_tool_call(
    app: &AppHandle,
    request: &LocalCoachChatRequest,
    retrieved_knowledge: &mut RetrievedKnowledge,
    stream_id: Option<&str>,
    tool_name: &str,
    args: &JsonValue,
) -> String {
    match tool_name {
        "get_scenario_context" => {
            emit_status_event(app, stream_id, "Reading local scenario context");
            build_scenario_context_result(request)
        }
        "get_coach_facts" => {
            emit_status_event(app, stream_id, "Reading local coach facts");
            build_coach_facts_result(request)
        }
        "query_coaching_knowledge" => {
            let question = args
                .get("question")
                .and_then(|value| value.as_str())
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .unwrap_or(request.question.trim())
                .to_string();
            let signal_keys = merge_unique_strings(
                &request.signal_keys,
                &json_array_strings(args.get("signalKeys")),
            );
            let context_tags = merge_unique_strings(
                &request.context_tags,
                &json_array_strings(args.get("contextTags")),
            );
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 8) as u32)
                .or(Some(6));

            emit_status_event(app, stream_id, "Planning knowledge lookup");
            emit_status_event(
                app,
                stream_id,
                &format!("Querying knowledge base: {}", question.trim()),
            );

            let before = retrieved_knowledge.items.len();
            let effective_question = {
                let original = request.question.trim();
                let tool_question = question.trim();
                if tool_question.is_empty() || tool_question.eq_ignore_ascii_case(original) {
                    original.to_string()
                } else if original.is_empty() {
                    tool_question.to_string()
                } else {
                    format!("{original} {tool_question}")
                }
            };
            let extra = query_retrieved_knowledge(
                app,
                request,
                effective_question,
                signal_keys,
                context_tags,
                limit,
            )
            .await;
            merge_retrieved_knowledge(retrieved_knowledge, extra);
            let new_count = retrieved_knowledge.items.len().saturating_sub(before);

            emit_status_event(
                app,
                stream_id,
                &format!(
                    "Knowledge base returned {} new entr{}",
                    new_count,
                    if new_count == 1 { "y" } else { "ies" }
                ),
            );

            build_kb_tool_result(retrieved_knowledge, new_count)
        }
        "get_player_stats" => {
            let scenario = args
                .get("scenarioName")
                .and_then(|value| value.as_str())
                .map(str::trim)
                .unwrap_or_default()
                .to_string();
            if scenario.is_empty() {
                return json!({
                    "ok": false,
                    "error": "scenarioName is required"
                })
                .to_string();
            }
            emit_status_event(
                app,
                stream_id,
                &format!("Reading player stats for {}", scenario),
            );
            match crate::coaching::get_scenario_overview(app, &scenario, None) {
                Ok(overview) => build_player_stats_result(&overview, &scenario),
                Err(err) => json!({
                    "ok": false,
                    "error": format!("Could not load stats for {scenario}: {err}")
                })
                .to_string(),
            }
        }
        "get_recent_scenarios_played" => {
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 20) as usize)
                .unwrap_or(8);
            emit_status_event(app, stream_id, "Reading recent scenarios");
            build_recent_scenarios_result(app, limit)
        }
        "get_local_most_played_scenarios" => {
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 20) as usize)
                .unwrap_or(8);
            emit_status_event(app, stream_id, "Reading most-played scenarios");
            build_local_most_played_result(app, limit)
        }
        "get_local_warmup_candidates" => {
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 10) as usize)
                .unwrap_or(5);
            emit_status_event(app, stream_id, "Evaluating warmup candidates");
            build_local_warmup_candidates_result(app, limit)
        }
        "query_local_sessions" => {
            emit_status_event(app, stream_id, "Querying local session rows");
            query_local_sessions_result(app, args, retrieved_knowledge)
        }
        "query_local_grouped_history" => {
            emit_status_event(app, stream_id, "Querying grouped local history");
            query_local_grouped_history_result(app, args, retrieved_knowledge)
        }
        "get_scenario_runs" => {
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(5, 60) as usize)
                .unwrap_or(15);
            let scenario = args
                .get("scenarioName")
                .and_then(|value| value.as_str())
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .unwrap_or(request.scenario_name.trim());
            emit_status_event(app, stream_id, "Reading scenario run data");
            build_scenario_runs_result(app, request, scenario, limit, retrieved_knowledge)
        }
        "get_local_scenario_aggregates" => {
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 30) as usize)
                .unwrap_or(12);
            emit_status_event(app, stream_id, "Reading scenario aggregate data");
            build_local_scenario_aggregates_result(app, limit, retrieved_knowledge)
        }
        "get_local_scenario_type_aggregates" => {
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 20) as usize)
                .unwrap_or(8);
            emit_status_event(app, stream_id, "Reading scenario-type aggregate data");
            build_local_scenario_type_aggregates_result(app, limit, retrieved_knowledge)
        }
        "get_local_activity_timeline" => {
            let days = args
                .get("days")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(7, 120) as usize)
                .unwrap_or(30);
            emit_status_event(app, stream_id, "Reading local activity timeline");
            build_local_activity_timeline_result(app, days, retrieved_knowledge)
        }
        "get_local_day_scenario_breakdown" => {
            let day = args
                .get("day")
                .and_then(|value| value.as_str())
                .map(str::trim)
                .unwrap_or_default()
                .to_string();
            let ranking_metric = args
                .get("rankingMetric")
                .and_then(|value| value.as_str())
                .map(str::trim)
                .filter(|value| matches!(*value, "avgScore" | "avgAccuracyPct" | "sessionCount"))
                .unwrap_or("avgScore");
            let limit = args
                .get("limit")
                .and_then(|value| value.as_u64())
                .map(|value| value.clamp(1, 20) as usize)
                .unwrap_or(12);
            emit_status_event(app, stream_id, "Reading day scenario breakdown");
            build_local_day_scenario_breakdown_result(
                app,
                if day.is_empty() {
                    None
                } else {
                    Some(day.as_str())
                },
                ranking_metric,
                limit,
                retrieved_knowledge,
            )
        }
        "create_visual" => {
            emit_status_event(app, stream_id, "Creating visual");
            create_visual_result(request, args, retrieved_knowledge)
        }
        _ => json!({
            "ok": false,
            "error": format!("unknown tool: {tool_name}")
        })
        .to_string(),
    }
}

fn json_array_strings(value: Option<&JsonValue>) -> Vec<String> {
    value
        .and_then(|candidate| candidate.as_array())
        .map(|values| {
            values
                .iter()
                .filter_map(|value| value.as_str())
                .map(|value| value.trim().to_string())
                .filter(|value| !value.is_empty())
                .collect::<Vec<_>>()
        })
        .unwrap_or_default()
}

fn scenario_type_of(record: &crate::session_store::SessionRecord) -> String {
    record
        .stats_panel
        .as_ref()
        .map(|panel| panel.scenario_type.trim().to_string())
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| "Unknown".to_string())
}

fn scenario_subtype_of(record: &crate::session_store::SessionRecord) -> String {
    record
        .stats_panel
        .as_ref()
        .and_then(|panel| panel.scenario_subtype.as_ref())
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| "Unknown".to_string())
}

fn extract_session_metric(
    record: &crate::session_store::SessionRecord,
    metric: &str,
) -> Option<f64> {
    match metric {
        "score" => Some(record.score),
        "accuracyPct" => scenario_accuracy_pct(record),
        "avgKps" => record
            .stats_panel
            .as_ref()
            .and_then(|panel| panel.avg_kps.map(|value| value as f64)),
        "avgTtkMs" => record
            .stats_panel
            .as_ref()
            .and_then(|panel| panel.avg_ttk_ms.map(|value| value as f64)),
        "bestTtkMs" => record
            .stats_panel
            .as_ref()
            .and_then(|panel| panel.best_ttk_ms.map(|value| value as f64)),
        "accuracyTrend" => record
            .stats_panel
            .as_ref()
            .and_then(|panel| panel.accuracy_trend.map(|value| value as f64)),
        "smoothnessComposite" => record
            .smoothness
            .as_ref()
            .map(|snapshot| snapshot.composite as f64),
        "jitter" => record
            .smoothness
            .as_ref()
            .map(|snapshot| snapshot.jitter as f64),
        "correctionRatio" => record
            .smoothness
            .as_ref()
            .map(|snapshot| snapshot.correction_ratio as f64),
        "directionalBias" => record
            .smoothness
            .as_ref()
            .map(|snapshot| snapshot.directional_bias as f64),
        "avgFireToHitMs" => record
            .shot_timing
            .as_ref()
            .and_then(|shot| shot.avg_fire_to_hit_ms.map(|value| value as f64)),
        "avgShotsToHit" => record
            .shot_timing
            .as_ref()
            .and_then(|shot| shot.avg_shots_to_hit.map(|value| value as f64)),
        _ => None,
    }
}

fn collect_allowed_days(
    records: &[crate::session_store::SessionRecord],
    trailing_days: Option<usize>,
) -> Option<std::collections::HashSet<String>> {
    let Some(trailing_days) = trailing_days else {
        return None;
    };
    let mut unique_days = records
        .iter()
        .map(|record| session_day_key(&record.timestamp))
        .filter(|day| !day.is_empty())
        .collect::<Vec<_>>();
    unique_days.sort();
    unique_days.dedup();
    let keep = unique_days
        .into_iter()
        .rev()
        .take(trailing_days)
        .collect::<std::collections::HashSet<_>>();
    Some(keep)
}

fn filter_local_records(
    app: &AppHandle,
    scenario_name: Option<&str>,
    scenario_type: Option<&str>,
    scenario_subtype: Option<&str>,
    day: Option<&str>,
    trailing_days: Option<usize>,
) -> Vec<crate::session_store::SessionRecord> {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return vec![];
    }

    let allowed_days = collect_allowed_days(&page.records, trailing_days);
    let scenario_name = scenario_name
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let scenario_type = scenario_type
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let scenario_subtype = scenario_subtype
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let day = day.map(str::trim).filter(|value| !value.is_empty());

    page.records
        .into_iter()
        .filter(|record| {
            if let Some(value) = scenario_name {
                if !record.scenario.trim().eq_ignore_ascii_case(value) {
                    return false;
                }
            }
            if let Some(value) = scenario_type {
                if !scenario_type_of(record).eq_ignore_ascii_case(value) {
                    return false;
                }
            }
            if let Some(value) = scenario_subtype {
                if !scenario_subtype_of(record).eq_ignore_ascii_case(value) {
                    return false;
                }
            }
            let record_day = session_day_key(&record.timestamp);
            if let Some(value) = day {
                if !record_day.eq_ignore_ascii_case(value) {
                    return false;
                }
            }
            if let Some(allowed) = &allowed_days {
                if !allowed.contains(&record_day) {
                    return false;
                }
            }
            true
        })
        .collect::<Vec<_>>()
}

fn sort_rows_by_key(rows: &mut [JsonValue], sort_by: &str, descending: bool) {
    rows.sort_by(|left, right| {
        let left_value = left.get(sort_by);
        let right_value = right.get(sort_by);
        let ordering = match (
            left_value.and_then(json_value_to_f64),
            right_value.and_then(json_value_to_f64),
        ) {
            (Some(left_num), Some(right_num)) => left_num
                .partial_cmp(&right_num)
                .unwrap_or(std::cmp::Ordering::Equal),
            _ => {
                let left_text = left_value
                    .and_then(json_value_to_chart_label)
                    .unwrap_or_default();
                let right_text = right_value
                    .and_then(json_value_to_chart_label)
                    .unwrap_or_default();
                left_text.cmp(&right_text)
            }
        };
        if descending {
            ordering.reverse()
        } else {
            ordering
        }
    });
}

fn query_local_sessions_result(
    app: &AppHandle,
    args: &JsonValue,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let scenario_name = args.get("scenarioName").and_then(|value| value.as_str());
    let scenario_type = args.get("scenarioType").and_then(|value| value.as_str());
    let scenario_subtype = args.get("scenarioSubtype").and_then(|value| value.as_str());
    let day = args.get("day").and_then(|value| value.as_str());
    let days = args
        .get("days")
        .and_then(|value| value.as_u64())
        .map(|value| value.clamp(1, 180) as usize);
    let sort_by = args
        .get("sortBy")
        .and_then(|value| value.as_str())
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .unwrap_or("timestamp");
    let descending = args
        .get("sortOrder")
        .and_then(|value| value.as_str())
        .map(|value| !value.eq_ignore_ascii_case("asc"))
        .unwrap_or(true);
    let limit = args
        .get("limit")
        .and_then(|value| value.as_u64())
        .map(|value| value.clamp(1, 60) as usize)
        .unwrap_or(20);

    let records = filter_local_records(
        app,
        scenario_name,
        scenario_type,
        scenario_subtype,
        day,
        days,
    );
    if records.is_empty() {
        return json!({
            "ok": false,
            "error": "No local sessions matched that filter"
        })
        .to_string();
    }

    let mut rows = records
        .into_iter()
        .map(|record| {
            json!({
                "sessionId": record.id,
                "scenarioName": record.scenario,
                "scenarioType": scenario_type_of(&record),
                "scenarioSubtype": scenario_subtype_of(&record),
                "timestamp": record.timestamp,
                "day": session_day_key(&record.timestamp),
                "score": record.score,
                "accuracyPct": scenario_accuracy_pct(&record),
                "kills": record.kills,
                "durationSecs": record.duration_secs,
                "avgKps": record.stats_panel.as_ref().and_then(|panel| panel.avg_kps.map(|value| value as f64)),
                "avgTtkMs": record.stats_panel.as_ref().and_then(|panel| panel.avg_ttk_ms.map(|value| value as f64)),
                "bestTtkMs": record.stats_panel.as_ref().and_then(|panel| panel.best_ttk_ms.map(|value| value as f64)),
                "accuracyTrend": record.stats_panel.as_ref().and_then(|panel| panel.accuracy_trend.map(|value| value as f64)),
                "smoothnessComposite": record.smoothness.as_ref().map(|snapshot| snapshot.composite as f64),
                "jitter": record.smoothness.as_ref().map(|snapshot| snapshot.jitter as f64),
                "correctionRatio": record.smoothness.as_ref().map(|snapshot| snapshot.correction_ratio as f64),
                "directionalBias": record.smoothness.as_ref().map(|snapshot| snapshot.directional_bias as f64),
                "avgFireToHitMs": record.shot_timing.as_ref().and_then(|shot| shot.avg_fire_to_hit_ms.map(|value| value as f64)),
                "avgShotsToHit": record.shot_timing.as_ref().and_then(|shot| shot.avg_shots_to_hit.map(|value| value as f64)),
            })
        })
        .collect::<Vec<_>>();

    sort_rows_by_key(&mut rows, sort_by, descending);
    rows.truncate(limit);

    let dataset_id = normalize_visual_id(
        &format!("local-sessions-{}-{}", sort_by, limit),
        "local sessions",
    );
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: "local_sessions".to_string(),
            default_x_key: if sort_by.eq_ignore_ascii_case("timestamp") {
                "timestamp".to_string()
            } else {
                "scenarioName".to_string()
            },
            rows: rows.clone(),
            available_metrics: vec![
                "score".to_string(),
                "accuracyPct".to_string(),
                "kills".to_string(),
                "durationSecs".to_string(),
                "avgKps".to_string(),
                "avgTtkMs".to_string(),
                "bestTtkMs".to_string(),
                "accuracyTrend".to_string(),
                "smoothnessComposite".to_string(),
                "jitter".to_string(),
                "correctionRatio".to_string(),
                "directionalBias".to_string(),
                "avgFireToHitMs".to_string(),
                "avgShotsToHit".to_string(),
            ],
            available_dimensions: vec![
                "timestamp".to_string(),
                "day".to_string(),
                "scenarioName".to_string(),
                "scenarioType".to_string(),
                "scenarioSubtype".to_string(),
            ],
        },
    );

    json!({
        "ok": true,
        "datasetId": dataset_id,
        "defaultXKey": if sort_by.eq_ignore_ascii_case("timestamp") { "timestamp" } else { "scenarioName" },
        "availableDimensions": ["timestamp", "day", "scenarioName", "scenarioType", "scenarioSubtype"],
        "availableMetrics": [
            "score", "accuracyPct", "kills", "durationSecs", "avgKps", "avgTtkMs", "bestTtkMs",
            "accuracyTrend", "smoothnessComposite", "jitter", "correctionRatio",
            "directionalBias", "avgFireToHitMs", "avgShotsToHit"
        ],
        "suggestedSeries": ["score", "accuracyPct", "smoothnessComposite", "avgKps"],
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            if sort_by.eq_ignore_ascii_case("timestamp") { "timestamp" } else { "scenarioName" },
            if sort_by.eq_ignore_ascii_case("timestamp") { "line" } else { "combo" },
            "local-session-query",
            "Local session query",
            "Filtered and sorted local session rows.",
            "Score",
            "Accuracy %",
            &[("score", "Score", "line"), ("accuracyPct", "Accuracy %", "line")],
            &["This chart is built from already filtered session rows so the model does not need to aggregate manually."],
        ),
        "rows": rows,
    }).to_string()
}

fn grouped_metric_list(
    args: &JsonValue,
    available_metrics: &[&str],
    default_metrics: &[&str],
) -> Vec<String> {
    let requested = json_array_strings(args.get("metrics"));
    let mut selected = requested
        .into_iter()
        .filter(|metric| {
            available_metrics
                .iter()
                .any(|available| available.eq_ignore_ascii_case(metric))
        })
        .collect::<Vec<_>>();
    if selected.is_empty() {
        selected = default_metrics
            .iter()
            .map(|value| (*value).to_string())
            .collect::<Vec<_>>();
    }
    selected.truncate(4);
    selected
}

fn query_local_grouped_history_result(
    app: &AppHandle,
    args: &JsonValue,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let group_by = args
        .get("groupBy")
        .and_then(|value| value.as_str())
        .map(str::trim)
        .unwrap_or_default();
    if !matches!(
        group_by,
        "day" | "scenarioName" | "scenarioType" | "scenarioSubtype"
    ) {
        return json!({
            "ok": false,
            "error": "groupBy must be one of day, scenarioName, scenarioType, or scenarioSubtype"
        })
        .to_string();
    }

    let scenario_name = args.get("scenarioName").and_then(|value| value.as_str());
    let scenario_type = args.get("scenarioType").and_then(|value| value.as_str());
    let scenario_subtype = args.get("scenarioSubtype").and_then(|value| value.as_str());
    let day = args.get("day").and_then(|value| value.as_str());
    let days = args
        .get("days")
        .and_then(|value| value.as_u64())
        .map(|value| value.clamp(1, 180) as usize);
    let sort_by = args
        .get("sortBy")
        .and_then(|value| value.as_str())
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| {
            if group_by == "day" {
                "day"
            } else {
                "sessionCount"
            }
        });
    let descending = args
        .get("sortOrder")
        .and_then(|value| value.as_str())
        .map(|value| !value.eq_ignore_ascii_case("asc"))
        .unwrap_or(group_by != "day");
    let limit = args
        .get("limit")
        .and_then(|value| value.as_u64())
        .map(|value| value.clamp(1, 50) as usize)
        .unwrap_or(12);

    let records = filter_local_records(
        app,
        scenario_name,
        scenario_type,
        scenario_subtype,
        day,
        days,
    );
    if records.is_empty() {
        return json!({
            "ok": false,
            "error": "No local sessions matched that grouped query"
        })
        .to_string();
    }

    #[derive(Default)]
    struct GroupAggregate {
        session_count: usize,
        score_sum: f64,
        best_score: Option<f64>,
        accuracy_sum: f64,
        accuracy_count: usize,
        best_accuracy: Option<f64>,
        kps_sum: f64,
        kps_count: usize,
        ttk_sum: f64,
        ttk_count: usize,
        accuracy_trend_sum: f64,
        accuracy_trend_count: usize,
        smoothness_sum: f64,
        smoothness_count: usize,
        jitter_sum: f64,
        jitter_count: usize,
        correction_sum: f64,
        correction_count: usize,
        bias_sum: f64,
        bias_count: usize,
        shots_to_hit_sum: f64,
        shots_to_hit_count: usize,
        fire_to_hit_sum: f64,
        fire_to_hit_count: usize,
        unique_scenarios: std::collections::HashSet<String>,
    }

    let mut grouped: std::collections::HashMap<String, GroupAggregate> =
        std::collections::HashMap::new();
    for record in records {
        let key = match group_by {
            "day" => session_day_key(&record.timestamp),
            "scenarioName" => record.scenario.trim().to_string(),
            "scenarioType" => scenario_type_of(&record),
            "scenarioSubtype" => scenario_subtype_of(&record),
            _ => String::new(),
        };
        if key.is_empty() {
            continue;
        }
        let entry = grouped.entry(key).or_default();
        entry.session_count += 1;
        entry.score_sum += record.score;
        entry.best_score = Some(
            entry
                .best_score
                .map(|existing| existing.max(record.score))
                .unwrap_or(record.score),
        );
        if let Some(value) = scenario_accuracy_pct(&record) {
            entry.accuracy_sum += value;
            entry.accuracy_count += 1;
            entry.best_accuracy = Some(
                entry
                    .best_accuracy
                    .map(|existing| existing.max(value))
                    .unwrap_or(value),
            );
        }
        if let Some(value) = extract_session_metric(&record, "avgKps") {
            entry.kps_sum += value;
            entry.kps_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "avgTtkMs") {
            entry.ttk_sum += value;
            entry.ttk_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "accuracyTrend") {
            entry.accuracy_trend_sum += value;
            entry.accuracy_trend_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "smoothnessComposite") {
            entry.smoothness_sum += value;
            entry.smoothness_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "jitter") {
            entry.jitter_sum += value;
            entry.jitter_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "correctionRatio") {
            entry.correction_sum += value;
            entry.correction_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "directionalBias") {
            entry.bias_sum += value;
            entry.bias_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "avgShotsToHit") {
            entry.shots_to_hit_sum += value;
            entry.shots_to_hit_count += 1;
        }
        if let Some(value) = extract_session_metric(&record, "avgFireToHitMs") {
            entry.fire_to_hit_sum += value;
            entry.fire_to_hit_count += 1;
        }
        entry
            .unique_scenarios
            .insert(record.scenario.trim().to_string());
    }

    let available_metrics = [
        "sessionCount",
        "uniqueScenarioCount",
        "avgScore",
        "bestScore",
        "avgAccuracyPct",
        "bestAccuracyPct",
        "avgKps",
        "avgTtkMs",
        "avgAccuracyTrend",
        "avgSmoothnessComposite",
        "avgJitter",
        "avgCorrectionRatio",
        "avgDirectionalBias",
        "avgShotsToHit",
        "avgFireToHitMs",
    ];
    let selected_metrics = grouped_metric_list(
        args,
        &available_metrics,
        &["sessionCount", "avgScore", "avgAccuracyPct"],
    );

    let mut rows = grouped
        .into_iter()
        .map(|(group, aggregate)| {
            let mut row = json!({
                "sessionCount": aggregate.session_count,
                "uniqueScenarioCount": aggregate.unique_scenarios.len(),
                "avgScore": if aggregate.session_count > 0 { Some(aggregate.score_sum / aggregate.session_count as f64) } else { None::<f64> },
                "bestScore": aggregate.best_score,
                "avgAccuracyPct": if aggregate.accuracy_count > 0 { Some(aggregate.accuracy_sum / aggregate.accuracy_count as f64) } else { None::<f64> },
                "bestAccuracyPct": aggregate.best_accuracy,
                "avgKps": if aggregate.kps_count > 0 { Some(aggregate.kps_sum / aggregate.kps_count as f64) } else { None::<f64> },
                "avgTtkMs": if aggregate.ttk_count > 0 { Some(aggregate.ttk_sum / aggregate.ttk_count as f64) } else { None::<f64> },
                "avgAccuracyTrend": if aggregate.accuracy_trend_count > 0 { Some(aggregate.accuracy_trend_sum / aggregate.accuracy_trend_count as f64) } else { None::<f64> },
                "avgSmoothnessComposite": if aggregate.smoothness_count > 0 { Some(aggregate.smoothness_sum / aggregate.smoothness_count as f64) } else { None::<f64> },
                "avgJitter": if aggregate.jitter_count > 0 { Some(aggregate.jitter_sum / aggregate.jitter_count as f64) } else { None::<f64> },
                "avgCorrectionRatio": if aggregate.correction_count > 0 { Some(aggregate.correction_sum / aggregate.correction_count as f64) } else { None::<f64> },
                "avgDirectionalBias": if aggregate.bias_count > 0 { Some(aggregate.bias_sum / aggregate.bias_count as f64) } else { None::<f64> },
                "avgShotsToHit": if aggregate.shots_to_hit_count > 0 { Some(aggregate.shots_to_hit_sum / aggregate.shots_to_hit_count as f64) } else { None::<f64> },
                "avgFireToHitMs": if aggregate.fire_to_hit_count > 0 { Some(aggregate.fire_to_hit_sum / aggregate.fire_to_hit_count as f64) } else { None::<f64> },
            });
            if let Some(object) = row.as_object_mut() {
                object.insert(group_by.to_string(), JsonValue::String(group));
            }
            row
        })
        .collect::<Vec<_>>();

    sort_rows_by_key(&mut rows, sort_by, descending);
    rows.truncate(limit);

    let dataset_id = normalize_visual_id(
        &format!("grouped-{}-{}", group_by, limit),
        &format!("grouped {}", group_by),
    );
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: format!("local_grouped_history_{}", group_by),
            default_x_key: group_by.to_string(),
            rows: rows.clone(),
            available_metrics: available_metrics
                .iter()
                .map(|value| (*value).to_string())
                .collect::<Vec<_>>(),
            available_dimensions: vec![group_by.to_string()],
        },
    );

    let series = selected_metrics
        .iter()
        .map(|metric| {
            let count_like = metric.ends_with("Count");
            (
                metric.as_str(),
                humanize_metric_label(metric),
                if group_by == "day" && count_like {
                    "bar"
                } else if count_like {
                    "bar"
                } else {
                    "line"
                },
            )
        })
        .collect::<Vec<_>>();
    let series_refs = series
        .iter()
        .map(|(key, label, kind)| (*key, label.as_str(), *kind))
        .collect::<Vec<_>>();

    json!({
        "ok": true,
        "datasetId": dataset_id,
        "defaultXKey": group_by,
        "availableDimensions": [group_by],
        "availableMetrics": available_metrics,
        "selectedMetrics": selected_metrics,
        "suggestedSeries": selected_metrics,
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            group_by,
            if group_by == "day" && series_refs.len() > 1 { "combo" } else if series_refs.iter().any(|(_, _, kind)| *kind == "bar") && series_refs.iter().any(|(_, _, kind)| *kind == "line") { "combo" } else if series_refs.iter().all(|(_, _, kind)| *kind == "bar") { "bar" } else { "line" },
            &format!("grouped-{}", group_by),
            &format!("Grouped by {}", group_by),
            "Filtered local history grouped by the requested dimension.",
            &humanize_metric_label(&selected_metrics[0]),
            selected_metrics.get(1).map(|metric| humanize_metric_label(metric)).unwrap_or_default().as_str(),
            &series_refs,
            &["The app already grouped and sorted this history, so reuse this dataset directly for charts or rankings."],
        ),
        "rows": rows,
    }).to_string()
}

fn recommended_create_visual_payload(
    dataset_id: &str,
    x_key: &str,
    kind: &str,
    id: &str,
    title: &str,
    subtitle: &str,
    primary_label: &str,
    secondary_label: &str,
    series: &[(&str, &str, &str)],
    detail_lines: &[&str],
) -> JsonValue {
    json!({
        "datasetId": dataset_id,
        "xKey": x_key,
        "kind": kind,
        "id": id,
        "title": title,
        "subtitle": subtitle,
        "primaryLabel": primary_label,
        "secondaryLabel": secondary_label,
        "series": series.iter().map(|(key, label, kind)| {
            json!({
                "key": key,
                "label": label,
                "kind": kind,
            })
        }).collect::<Vec<_>>(),
        "detailLines": detail_lines,
    })
}

fn build_player_stats_result(
    overview: &crate::coaching::ScenarioCoachingOverview,
    scenario: &str,
) -> String {
    let mut lines = vec![format!("Tool result: player stats for \"{}\".", scenario)];
    lines.push(format!("Scenario type: {}", overview.scenario_type));
    if let Some(avg) = overview.avg_score {
        lines.push(format!("Average score: {:.0} pts", avg));
    }
    if let Some(slope) = overview.slope_pts_per_run {
        lines.push(format!(
            "Learning slope: {}{:.1} pts/run",
            if slope >= 0.0 { "+" } else { "" },
            slope
        ));
    }
    if let Some(cv) = overview.score_cv_pct {
        lines.push(format!("Score consistency spread: {:.1}%", cv));
    }
    if overview.is_plateau {
        lines.push("Plateau detected: yes".to_string());
    }
    if let (Some(p10), Some(p50), Some(p90)) =
        (overview.p10_score, overview.p50_score, overview.p90_score)
    {
        lines.push(format!(
            "Score range: p10={:.0}  p50={:.0}  p90={:.0}",
            p10, p50, p90
        ));
    }
    if let Some(warmup) = &overview.warmup_stats {
        lines.push(format!(
            "Warm-up tax: opening runs land ~{:.0}% below settled-in runs.",
            warmup.drop_pct
        ));
    }
    lines.join("\n")
}

fn build_recent_scenarios_result(app: &AppHandle, limit: usize) -> String {
    let recent = crate::session_store::get_recent_scenarios(app, limit);
    if recent.is_empty() {
        return "Tool result: no recent scenarios found in local session history.".to_string();
    }

    let mut lines = vec!["Tool result: recent scenarios played locally.".to_string()];
    for entry in recent {
        lines.push(format!(
            "- {} ({})",
            entry.scenario.trim(),
            entry.timestamp.trim()
        ));
    }
    lines.join("\n")
}

fn build_local_most_played_result(app: &AppHandle, limit: usize) -> String {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return "Tool result: no local session history found.".to_string();
    }

    #[derive(Default)]
    struct ScenarioAggregate {
        plays: usize,
        score_sum: f64,
        best_score: f64,
        last_timestamp: String,
    }

    let mut by_scenario: std::collections::HashMap<String, ScenarioAggregate> =
        std::collections::HashMap::new();
    for record in page.records {
        let key = record.scenario.trim().to_string();
        if key.is_empty() {
            continue;
        }
        let entry = by_scenario.entry(key).or_default();
        entry.plays += 1;
        entry.score_sum += record.score;
        if entry.plays == 1 || record.score > entry.best_score {
            entry.best_score = record.score;
        }
        if record.timestamp > entry.last_timestamp {
            entry.last_timestamp = record.timestamp;
        }
    }

    let mut rows = by_scenario.into_iter().collect::<Vec<_>>();
    rows.sort_by(|(left_name, left), (right_name, right)| {
        right
            .plays
            .cmp(&left.plays)
            .then_with(|| right.last_timestamp.cmp(&left.last_timestamp))
            .then_with(|| left_name.cmp(right_name))
    });

    let mut lines = vec!["Tool result: most-played local scenarios.".to_string()];
    for (scenario, aggregate) in rows.into_iter().take(limit) {
        let avg_score = if aggregate.plays > 0 {
            aggregate.score_sum / aggregate.plays as f64
        } else {
            0.0
        };
        lines.push(format!(
            "- {} | plays: {} | avg score: {:.0} | best: {:.0} | last played: {}",
            scenario, aggregate.plays, avg_score, aggregate.best_score, aggregate.last_timestamp
        ));
    }
    lines.join("\n")
}

fn build_local_warmup_candidates_result(app: &AppHandle, limit: usize) -> String {
    let candidates = collect_local_warmup_candidates(app, 12);
    if candidates.is_empty() {
        return "Tool result: could not derive any warmup candidates from local history yet."
            .to_string();
    }

    let mut lines = vec!["Tool result: local warmup candidates.".to_string()];
    for candidate in candidates.into_iter().take(limit) {
        let mut line = format!("- {} | {}", candidate.scenario, candidate.reason);
        if let Some(avg_score) = candidate.avg_score {
            line.push_str(&format!(" | avg score: {:.0}", avg_score));
        }
        if let Some(variance) = candidate.variance {
            line.push_str(&format!(" | variance: {:.1}%", variance));
        }
        if let Some(warmup_drop) = candidate.warmup_drop {
            line.push_str(&format!(" | warm-up dip: {:.0}%", warmup_drop));
        }
        lines.push(line);
    }
    lines.join("\n")
}

fn scenario_accuracy_pct(record: &crate::session_store::SessionRecord) -> Option<f64> {
    record
        .stats_panel
        .as_ref()
        .and_then(|panel| panel.accuracy_pct.map(|value| value as f64))
        .or_else(|| {
            let fallback = record.accuracy;
            (fallback.is_finite() && fallback >= 0.0).then_some(fallback)
        })
}

fn upsert_visual_dataset(
    datasets: &mut Vec<LocalCoachVisualDataset>,
    dataset: LocalCoachVisualDataset,
) {
    if let Some(existing) = datasets
        .iter_mut()
        .find(|existing| existing.id.eq_ignore_ascii_case(&dataset.id))
    {
        *existing = dataset;
        return;
    }
    datasets.push(dataset);
}

fn build_scenario_runs_result(
    app: &AppHandle,
    request: &LocalCoachChatRequest,
    scenario_name: &str,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let scenario_name = scenario_name.trim();
    if scenario_name.is_empty() {
        return json!({
            "ok": false,
            "error": "scenarioName is required when there is no current scenario anchor"
        })
        .to_string();
    }

    let page = crate::session_store::get_session_page(app, 0, 100_000);
    let mut records = page
        .records
        .into_iter()
        .filter(|record| record.scenario.trim().eq_ignore_ascii_case(scenario_name))
        .collect::<Vec<_>>();
    records.sort_by(|left, right| left.timestamp.cmp(&right.timestamp));
    let runs = records
        .into_iter()
        .rev()
        .take(limit)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .enumerate()
        .map(|(index, record)| {
            json!({
                "index": index + 1,
                "timestamp": record.timestamp,
                "score": record.score,
                "accuracyPct": scenario_accuracy_pct(&record),
                "kills": record.kills,
                "durationSecs": record.duration_secs,
                "avgKps": record.stats_panel.as_ref().and_then(|panel| panel.avg_kps.map(|value| value as f64)),
                "avgTtkMs": record.stats_panel.as_ref().and_then(|panel| panel.avg_ttk_ms.map(|value| value as f64)),
                "bestTtkMs": record.stats_panel.as_ref().and_then(|panel| panel.best_ttk_ms.map(|value| value as f64)),
                "accuracyTrend": record.stats_panel.as_ref().and_then(|panel| panel.accuracy_trend.map(|value| value as f64)),
            })
        })
        .collect::<Vec<_>>();

    let dataset_id = normalize_visual_id(
        &format!("scenario-runs-{}-{}", scenario_name, limit),
        scenario_name,
    );
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: "scenario_runs".to_string(),
            default_x_key: "timestamp".to_string(),
            rows: runs.clone(),
            available_metrics: vec![
                "score".to_string(),
                "accuracyPct".to_string(),
                "kills".to_string(),
                "durationSecs".to_string(),
                "avgKps".to_string(),
                "avgTtkMs".to_string(),
                "bestTtkMs".to_string(),
                "accuracyTrend".to_string(),
            ],
            available_dimensions: vec!["index".to_string(), "timestamp".to_string()],
        },
    );

    json!({
        "ok": true,
        "datasetId": dataset_id,
        "defaultXKey": "timestamp",
        "availableDimensions": ["index", "timestamp"],
        "suggestedSeries": ["score", "accuracyPct", "avgKps", "avgTtkMs"],
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            "timestamp",
            "line",
            "scenario-runs-trend",
            "Recent run trend",
            "Score and accuracy over recent local runs.",
            "Score",
            "Accuracy %",
            &[("score", "Score", "line"), ("accuracyPct", "Accuracy %", "line")],
            &["Use the fetched run timeline directly instead of inventing chart points."],
        ),
        "scenarioName": scenario_name,
        "scenarioType": if request.scenario_name.trim().eq_ignore_ascii_case(scenario_name) {
            request.scenario_type.trim().to_string()
        } else {
            String::new()
        },
        "availableMetrics": [
            "score",
            "accuracyPct",
            "kills",
            "durationSecs",
            "avgKps",
            "avgTtkMs",
            "bestTtkMs",
            "accuracyTrend"
        ],
        "runs": runs,
        "rows": runs,
    })
    .to_string()
}

fn build_local_scenario_aggregates_result(
    app: &AppHandle,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return json!({
            "ok": false,
            "error": "no local session history found"
        })
        .to_string();
    }

    #[derive(Default)]
    struct ScenarioAggregate {
        plays: usize,
        score_sum: f64,
        best_score: f64,
        accuracy_sum: f64,
        accuracy_count: usize,
        best_accuracy: Option<f64>,
        last_timestamp: String,
        scenario_type: String,
    }

    let mut by_scenario: std::collections::HashMap<String, ScenarioAggregate> =
        std::collections::HashMap::new();
    for record in page.records {
        let scenario = record.scenario.trim().to_string();
        if scenario.is_empty() {
            continue;
        }
        let entry = by_scenario.entry(scenario).or_default();
        entry.plays += 1;
        entry.score_sum += record.score;
        if entry.plays == 1 || record.score > entry.best_score {
            entry.best_score = record.score;
        }
        if let Some(accuracy) = scenario_accuracy_pct(&record) {
            entry.accuracy_sum += accuracy;
            entry.accuracy_count += 1;
            entry.best_accuracy = Some(
                entry
                    .best_accuracy
                    .map(|existing| existing.max(accuracy))
                    .unwrap_or(accuracy),
            );
        }
        if record.timestamp > entry.last_timestamp {
            entry.last_timestamp = record.timestamp.clone();
        }
        if entry.scenario_type.trim().is_empty() {
            entry.scenario_type = record
                .stats_panel
                .as_ref()
                .map(|panel| panel.scenario_type.trim().to_string())
                .unwrap_or_default();
        }
    }

    let mut rows = by_scenario.into_iter().collect::<Vec<_>>();
    rows.sort_by(|(left_name, left), (right_name, right)| {
        right
            .plays
            .cmp(&left.plays)
            .then_with(|| right.last_timestamp.cmp(&left.last_timestamp))
            .then_with(|| left_name.cmp(right_name))
    });

    let scenarios = rows
        .into_iter()
        .take(limit)
        .map(|(scenario, aggregate)| {
            json!({
                "scenarioName": scenario,
                "scenarioType": aggregate.scenario_type,
                "plays": aggregate.plays,
                "avgScore": if aggregate.plays > 0 {
                    Some(aggregate.score_sum / aggregate.plays as f64)
                } else {
                    None::<f64>
                },
                "bestScore": aggregate.best_score,
                "avgAccuracyPct": if aggregate.accuracy_count > 0 {
                    Some(aggregate.accuracy_sum / aggregate.accuracy_count as f64)
                } else {
                    None::<f64>
                },
                "bestAccuracyPct": aggregate.best_accuracy,
                "lastPlayedTimestamp": aggregate.last_timestamp,
            })
        })
        .collect::<Vec<_>>();

    let dataset_id = format!("local-scenario-aggregates-{}", limit);
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: "local_scenario_aggregates".to_string(),
            default_x_key: "scenarioName".to_string(),
            rows: scenarios.clone(),
            available_metrics: vec![
                "plays".to_string(),
                "avgScore".to_string(),
                "bestScore".to_string(),
                "avgAccuracyPct".to_string(),
                "bestAccuracyPct".to_string(),
            ],
            available_dimensions: vec![
                "scenarioName".to_string(),
                "scenarioType".to_string(),
                "lastPlayedTimestamp".to_string(),
            ],
        },
    );

    json!({
        "ok": true,
        "datasetId": dataset_id,
        "defaultXKey": "scenarioName",
        "availableDimensions": ["scenarioName", "scenarioType", "lastPlayedTimestamp"],
        "suggestedSeries": ["plays", "avgScore", "avgAccuracyPct", "bestScore"],
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            "scenarioName",
            "combo",
            "scenario-comparison",
            "Scenario comparison",
            "Local scenarios compared by volume and performance.",
            "Plays",
            "Avg Score",
            &[
                ("plays", "Plays", "bar"),
                ("avgScore", "Avg Score", "line"),
                ("avgAccuracyPct", "Avg Accuracy %", "line"),
            ],
            &["Use bars for volume and lines for performance so the scales stay readable."],
        ),
        "availableMetrics": [
            "plays",
            "avgScore",
            "bestScore",
            "avgAccuracyPct",
            "bestAccuracyPct"
        ],
        "scenarios": scenarios,
        "rows": scenarios,
    })
    .to_string()
}

fn build_local_scenario_type_aggregates_result(
    app: &AppHandle,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return json!({
            "ok": false,
            "error": "no local session history found"
        })
        .to_string();
    }

    #[derive(Default)]
    struct TypeAggregate {
        plays: usize,
        score_sum: f64,
        accuracy_sum: f64,
        accuracy_count: usize,
        unique_scenarios: std::collections::HashSet<String>,
    }

    let mut by_type: std::collections::HashMap<String, TypeAggregate> =
        std::collections::HashMap::new();
    for record in page.records {
        let scenario_type = record
            .stats_panel
            .as_ref()
            .map(|panel| panel.scenario_type.trim().to_string())
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "Unknown".to_string());
        let aggregate = by_type.entry(scenario_type).or_default();
        aggregate.plays += 1;
        aggregate.score_sum += record.score;
        if let Some(accuracy) = scenario_accuracy_pct(&record) {
            aggregate.accuracy_sum += accuracy;
            aggregate.accuracy_count += 1;
        }
        aggregate
            .unique_scenarios
            .insert(record.scenario.trim().to_string());
    }

    let mut rows = by_type.into_iter().collect::<Vec<_>>();
    rows.sort_by(|(left_name, left), (right_name, right)| {
        right
            .plays
            .cmp(&left.plays)
            .then_with(|| left_name.cmp(right_name))
    });

    let scenario_types = rows
        .into_iter()
        .take(limit)
        .map(|(scenario_type, aggregate)| {
            json!({
                "scenarioType": scenario_type,
                "plays": aggregate.plays,
                "uniqueScenarioCount": aggregate.unique_scenarios.len(),
                "avgScore": if aggregate.plays > 0 {
                    Some(aggregate.score_sum / aggregate.plays as f64)
                } else {
                    None::<f64>
                },
                "avgAccuracyPct": if aggregate.accuracy_count > 0 {
                    Some(aggregate.accuracy_sum / aggregate.accuracy_count as f64)
                } else {
                    None::<f64>
                },
            })
        })
        .collect::<Vec<_>>();

    let dataset_id = format!("local-scenario-type-aggregates-{}", limit);
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: "local_scenario_type_aggregates".to_string(),
            default_x_key: "scenarioType".to_string(),
            rows: scenario_types.clone(),
            available_metrics: vec![
                "plays".to_string(),
                "uniqueScenarioCount".to_string(),
                "avgScore".to_string(),
                "avgAccuracyPct".to_string(),
            ],
            available_dimensions: vec!["scenarioType".to_string()],
        },
    );

    json!({
        "ok": true,
        "datasetId": dataset_id,
        "defaultXKey": "scenarioType",
        "availableDimensions": ["scenarioType"],
        "suggestedSeries": ["plays", "uniqueScenarioCount", "avgScore", "avgAccuracyPct"],
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            "scenarioType",
            "combo",
            "scenario-type-comparison",
            "Scenario type comparison",
            "Volume and quality by scenario type.",
            "Plays",
            "Avg Score",
            &[
                ("plays", "Plays", "bar"),
                ("uniqueScenarioCount", "Unique Scenarios", "bar"),
                ("avgScore", "Avg Score", "line"),
                ("avgAccuracyPct", "Avg Accuracy %", "line"),
            ],
            &["For scenario types, compare counts with bars and quality metrics with lines."],
        ),
        "availableMetrics": [
            "plays",
            "uniqueScenarioCount",
            "avgScore",
            "avgAccuracyPct"
        ],
        "scenarioTypes": scenario_types,
        "rows": scenario_types,
    })
    .to_string()
}

fn build_local_activity_timeline_result(
    app: &AppHandle,
    days: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return json!({
            "ok": false,
            "error": "no local session history found"
        })
        .to_string();
    }

    #[derive(Default)]
    struct DayAggregate {
        sessions: usize,
        score_sum: f64,
        accuracy_sum: f64,
        accuracy_count: usize,
        scenarios: std::collections::HashSet<String>,
    }

    let mut by_day: std::collections::BTreeMap<String, DayAggregate> =
        std::collections::BTreeMap::new();
    for record in page.records {
        let day = record
            .timestamp
            .split('-')
            .next()
            .map(str::trim)
            .unwrap_or_default()
            .to_string();
        if day.is_empty() {
            continue;
        }
        let aggregate = by_day.entry(day).or_default();
        aggregate.sessions += 1;
        aggregate.score_sum += record.score;
        if let Some(accuracy) = scenario_accuracy_pct(&record) {
            aggregate.accuracy_sum += accuracy;
            aggregate.accuracy_count += 1;
        }
        aggregate
            .scenarios
            .insert(record.scenario.trim().to_string());
    }

    let timeline = by_day
        .into_iter()
        .rev()
        .take(days)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .map(|(day, aggregate)| {
            json!({
                "day": day,
                "sessionCount": aggregate.sessions,
                "uniqueScenarioCount": aggregate.scenarios.len(),
                "avgScore": if aggregate.sessions > 0 {
                    Some(aggregate.score_sum / aggregate.sessions as f64)
                } else {
                    None::<f64>
                },
                "avgAccuracyPct": if aggregate.accuracy_count > 0 {
                    Some(aggregate.accuracy_sum / aggregate.accuracy_count as f64)
                } else {
                    None::<f64>
                },
            })
        })
        .collect::<Vec<_>>();

    let dataset_id = format!("local-activity-timeline-{}d", days);
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: "local_activity_timeline".to_string(),
            default_x_key: "day".to_string(),
            rows: timeline.clone(),
            available_metrics: vec![
                "sessionCount".to_string(),
                "uniqueScenarioCount".to_string(),
                "avgScore".to_string(),
                "avgAccuracyPct".to_string(),
            ],
            available_dimensions: vec!["day".to_string()],
        },
    );

    json!({
        "ok": true,
        "datasetId": dataset_id,
        "defaultXKey": "day",
        "availableDimensions": ["day"],
        "suggestedSeries": ["sessionCount", "uniqueScenarioCount", "avgScore", "avgAccuracyPct"],
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            "day",
            "combo",
            "practice-patterns-over-time",
            "Practice patterns over time",
            "Sessions, scenario variety, score, and accuracy over recent days.",
            "Sessions",
            "Avg Score",
            &[
                ("sessionCount", "Sessions", "bar"),
                ("uniqueScenarioCount", "Unique Scenarios", "bar"),
                ("avgScore", "Avg Score", "line"),
                ("avgAccuracyPct", "Avg Accuracy %", "line"),
            ],
            &["Use the day field on the x-axis and keep counts as bars while score and accuracy stay as lines."],
        ),
        "availableMetrics": [
            "sessionCount",
            "uniqueScenarioCount",
            "avgScore",
            "avgAccuracyPct"
        ],
        "days": timeline,
        "rows": timeline,
    })
    .to_string()
}

fn session_day_key(timestamp: &str) -> String {
    timestamp
        .split('-')
        .next()
        .map(str::trim)
        .unwrap_or_default()
        .to_string()
}

fn build_local_day_scenario_breakdown_result(
    app: &AppHandle,
    day: Option<&str>,
    ranking_metric: &str,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return json!({
            "ok": false,
            "error": "no local session history found"
        })
        .to_string();
    }

    #[derive(Default, Clone)]
    struct DayAggregate {
        session_count: usize,
        score_sum: f64,
        accuracy_sum: f64,
        accuracy_count: usize,
        scenarios: std::collections::HashSet<String>,
    }

    let mut by_day: std::collections::BTreeMap<String, DayAggregate> =
        std::collections::BTreeMap::new();
    for record in &page.records {
        let day_key = session_day_key(&record.timestamp);
        if day_key.is_empty() {
            continue;
        }
        let entry = by_day.entry(day_key).or_default();
        entry.session_count += 1;
        entry.score_sum += record.score;
        if let Some(accuracy) = scenario_accuracy_pct(record) {
            entry.accuracy_sum += accuracy;
            entry.accuracy_count += 1;
        }
        entry.scenarios.insert(record.scenario.trim().to_string());
    }

    let chosen_day = if let Some(day) = day.map(str::trim).filter(|value| !value.is_empty()) {
        if by_day.contains_key(day) {
            day.to_string()
        } else {
            return json!({
                "ok": false,
                "error": format!("No local sessions found for day {}", day)
            })
            .to_string();
        }
    } else {
        let mut ranked = by_day
            .iter()
            .map(|(day_key, aggregate)| {
                let score = match ranking_metric {
                    "avgAccuracyPct" => {
                        if aggregate.accuracy_count > 0 {
                            aggregate.accuracy_sum / aggregate.accuracy_count as f64
                        } else {
                            0.0
                        }
                    }
                    "sessionCount" => aggregate.session_count as f64,
                    _ => {
                        if aggregate.session_count > 0 {
                            aggregate.score_sum / aggregate.session_count as f64
                        } else {
                            0.0
                        }
                    }
                };
                (day_key.clone(), score)
            })
            .collect::<Vec<_>>();
        ranked.sort_by(|(left_day, left_score), (right_day, right_score)| {
            right_score
                .partial_cmp(left_score)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| right_day.cmp(left_day))
        });
        match ranked.first() {
            Some((day_key, _)) => day_key.clone(),
            None => {
                return json!({
                    "ok": false,
                    "error": "No valid local practice days were found"
                })
                .to_string();
            }
        }
    };

    #[derive(Default)]
    struct ScenarioAggregate {
        sessions: usize,
        score_sum: f64,
        best_score: f64,
        accuracy_sum: f64,
        accuracy_count: usize,
        best_accuracy: Option<f64>,
        scenario_type: String,
    }

    let mut by_scenario: std::collections::HashMap<String, ScenarioAggregate> =
        std::collections::HashMap::new();
    for record in page
        .records
        .into_iter()
        .filter(|record| session_day_key(&record.timestamp) == chosen_day)
    {
        let scenario_name = record.scenario.trim().to_string();
        if scenario_name.is_empty() {
            continue;
        }
        let entry = by_scenario.entry(scenario_name).or_default();
        entry.sessions += 1;
        entry.score_sum += record.score;
        if entry.sessions == 1 || record.score > entry.best_score {
            entry.best_score = record.score;
        }
        if let Some(accuracy) = scenario_accuracy_pct(&record) {
            entry.accuracy_sum += accuracy;
            entry.accuracy_count += 1;
            entry.best_accuracy = Some(
                entry
                    .best_accuracy
                    .map(|existing| existing.max(accuracy))
                    .unwrap_or(accuracy),
            );
        }
        if entry.scenario_type.trim().is_empty() {
            entry.scenario_type = record
                .stats_panel
                .as_ref()
                .map(|panel| panel.scenario_type.trim().to_string())
                .unwrap_or_default();
        }
    }

    let mut scenarios = by_scenario.into_iter().collect::<Vec<_>>();
    scenarios.sort_by(|(left_name, left), (right_name, right)| {
        let left_avg = if left.sessions > 0 {
            left.score_sum / left.sessions as f64
        } else {
            0.0
        };
        let right_avg = if right.sessions > 0 {
            right.score_sum / right.sessions as f64
        } else {
            0.0
        };
        right_avg
            .partial_cmp(&left_avg)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| right.sessions.cmp(&left.sessions))
            .then_with(|| left_name.cmp(right_name))
    });

    let rows = scenarios
        .into_iter()
        .take(limit)
        .map(|(scenario_name, aggregate)| {
            json!({
                "scenarioName": scenario_name,
                "scenarioType": aggregate.scenario_type,
                "sessionCount": aggregate.sessions,
                "avgScore": if aggregate.sessions > 0 {
                    Some(aggregate.score_sum / aggregate.sessions as f64)
                } else {
                    None::<f64>
                },
                "bestScore": aggregate.best_score,
                "avgAccuracyPct": if aggregate.accuracy_count > 0 {
                    Some(aggregate.accuracy_sum / aggregate.accuracy_count as f64)
                } else {
                    None::<f64>
                },
                "bestAccuracyPct": aggregate.best_accuracy,
            })
        })
        .collect::<Vec<_>>();

    let Some(day_summary) = by_day.get(&chosen_day).cloned() else {
        return json!({
            "ok": false,
            "error": format!("Could not summarize day {}", chosen_day)
        })
        .to_string();
    };

    let dataset_id = format!("local-day-scenario-breakdown-{}", chosen_day);
    upsert_visual_dataset(
        &mut retrieved_knowledge.datasets,
        LocalCoachVisualDataset {
            id: dataset_id.clone(),
            source: "local_day_scenario_breakdown".to_string(),
            default_x_key: "scenarioName".to_string(),
            rows: rows.clone(),
            available_metrics: vec![
                "sessionCount".to_string(),
                "avgScore".to_string(),
                "bestScore".to_string(),
                "avgAccuracyPct".to_string(),
                "bestAccuracyPct".to_string(),
            ],
            available_dimensions: vec!["scenarioName".to_string(), "scenarioType".to_string()],
        },
    );

    json!({
        "ok": true,
        "day": chosen_day,
        "rankingMetric": ranking_metric,
        "daySummary": {
            "sessionCount": day_summary.session_count,
            "uniqueScenarioCount": day_summary.scenarios.len(),
            "avgScore": if day_summary.session_count > 0 {
                Some(day_summary.score_sum / day_summary.session_count as f64)
            } else {
                None::<f64>
            },
            "avgAccuracyPct": if day_summary.accuracy_count > 0 {
                Some(day_summary.accuracy_sum / day_summary.accuracy_count as f64)
            } else {
                None::<f64>
            }
        },
        "datasetId": dataset_id,
        "defaultXKey": "scenarioName",
        "availableDimensions": ["scenarioName", "scenarioType"],
        "suggestedSeries": ["sessionCount", "avgScore", "avgAccuracyPct"],
        "recommendedCreateVisual": recommended_create_visual_payload(
            &dataset_id,
            "scenarioName",
            "combo",
            "day-scenario-breakdown",
            &format!("Scenario performance on {}", chosen_day),
            "Per-scenario breakdown for the selected local practice day.",
            "Sessions",
            "Avg Score",
            &[
                ("sessionCount", "Sessions", "bar"),
                ("avgScore", "Avg Score", "line"),
                ("avgAccuracyPct", "Avg Accuracy %", "line"),
            ],
            &["Use the selected day’s scenario rows directly so the chart compares scenarios inside that day."],
        ),
        "availableMetrics": [
            "sessionCount",
            "avgScore",
            "bestScore",
            "avgAccuracyPct",
            "bestAccuracyPct"
        ],
        "rows": rows,
    })
    .to_string()
}

fn normalize_visual_id(raw_id: &str, title: &str) -> String {
    let candidate = if raw_id.trim().is_empty() {
        title
    } else {
        raw_id
    };
    let mut slug = candidate
        .trim()
        .chars()
        .map(|ch| match ch {
            'a'..='z' | '0'..='9' => ch,
            'A'..='Z' => ch.to_ascii_lowercase(),
            _ => '-',
        })
        .collect::<String>();
    while slug.contains("--") {
        slug = slug.replace("--", "-");
    }
    slug.trim_matches('-').to_string()
}

fn normalize_visual_series_kind(kind: &str) -> String {
    match kind.trim().to_ascii_lowercase().as_str() {
        "bar" => "bar".to_string(),
        _ => "line".to_string(),
    }
}

fn build_effective_visual_series(
    requested_kind: &str,
    primary_label: &str,
    secondary_label: &str,
    requested_series: &[LocalCoachVisualSeries],
    points: &[LocalCoachVisualPoint],
) -> Vec<LocalCoachVisualSeries> {
    if !requested_series.is_empty() {
        return requested_series
            .iter()
            .filter(|series| !series.key.trim().is_empty())
            .map(|series| LocalCoachVisualSeries {
                key: series.key.trim().to_string(),
                label: if series.label.trim().is_empty() {
                    series.key.trim().to_string()
                } else {
                    series.label.trim().to_string()
                },
                kind: normalize_visual_series_kind(&series.kind),
                color: series.color.trim().to_string(),
            })
            .collect::<Vec<_>>();
    }

    let default_kind = match requested_kind.trim().to_ascii_lowercase().as_str() {
        "bar" => "bar",
        _ => "line",
    };

    let mut inferred = vec![LocalCoachVisualSeries {
        key: "value".to_string(),
        label: if primary_label.trim().is_empty() {
            "Value".to_string()
        } else {
            primary_label.trim().to_string()
        },
        kind: default_kind.to_string(),
        color: String::new(),
    }];

    let has_secondary = points.iter().any(|point| {
        point.secondary_value.is_some() || point.values.contains_key("secondaryValue")
    });
    if has_secondary {
        inferred.push(LocalCoachVisualSeries {
            key: "secondaryValue".to_string(),
            label: if secondary_label.trim().is_empty() {
                "Secondary".to_string()
            } else {
                secondary_label.trim().to_string()
            },
            kind: default_kind.to_string(),
            color: String::new(),
        });
    }

    inferred
}

fn metric_like_label(label: &str) -> bool {
    let normalized = label
        .trim()
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric())
        .collect::<String>()
        .to_ascii_lowercase();
    matches!(
        normalized.as_str(),
        "sessioncount"
            | "uniquescenariocount"
            | "avgscore"
            | "avgaccuracypct"
            | "plays"
            | "bestscore"
            | "bestaccuracypct"
            | "value"
            | "secondaryvalue"
    )
}

fn question_prefers_time_axis(question: &str) -> bool {
    let lower = question.to_ascii_lowercase();
    lower.contains("over time")
        || lower.contains("timeline")
        || lower.contains("trend")
        || lower.contains("last ")
        || lower.contains("past ")
        || lower.contains("per day")
        || lower.contains("daily")
        || lower.contains("week")
        || lower.contains("month")
        || lower.contains("days")
}

fn visual_shape_looks_transposed(
    request: &LocalCoachChatRequest,
    kind: &str,
    points: &[LocalCoachVisualPoint],
    series: &[LocalCoachVisualSeries],
) -> bool {
    if !question_prefers_time_axis(&request.question) {
        return false;
    }

    if !matches!(kind, "line" | "combo" | "bar") {
        return false;
    }

    if points.len() > 4 {
        return false;
    }

    let metric_labels = points
        .iter()
        .filter(|point| metric_like_label(&point.label))
        .count();
    if metric_labels != points.len() {
        return false;
    }

    !series.is_empty() || points.len() >= 2
}

fn json_value_to_chart_label(value: &JsonValue) -> Option<String> {
    match value {
        JsonValue::String(text) => {
            let trimmed = text.trim();
            (!trimmed.is_empty()).then(|| trimmed.to_string())
        }
        JsonValue::Number(number) => Some(number.to_string()),
        JsonValue::Bool(boolean) => Some(boolean.to_string()),
        _ => None,
    }
}

fn json_value_to_f64(value: &JsonValue) -> Option<f64> {
    match value {
        JsonValue::Number(number) => number.as_f64(),
        JsonValue::String(text) => text.trim().parse::<f64>().ok(),
        _ => None,
    }
}

fn build_points_from_dataset(
    dataset: &LocalCoachVisualDataset,
    x_key: &str,
    series: &[LocalCoachVisualSeries],
) -> anyhow::Result<Vec<LocalCoachVisualPoint>> {
    let x_key = x_key.trim();
    if x_key.is_empty() {
        return Err(anyhow!("xKey is required when using datasetId"));
    }
    if series.is_empty() {
        return Err(anyhow!(
            "series is required when using datasetId so the app knows which metrics to chart"
        ));
    }

    let mut points = Vec::new();
    for row in &dataset.rows {
        let Some(object) = row.as_object() else {
            continue;
        };
        let Some(label_value) = object.get(x_key) else {
            continue;
        };
        let Some(label) = json_value_to_chart_label(label_value) else {
            continue;
        };

        let mut values = HashMap::new();
        let mut first_value = None;
        let mut second_value = None;
        for series_item in series {
            let key = series_item.key.trim();
            if key.is_empty() {
                continue;
            }
            let Some(raw_value) = object.get(key) else {
                continue;
            };
            let Some(value) = json_value_to_f64(raw_value) else {
                continue;
            };
            values.insert(key.to_string(), value);
            if first_value.is_none() {
                first_value = Some(value);
            } else if second_value.is_none() {
                second_value = Some(value);
            }
        }

        if values.is_empty() {
            continue;
        }

        points.push(LocalCoachVisualPoint {
            label,
            value: first_value.unwrap_or(0.0),
            secondary_value: second_value,
            note: String::new(),
            values,
        });
    }

    if points.is_empty() {
        return Err(anyhow!(
            "No chartable rows were found in dataset {} for xKey {} and the requested series",
            dataset.id,
            x_key
        ));
    }

    Ok(points)
}

fn create_visual_result(
    request: &LocalCoachChatRequest,
    args: &JsonValue,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let parsed = serde_json::from_value::<VisualCreateRequest>(args.clone()).unwrap_or_default();
    let kind = parsed.kind.trim().to_ascii_lowercase();
    if !matches!(kind.as_str(), "line" | "bar" | "combo") {
        return json!({
            "ok": false,
            "error": "unsupported visual kind; use line, bar, or combo"
        })
        .to_string();
    }
    if parsed.dataset_id.trim().is_empty() {
        return json!({
            "ok": false,
            "error": "create_visual now requires datasetId for local AimMod charts. Fetch a raw data tool first, then call create_visual with datasetId, xKey, and series."
        })
        .to_string();
    }

    let id = normalize_visual_id(&parsed.id, &parsed.title);
    if id.is_empty() {
        return json!({
            "ok": false,
            "error": "visual needs a stable id or title"
        })
        .to_string();
    }

    let effective_series = build_effective_visual_series(
        &kind,
        &parsed.primary_label,
        &parsed.secondary_label,
        &parsed.series,
        &parsed.points,
    );

    let dataset_id = parsed.dataset_id.trim();
    let Some(dataset) = retrieved_knowledge
        .datasets
        .iter()
        .find(|dataset| dataset.id.eq_ignore_ascii_case(dataset_id))
    else {
        return json!({
            "ok": false,
            "error": format!("Unknown datasetId `{}`. Fetch data with a raw data tool first, then pass the returned datasetId to create_visual.", dataset_id)
        })
        .to_string();
    };
    let final_points = match build_points_from_dataset(
        dataset,
        if parsed.x_key.trim().is_empty() {
            &dataset.default_x_key
        } else {
            &parsed.x_key
        },
        &effective_series,
    ) {
        Ok(points) => points,
        Err(err) => {
            return json!({
                "ok": false,
                "error": err.to_string()
            })
            .to_string();
        }
    };

    if visual_shape_looks_transposed(request, &kind, &final_points, &effective_series) {
        return json!({
            "ok": false,
            "error": "This visual shape looks transposed for a time-based question. Use the time buckets (days/runs) as point labels on the x-axis, and put metrics like sessions, uniqueScenarioCount, avgScore, and avgAccuracyPct into series/point values instead of using those metric names as point labels."
        })
        .to_string();
    }

    let visual = LocalCoachVisual {
        id: id.clone(),
        kind,
        title: parsed.title.trim().to_string(),
        subtitle: parsed.subtitle.trim().to_string(),
        primary_label: parsed.primary_label.trim().to_string(),
        secondary_label: parsed.secondary_label.trim().to_string(),
        points: final_points.into_iter().take(120).collect::<Vec<_>>(),
        series: effective_series,
        detail_lines: parsed
            .detail_lines
            .into_iter()
            .map(|line| line.trim().to_string())
            .filter(|line| !line.is_empty())
            .take(8)
            .collect::<Vec<_>>(),
    };
    upsert_visual(&mut retrieved_knowledge.visuals, visual);

    json!({
        "ok": true,
        "visualId": id,
        "referenceToken": format!("[[visual:{id}]]"),
        "message": "Visual created. Reference it inline in the answer with the returned referenceToken."
    })
    .to_string()
}

#[derive(Clone)]
struct WarmupCandidate {
    scenario: String,
    score: f64,
    avg_score: Option<f64>,
    variance: Option<f64>,
    warmup_drop: Option<f64>,
    reason: String,
}

fn collect_local_warmup_candidates(app: &AppHandle, candidate_pool: usize) -> Vec<WarmupCandidate> {
    let recent = crate::session_store::get_recent_scenarios(app, 30);
    if recent.is_empty() {
        return vec![];
    }

    let mut unique = Vec::<String>::new();
    for entry in recent {
        let scenario = entry.scenario.trim();
        if scenario.is_empty() {
            continue;
        }
        if unique
            .iter()
            .any(|existing| existing.eq_ignore_ascii_case(scenario))
        {
            continue;
        }
        unique.push(scenario.to_string());
        if unique.len() >= candidate_pool {
            break;
        }
    }

    let mut candidates = Vec::<WarmupCandidate>::new();
    for scenario in unique {
        let Ok(overview) = crate::coaching::get_scenario_overview(app, &scenario, None) else {
            continue;
        };

        let mut score = 0.0;
        if let Some(cv) = overview.score_cv_pct {
            score += (20.0 - cv).clamp(0.0, 20.0);
        }
        if let Some(warmup) = &overview.warmup_stats {
            score += (15.0 - warmup.drop_pct).clamp(0.0, 15.0);
        } else {
            score += 6.0;
        }
        if !overview.is_plateau {
            score += 3.0;
        }

        let reason = match (&overview.warmup_stats, overview.score_cv_pct) {
            (Some(warmup), Some(cv)) => format!(
                "warm-up dip about {:.0}% with variance around {:.1}%",
                warmup.drop_pct, cv
            ),
            (Some(warmup), None) => format!("warm-up dip about {:.0}%", warmup.drop_pct),
            (None, Some(cv)) => format!("variance around {:.1}%", cv),
            (None, None) => "recently played with usable local history".to_string(),
        };

        candidates.push(WarmupCandidate {
            scenario,
            score,
            avg_score: overview.avg_score,
            variance: overview.score_cv_pct,
            warmup_drop: overview.warmup_stats.as_ref().map(|warmup| warmup.drop_pct),
            reason,
        });
    }

    candidates.sort_by(|left, right| {
        right
            .score
            .partial_cmp(&left.score)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    candidates
}

fn build_current_scenario_score_trend_visual(
    app: &AppHandle,
    request: &LocalCoachChatRequest,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let scenario = request.scenario_name.trim();
    if scenario.is_empty() {
        return "Tool result: cannot build a current-scenario trend visual without an active scenario.".to_string();
    }

    let page = crate::session_store::get_session_page(app, 0, 100_000);
    let mut records = page
        .records
        .into_iter()
        .filter(|record| record.scenario.trim().eq_ignore_ascii_case(scenario))
        .collect::<Vec<_>>();
    if records.len() < 3 {
        return format!(
            "Tool result: not enough local runs to chart a score trend for {}.",
            scenario
        );
    }
    records.sort_by(|left, right| left.timestamp.cmp(&right.timestamp));

    let recent = records
        .into_iter()
        .rev()
        .take(limit)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect::<Vec<_>>();
    let scores = recent.iter().map(|record| record.score).collect::<Vec<_>>();
    let points = recent
        .iter()
        .enumerate()
        .map(|(index, record)| {
            let start = index.saturating_sub(2);
            let window = &scores[start..=index];
            let rolling = window.iter().sum::<f64>() / window.len() as f64;
            LocalCoachVisualPoint {
                label: format!("#{}", index + 1),
                value: record.score,
                secondary_value: Some(rolling),
                note: record.timestamp.clone(),
                values: HashMap::new(),
            }
        })
        .collect::<Vec<_>>();

    upsert_visual(
        &mut retrieved_knowledge.visuals,
        LocalCoachVisual {
            id: format!("score-trend-{}", scenario.to_lowercase().replace(' ', "-")),
            kind: "line".to_string(),
            title: format!("{} score trend", scenario),
            subtitle: format!("Last {} local runs for the current scenario.", points.len()),
            primary_label: "Score".to_string(),
            secondary_label: "Rolling avg".to_string(),
            points,
            series: vec![],
            detail_lines: vec![],
        },
    );

    format!(
        "Tool result: created a score trend visual for {} using the last {} local runs.",
        scenario,
        scores.len()
    )
}

fn build_local_most_played_visual(
    app: &AppHandle,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let page = crate::session_store::get_session_page(app, 0, 100_000);
    if page.records.is_empty() {
        return "Tool result: no local session history found for a scenario mix visual."
            .to_string();
    }

    let mut counts: std::collections::HashMap<String, usize> = std::collections::HashMap::new();
    for record in page.records {
        let scenario = record.scenario.trim();
        if scenario.is_empty() {
            continue;
        }
        *counts.entry(scenario.to_string()).or_insert(0) += 1;
    }
    let mut rows = counts.into_iter().collect::<Vec<_>>();
    rows.sort_by(|(left_name, left), (right_name, right)| {
        right.cmp(left).then_with(|| left_name.cmp(right_name))
    });
    let points = rows
        .into_iter()
        .take(limit)
        .map(|(scenario, plays)| LocalCoachVisualPoint {
            label: scenario,
            value: plays as f64,
            secondary_value: None,
            note: String::new(),
            values: HashMap::new(),
        })
        .collect::<Vec<_>>();
    if points.is_empty() {
        return "Tool result: no local scenario mix visual could be created.".to_string();
    }

    upsert_visual(
        &mut retrieved_knowledge.visuals,
        LocalCoachVisual {
            id: "local-most-played-scenarios".to_string(),
            kind: "bar".to_string(),
            title: "Most-played local scenarios".to_string(),
            subtitle: "What your recent history has centered on the most.".to_string(),
            primary_label: "Plays".to_string(),
            secondary_label: String::new(),
            points,
            series: vec![],
            detail_lines: vec![],
        },
    );

    "Tool result: created a local most-played scenarios visual.".to_string()
}

fn build_local_warmup_candidates_visual(
    app: &AppHandle,
    limit: usize,
    retrieved_knowledge: &mut RetrievedKnowledge,
) -> String {
    let candidates = collect_local_warmup_candidates(app, 12);
    if candidates.is_empty() {
        return "Tool result: no warmup candidate visual could be created from local history yet."
            .to_string();
    }

    let points = candidates
        .into_iter()
        .take(limit)
        .map(|candidate| LocalCoachVisualPoint {
            label: candidate.scenario,
            value: candidate.score,
            secondary_value: candidate.warmup_drop,
            note: candidate.reason,
            values: HashMap::new(),
        })
        .collect::<Vec<_>>();

    upsert_visual(
        &mut retrieved_knowledge.visuals,
        LocalCoachVisual {
            id: "local-warmup-candidates".to_string(),
            kind: "bar".to_string(),
            title: "Warmup candidates from your history".to_string(),
            subtitle: "Higher score means steadier local warmup fit; secondary value is warm-up dip when available.".to_string(),
            primary_label: "Warmup fit".to_string(),
            secondary_label: "Warm-up dip %".to_string(),
            points,
            series: vec![],
            detail_lines: vec![],
        },
    );

    "Tool result: created a warmup candidate visual from local history.".to_string()
}

fn upsert_visual(visuals: &mut Vec<LocalCoachVisual>, visual: LocalCoachVisual) {
    if let Some(existing) = visuals
        .iter_mut()
        .find(|existing| existing.id.eq_ignore_ascii_case(&visual.id))
    {
        *existing = visual;
        return;
    }
    visuals.push(visual);
}

fn build_scenario_context_result(request: &LocalCoachChatRequest) -> String {
    let mut lines = vec!["Tool result: current scenario context.".to_string()];
    if !request.scenario_name.trim().is_empty() {
        lines.push(format!("Scenario: {}", request.scenario_name.trim()));
    }
    if !request.scenario_type.trim().is_empty() {
        lines.push(format!("Scenario type: {}", request.scenario_type.trim()));
    }
    if !request.scenario_summary.trim().is_empty() {
        lines.push(format!(
            "Scenario summary: {}",
            request.scenario_summary.trim()
        ));
    }
    if !request.global_summary.trim().is_empty() {
        lines.push(format!("Global summary: {}", request.global_summary.trim()));
    }
    if !request.focus_area.trim().is_empty() {
        lines.push(format!("Focus preference: {}", request.focus_area.trim()));
    }
    if !request.challenge_preference.trim().is_empty() {
        lines.push(format!(
            "Challenge preference: {}",
            request.challenge_preference.trim()
        ));
    }
    if !request.time_preference.trim().is_empty() {
        lines.push(format!(
            "Time preference: {}",
            request.time_preference.trim()
        ));
    }
    if !request.coaching_cards.is_empty() {
        lines.push("Coaching cards:".to_string());
        for card in request.coaching_cards.iter().take(5) {
            let mut card_line = format!(
                "- {} [{}]: {} | Tip: {}",
                card.title.trim(),
                card.badge.trim(),
                card.body.trim(),
                card.tip.trim()
            );
            if !card.signals.is_empty() {
                card_line.push_str(&format!(" | Signals: {}", card.signals.join(", ")));
            }
            lines.push(card_line);
        }
    }
    lines.join("\n")
}

fn build_coach_facts_result(request: &LocalCoachChatRequest) -> String {
    if request.coach_facts.is_empty() {
        return "Tool result: no local coach facts available.".to_string();
    }
    let mut lines = vec!["Tool result: local coach facts.".to_string()];
    for fact in request.coach_facts.iter().take(12) {
        let mut line = format!(
            "- {} [{}]: {}",
            fact.label.trim(),
            fact.key.trim(),
            fact.value_text.trim()
        );
        if !fact.direction.trim().is_empty() {
            line.push_str(&format!(" | direction: {}", fact.direction.trim()));
        }
        if !fact.confidence.trim().is_empty() {
            line.push_str(&format!(" | confidence: {}", fact.confidence.trim()));
        }
        lines.push(line);
    }
    lines.join("\n")
}

fn build_kb_tool_result(retrieved_knowledge: &RetrievedKnowledge, new_count: usize) -> String {
    let mut lines = vec![format!(
        "Tool result: {} new KB entr{}.",
        new_count,
        if new_count == 1 { "y" } else { "ies" }
    )];
    if !retrieved_knowledge.answer_plan.intent.trim().is_empty() {
        lines.push(format!(
            "Intent: {} | Response shape: {} | Must answer directly: {}",
            retrieved_knowledge.answer_plan.intent.trim(),
            retrieved_knowledge.answer_plan.response_shape.trim(),
            retrieved_knowledge.answer_plan.must_answer_directly
        ));
    }
    for finding in retrieved_knowledge
        .answer_plan
        .primary_findings
        .iter()
        .take(3)
    {
        lines.push(format!("Finding: {}", finding.trim()));
    }
    for item in retrieved_knowledge.items.iter().take(5) {
        let mut line = format!(
            "[{}] {}: {}",
            item.id.trim(),
            item.title.trim(),
            item.summary.trim()
        );
        if let Some(drill) = item.drills.first() {
            line.push_str(&format!(" | Drill: {}", drill.label.trim()));
        }
        lines.push(line);
    }
    if new_count == 0 {
        lines.push("No new entries found.".to_string());
    }
    lines.join("\n")
}

struct ActiveRuntime {
    endpoint: String,
    model_id: String,
}

async fn ensure_runtime_started(
    app: &AppHandle,
    runtime_state: &Arc<Mutex<LocalLlmRuntimeState>>,
    gpu_layers: i32,
) -> anyhow::Result<ActiveRuntime> {
    let assets = ensure_assets_available(app).await?;
    let missing_runner_deps = missing_runner_dependencies(&assets);
    if !assets.runner_path.is_file() {
        return Err(anyhow!(
            "Local coach runtime missing runner binary at {}",
            assets.runner_path.display()
        ));
    }
    if !missing_runner_deps.is_empty() {
        return Err(anyhow!(format_missing_runner_dependency_message(
            &assets,
            &missing_runner_deps
        )));
    }
    if !assets.model_path.is_file() {
        return Err(anyhow!(
            "Local coach runtime missing model file at {}",
            assets.model_path.display()
        ));
    }

    {
        let mut runtime = runtime_state
            .lock()
            .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
        if process_is_alive(&mut runtime)? {
            // If the user changed the GPU mode we need to restart with the new flag.
            if runtime.active_gpu_layers != gpu_layers {
                if let Some(mut child) = runtime.child.take() {
                    let _ = child.kill();
                    let _ = child.wait();
                }
                runtime.model_id = None;
                runtime.endpoint = None;
                runtime.last_error = None;
                runtime.launched_at_unix_ms = None;
            } else {
                let endpoint = runtime
                    .endpoint
                    .clone()
                    .unwrap_or_else(|| assets.endpoint.clone());
                let model_id = runtime
                    .model_id
                    .clone()
                    .unwrap_or_else(|| assets.manifest.model_id.clone());
                if !model_id.trim().is_empty() {
                    return Ok(ActiveRuntime { endpoint, model_id });
                }
            }
        }
    }

    // Before spawning, check if a llama-server from a previous run is still
    // listening on the preferred port.  If it responds to /v1/models we can
    // adopt it directly without starting a duplicate process.
    let preferred_endpoint = format!(
        "http://{}:{}/v1",
        if assets.manifest.host.trim().is_empty() {
            DEFAULT_HOST
        } else {
            assets.manifest.host.trim()
        },
        assets.manifest.port,
    );
    // Adopt an orphan server only in CPU-only mode (gpu_layers == 0) where we
    // know it was launched without GPU flags.  For GPU modes we cannot verify
    // the orphan's launch flags, so we skip adoption and let select_runtime_endpoint
    // find a free port for a fresh spawn with the correct --n-gpu-layers value.
    if gpu_layers == 0 {
        if let Some(model_id) =
            probe_existing_server(&preferred_endpoint, &assets.manifest.model_id).await
        {
            let mut runtime = runtime_state
                .lock()
                .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
            runtime.endpoint = Some(preferred_endpoint.clone());
            runtime.model_id = Some(model_id.clone());
            runtime.last_error = None;
            runtime.active_gpu_layers = 0;
            return Ok(ActiveRuntime {
                endpoint: preferred_endpoint,
                model_id,
            });
        }
    }

    let (host, port, endpoint) =
        select_runtime_endpoint(&assets.manifest.host, assets.manifest.port)?;

    prepare_runtime_log_file(&assets.stdout_log_path)?;
    prepare_runtime_log_file(&assets.stderr_log_path)?;

    let mut command = Command::new(&assets.runner_path);
    command
        .arg("-m")
        .arg(&assets.model_path)
        .arg("--host")
        .arg(&host)
        .arg("--port")
        .arg(port.to_string())
        .arg("--ctx-size")
        .arg(assets.manifest.context_size.to_string())
        .arg("--n-gpu-layers")
        .arg(gpu_layers.to_string())
        .stdin(Stdio::null())
        .stdout(Stdio::from(
            fs::File::create(&assets.stdout_log_path).with_context(|| {
                format!(
                    "could not create local coach stdout log at {}",
                    assets.stdout_log_path.display()
                )
            })?,
        ))
        .stderr(Stdio::from(
            fs::File::create(&assets.stderr_log_path).with_context(|| {
                format!(
                    "could not create local coach stderr log at {}",
                    assets.stderr_log_path.display()
                )
            })?,
        ));

    if let Some(threads) = assets.manifest.threads.filter(|threads| *threads > 0) {
        command.arg("--threads").arg(threads.to_string());
    }
    for arg in &assets.manifest.extra_args {
        command.arg(arg);
    }
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000);
    }

    let child = command.spawn().with_context(|| {
        format!(
            "could not launch local coach runtime at {}",
            assets.runner_path.display()
        )
    })?;
    let pid = child.id();
    {
        let mut runtime = runtime_state
            .lock()
            .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
        runtime.child = Some(child);
        runtime.launched_at_unix_ms = Some(now_unix_ms());
        runtime.last_error = None;
        runtime.model_id = None;
        runtime.endpoint = Some(endpoint.clone());
        runtime.active_gpu_layers = gpu_layers;
    }

    let model_id = wait_until_ready(runtime_state, &assets, &endpoint)
        .await
        .with_context(|| {
            format!(
                "local coach runtime failed to become ready on {} (pid {}). Check {} for startup logs.",
                endpoint,
                pid,
                assets.stderr_log_path.display()
            )
        })?;

    {
        let mut runtime = runtime_state
            .lock()
            .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
        runtime.model_id = Some(model_id.clone());
        runtime.last_error = None;
    }

    Ok(ActiveRuntime { endpoint, model_id })
}

async fn wait_until_ready(
    runtime_state: &Arc<Mutex<LocalLlmRuntimeState>>,
    assets: &ResolvedRuntimeAssets,
    endpoint: &str,
) -> anyhow::Result<String> {
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()
        .context("could not build local coach readiness client")?;
    let deadline = std::time::Instant::now() + Duration::from_millis(STARTUP_TIMEOUT_MS);

    while std::time::Instant::now() < deadline {
        {
            let mut runtime = runtime_state
                .lock()
                .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
            if !process_is_alive(&mut runtime)? {
                let mut detail = runtime
                    .last_error
                    .clone()
                    .unwrap_or_else(|| "local coach runtime exited during startup".to_string());
                if let Some(stderr_tail) = read_log_tail(&assets.stderr_log_path) {
                    detail.push_str("\n\nstderr tail:\n");
                    detail.push_str(&stderr_tail);
                }
                return Err(anyhow!(detail));
            }
        }

        match client.get(format!("{}/models", endpoint)).send().await {
            Ok(response) => {
                if let Ok(ok_response) = response.error_for_status() {
                    let models = ok_response
                        .json::<OpenAiModelsResponse>()
                        .await
                        .context("could not decode local model list")?;
                    if let Some(model_id) = select_model_id(&models, &assets.manifest.model_id) {
                        return Ok(model_id);
                    }
                }
            }
            Err(_) => {}
        }

        tokio::time::sleep(Duration::from_millis(POLL_INTERVAL_MS)).await;
    }

    Err(anyhow!(
        "timed out waiting for local coach runtime to listen on {}. Check {} for startup logs",
        endpoint,
        assets.stderr_log_path.display()
    ))
}

fn prepare_runtime_log_file(path: &Path) -> anyhow::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).with_context(|| {
            format!(
                "could not create local coach log directory {}",
                parent.display()
            )
        })?;
    }
    if path.exists() {
        fs::remove_file(path)
            .with_context(|| format!("could not reset local coach log {}", path.display()))?;
    }
    Ok(())
}

fn read_log_tail(path: &Path) -> Option<String> {
    let raw = fs::read_to_string(path).ok()?;
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let mut lines = trimmed.lines().rev().take(20).collect::<Vec<_>>();
    lines.reverse();
    Some(lines.join("\n"))
}

fn select_runtime_endpoint(
    host: &str,
    preferred_port: u16,
) -> anyhow::Result<(String, u16, String)> {
    let normalized_host = if host.trim().is_empty() {
        DEFAULT_HOST.to_string()
    } else {
        host.trim().to_string()
    };

    let selected_port = if port_is_available(&normalized_host, preferred_port) {
        preferred_port
    } else {
        find_open_port(&normalized_host)?
    };

    Ok((
        normalized_host.clone(),
        selected_port,
        format!("http://{}:{}/v1", normalized_host, selected_port),
    ))
}

fn port_is_available(host: &str, port: u16) -> bool {
    TcpListener::bind((host, port)).is_ok()
}

fn find_open_port(host: &str) -> anyhow::Result<u16> {
    let listener = TcpListener::bind((host, 0))
        .with_context(|| format!("could not allocate an open local port on {}", host))?;
    let port = listener
        .local_addr()
        .context("could not read allocated local LLM port")?
        .port();
    drop(listener);
    Ok(port)
}

fn select_model_id(models: &OpenAiModelsResponse, preferred: &str) -> Option<String> {
    if !preferred.trim().is_empty()
        && models
            .data
            .iter()
            .any(|model| model.id.trim() == preferred.trim())
    {
        return Some(preferred.trim().to_string());
    }
    models.data.iter().find_map(|model| {
        let id = model.id.trim();
        if id.is_empty() {
            None
        } else {
            Some(id.to_string())
        }
    })
}

/// Try to contact a llama-server that may already be running (e.g. from a
/// previous app instance that didn't exit cleanly).  Returns the model ID on
/// success so the caller can adopt the existing process instead of spawning a
/// new one.
async fn probe_existing_server(endpoint: &str, preferred_model_id: &str) -> Option<String> {
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(3))
        .build()
        .ok()?;
    let response = client
        .get(format!("{}/models", endpoint))
        .send()
        .await
        .ok()?
        .error_for_status()
        .ok()?;
    let models = response.json::<OpenAiModelsResponse>().await.ok()?;
    select_model_id(&models, preferred_model_id)
}

fn process_is_alive(runtime: &mut LocalLlmRuntimeState) -> anyhow::Result<bool> {
    let Some(child) = runtime.child.as_mut() else {
        return Ok(false);
    };
    match child
        .try_wait()
        .context("could not query local coach process")?
    {
        Some(status) => {
            runtime.child = None;
            runtime.model_id = None;
            runtime.endpoint = None;
            runtime.last_error = Some(format!("local coach runtime exited with status {status}"));
            Ok(false)
        }
        None => Ok(true),
    }
}

async fn ensure_assets_available(app: &AppHandle) -> anyhow::Result<ResolvedRuntimeAssets> {
    if let Some(assets) = resolve_usable_runtime_assets(app)? {
        return Ok(assets);
    }

    let app_handle = app.clone();
    tokio::task::spawn_blocking(move || {
        install_assets_blocking(&app_handle)?;
        resolve_usable_runtime_assets(&app_handle)?.ok_or_else(|| {
            anyhow!("local coach assets installed, but no usable runtime could be found")
        })
    })
    .await
    .context("local coach asset install task failed")?
}

fn resolve_runtime_assets(app: &AppHandle) -> anyhow::Result<ResolvedRuntimeAssets> {
    resolve_usable_runtime_assets(app)?
        .ok_or_else(|| anyhow!("AimMod local coach assets are not installed yet."))
}

fn resolve_usable_runtime_assets(app: &AppHandle) -> anyhow::Result<Option<ResolvedRuntimeAssets>> {
    for root in candidate_asset_roots(app)? {
        if let Ok(assets) = resolve_runtime_assets_from_root(&root) {
            if assets.runner_path.is_file()
                && assets.model_path.is_file()
                && missing_runner_dependencies(&assets).is_empty()
            {
                return Ok(Some(assets));
            }
        }
    }
    Ok(None)
}

fn resolve_any_runtime_assets(app: &AppHandle) -> anyhow::Result<Option<ResolvedRuntimeAssets>> {
    for root in candidate_asset_roots(app)? {
        if let Ok(assets) = resolve_runtime_assets_from_root(&root) {
            return Ok(Some(assets));
        }
    }
    Ok(None)
}

fn candidate_asset_roots(app: &AppHandle) -> anyhow::Result<Vec<PathBuf>> {
    Ok(vec![installed_asset_root(app)?])
}

fn resolve_runtime_assets_from_root(asset_root: &Path) -> anyhow::Result<ResolvedRuntimeAssets> {
    let manifest_path = asset_root.join("manifest.json");
    let manifest = load_manifest(&manifest_path)?;
    let runner_path = resolve_runtime_path(asset_root, &manifest.runtime_path);
    let runner_dir = runner_path
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_else(|| asset_root.join("runner"));
    let model_path = resolve_data_path(asset_root, &manifest.model_path);
    let logs_dir = asset_root.join("logs");
    let endpoint = format!("http://{}:{}/v1", manifest.host.trim(), manifest.port);

    Ok(ResolvedRuntimeAssets {
        asset_root: asset_root.to_path_buf(),
        manifest_path,
        runner_path,
        runner_dir,
        model_path,
        stdout_log_path: logs_dir.join("llama-server.stdout.log"),
        stderr_log_path: logs_dir.join("llama-server.stderr.log"),
        endpoint,
        manifest,
    })
}

fn install_assets_blocking(app: &AppHandle) -> anyhow::Result<()> {
    let install_root = installed_asset_root(app)?;
    fs::create_dir_all(&install_root).with_context(|| {
        format!(
            "could not create local coach install directory {}",
            install_root.display()
        )
    })?;

    let remote_manifest = download_remote_asset_manifest()?;
    let runtime_key = current_runtime_key()?;
    let runtime_package = remote_manifest
        .runtime
        .get(&runtime_key)
        .cloned()
        .ok_or_else(|| {
            anyhow!("remote coach manifest does not include a runtime package for {runtime_key}")
        })?;

    let staging_root = install_root.join(format!("staging-{}", now_unix_ms()));
    let extracted_runtime = staging_root.join("runtime-extracted");
    fs::create_dir_all(&extracted_runtime).with_context(|| {
        format!(
            "could not create local coach staging directory {}",
            extracted_runtime.display()
        )
    })?;

    let runtime_archive_path = staging_root.join(match runtime_package.archive_type.trim() {
        "zip" | "" => "runtime.zip",
        other => {
            return Err(anyhow!(
                "unsupported local coach runtime archive type: {other}"
            ));
        }
    });
    download_file_blocking(&runtime_package.url, &runtime_archive_path).with_context(|| {
        format!(
            "could not download local coach runtime from {}",
            runtime_package.url
        )
    })?;
    verify_file_sha256(&runtime_archive_path, &runtime_package.sha256)
        .context("local coach runtime checksum did not match")?;
    extract_zip_file(&runtime_archive_path, &extracted_runtime)
        .context("could not extract local coach runtime archive")?;

    let source_runner_dir = find_runner_dir(&extracted_runtime).ok_or_else(|| {
        anyhow!("could not find llama-server.exe in the downloaded local coach runtime archive")
    })?;
    let target_runner_dir = install_root.join("runner");
    if target_runner_dir.exists() {
        fs::remove_dir_all(&target_runner_dir).with_context(|| {
            format!(
                "could not replace local coach runtime at {}",
                target_runner_dir.display()
            )
        })?;
    }
    copy_directory_recursive(&source_runner_dir, &target_runner_dir)?;

    for (index, extra) in runtime_package.extras.iter().enumerate() {
        let extra_archive_path = staging_root.join(format!("runtime-extra-{}.zip", index + 1));
        let extra_extract_root = staging_root.join(format!("runtime-extra-{}", index + 1));
        fs::create_dir_all(&extra_extract_root).with_context(|| {
            format!(
                "could not create local coach extra package staging directory {}",
                extra_extract_root.display()
            )
        })?;
        download_file_blocking(&extra.url, &extra_archive_path).with_context(|| {
            format!(
                "could not download local coach runtime companion package from {}",
                extra.url
            )
        })?;
        verify_file_sha256(&extra_archive_path, &extra.sha256)
            .context("local coach runtime companion package checksum did not match")?;
        match extra.archive_type.trim() {
            "zip" | "" => {
                extract_zip_file(&extra_archive_path, &extra_extract_root)
                    .context("could not extract local coach runtime companion archive")?;
                copy_directory_recursive(&extra_extract_root, &target_runner_dir)?;
            }
            other => {
                return Err(anyhow!(
                    "unsupported local coach runtime companion archive type: {other}"
                ));
            }
        }
    }

    let model_filename = if remote_manifest.model.filename.trim().is_empty() {
        "aimmod-coach.gguf".to_string()
    } else {
        remote_manifest.model.filename.trim().to_string()
    };
    let model_dir = install_root.join("models");
    fs::create_dir_all(&model_dir).with_context(|| {
        format!(
            "could not create local coach model directory {}",
            model_dir.display()
        )
    })?;
    let model_target = model_dir.join(&model_filename);
    let model_temp = staging_root.join("model.download");
    download_file_blocking(&remote_manifest.model.url, &model_temp).with_context(|| {
        format!(
            "could not download local coach model from {}",
            remote_manifest.model.url
        )
    })?;
    verify_file_sha256(&model_temp, &remote_manifest.model.sha256)
        .context("local coach model checksum did not match")?;
    fs::rename(&model_temp, &model_target)
        .or_else(|_| {
            fs::copy(&model_temp, &model_target)?;
            fs::remove_file(&model_temp)
        })
        .with_context(|| {
            format!(
                "could not move local coach model into {}",
                model_target.display()
            )
        })?;

    let local_manifest = RuntimeManifest {
        model_path: format!("models/{model_filename}"),
        ..RuntimeManifest::default()
    };
    let local_manifest_path = install_root.join("manifest.json");
    fs::write(
        &local_manifest_path,
        serde_json::to_vec_pretty(&local_manifest)
            .context("could not encode local coach manifest")?,
    )
    .with_context(|| {
        format!(
            "could not write local coach manifest to {}",
            local_manifest_path.display()
        )
    })?;

    let _ = fs::remove_dir_all(&staging_root);
    Ok(())
}

fn installed_asset_root(app: &AppHandle) -> anyhow::Result<PathBuf> {
    Ok(app
        .path()
        .app_local_data_dir()
        .map_err(|e| anyhow!("could not resolve AimMod local data directory: {e}"))?
        .join("tools")
        .join("local-llm"))
}

fn remote_manifest_url() -> String {
    std::env::var("AIMMOD_LOCAL_LLM_MANIFEST_URL")
        .unwrap_or_else(|_| DEFAULT_REMOTE_MANIFEST_URL.to_string())
}

fn download_remote_asset_manifest() -> anyhow::Result<RemoteAssetManifest> {
    let url = remote_manifest_url();
    let text = reqwest::blocking::get(&url)
        .and_then(|response| response.error_for_status())
        .with_context(|| format!("could not download local coach asset manifest from {url}"))?
        .text()
        .context("could not read local coach asset manifest response body")?;
    serde_json::from_str::<RemoteAssetManifest>(&text)
        .with_context(|| format!("could not parse local coach asset manifest from {url}"))
}

fn current_runtime_key() -> anyhow::Result<String> {
    #[cfg(target_os = "windows")]
    {
        match std::env::consts::ARCH {
            "x86_64" => Ok("windows-x64".to_string()),
            "aarch64" => Ok("windows-arm64".to_string()),
            "x86" => Ok("windows-x86".to_string()),
            other => Err(anyhow!(
                "unsupported Windows architecture for local coach runtime: {other}"
            )),
        }
    }
    #[cfg(target_os = "linux")]
    {
        match std::env::consts::ARCH {
            "x86_64" => Ok("linux-x64".to_string()),
            "aarch64" => Ok("linux-arm64".to_string()),
            other => Err(anyhow!(
                "unsupported Linux architecture for local coach runtime: {other}"
            )),
        }
    }
    #[cfg(target_os = "macos")]
    {
        match std::env::consts::ARCH {
            "x86_64" => Ok("macos-x64".to_string()),
            "aarch64" => Ok("macos-arm64".to_string()),
            other => Err(anyhow!(
                "unsupported macOS architecture for local coach runtime: {other}"
            )),
        }
    }
}

fn download_file_blocking(url: &str, destination: &Path) -> anyhow::Result<()> {
    let mut response = reqwest::blocking::get(url)
        .and_then(|response| response.error_for_status())
        .with_context(|| format!("could not start download from {url}"))?;
    if let Some(parent) = destination.parent() {
        fs::create_dir_all(parent).with_context(|| {
            format!(
                "could not create download parent directory {}",
                parent.display()
            )
        })?;
    }
    let mut file = fs::File::create(destination)
        .with_context(|| format!("could not create download target {}", destination.display()))?;
    std::io::copy(&mut response, &mut file)
        .with_context(|| format!("could not write download target {}", destination.display()))?;
    file.flush()
        .with_context(|| format!("could not flush downloaded file {}", destination.display()))?;
    Ok(())
}

fn verify_file_sha256(path: &Path, expected: &str) -> anyhow::Result<()> {
    let expected = expected.trim().to_ascii_lowercase();
    if expected.len() != 64 || !expected.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(anyhow!("invalid sha256 checksum in local coach manifest"));
    }

    use sha2::{Digest, Sha256};
    let mut file = fs::File::open(path)
        .with_context(|| format!("could not open downloaded file {}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file
            .read(&mut buffer)
            .with_context(|| format!("could not read downloaded file {}", path.display()))?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    let digest = hasher.finalize();
    let mut actual = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use std::fmt::Write as _;
        let _ = write!(&mut actual, "{byte:02x}");
    }
    if actual != expected {
        return Err(anyhow!(
            "expected sha256 {} but downloaded {} for {}",
            expected,
            actual,
            path.display()
        ));
    }
    Ok(())
}

fn extract_zip_file(archive_path: &Path, destination: &Path) -> anyhow::Result<()> {
    let file = fs::File::open(archive_path)
        .with_context(|| format!("could not open archive {}", archive_path.display()))?;
    let mut archive = zip::ZipArchive::new(file)
        .with_context(|| format!("could not open zip archive {}", archive_path.display()))?;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .context("could not read local coach archive entry")?;
        let Some(enclosed_path) = entry.enclosed_name().map(|path| path.to_path_buf()) else {
            continue;
        };
        let out_path = destination.join(enclosed_path);
        if entry.is_dir() {
            fs::create_dir_all(&out_path).with_context(|| {
                format!("could not create archive directory {}", out_path.display())
            })?;
            continue;
        }
        if let Some(parent) = out_path.parent() {
            fs::create_dir_all(parent)
                .with_context(|| format!("could not create archive parent {}", parent.display()))?;
        }
        let mut output = fs::File::create(&out_path)
            .with_context(|| format!("could not create extracted file {}", out_path.display()))?;
        std::io::copy(&mut entry, &mut output)
            .with_context(|| format!("could not extract archive file {}", out_path.display()))?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            if let Some(mode) = entry.unix_mode() {
                let _ = fs::set_permissions(&out_path, fs::Permissions::from_mode(mode));
            }
        }
    }
    Ok(())
}

fn find_runner_dir(root: &Path) -> Option<PathBuf> {
    let expected = if cfg!(target_os = "windows") {
        "llama-server.exe"
    } else {
        "llama-server"
    };
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let entries = fs::read_dir(&dir).ok()?;
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
                continue;
            }
            if path
                .file_name()
                .and_then(|name| name.to_str())
                .map(|name| name.eq_ignore_ascii_case(expected))
                .unwrap_or(false)
            {
                return path.parent().map(Path::to_path_buf);
            }
        }
    }
    None
}

fn copy_directory_recursive(source: &Path, target: &Path) -> anyhow::Result<()> {
    fs::create_dir_all(target)
        .with_context(|| format!("could not create target directory {}", target.display()))?;
    for entry in fs::read_dir(source)
        .with_context(|| format!("could not read source directory {}", source.display()))?
    {
        let entry = entry.context("could not read source directory entry")?;
        let from = entry.path();
        let to = target.join(entry.file_name());
        if from.is_dir() {
            copy_directory_recursive(&from, &to)?;
        } else {
            if let Some(parent) = to.parent() {
                fs::create_dir_all(parent).with_context(|| {
                    format!("could not create target parent {}", parent.display())
                })?;
            }
            fs::copy(&from, &to).with_context(|| {
                format!("could not copy {} to {}", from.display(), to.display())
            })?;
        }
    }
    Ok(())
}

fn load_manifest(path: &Path) -> anyhow::Result<RuntimeManifest> {
    let raw = fs::read_to_string(path)
        .with_context(|| format!("could not read local coach manifest at {}", path.display()))?;
    let mut manifest = serde_json::from_str::<RuntimeManifest>(&raw)
        .with_context(|| format!("could not parse local coach manifest at {}", path.display()))?;
    if manifest.context_size < MIN_CONTEXT_SIZE {
        manifest.context_size = DEFAULT_CONTEXT_SIZE.max(MIN_CONTEXT_SIZE);
    }
    Ok(manifest)
}

fn resolve_runtime_path(asset_root: &Path, relative: &str) -> PathBuf {
    let base = asset_root.join(relative);
    #[cfg(target_os = "windows")]
    {
        if base.is_file() {
            return base;
        }
        let exe_candidate = base.with_extension("exe");
        if exe_candidate.is_file() {
            return exe_candidate;
        }
    }
    base
}

fn resolve_data_path(asset_root: &Path, relative: &str) -> PathBuf {
    asset_root.join(relative)
}

fn build_runtime_status(
    app: &AppHandle,
    runtime_state: &Arc<Mutex<LocalLlmRuntimeState>>,
) -> anyhow::Result<LocalLlmRuntimeStatus> {
    let assets = match resolve_any_runtime_assets(app)? {
        Some(assets) => assets,
        None => {
            let install_root = installed_asset_root(app)?;
            return Ok(LocalLlmRuntimeStatus {
                state: "missing_assets".to_string(),
                detail: format!(
                    "AimMod local coach assets are not installed yet. Press Download assets in the Coaching tab, or ask the local coach and AimMod will try to install them automatically from {}.",
                    remote_manifest_url()
                ),
                can_start: true,
                asset_root: install_root.display().to_string(),
                manifest_path: install_root.join("manifest.json").display().to_string(),
                runner_path: install_root
                    .join("runner")
                    .join(if cfg!(target_os = "windows") {
                        "llama-server.exe"
                    } else {
                        "llama-server"
                    })
                    .display()
                    .to_string(),
                model_path: install_root
                    .join("models")
                    .join("aimmod-coach.gguf")
                    .display()
                    .to_string(),
                endpoint: format!("http://{}:{}/v1", DEFAULT_HOST, DEFAULT_PORT),
                model_id: String::new(),
                pid: None,
                launched_at_unix_ms: None,
                active_gpu_layers: 0,
            });
        }
    };
    let mut runtime = runtime_state
        .lock()
        .map_err(|_| anyhow!("local LLM runtime lock poisoned"))?;
    let alive = process_is_alive(&mut runtime)?;
    let runner_exists = assets.runner_path.is_file();
    let missing_runner_deps = missing_runner_dependencies(&assets);
    let model_exists = assets.model_path.is_file();
    let pid = runtime.child.as_ref().map(Child::id);
    let endpoint = runtime
        .endpoint
        .clone()
        .unwrap_or_else(|| assets.endpoint.clone());
    let model_id = runtime
        .model_id
        .clone()
        .or_else(|| {
            (!assets.manifest.model_id.trim().is_empty()).then(|| assets.manifest.model_id.clone())
        })
        .unwrap_or_default();

    let backend = detect_runner_backend(&assets);
    let gpu_runtime_hint = if runtime.active_gpu_layers > 0 && backend == "cpu" {
        Some("GPU layers are enabled in AimMod, but the installed llama.cpp runtime is CPU-only. Install a CUDA, Vulkan, SYCL, or HIP runtime package instead of the CPU package.".to_string())
    } else if runtime.active_gpu_layers > 0
        && backend == "cuda"
        && !bundled_cuda_runtime_present(&assets)
    {
        Some("CUDA backend detected, but bundled CUDA runtime DLLs were not found next to llama-server.exe. Install the companion CUDA DLL package or make sure the matching CUDA runtime is available on PATH.".to_string())
    } else {
        None
    };

    let (state, detail, can_start) = if !runner_exists || !model_exists {
        (
            "missing_assets".to_string(),
            format!(
                "Local coach assets are missing. Press Download assets in the Coaching tab and AimMod will install them into {} from {}.",
                assets.asset_root.display(),
                remote_manifest_url()
            ),
            true,
        )
    } else if !missing_runner_deps.is_empty() {
        (
            "missing_assets".to_string(),
            format_missing_runner_dependency_message(&assets, &missing_runner_deps),
            false,
        )
    } else if alive {
        (
            "ready".to_string(),
            match gpu_runtime_hint {
                Some(hint) => format!(
                    "AimMod local coach runtime is running. Backend: {}. {}",
                    backend, hint
                ),
                None => format!(
                    "AimMod local coach runtime is running. Backend: {}.",
                    backend
                ),
            },
            true,
        )
    } else if let Some(error) = runtime.last_error.clone() {
        (
            "error".to_string(),
            match gpu_runtime_hint {
                Some(hint) => format!("{}\n\n{}", error, hint),
                None => error,
            },
            true,
        )
    } else {
        (
            "stopped".to_string(),
            match gpu_runtime_hint {
                Some(hint) => format!(
                    "AimMod local coach runtime is installed but not started yet. Backend: {}. {}",
                    backend, hint
                ),
                None => format!(
                    "AimMod local coach runtime is installed but not started yet. Backend: {}.",
                    backend
                ),
            },
            true,
        )
    };

    Ok(LocalLlmRuntimeStatus {
        state,
        detail,
        can_start,
        asset_root: assets.asset_root.display().to_string(),
        manifest_path: assets.manifest_path.display().to_string(),
        runner_path: assets.runner_path.display().to_string(),
        model_path: assets.model_path.display().to_string(),
        endpoint,
        model_id,
        pid,
        launched_at_unix_ms: runtime.launched_at_unix_ms,
        active_gpu_layers: runtime.active_gpu_layers,
    })
}

fn missing_runner_dependencies(assets: &ResolvedRuntimeAssets) -> Vec<String> {
    required_runner_dependencies()
        .iter()
        .filter_map(|filename| {
            let candidate = assets.runner_dir.join(filename);
            if candidate.is_file() {
                None
            } else {
                Some((*filename).to_string())
            }
        })
        .collect()
}

fn detect_runner_backend(assets: &ResolvedRuntimeAssets) -> &'static str {
    let has = |name: &str| assets.runner_dir.join(name).is_file();
    if has("ggml-cuda.dll") {
        "cuda"
    } else if has("ggml-vulkan.dll") {
        "vulkan"
    } else if has("ggml-sycl.dll") {
        "sycl"
    } else if has("ggml-hip.dll") {
        "hip"
    } else {
        "cpu"
    }
}

fn bundled_cuda_runtime_present(assets: &ResolvedRuntimeAssets) -> bool {
    let Ok(entries) = fs::read_dir(&assets.runner_dir) else {
        return false;
    };
    entries.flatten().any(|entry| {
        entry
            .file_name()
            .to_str()
            .map(|name| {
                let lower = name.to_ascii_lowercase();
                lower.starts_with("cudart64_")
                    || lower.starts_with("cublas64_")
                    || lower.starts_with("cublaslt64_")
            })
            .unwrap_or(false)
    })
}

fn required_runner_dependencies() -> &'static [&'static str] {
    &["llama.dll", "ggml-base.dll", "ggml.dll", "mtmd.dll"]
}

fn format_missing_runner_dependency_message(
    assets: &ResolvedRuntimeAssets,
    missing: &[String],
) -> String {
    format!(
        "Local coach runtime is missing sidecar DLLs next to {}: {}. Reinstall the local coach assets from {} so AimMod can redownload the full llama.cpp runtime package. If it still fails after that, install the Microsoft Visual C++ 2015-2022 Redistributable because this build also links MSVCP140/VCRUNTIME/UCRT.",
        assets.runner_path.display(),
        missing.join(", "),
        remote_manifest_url()
    )
}

fn answer_direct_knowledge_question(
    request: &LocalCoachChatRequest,
    knowledge_items: &[LocalCoachKnowledgePreview],
) -> Option<String> {
    if knowledge_items.is_empty() {
        return None;
    }

    if is_identity_question(&request.question) {
        return Some(build_identity_response(&request.question, knowledge_items));
    }
    if is_scenario_context_question(&request.question) {
        return Some(build_scenario_context_response(request, knowledge_items));
    }

    None
}

fn is_identity_question(question: &str) -> bool {
    let normalized = question.trim().to_lowercase();
    normalized.starts_with("who is ") || normalized.starts_with("who's ")
}

fn is_scenario_context_question(question: &str) -> bool {
    let normalized = question.trim().to_lowercase();
    normalized.contains("context of")
        || (normalized.contains("what is") && normalized.contains("scenario"))
        || normalized.contains("about this scenario")
}

fn question_subject(question: &str) -> String {
    let normalized = question.trim().trim_end_matches('?').trim().to_lowercase();
    if let Some(rest) = normalized.strip_prefix("who is ") {
        return rest.trim_matches('\'').trim_matches('"').trim().to_string();
    }
    if let Some(rest) = normalized.strip_prefix("who's ") {
        return rest.trim_matches('\'').trim_matches('"').trim().to_string();
    }
    normalized
}

fn build_identity_response(
    question: &str,
    knowledge_items: &[LocalCoachKnowledgePreview],
) -> String {
    let subject = question_subject(question);
    let mut matching_sources = Vec::new();
    let mut all_sources = Vec::new();
    let mut seen = Vec::<String>::new();
    for item in knowledge_items {
        for source in &item.sources {
            let author = source.author.trim();
            let title = source.title.trim();
            let author_lower = author.to_lowercase();
            let title_lower = title.to_lowercase();
            let key = format!("{}::{}", author_lower, title_lower);
            if seen.iter().any(|existing| existing == &key) {
                continue;
            }
            seen.push(key);
            all_sources.push(source.clone());
            if !subject.is_empty()
                && (author_lower.contains(&subject) || title_lower.contains(&subject))
            {
                matching_sources.push(source.clone());
            }
        }
    }

    let mut lines = Vec::new();
    if matching_sources.is_empty() && all_sources.len() == 1 {
        matching_sources = all_sources;
    }

    if let Some(primary) = matching_sources.first() {
        let display_name = if primary.author.trim().is_empty() {
            primary.title.trim()
        } else {
            primary.author.trim()
        };
        lines.push(format!(
            "In the current AimMod knowledge base, **{}** is known as a coaching source author rather than a full biography entry.",
            display_name
        ));
        lines.push(String::new());
        lines.push("Right now the KB uses this source context:".to_string());
        for source in matching_sources.iter().take(2) {
            let mut line = format!("- **{}**", source.title.trim());
            if !source.author.trim().is_empty() {
                line.push_str(&format!(" by **{}**", source.author.trim()));
            }
            if !source.url.trim().is_empty() {
                line.push_str(&format!(" ({})", source.url.trim()));
            }
            lines.push(line);
        }
        lines.push(String::new());
        lines.push(
            "So I can tell you how AimMod is using that source in the coaching KB, but I should not guess a broader profile beyond that."
                .to_string(),
        );
    } else {
        lines.push(
            "I do not want to guess here. The current AimMod knowledge base does not have a grounded identity entry for that person yet."
                .to_string(),
        );
    }

    append_item_sources_line(&mut lines, knowledge_items, 3);
    lines.join("\n")
}

fn build_scenario_context_response(
    request: &LocalCoachChatRequest,
    knowledge_items: &[LocalCoachKnowledgePreview],
) -> String {
    let mut lines = vec![format!(
        "Here is the current AimMod context for **{}**.",
        request.scenario_name.trim()
    )];

    if !request.scenario_type.trim().is_empty() {
        lines.push(String::new());
        lines.push(format!(
            "- Scenario type: **{}**",
            request.scenario_type.trim()
        ));
    }
    if !request.scenario_summary.trim().is_empty() {
        lines.push(format!(
            "- Current app summary: {}",
            request.scenario_summary.trim()
        ));
    }
    if !knowledge_items.is_empty() {
        lines.push(String::new());
        lines.push("Best matching knowledge base context:".to_string());
        for item in knowledge_items.iter().take(2) {
            lines.push(format!(
                "- **{}**: {}",
                item.title.trim(),
                item.summary.trim()
            ));
        }
    }
    append_item_sources_line(&mut lines, knowledge_items, 3);
    lines.join("\n")
}

fn append_item_sources_line(
    lines: &mut Vec<String>,
    knowledge_items: &[LocalCoachKnowledgePreview],
    limit: usize,
) {
    let sources = knowledge_items
        .iter()
        .take(limit)
        .map(|item| format!("[{}] {}", item.id.trim(), item.title.trim()))
        .collect::<Vec<_>>()
        .join("; ");
    if !sources.is_empty() {
        lines.push(String::new());
        lines.push(format!("Sources: {}", sources));
    }
}

fn response_roleplays_tool_use(message: &str) -> bool {
    let normalized = message.trim().to_ascii_lowercase();
    if normalized.is_empty() {
        return false;
    }

    let direct_fake_action = [
        "please wait while i fetch",
        "please wait while we fetch",
        "fetching ",
        "creating a line chart",
        "creating a bar chart",
        "chart created successfully",
        "once i have that data",
        "once we have the data",
        "i need to fetch",
        "we need to fetch",
        "i need to query",
        "we need to query",
        "i need to look up",
        "we need to look up",
    ];

    if direct_fake_action
        .iter()
        .any(|phrase| normalized.contains(phrase))
    {
        return true;
    }

    let future_markers = [
        "let's fetch",
        "let us fetch",
        "i'll fetch",
        "i will fetch",
        "let's query",
        "i'll query",
        "i will query",
        "let's look up",
        "i'll look up",
        "i will look up",
        "i'll create a chart",
        "i will create a chart",
        "i can create a line chart",
        "i can create a bar chart",
    ];

    future_markers
        .iter()
        .any(|phrase| normalized.contains(phrase))
}

fn response_prints_pseudo_tool_call(message: &str) -> bool {
    let normalized = message.trim().to_ascii_lowercase();
    if normalized.is_empty() {
        return false;
    }

    const TOOL_NAMES: &[&str] = &[
        "get_scenario_context",
        "get_coach_facts",
        "query_coaching_knowledge",
        "get_player_stats",
        "get_recent_scenarios_played",
        "get_local_most_played_scenarios",
        "get_local_warmup_candidates",
        "get_scenario_runs",
        "get_local_scenario_aggregates",
        "get_local_scenario_type_aggregates",
        "get_local_activity_timeline",
        "create_visual",
    ];

    TOOL_NAMES.iter().any(|tool| {
        let bracketed = format!("[{}(", tool);
        let plain = format!("{}(", tool);
        let json_like = format!("\"{}\"", tool);
        normalized.contains(&bracketed)
            || normalized.starts_with(&plain)
            || normalized.contains(&format!("\n{}(", tool))
            || (normalized.contains("tool call") && normalized.contains(&json_like))
    })
}

fn parse_tool_error_message(result: &str) -> Option<String> {
    let Ok(value) = serde_json::from_str::<JsonValue>(result) else {
        return None;
    };
    let ok = value.get("ok").and_then(|candidate| candidate.as_bool());
    if ok == Some(false) {
        return value
            .get("error")
            .and_then(|candidate| candidate.as_str())
            .map(|error| error.trim().to_string())
            .filter(|error| !error.is_empty());
    }
    None
}

fn extract_visual_reference_ids(message: &str) -> Vec<String> {
    let mut refs = Vec::new();
    let bytes = message.as_bytes();
    let mut index = 0usize;
    while index < bytes.len() {
        let slice = &message[index..];
        let Some(start_rel) = slice.find("[[") else {
            break;
        };
        let start = index + start_rel + 2;
        let Some(end_rel) = message[start..].find("]]") else {
            break;
        };
        let end = start + end_rel;
        let mut token = message[start..end].trim().to_ascii_lowercase();
        if let Some(stripped) = token.strip_prefix("visual:") {
            token = stripped.trim().to_string();
        }
        if !token.is_empty() {
            refs.push(token);
        }
        index = end + 2;
    }
    refs
}

fn message_contains_visual_embed_token(message: &str) -> bool {
    message.contains("[[") && message.contains("]]")
}

fn describe_invalid_visual_output(
    message: &str,
    retrieved_knowledge: &RetrievedKnowledge,
) -> Option<String> {
    let referenced_ids = extract_visual_reference_ids(message);
    let available_ids = retrieved_knowledge
        .visuals
        .iter()
        .map(|visual| visual.id.trim().to_ascii_lowercase())
        .collect::<std::collections::HashSet<_>>();

    let unresolved_ids = referenced_ids
        .iter()
        .filter(|id| !available_ids.contains(*id))
        .cloned()
        .collect::<Vec<_>>();
    if !unresolved_ids.is_empty() {
        return Some(format!(
            "the reply referenced visual ids that do not exist: {}",
            unresolved_ids.join(", ")
        ));
    }

    let normalized = message.trim().to_ascii_lowercase();
    let claims_visual = normalized.contains("here is a visual")
        || normalized.contains("here is the visual")
        || normalized.contains("here is a chart")
        || normalized.contains("here are your charts")
        || normalized.contains("here are the charts")
        || normalized.contains("shown in the chart")
        || normalized.contains("the chart below")
        || normalized.contains("the visual below");

    if normalized.contains("![") {
        return Some(
            "the reply used markdown image syntax for a generated chart; generated charts must use [[visual:visual-id]] instead"
                .to_string(),
        );
    }

    if claims_visual && retrieved_knowledge.visuals.is_empty() {
        return Some(
            "the reply claimed a visual/chart existed, but no chart data had been created"
                .to_string(),
        );
    }

    if message_contains_visual_embed_token(message) && referenced_ids.is_empty() {
        return Some(
            "the reply used embed-style visual markup that did not reference any created visual id"
                .to_string(),
        );
    }

    None
}

fn build_system_prompt() -> String {
    [
        "You are AimMod Coach for KovaaK's and FPS mouse aim training.",
        "This is always about virtual aim practice, never real-world firearms.",
        "Use tools when they materially improve the answer; for greetings, answer directly.",
        "Do not narrate tool use. Either call a tool or answer.",
        "If the UI shows a current scenario, treat that as known.",
        "For knowledge questions, hub knowledge and hub answer planning are the source of truth.",
        "Only use facts from tools and retrieved knowledge. Do not invent scenario facts, metrics, or sensitivity numbers.",
        "If the KB is thin, say so instead of guessing.",
        "For visuals, first fetch a raw dataset tool, then call create_visual with datasetId, xKey, and series.",
        "Never call create_visual before a dataset tool has returned datasetId in the same answer flow.",
        "If a dataset tool returns recommendedCreateVisual, prefer using that plan directly for your next create_visual call.",
        "For time-based charts, use time buckets on the x-axis and metrics as series.",
        "Reference created visuals inline with exactly [[visual:visual-id]]. Never use markdown image syntax or fake embeds.",
        "If you need clarification, ask one short direct question.",
        "Answer the player's actual question directly and first.",
        "Setup questions: answer directly with a concrete recommendation.",
        "Performance questions: use short markdown with headings Diagnosis, Why, Next Session.",
        "Mixed questions: answer directly first, add context only if useful.",
        "If hub knowledge is present, end with: Sources: [item-id] Title; [item-id] Title.",
        "Only cite retrieved hub knowledge items.",
        "Keep answers concise.",
    ]
    .join(" ")
}

fn truncate_for_prompt(value: &str, max_chars: usize) -> String {
    let trimmed = value.trim();
    if trimmed.chars().count() <= max_chars {
        return trimmed.to_string();
    }
    let mut out = trimmed.chars().take(max_chars).collect::<String>();
    out.push('…');
    out
}

fn build_user_prompt(request: &LocalCoachChatRequest) -> String {
    let mut lines = Vec::new();
    lines.push(
        "Domain: AimMod / KovaaK's mouse aim training in a video-game aim trainer.".to_string(),
    );
    lines.push("Important: this is not about real-life shooting or firearms.".to_string());
    lines.push("Use scenario/drill language, not weapon handling language.".to_string());
    lines.push("Fetch extra context with tools instead of assuming it.".to_string());
    lines
        .push("If you create a visual, reference it inline with [[visual:visual-id]].".to_string());
    lines.push("Prefer dataset-backed visuals: after a raw data tool call, use the returned datasetId with create_visual instead of hand-writing chart points.".to_string());
    lines.push(
        "Do not call create_visual until a raw data tool has already returned datasetId."
            .to_string(),
    );
    lines.push("If the tool result includes recommendedCreateVisual, use that as your starting point instead of inventing your own chart payload.".to_string());
    lines.push(String::new());
    if !request.conversation_history.is_empty() {
        lines.push("Conversation so far:".to_string());
        for turn in request
            .conversation_history
            .iter()
            .rev()
            .take(2)
            .collect::<Vec<_>>()
            .into_iter()
            .rev()
        {
            lines.push(format!(
                "User: {}",
                truncate_for_prompt(&turn.question, 220)
            ));
            lines.push(format!("Coach: {}", truncate_for_prompt(&turn.answer, 320)));
        }
        lines.push(String::new());
        lines.push(format!(
            "Follow-up question: {}",
            truncate_for_prompt(&request.question, 260)
        ));
    } else {
        lines.push(format!(
            "Question: {}",
            truncate_for_prompt(&request.question, 260)
        ));
    }
    if !request.scenario_name.trim().is_empty() || !request.scenario_type.trim().is_empty() {
        lines.push(String::new());
        lines.push(format!(
            "Current UI anchor: scenario=\"{}\" type=\"{}\"",
            request.scenario_name.trim(),
            request.scenario_type.trim()
        ));
    }
    lines.join("\n")
}

fn finalize_grounded_response(
    request: &LocalCoachChatRequest,
    raw_message: &str,
    retrieved_knowledge: &RetrievedKnowledge,
) -> String {
    let trimmed = raw_message.trim();
    // Empty response: show the gap message.
    if trimmed.is_empty() {
        return build_knowledge_gap_response(request, retrieved_knowledge);
    }
    if retrieved_knowledge.answer_plan.must_answer_directly
        && !retrieved_knowledge.items.is_empty()
        && !response_is_grounded(trimmed, &retrieved_knowledge.items)
    {
        return build_knowledge_fallback_response(request, &retrieved_knowledge.items);
    }
    trimmed.to_string()
}

fn response_is_grounded(response: &str, knowledge_items: &[LocalCoachKnowledgePreview]) -> bool {
    let normalized = response.to_lowercase();
    if !normalized.contains("sources:") {
        return false;
    }

    knowledge_items.iter().any(|item| {
        let id = item.id.trim().to_lowercase();
        let title = item.title.trim().to_lowercase();
        (!id.is_empty() && normalized.contains(&id))
            || (!title.is_empty() && normalized.contains(&title))
    })
}

fn build_knowledge_gap_response(
    request: &LocalCoachChatRequest,
    retrieved_knowledge: &RetrievedKnowledge,
) -> String {
    if question_requests_visual(&request.question)
        && !retrieved_knowledge.datasets.is_empty()
        && let Some(response) = build_visual_fallback_response(request, retrieved_knowledge)
    {
        return response;
    }

    if retrieved_knowledge.items.is_empty()
        && (!retrieved_knowledge.answer_plan.primary_findings.is_empty()
            || !retrieved_knowledge.answer_plan.suggested_actions.is_empty())
    {
        return build_answer_plan_fallback_response(request, &retrieved_knowledge.answer_plan);
    }

    if retrieved_knowledge.items.is_empty() {
        let context_hint = if request.scenario_name.trim().is_empty() {
            "the general aim training knowledge base".to_string()
        } else {
            format!("**{}**", request.scenario_name.trim())
        };
        return format!(
            "I don't want to guess here. The current AimMod knowledge base does not have a matching entry for **{}** in {} yet.\n\nTry asking about a specific mechanic, training concept, or scenario category — once more KB entries are added this question will be answerable.",
            request.question.trim(),
            context_hint
        );
    }

    build_knowledge_fallback_response(request, &retrieved_knowledge.items)
}

fn question_requests_visual(question: &str) -> bool {
    let lower = question.trim().to_ascii_lowercase();
    lower.contains("chart")
        || lower.contains("visual")
        || lower.contains("graph")
        || lower.contains("plot")
}

fn metric_requested_in_question(question: &str, metric: &str) -> bool {
    let q = question.to_ascii_lowercase();
    match metric {
        "sessionCount" => q.contains("session"),
        "uniqueScenarioCount" => {
            q.contains("unique scenario")
                || q.contains("scenario count")
                || q.contains("unique scenarios")
        }
        "avgScore" => {
            q.contains("avg score") || q.contains("average score") || q.contains("score over")
        }
        "avgAccuracyPct" => {
            q.contains("avg accuracy")
                || q.contains("average accuracy")
                || q.contains("accuracy over")
                || q.contains("accuracy %")
        }
        "plays" => q.contains("plays") || q.contains("play count"),
        "bestScore" => q.contains("best score"),
        "bestAccuracyPct" => q.contains("best accuracy"),
        "score" => q.contains("score"),
        "accuracyPct" => q.contains("accuracy"),
        _ => false,
    }
}

fn default_metric_priority(dataset: &LocalCoachVisualDataset) -> Vec<String> {
    match dataset.source.as_str() {
        "local_activity_timeline" => vec![
            "sessionCount".to_string(),
            "uniqueScenarioCount".to_string(),
            "avgScore".to_string(),
            "avgAccuracyPct".to_string(),
        ],
        "local_scenario_type_aggregates" => vec![
            "plays".to_string(),
            "uniqueScenarioCount".to_string(),
            "avgScore".to_string(),
            "avgAccuracyPct".to_string(),
        ],
        "local_scenario_aggregates" => vec![
            "plays".to_string(),
            "avgScore".to_string(),
            "avgAccuracyPct".to_string(),
            "bestScore".to_string(),
        ],
        "scenario_runs" => vec![
            "score".to_string(),
            "accuracyPct".to_string(),
            "avgKps".to_string(),
            "avgTtkMs".to_string(),
        ],
        _ => dataset.available_metrics.clone(),
    }
}

fn build_dataset_fallback_series(
    request: &LocalCoachChatRequest,
    dataset: &LocalCoachVisualDataset,
) -> Vec<LocalCoachVisualSeries> {
    let mut selected = dataset
        .available_metrics
        .iter()
        .filter(|metric| metric_requested_in_question(&request.question, metric))
        .cloned()
        .collect::<Vec<_>>();

    if selected.is_empty() {
        for metric in default_metric_priority(dataset) {
            if dataset
                .available_metrics
                .iter()
                .any(|available| available == &metric)
            {
                selected.push(metric);
            }
            if selected.len() >= 4 {
                break;
            }
        }
    }

    if selected.is_empty() {
        selected = dataset
            .available_metrics
            .iter()
            .take(2)
            .cloned()
            .collect::<Vec<_>>();
    }

    let use_combo = question_prefers_time_axis(&request.question) && selected.len() > 1;
    selected
        .into_iter()
        .map(|metric| {
            let lower = metric.to_ascii_lowercase();
            let is_count_like = lower.contains("count") || lower == "plays";
            LocalCoachVisualSeries {
                key: metric.clone(),
                label: metric,
                kind: if use_combo && is_count_like {
                    "bar".to_string()
                } else {
                    "line".to_string()
                },
                color: String::new(),
            }
        })
        .collect::<Vec<_>>()
}

fn humanize_metric_label(metric: &str) -> String {
    match metric {
        "sessionCount" => "Sessions".to_string(),
        "uniqueScenarioCount" => "Unique Scenarios".to_string(),
        "avgScore" => "Avg Score".to_string(),
        "avgAccuracyPct" => "Avg Accuracy %".to_string(),
        "plays" => "Plays".to_string(),
        "bestScore" => "Best Score".to_string(),
        "bestAccuracyPct" => "Best Accuracy %".to_string(),
        "accuracyPct" => "Accuracy %".to_string(),
        "avgAccuracyTrend" => "Avg Accuracy Trend".to_string(),
        "avgKps" => "Avg KPS".to_string(),
        "avgTtkMs" => "Avg TTK ms".to_string(),
        "bestTtkMs" => "Best TTK ms".to_string(),
        "avgSmoothnessComposite" => "Avg Smoothness".to_string(),
        "avgJitter" => "Avg Jitter".to_string(),
        "avgCorrectionRatio" => "Avg Correction Ratio".to_string(),
        "avgDirectionalBias" => "Avg Directional Bias".to_string(),
        "avgShotsToHit" => "Avg Shots To Hit".to_string(),
        "avgFireToHitMs" => "Avg Fire To Hit ms".to_string(),
        other => other.to_string(),
    }
}

fn build_dataset_visual_fallback(
    request: &LocalCoachChatRequest,
    retrieved_knowledge: &RetrievedKnowledge,
) -> Option<LocalCoachVisual> {
    if !question_requests_visual(&request.question) {
        return None;
    }
    let dataset = retrieved_knowledge.datasets.last()?;
    let series = build_dataset_fallback_series(request, dataset);
    if series.is_empty() {
        return None;
    }
    let points = build_points_from_dataset(dataset, &dataset.default_x_key, &series).ok()?;
    let title = if request.question.trim().is_empty() {
        "Local coach chart".to_string()
    } else {
        truncate_for_prompt(request.question.trim(), 90)
    };
    let subtitle = format!(
        "Built from local {} data using {}.",
        dataset.source.replace('_', " "),
        dataset.default_x_key
    );
    let detail_lines = vec![format!(
        "Metrics shown: {}.",
        series
            .iter()
            .map(|entry| humanize_metric_label(&entry.key))
            .collect::<Vec<_>>()
            .join(", ")
    )];

    Some(LocalCoachVisual {
        id: normalize_visual_id(
            &format!("fallback-{}-{}", dataset.source, dataset.id),
            &title,
        ),
        kind: if question_prefers_time_axis(&request.question) && series.len() > 1 {
            "combo".to_string()
        } else if dataset.default_x_key == "day"
            || dataset.default_x_key == "timestamp"
            || dataset.default_x_key == "index"
        {
            "line".to_string()
        } else {
            "bar".to_string()
        },
        title,
        subtitle,
        primary_label: humanize_metric_label(&series[0].key),
        secondary_label: series
            .get(1)
            .map(|entry| humanize_metric_label(&entry.key))
            .unwrap_or_default(),
        points,
        series,
        detail_lines,
    })
}

fn ensure_visual_fallback(
    request: &LocalCoachChatRequest,
    retrieved_knowledge: &mut RetrievedKnowledge,
) {
    if !retrieved_knowledge.visuals.is_empty() {
        return;
    }
    if let Some(visual) = build_dataset_visual_fallback(request, retrieved_knowledge) {
        upsert_visual(&mut retrieved_knowledge.visuals, visual);
    }
}

fn build_visual_fallback_response(
    request: &LocalCoachChatRequest,
    retrieved_knowledge: &RetrievedKnowledge,
) -> Option<String> {
    let visuals = if retrieved_knowledge.visuals.is_empty() {
        build_dataset_visual_fallback(request, retrieved_knowledge)
            .map(|visual| vec![visual])
            .unwrap_or_default()
    } else {
        retrieved_knowledge.visuals.clone()
    };

    if visuals.is_empty() {
        return None;
    }

    let mut lines = if request.question.trim().is_empty() {
        vec!["I pulled together these visuals from your local AimMod data:".to_string()]
    } else {
        vec![format!(
            "I pulled together these visuals for **{}**:",
            request.question.trim()
        )]
    };
    lines.push(String::new());

    for visual in visuals.iter().take(4) {
        lines.push(format!("[[visual:{}]]", visual.id.trim()));
        if !visual.subtitle.trim().is_empty() {
            lines.push(visual.subtitle.trim().to_string());
        }
        if let Some(detail) = visual.detail_lines.first() {
            lines.push(detail.trim().to_string());
        }
        lines.push(String::new());
    }

    lines.push(
        "I can break down what each one means or zoom into a narrower slice next.".to_string(),
    );

    Some(lines.join("\n").trim().to_string())
}

fn build_answer_plan_fallback_response(
    request: &LocalCoachChatRequest,
    answer_plan: &LocalCoachAnswerPlan,
) -> String {
    let mut lines = vec![
        "I don't want to improvise here, so I'm grounding this answer in the current hub coaching plan built from your local coach facts.".to_string(),
        String::new(),
    ];

    let direct_intents = [
        "setup",
        "identity",
        "scenario_context",
        "scenario_recommendation",
    ];
    if direct_intents
        .iter()
        .any(|intent| intent.eq_ignore_ascii_case(answer_plan.intent.trim()))
    {
        lines.push(format!("For **{}**:", request.question.trim()));
    } else {
        lines.push("## Diagnosis".to_string());
    }

    if !answer_plan.primary_findings.is_empty() {
        for finding in answer_plan.primary_findings.iter().take(3) {
            lines.push(format!("- {}", finding.trim()));
        }
    } else {
        lines.push(
            "The hub has a response shape for this question, but not enough matching findings yet to answer more specifically."
                .to_string(),
        );
    }

    if !answer_plan.suggested_actions.is_empty() {
        lines.push(String::new());
        if direct_intents
            .iter()
            .any(|intent| intent.eq_ignore_ascii_case(answer_plan.intent.trim()))
        {
            lines.push("Best next moves:".to_string());
        } else {
            lines.push("## Next Session".to_string());
        }
        for action in answer_plan.suggested_actions.iter().take(3) {
            lines.push(format!("- {}", action.trim()));
        }
    }

    if !answer_plan.clarifying_question.trim().is_empty() {
        lines.push(String::new());
        lines.push(format!(
            "If you want a tighter answer: {}",
            answer_plan.clarifying_question.trim()
        ));
    }

    lines.join("\n")
}

fn build_knowledge_fallback_response(
    request: &LocalCoachChatRequest,
    knowledge_items: &[LocalCoachKnowledgePreview],
) -> String {
    let context_label = if request.scenario_name.trim().is_empty() {
        "general aim training".to_string()
    } else {
        format!("**{}**", request.scenario_name.trim())
    };
    let mut lines = vec![
        "I don't want to improvise here, so I'm grounding this answer directly in the current AimMod knowledge base.".to_string(),
        String::new(),
        format!(
            "For **{}** ({context_label}), the best matching knowledge says:",
            request.question.trim(),
        ),
    ];

    for item in knowledge_items.iter().take(2) {
        lines.push(format!(
            "- **{}**: {}",
            item.title.trim(),
            item.summary.trim()
        ));
        if let Some(why) = item.why.first() {
            lines.push(format!("  Why: {}", why.trim()));
        }
        for action in item.actions.iter().take(2) {
            lines.push(format!("  Do: {}", action.trim()));
        }
        if let Some(avoid) = item.avoid.first() {
            lines.push(format!("  Avoid: {}", avoid.trim()));
        }
        if let Some(drill) = item.drills.first() {
            let label = drill.label.trim();
            let reason = drill.reason.trim();
            let query = drill.query.trim();
            if !label.is_empty() && !reason.is_empty() {
                lines.push(format!("  Drill: **{}** — {}", label, reason));
            } else if !label.is_empty() {
                lines.push(format!("  Drill: **{}**", label));
            } else if !query.is_empty() {
                lines.push(format!("  Drill query: {}", query));
            }
        }
    }

    let sources = knowledge_items
        .iter()
        .take(3)
        .map(|item| format!("[{}] {}", item.id.trim(), item.title.trim()))
        .collect::<Vec<_>>()
        .join("; ");
    if !sources.is_empty() {
        lines.push(String::new());
        lines.push(format!("Sources: {}", sources));
    }

    lines.join("\n")
}

fn now_unix_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_else(|_| Duration::from_secs(0))
        .as_millis() as u64
}
