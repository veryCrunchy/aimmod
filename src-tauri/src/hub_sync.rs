use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use anyhow::Context;
use chrono::{Local, NaiveDateTime, TimeZone, Utc};
use once_cell::sync::Lazy;
use parking_lot::Mutex;
use reqwest::Client;
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, Manager};

use crate::bridge::{BridgeRunSnapshot, BridgeRunTimelinePoint};
use crate::file_watcher::SessionResult;
use crate::session_store::{ShotTimingSnapshot, SmoothnessSnapshot, StatsPanelSnapshot};

pub const HUB_SCHEMA_VERSION: u32 = 11;
pub const HUB_REPLAY_MEDIA_SCHEMA_VERSION: u32 = 3;
pub const HUB_MOUSE_PATH_SCHEMA_VERSION: u32 = 2;
const CONNECT_PROTOCOL_VERSION: &str = "1";
const INGEST_PATH: &str = "/aimmod.hub.v1.HubService/IngestSession";
const BATCH_INGEST_PATH: &str = "/ingest/batch";
const REPLAY_MEDIA_UPLOAD_PATH: &str = "/media/replays/upload";
const REPLAY_MOUSE_PATH_UPLOAD_PATH: &str = "/replays/mouse-path/upload";
const BATCH_SYNC_CHUNK_SIZE: usize = 25;
pub const HUB_SYNC_STATUS_EVENT: &str = "hub-sync-status";

static CLIENT: Lazy<Client> = Lazy::new(|| {
    Client::builder()
        .timeout(Duration::from_secs(20))
        .http1_only()
        .build()
        .expect("failed to build AimMod Hub client")
});
static PENDING_SYNC_RUNNING: AtomicBool = AtomicBool::new(false);
static MOUSE_PATH_SYNC_RUNNING: AtomicBool = AtomicBool::new(false);
static SYNC_STATUS: Lazy<Mutex<HubSyncStatus>> = Lazy::new(|| {
    Mutex::new(HubSyncStatus {
        sync_in_progress: false,
        pending_count: 0,
        last_success_at_unix_ms: None,
        last_error: None,
        last_error_at_unix_ms: None,
        last_uploaded_session_id: None,
        last_replay_media_upload_at_unix_ms: None,
        last_replay_media_error: None,
        last_replay_media_error_at_unix_ms: None,
        last_replay_media_session_id: None,
    })
});
static UPLOAD_IDENTITY_CACHE: Lazy<Mutex<HashMap<String, UploadIdentityCacheEntry>>> =
    Lazy::new(|| Mutex::new(HashMap::new()));
static AUX_UPLOAD_SEMAPHORE: Lazy<Arc<tokio::sync::Semaphore>> =
    Lazy::new(|| Arc::new(tokio::sync::Semaphore::new(2)));

#[derive(Clone)]
pub struct SessionUploadInput {
    pub session_id: String,
    pub result: SessionResult,
    pub smoothness: Option<SmoothnessSnapshot>,
    pub stats_panel: Option<StatsPanelSnapshot>,
    pub shot_timing: Option<ShotTimingSnapshot>,
    pub run_snapshot: Option<BridgeRunSnapshot>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HubSyncStatus {
    pub sync_in_progress: bool,
    pub pending_count: usize,
    pub last_success_at_unix_ms: Option<u64>,
    pub last_error: Option<String>,
    pub last_error_at_unix_ms: Option<u64>,
    pub last_uploaded_session_id: Option<String>,
    pub last_replay_media_upload_at_unix_ms: Option<u64>,
    pub last_replay_media_error: Option<String>,
    pub last_replay_media_error_at_unix_ms: Option<u64>,
    pub last_replay_media_session_id: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HubSyncOverview {
    pub configured: bool,
    pub enabled: bool,
    pub account_label: Option<String>,
    pub status: HubSyncStatus,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceLinkStartResponse {
    device_code: String,
    user_code: String,
    verification_uri: String,
    verification_uri_complete: String,
    expires_in: i64,
    interval: i64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceLinkPollResponse {
    status: String,
    user: Option<DeviceLinkUser>,
    upload_token: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceLinkUser {
    username: String,
    display_name: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HubDeviceLinkSession {
    pub device_code: String,
    pub user_code: String,
    pub verification_uri: String,
    pub verification_uri_complete: String,
    pub expires_in_secs: u64,
    pub interval_secs: u64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HubDeviceLinkPollStatus {
    pub status: String,
    pub account_label: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DeviceLinkStartRequest {
    label: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DeviceLinkPollRequest {
    device_code: String,
}

fn now_unix_ms() -> u64 {
    Utc::now().timestamp_millis().max(0) as u64
}

fn build_sync_overview(app: &AppHandle, refresh_pending: bool) -> anyhow::Result<HubSyncOverview> {
    let settings = crate::settings::load(app)?;
    if refresh_pending {
        let pending_count = crate::session_store::get_pending_hub_upload_page(app, 0, 1).total;
        SYNC_STATUS.lock().pending_count = pending_count;
    }
    let status = SYNC_STATUS.lock().clone();
    let discovered_identity = derive_bridge_upload_identity();
    let can_upload_without_link = discovered_identity.is_some();
    let account_label = if settings.hub_account_label.trim().is_empty() {
        discovered_identity.as_ref().and_then(|identity| {
            let label = first_nonempty_owned(&[
                identity.user_display_name.as_str(),
                identity.kovaaks_username.as_str(),
                identity.steam_display_name.as_str(),
                identity.steam_id.as_str(),
            ]);
            (!label.is_empty()).then_some(label)
        })
    } else {
        Some(settings.hub_account_label.trim().to_string())
    };
    Ok(HubSyncOverview {
        configured: !normalize_base_url(&settings.hub_api_base_url).is_empty()
            && (!settings.hub_upload_token.trim().is_empty() || can_upload_without_link),
        enabled: settings.hub_sync_enabled,
        account_label,
        status,
    })
}

fn emit_sync_status(app: &AppHandle) {
    match build_sync_overview(app, false) {
        Ok(overview) => {
            let _ = app.emit(HUB_SYNC_STATUS_EVENT, &overview);
        }
        Err(error) => {
            log::debug!("hub_sync: could not emit sync status update: {error}");
        }
    }
}

fn set_sync_in_progress(app: &AppHandle, value: bool) {
    SYNC_STATUS.lock().sync_in_progress = value;
    emit_sync_status(app);
}

fn record_sync_success(app: &AppHandle, session_id: Option<&str>) {
    let mut status = SYNC_STATUS.lock();
    status.sync_in_progress = false;
    status.last_success_at_unix_ms = Some(now_unix_ms());
    status.last_error = None;
    status.last_error_at_unix_ms = None;
    status.last_uploaded_session_id = session_id.map(ToOwned::to_owned);
    drop(status);
    emit_sync_status(app);
}

fn record_sync_error(app: &AppHandle, message: impl Into<String>) {
    let mut status = SYNC_STATUS.lock();
    status.sync_in_progress = false;
    status.last_error = Some(message.into());
    status.last_error_at_unix_ms = Some(now_unix_ms());
    drop(status);
    emit_sync_status(app);
}

fn record_replay_media_upload_success(app: &AppHandle, session_id: &str) {
    let mut status = SYNC_STATUS.lock();
    status.last_replay_media_upload_at_unix_ms = Some(now_unix_ms());
    status.last_replay_media_error = None;
    status.last_replay_media_error_at_unix_ms = None;
    status.last_replay_media_session_id = Some(session_id.to_string());
    drop(status);
    emit_sync_status(app);
}

fn record_replay_media_upload_error(app: &AppHandle, session_id: &str, message: impl Into<String>) {
    let mut status = SYNC_STATUS.lock();
    status.last_replay_media_error = Some(message.into());
    status.last_replay_media_error_at_unix_ms = Some(now_unix_ms());
    status.last_replay_media_session_id = Some(session_id.to_string());
    drop(status);
    emit_sync_status(app);
}

fn verbose_transport_error(error: &anyhow::Error) -> String {
    format!("{error:#}")
}

fn record_pending_upload_failure(app: &AppHandle, session_id: &str, message: &str) {
    crate::session_store::mark_session_hub_upload_failed(app, session_id, message);
}

fn refresh_pending_count(app: &AppHandle) {
    let pending_count = crate::session_store::get_pending_hub_upload_page(app, 0, 1).total;
    SYNC_STATUS.lock().pending_count = pending_count;
    emit_sync_status(app);
}

fn first_nonempty_owned(candidates: &[&str]) -> String {
    for candidate in candidates {
        let trimmed = candidate.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }
    String::new()
}

fn looks_like_steam_id(value: &str) -> bool {
    let trimmed = value.trim();
    trimmed.len() >= 17 && trimmed.bytes().all(|byte| byte.is_ascii_digit())
}

fn build_upload_external_id(
    kovaaks_user_id: &str,
    kovaaks_username: &str,
    steam_id: &str,
) -> String {
    let kovaaks_user_id = kovaaks_user_id.trim();
    let kovaaks_username = kovaaks_username.trim();
    let steam_id = steam_id.trim();

    if !kovaaks_user_id.is_empty() {
        format!("kovaaks:{kovaaks_user_id}")
    } else if !kovaaks_username.is_empty() {
        format!("kovaaks:username:{}", kovaaks_username.to_ascii_lowercase())
    } else if !steam_id.is_empty() {
        format!("steam:{steam_id}")
    } else {
        String::new()
    }
}

fn derive_bridge_upload_identity() -> Option<UploadIdentity> {
    let user = crate::bridge::current_kovaaks_user()?;
    let kovaaks_user_id = user.kovaaks_user_id.trim().to_string();
    let kovaaks_username = user.username.trim().to_string();
    let steam_id = user.steam_id.trim().to_string();
    if kovaaks_user_id.is_empty() && kovaaks_username.is_empty() && steam_id.is_empty() {
        return None;
    }

    let user_external_id = build_upload_external_id(&kovaaks_user_id, &kovaaks_username, &steam_id);

    Some(UploadIdentity {
        user_external_id,
        kovaaks_user_id,
        kovaaks_username: kovaaks_username.clone(),
        user_display_name: first_nonempty_owned(&[
            user.display_name.as_str(),
            user.steam_name.as_str(),
            kovaaks_username.as_str(),
        ]),
        avatar_url: user.avatar_url.trim().to_string(),
        steam_id,
        steam_display_name: first_nonempty_owned(&[
            user.steam_name.as_str(),
            user.display_name.as_str(),
        ]),
    })
}

fn derive_upload_identity_from_profile(
    profile: &crate::settings::FriendProfile,
) -> Option<UploadIdentity> {
    let steam_id = profile.steam_id.trim().to_string();
    let kovaaks_username = if profile.bridge_managed {
        let username = profile.username.trim();
        if username.is_empty() || looks_like_steam_id(username) {
            String::new()
        } else {
            username.to_string()
        }
    } else {
        String::new()
    };

    if steam_id.is_empty() && kovaaks_username.is_empty() {
        return None;
    }

    let user_display_name = first_nonempty_owned(&[
        profile.steam_account_name.as_str(),
        profile.username.as_str(),
        steam_id.as_str(),
    ]);
    let steam_display_name = first_nonempty_owned(&[
        profile.steam_account_name.as_str(),
        user_display_name.as_str(),
        steam_id.as_str(),
    ]);

    Some(UploadIdentity {
        user_external_id: build_upload_external_id("", &kovaaks_username, &steam_id),
        kovaaks_user_id: String::new(),
        kovaaks_username,
        user_display_name,
        avatar_url: profile.avatar_url.trim().to_string(),
        steam_id,
        steam_display_name,
    })
}

async fn resolve_upload_identity() -> Option<UploadIdentity> {
    let Some(mut identity) = derive_bridge_upload_identity() else {
        let profile = crate::resolve_current_identity_profile().await?;
        return derive_upload_identity_from_profile(&profile);
    };
    let steam_id = identity.steam_id.trim().to_string();
    let needs_enrichment = identity.kovaaks_username.trim().is_empty()
        || looks_like_steam_id(&identity.kovaaks_username);

    if steam_id.is_empty() || !needs_enrichment {
        return Some(identity);
    }

    let display_name = first_nonempty_owned(&[
        identity.steam_display_name.as_str(),
        identity.user_display_name.as_str(),
        identity.kovaaks_username.as_str(),
        steam_id.as_str(),
    ]);
    let ttl = Duration::from_secs(15 * 60);
    let now = Instant::now();

    let cached_profile = {
        let cache = UPLOAD_IDENTITY_CACHE.lock();
        cache.get(&steam_id).and_then(|entry| {
            if entry.display_name == display_name && now.duration_since(entry.checked_at) < ttl {
                entry.profile.clone()
            } else {
                None
            }
        })
    };

    let profile = if let Some(profile) = cached_profile {
        Some(profile)
    } else {
        match crate::kovaaks_api::find_user_by_steam_id(&steam_id, &display_name).await {
            Ok(profile) => {
                let mut cache = UPLOAD_IDENTITY_CACHE.lock();
                cache.insert(
                    steam_id.clone(),
                    UploadIdentityCacheEntry {
                        display_name: display_name.clone(),
                        checked_at: now,
                        profile: profile.clone(),
                    },
                );
                profile
            }
            Err(err) => {
                log::warn!(
                    "hub_sync: failed to enrich upload identity steam_id='{}' display='{}': {}",
                    steam_id,
                    display_name,
                    err
                );
                None
            }
        }
    };

    if let Some(profile) = profile {
        identity.kovaaks_username = first_nonempty_owned(&[
            profile.username.as_str(),
            identity.kovaaks_username.as_str(),
        ]);
        identity.user_display_name = first_nonempty_owned(&[
            identity.user_display_name.as_str(),
            profile.username.as_str(),
            profile.steam_account_name.as_str(),
            display_name.as_str(),
        ]);
        identity.avatar_url =
            first_nonempty_owned(&[identity.avatar_url.as_str(), profile.avatar_url.as_str()]);
        identity.steam_display_name = first_nonempty_owned(&[
            identity.steam_display_name.as_str(),
            profile.steam_account_name.as_str(),
            display_name.as_str(),
        ]);
        identity.user_external_id = build_upload_external_id(
            &identity.kovaaks_user_id,
            &identity.kovaaks_username,
            &identity.steam_id,
        );
    }

    Some(identity)
}

fn log_upload_identity(context: &str, upload_identity: Option<&UploadIdentity>) {
    match upload_identity {
        Some(identity) => log::info!(
            "hub_sync: {} identity external_id='{}' steam_id='{}' steam_display='{}' kovaaks_username='{}' kovaaks_user_id='{}'",
            context,
            identity.user_external_id,
            identity.steam_id,
            identity.steam_display_name,
            identity.kovaaks_username,
            identity.kovaaks_user_id,
        ),
        None => log::info!("hub_sync: {} identity unavailable", context),
    }
}

pub fn get_sync_overview(app: &AppHandle) -> anyhow::Result<HubSyncOverview> {
    build_sync_overview(app, true)
}

fn open_external_url(url: &str) -> anyhow::Result<()> {
    #[cfg(target_os = "windows")]
    {
        std::process::Command::new("cmd")
            .args(["/C", "start", "", url])
            .spawn()?;
        return Ok(());
    }
    #[cfg(target_os = "macos")]
    {
        std::process::Command::new("open").arg(url).spawn()?;
        return Ok(());
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        std::process::Command::new("xdg-open").arg(url).spawn()?;
        return Ok(());
    }
    #[cfg(not(any(
        target_os = "windows",
        target_os = "macos",
        all(unix, not(target_os = "macos"))
    )))]
    {
        anyhow::bail!("opening a browser is not supported on this platform")
    }
}

pub async fn start_device_link(
    app: &AppHandle,
    base_url: Option<String>,
) -> anyhow::Result<HubDeviceLinkSession> {
    let settings = crate::settings::load(app)?;
    let base_url = normalize_base_url(base_url.as_deref().unwrap_or(&settings.hub_api_base_url));
    if base_url.is_empty() {
        anyhow::bail!("AimMod Hub API base URL is required");
    }

    let response = CLIENT
        .post(format!("{base_url}/auth/device/start"))
        .header("Content-Type", "application/json")
        .json(&DeviceLinkStartRequest {
            label: "AimMod desktop".to_string(),
        })
        .send()
        .await
        .with_context(|| format!("error sending request for url ({base_url}/auth/device/start)"))?;

    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        anyhow::bail!("hub device start returned {status}: {body}");
    }

    let payload = response.json::<DeviceLinkStartResponse>().await?;
    open_external_url(&payload.verification_uri_complete)?;

    Ok(HubDeviceLinkSession {
        device_code: payload.device_code,
        user_code: payload.user_code,
        verification_uri: payload.verification_uri,
        verification_uri_complete: payload.verification_uri_complete,
        expires_in_secs: payload.expires_in.max(0) as u64,
        interval_secs: payload.interval.max(1) as u64,
    })
}

pub async fn poll_device_link(
    app: &AppHandle,
    base_url: Option<String>,
    device_code: String,
) -> anyhow::Result<HubDeviceLinkPollStatus> {
    let settings = crate::settings::load(app)?;
    let base_url = normalize_base_url(base_url.as_deref().unwrap_or(&settings.hub_api_base_url));
    if base_url.is_empty() {
        anyhow::bail!("AimMod Hub API base URL is required");
    }

    let response = CLIENT
        .post(format!("{base_url}/auth/device/poll"))
        .header("Content-Type", "application/json")
        .json(&DeviceLinkPollRequest { device_code })
        .send()
        .await
        .with_context(|| format!("error sending request for url ({base_url}/auth/device/poll)"))?;

    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        anyhow::bail!("hub device poll returned {status}: {body}");
    }

    let payload = response.json::<DeviceLinkPollResponse>().await?;
    if payload.status == "approved"
        && payload
            .upload_token
            .as_deref()
            .is_some_and(|token| !token.trim().is_empty())
    {
        let mut next_settings = settings.clone();
        next_settings.hub_api_base_url = base_url;
        next_settings.hub_upload_token = payload.upload_token.clone().unwrap_or_default();
        next_settings.hub_sync_enabled = true;
        next_settings.hub_account_label = payload
            .user
            .as_ref()
            .map(|user| {
                let display = user.display_name.trim();
                if !display.is_empty() {
                    display.to_string()
                } else {
                    user.username.trim().to_string()
                }
            })
            .unwrap_or_default();
        crate::settings::persist(app, &next_settings)?;
        if let Ok(mut locked) = app.state::<crate::AppState>().settings.lock() {
            *locked = next_settings;
        }
        queue_pending_session_sync(app);
        queue_pending_mouse_path_sync(app);
    }

    Ok(HubDeviceLinkPollStatus {
        status: payload.status,
        account_label: payload.user.map(|user| {
            let display = user.display_name.trim();
            if !display.is_empty() {
                display.to_string()
            } else {
                user.username
            }
        }),
    })
}

pub fn clear_linked_account(app: &AppHandle) -> anyhow::Result<()> {
    let mut settings = crate::settings::load(app)?;
    settings.hub_upload_token.clear();
    settings.hub_account_label.clear();
    settings.hub_sync_enabled = false;
    crate::settings::persist(app, &settings)?;
    if let Ok(mut locked) = app.state::<crate::AppState>().settings.lock() {
        *locked = settings;
    }
    refresh_pending_count(app);
    Ok(())
}

pub fn force_full_resync(app: &AppHandle) -> anyhow::Result<()> {
    match crate::stats_db::backfill_session_classifications(app) {
        Ok(updated) if updated > 0 => {
            log::info!(
                "hub_sync: backfilled stored scenario classification for {} session(s) before full resync",
                updated
            );
        }
        Ok(_) => {}
        Err(error) => {
            log::warn!(
                "hub_sync: stored scenario classification backfill failed before full resync: {error}"
            );
        }
    }
    crate::session_store::reset_all_hub_upload_marks(app);
    if let Err(error) = crate::stats_db::clear_replay_media_upload_marks(app) {
        log::warn!(
            "hub_sync: could not clear replay media upload marks before full resync: {error}"
        );
    }
    if let Err(error) = crate::stats_db::clear_mouse_path_upload_marks(app) {
        log::warn!("hub_sync: could not clear mouse path upload marks before full resync: {error}");
    }
    refresh_pending_count(app);
    queue_pending_session_sync(app);
    queue_pending_mouse_path_sync(app);
    Ok(())
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct IngestSessionRequest {
    app_version: String,
    schema_version: u32,
    user_external_id: String,
    kovaaks_user_id: String,
    kovaaks_username: String,
    user_display_name: String,
    avatar_url: String,
    steam_id: String,
    steam_display_name: String,
    session_id: String,
    scenario_name: String,
    scenario_type: String,
    score: f64,
    accuracy: f64,
    duration_ms: u64,
    played_at_iso: String,
    summary: HashMap<String, SessionSummaryValue>,
    feature_set: HashMap<String, SessionSummaryValue>,
    timeline_seconds: Vec<TimelineSecondPayload>,
    context_windows: Vec<ContextWindowPayload>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct IngestBatchRequest {
    sessions: Vec<IngestSessionRequest>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct IngestBatchResponse {
    #[serde(default)]
    uploaded_session_ids: Vec<String>,
    #[serde(default)]
    failures: Vec<IngestBatchFailure>,
    #[serde(default)]
    uploaded_count: usize,
    #[serde(default)]
    failed_count: usize,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct IngestBatchFailure {
    session_id: String,
    message: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionSummaryValue {
    #[serde(skip_serializing_if = "Option::is_none")]
    string_value: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    number_value: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    bool_value: Option<bool>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct TimelineSecondPayload {
    t_sec: u32,
    score: f64,
    accuracy: f64,
    damage_eff: f64,
    spm: f64,
    shots: u32,
    hits: u32,
    kills: u32,
    paused: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ContextWindowPayload {
    start_ms: u64,
    end_ms: u64,
    window_type: String,
    label: String,
    feature_summary: HashMap<String, SessionSummaryValue>,
    coaching_tags: Vec<String>,
}

#[derive(Clone, Debug, Default)]
struct UploadIdentity {
    user_external_id: String,
    kovaaks_user_id: String,
    kovaaks_username: String,
    user_display_name: String,
    avatar_url: String,
    steam_id: String,
    steam_display_name: String,
}

#[derive(Clone, Debug)]
struct UploadIdentityCacheEntry {
    display_name: String,
    checked_at: Instant,
    profile: Option<crate::kovaaks_api::UserProfile>,
}

pub fn queue_session_upload(app: &AppHandle, input: SessionUploadInput) {
    set_sync_in_progress(app, true);
    let session_id = input.session_id.clone();
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        if let Err(error) = upload_session(&app, input).await {
            record_pending_upload_failure(&app, &session_id, &error.to_string());
            record_sync_error(&app, error.to_string());
            log::warn!("hub_sync: upload failed: {error}");
        }
        refresh_pending_count(&app);
    });
}

pub fn queue_pending_session_sync(app: &AppHandle) {
    let settings = match crate::settings::load(app) {
        Ok(settings) => settings,
        Err(error) => {
            log::warn!("hub_sync: could not load settings for pending sync check: {error}");
            return;
        }
    };

    if !settings.hub_sync_enabled {
        return;
    }

    if normalize_base_url(&settings.hub_api_base_url).is_empty() {
        return;
    }

    if PENDING_SYNC_RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }

    set_sync_in_progress(app, true);
    refresh_pending_count(app);
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let result = sync_pending_sessions(&app).await;
        PENDING_SYNC_RUNNING.store(false, Ordering::SeqCst);
        if let Err(error) = result {
            record_sync_error(&app, error.to_string());
            log::warn!("hub_sync: pending sync failed: {error}");
        }
        refresh_pending_count(&app);
    });
}

pub fn queue_pending_mouse_path_sync(app: &AppHandle) {
    let settings = match crate::settings::load(app) {
        Ok(settings) => settings,
        Err(error) => {
            log::warn!("hub_sync: could not load settings for mouse path sync check: {error}");
            return;
        }
    };

    if !settings.hub_sync_enabled {
        return;
    }

    if normalize_base_url(&settings.hub_api_base_url).is_empty() {
        return;
    }

    if settings.hub_upload_token.trim().is_empty() {
        return;
    }

    if MOUSE_PATH_SYNC_RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }

    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let result = sync_pending_mouse_paths(&app).await;
        MOUSE_PATH_SYNC_RUNNING.store(false, Ordering::SeqCst);
        if let Err(error) = result {
            log::warn!("hub_sync: mouse path sync failed: {error}");
        }
    });
}

async fn sync_pending_mouse_paths(app: &AppHandle) -> anyhow::Result<()> {
    const MOUSE_PATH_SYNC_PAGE_SIZE: usize = 200;
    let mut session_ids = Vec::new();
    let mut offset = 0usize;
    loop {
        let batch = crate::stats_db::get_pending_mouse_path_upload_session_ids(
            app,
            offset,
            MOUSE_PATH_SYNC_PAGE_SIZE,
            HUB_MOUSE_PATH_SCHEMA_VERSION,
        )?;
        if batch.is_empty() {
            break;
        }
        offset += batch.len();
        session_ids.extend(batch);
    }

    if session_ids.is_empty() {
        return Ok(());
    }

    log::info!(
        "hub_sync: queued automatic mouse path resync for {} local session(s)",
        session_ids.len()
    );

    let total = session_ids.len();
    let mut uploaded = 0usize;
    let mut failed = 0usize;
    for (index, session_id) in session_ids.into_iter().enumerate() {
        match upload_mouse_path_for_session(app, &session_id).await {
            Ok(_) => uploaded += 1,
            Err(error) => {
                failed += 1;
                log::warn!(
                    "hub_sync: automatic mouse path resync skipped for {}: {}",
                    session_id,
                    verbose_transport_error(&error)
                );
            }
        }
        let processed = index + 1;
        if processed % 25 == 0 || processed == total {
            log::info!(
                "hub_sync: automatic mouse path resync progress processed={} uploaded={} failed={} total={}",
                processed,
                uploaded,
                failed,
                total
            );
        }
    }

    log::info!(
        "hub_sync: automatic mouse path resync complete uploaded={} failed={} total={}",
        uploaded,
        failed,
        total
    );
    Ok(())
}

async fn upload_session(app: &AppHandle, input: SessionUploadInput) -> anyhow::Result<()> {
    let settings = crate::settings::load(app)?;
    if !settings.hub_sync_enabled {
        record_sync_success(app, None);
        return Ok(());
    }

    let base_url = normalize_base_url(&settings.hub_api_base_url);
    let upload_token = settings.hub_upload_token.trim();
    let upload_identity = resolve_upload_identity().await;
    if base_url.is_empty() || (upload_token.is_empty() && upload_identity.is_none()) {
        log::info!(
            "hub_sync: skipping upload for {} because hub sync is not fully configured",
            input.session_id
        );
        record_sync_success(app, None);
        return Ok(());
    }
    log_upload_identity(
        &format!("session {}", input.session_id),
        upload_identity.as_ref(),
    );

    let persisted_run = crate::stats_db::get_run_summary(app, &input.session_id)
        .ok()
        .flatten()
        .or(input.run_snapshot.clone());
    let persisted_timeline = crate::stats_db::get_run_timeline(app, &input.session_id)
        .unwrap_or_else(|_| {
            persisted_run
                .as_ref()
                .map(|run| run.timeline.clone())
                .unwrap_or_default()
        });
    let context_windows =
        crate::stats_db::get_replay_context_windows(app, &input.session_id).unwrap_or_default();
    let shot_telemetry =
        crate::stats_db::get_shot_telemetry(app, &input.session_id).unwrap_or_default();
    let classification_stats = input.stats_panel.clone().unwrap_or_else(|| {
        // Fall back to CSV data so the classifier has kills/damage/TTK even
        // for historical sessions that were never seen by the stats panel hook.
        crate::session_store::StatsPanelSnapshot {
            kills: Some(input.result.kills),
            total_damage: (input.result.damage_done > 0.0).then(|| input.result.damage_done as f32),
            avg_ttk_ms: (input.result.avg_ttk > 0.0)
                .then(|| (input.result.avg_ttk * 1000.0) as f32), // avg_ttk from CSV is in seconds
            ..Default::default()
        }
    });
    let inferred_classification = crate::bridge::classify_persisted_session(
        Some(input.result.scenario.as_str()),
        &classification_stats,
        persisted_run.as_ref(),
        &shot_telemetry,
    );

    let scenario_type = {
        let inferred = inferred_classification.family.trim();
        if !inferred.is_empty() && inferred != "Unknown" {
            inferred.to_string()
        } else {
            input
                .stats_panel
                .as_ref()
                .map(|stats| stats.scenario_type.trim())
                .filter(|value| !value.is_empty() && *value != "Unknown")
                .map(str::to_string)
                .unwrap_or_else(|| "Unknown".to_string())
        }
    };

    let score = resolve_upload_score(&input.result, persisted_run.as_ref(), &persisted_timeline);
    let accuracy = persisted_run
        .as_ref()
        .and_then(|run| run.accuracy_pct)
        .or_else(|| {
            input
                .stats_panel
                .as_ref()
                .and_then(|stats| stats.accuracy_pct.map(f64::from))
        })
        .unwrap_or(input.result.accuracy);
    let duration_ms = persisted_run
        .as_ref()
        .and_then(|run| run.duration_secs)
        .map(secs_to_ms)
        .unwrap_or_else(|| secs_to_ms(input.result.duration_secs));

    let payload = build_ingest_payload(
        &input,
        persisted_run.as_ref(),
        &persisted_timeline,
        &context_windows,
        scenario_type,
        score,
        accuracy,
        duration_ms,
        upload_identity.as_ref(),
    );

    let url = format!("{base_url}{INGEST_PATH}");
    let mut request = CLIENT
        .post(&url)
        .header("Content-Type", "application/json")
        .header("Connect-Protocol-Version", CONNECT_PROTOCOL_VERSION)
        .header(
            reqwest::header::USER_AGENT,
            format!("AimMod/{}", crate::app_version::raw_version()),
        );
    if !upload_token.is_empty() {
        request = request.header("Authorization", format!("Bearer {upload_token}"));
    }
    let response = request
        .json(&payload)
        .send()
        .await
        .with_context(|| format!("error sending request for url ({})", url))?;

    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        anyhow::bail!("hub ingest returned {status}: {body}");
    }

    crate::session_store::mark_session_hub_uploaded(app, &input.session_id);
    try_upload_replay_media_for_session(app, &input.result, &input.session_id).await;
    try_upload_mouse_path_for_session(app, &input.session_id).await;
    crate::benchmark_overlay::invalidate();
    crate::overlay_service::notify_state_changed();
    record_sync_success(app, Some(&input.session_id));
    refresh_pending_count(app);
    log::info!("hub_sync: uploaded session {}", input.session_id);
    Ok(())
}

async fn sync_pending_sessions(app: &AppHandle) -> anyhow::Result<()> {
    let settings = crate::settings::load(app)?;
    let upload_identity = resolve_upload_identity().await;
    if !settings.hub_sync_enabled
        || normalize_base_url(&settings.hub_api_base_url).is_empty()
        || (settings.hub_upload_token.trim().is_empty() && upload_identity.is_none())
    {
        record_sync_success(app, None);
        return Ok(());
    }

    let overview = crate::session_store::get_pending_hub_upload_page(app, 0, 1);
    if overview.total == 0 {
        record_sync_success(app, None);
        return Ok(());
    }

    let mut uploaded = 0usize;
    let mut failed = 0usize;
    let limit = 200usize;

    log::info!(
        "hub_sync: starting pending sync for {} local session(s)",
        overview.total
    );

    loop {
        let page = crate::session_store::get_pending_hub_upload_page(app, 0, limit);
        if page.records.is_empty() {
            break;
        }

        for chunk in page.records.chunks(BATCH_SYNC_CHUNK_SIZE) {
            match upload_session_batch(app, chunk).await {
                Ok(batch_result) => {
                    uploaded += batch_result.uploaded_count;
                    failed += batch_result.failed_count;
                    refresh_pending_count(app);
                    log::info!(
                        "hub_sync: pending sync progress uploaded={} failed={} remaining_estimate={}",
                        uploaded,
                        failed,
                        crate::session_store::get_pending_hub_upload_page(app, 0, 1).total
                    );
                    for failure in batch_result.failures {
                        record_pending_upload_failure(app, &failure.session_id, &failure.message);
                        log::warn!(
                            "hub_sync: could not upload pending session {}: {}",
                            failure.session_id,
                            failure.message
                        );
                    }
                }
                Err(error) => {
                    failed += chunk.len();
                    refresh_pending_count(app);
                    for record in chunk {
                        record_pending_upload_failure(app, &record.id, &error.to_string());
                        log::warn!(
                            "hub_sync: could not upload pending session {}: {error}",
                            record.id
                        );
                    }
                }
            }
        }

        if !page.has_more {
            break;
        }
    }

    log::info!(
        "hub_sync: pending sync complete (uploaded {}, failed {})",
        uploaded,
        failed
    );
    if failed == 0 {
        record_sync_success(app, None);
    } else {
        record_sync_error(app, format!("{} pending upload(s) failed", failed));
    }
    refresh_pending_count(app);
    Ok(())
}

fn build_ingest_payload(
    input: &SessionUploadInput,
    persisted_run: Option<&BridgeRunSnapshot>,
    persisted_timeline: &[BridgeRunTimelinePoint],
    context_windows: &[crate::stats_db::SessionReplayContextWindow],
    scenario_type: String,
    score: f64,
    accuracy: f64,
    duration_ms: u64,
    upload_identity: Option<&UploadIdentity>,
) -> IngestSessionRequest {
    let upload_identity = upload_identity.cloned().unwrap_or_default();
    IngestSessionRequest {
        app_version: crate::app_version::raw_version().to_string(),
        schema_version: HUB_SCHEMA_VERSION,
        user_external_id: upload_identity.user_external_id,
        kovaaks_user_id: upload_identity.kovaaks_user_id,
        kovaaks_username: upload_identity.kovaaks_username,
        user_display_name: upload_identity.user_display_name,
        avatar_url: upload_identity.avatar_url,
        steam_id: upload_identity.steam_id,
        steam_display_name: upload_identity.steam_display_name,
        session_id: input.session_id.clone(),
        scenario_name: input.result.scenario.clone(),
        scenario_type: scenario_type.clone(),
        score,
        accuracy,
        duration_ms,
        played_at_iso: played_at_iso(&input.result.timestamp),
        summary: build_summary_map(input, persisted_run, context_windows.len(), &scenario_type),
        feature_set: build_feature_map(input),
        timeline_seconds: build_timeline_payload(persisted_timeline),
        context_windows: build_context_windows_payload(
            context_windows,
            persisted_run.and_then(|r| r.score_per_minute),
            persisted_run.and_then(|r| r.accuracy_pct),
            persisted_run.and_then(|r| r.kills_per_second),
        ),
    }
}

async fn upload_session_batch(
    app: &AppHandle,
    records: &[crate::session_store::SessionRecord],
) -> anyhow::Result<IngestBatchResponse> {
    let settings = crate::settings::load(app)?;
    let base_url = normalize_base_url(&settings.hub_api_base_url);
    let upload_token = settings.hub_upload_token.trim();
    let upload_identity = resolve_upload_identity().await;
    if !settings.hub_sync_enabled
        || base_url.is_empty()
        || (upload_token.is_empty() && upload_identity.is_none())
    {
        anyhow::bail!("hub sync is not fully configured");
    }
    log_upload_identity(
        &format!("batch of {} session(s)", records.len()),
        upload_identity.as_ref(),
    );

    let mut sessions = Vec::with_capacity(records.len());
    let mut local_session_ids = Vec::with_capacity(records.len());
    for record in records {
        let input = SessionUploadInput {
            session_id: record.id.clone(),
            result: SessionResult {
                scenario: record.scenario.clone(),
                score: record.score,
                accuracy: record.accuracy,
                kills: record.kills,
                deaths: record.deaths,
                duration_secs: record.duration_secs,
                avg_ttk: record.avg_ttk,
                damage_done: record.damage_done,
                timestamp: record.timestamp.clone(),
                csv_path: String::new(),
            },
            smoothness: record.smoothness.clone(),
            stats_panel: record.stats_panel.clone(),
            shot_timing: record.shot_timing.clone(),
            run_snapshot: None,
        };

        let persisted_run = crate::stats_db::get_run_summary(app, &input.session_id)
            .ok()
            .flatten()
            .or(input.run_snapshot.clone());
        let persisted_timeline = crate::stats_db::get_run_timeline(app, &input.session_id)
            .unwrap_or_else(|_| {
                persisted_run
                    .as_ref()
                    .map(|run| run.timeline.clone())
                    .unwrap_or_default()
            });
        let context_windows =
            crate::stats_db::get_replay_context_windows(app, &input.session_id).unwrap_or_default();
        let shot_telemetry =
            crate::stats_db::get_shot_telemetry(app, &input.session_id).unwrap_or_default();
        let classification_stats = input.stats_panel.clone().unwrap_or_else(|| {
            crate::session_store::StatsPanelSnapshot {
                kills: Some(input.result.kills),
                total_damage: (input.result.damage_done > 0.0)
                    .then(|| input.result.damage_done as f32),
                avg_ttk_ms: (input.result.avg_ttk > 0.0)
                    .then(|| (input.result.avg_ttk * 1000.0) as f32), // avg_ttk from CSV is in seconds
                ..Default::default()
            }
        });
        let inferred_classification = crate::bridge::classify_persisted_session(
            Some(input.result.scenario.as_str()),
            &classification_stats,
            persisted_run.as_ref(),
            &shot_telemetry,
        );
        let scenario_type = {
            let inferred = inferred_classification.family.trim();
            if !inferred.is_empty() && inferred != "Unknown" {
                inferred.to_string()
            } else {
                input
                    .stats_panel
                    .as_ref()
                    .map(|stats| stats.scenario_type.trim())
                    .filter(|value| !value.is_empty() && *value != "Unknown")
                    .map(str::to_string)
                    .unwrap_or_else(|| "Unknown".to_string())
            }
        };
        let score =
            resolve_upload_score(&input.result, persisted_run.as_ref(), &persisted_timeline);
        let accuracy = persisted_run
            .as_ref()
            .and_then(|run| run.accuracy_pct)
            .or_else(|| {
                input
                    .stats_panel
                    .as_ref()
                    .and_then(|stats| stats.accuracy_pct.map(f64::from))
            })
            .unwrap_or(input.result.accuracy);
        let duration_ms = persisted_run
            .as_ref()
            .and_then(|run| run.duration_secs)
            .map(secs_to_ms)
            .unwrap_or_else(|| secs_to_ms(input.result.duration_secs));

        local_session_ids.push(record.id.clone());
        sessions.push(build_ingest_payload(
            &input,
            persisted_run.as_ref(),
            &persisted_timeline,
            &context_windows,
            scenario_type,
            score,
            accuracy,
            duration_ms,
            upload_identity.as_ref(),
        ));
    }

    let mut request = CLIENT
        .post(format!("{base_url}{BATCH_INGEST_PATH}"))
        .header("Content-Type", "application/json")
        .header(
            reqwest::header::USER_AGENT,
            format!("AimMod/{}", crate::app_version::raw_version()),
        );
    if !upload_token.is_empty() {
        request = request.header("Authorization", format!("Bearer {upload_token}"));
    }
    let response = request
        .json(&IngestBatchRequest { sessions })
        .send()
        .await
        .with_context(|| {
            format!("error sending request for url ({base_url}{BATCH_INGEST_PATH})")
        })?;

    let status = response.status();
    let mut payload = response.json::<IngestBatchResponse>().await?;
    if payload.uploaded_session_ids.is_empty()
        && payload.failures.is_empty()
        && !status.is_success()
    {
        anyhow::bail!("hub batch ingest returned {status} with an empty result body");
    }

    log::info!(
        "hub_sync: batch ingest accepted uploaded={} failed={}",
        payload.uploaded_session_ids.len(),
        payload.failures.len()
    );

    let mut next_fallback_index = 0usize;
    for failure in &mut payload.failures {
        if !failure.session_id.trim().is_empty() {
            if let Some(index) = local_session_ids
                .iter()
                .position(|local_id| local_id == &failure.session_id)
            {
                next_fallback_index = next_fallback_index.max(index + 1);
            }
            continue;
        }

        while next_fallback_index < local_session_ids.len()
            && payload
                .uploaded_session_ids
                .iter()
                .any(|uploaded_id| uploaded_id == &local_session_ids[next_fallback_index])
        {
            next_fallback_index += 1;
        }

        if let Some(local_id) = local_session_ids.get(next_fallback_index) {
            failure.session_id = local_id.clone();
            next_fallback_index += 1;
        }
    }

    for session_id in &payload.uploaded_session_ids {
        crate::session_store::mark_session_hub_uploaded(app, session_id);
    }
    for record in records {
        if payload
            .uploaded_session_ids
            .iter()
            .any(|id| id == &record.id)
        {
            queue_post_ingest_aux_uploads(
                app,
                SessionResult {
                    scenario: record.scenario.clone(),
                    score: record.score,
                    accuracy: record.accuracy,
                    kills: record.kills,
                    deaths: record.deaths,
                    duration_secs: record.duration_secs,
                    avg_ttk: record.avg_ttk,
                    damage_done: record.damage_done,
                    timestamp: record.timestamp.clone(),
                    csv_path: String::new(),
                },
                record.id.clone(),
            );
        }
    }
    if let Some(last) = payload.uploaded_session_ids.last() {
        record_sync_success(app, Some(last));
    }
    refresh_pending_count(app);
    Ok(payload)
}

async fn try_upload_replay_media_for_session(
    app: &AppHandle,
    result: &SessionResult,
    session_id: &str,
) {
    if let Err(error) = upload_replay_media_for_session(app, result, session_id).await {
        let message = verbose_transport_error(&error);
        record_replay_media_upload_error(app, session_id, message.clone());
        log::warn!(
            "hub_sync: replay media upload skipped for {}: {}",
            session_id,
            message
        );
    }
}

fn queue_post_ingest_aux_uploads(app: &AppHandle, result: SessionResult, session_id: String) {
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let Ok(_permit) = AUX_UPLOAD_SEMAPHORE.clone().acquire_owned().await else {
            return;
        };
        try_upload_replay_media_for_session(&app, &result, &session_id).await;
        try_upload_mouse_path_for_session(&app, &session_id).await;
    });
}

async fn upload_replay_media_for_session(
    app: &AppHandle,
    result: &SessionResult,
    session_id: &str,
) -> anyhow::Result<()> {
    let settings = crate::settings::load(app)?;
    let base_url = normalize_base_url(&settings.hub_api_base_url);
    let upload_token = settings.hub_upload_token.trim();
    if !settings.hub_sync_enabled || base_url.is_empty() || upload_token.is_empty() {
        return Ok(());
    }
    if !should_upload_replay_media(app, &settings, result, session_id)? {
        return Ok(());
    }

    let temp_video = crate::replay_store::encode_replay_video_to_temp(
        app,
        session_id,
        &settings.replay_media_upload_quality,
    )
    .map_err(anyhow::Error::msg)?;
    let video_bytes = std::fs::read(&temp_video).with_context(|| {
        format!(
            "could not read encoded replay media {}",
            temp_video.display()
        )
    })?;

    let url = format!(
        "{base_url}{REPLAY_MEDIA_UPLOAD_PATH}?sessionId={}&quality={}",
        urlencoding::encode(session_id),
        urlencoding::encode(&settings.replay_media_upload_quality),
    );
    let response = CLIENT
        .post(&url)
        .header("Content-Type", "video/mp4")
        .header("Authorization", format!("Bearer {upload_token}"))
        .header(
            reqwest::header::USER_AGENT,
            format!("AimMod/{}", crate::app_version::raw_version()),
        )
        .body(video_bytes)
        .send()
        .await
        .with_context(|| format!("error sending request for url ({})", url))?;
    let _ = std::fs::remove_file(&temp_video);

    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        anyhow::bail!("hub replay media upload returned {status}: {body}");
    }

    crate::stats_db::mark_replay_media_uploaded(
        app,
        session_id,
        HUB_REPLAY_MEDIA_SCHEMA_VERSION,
        &settings.replay_media_upload_quality,
    )?;
    record_replay_media_upload_success(app, session_id);
    Ok(())
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MousePathUploadPoint {
    x: f64,
    y: f64,
    timestamp_ms: u64,
    is_click: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MousePathUploadPayload {
    points: Vec<MousePathUploadPoint>,
    hit_timestamps_ms: Vec<u64>,
    playback_offset_ms: u64,
    video_offset_ms: u64,
}

fn canonicalize_mouse_path_timeline(
    mut positions: Vec<crate::mouse_hook::RawPositionPoint>,
    mut hit_timestamps_ms: Vec<u64>,
    target_duration_ms: u64,
) -> (Vec<crate::mouse_hook::RawPositionPoint>, Vec<u64>, u64) {
    if positions.is_empty() {
        return (positions, hit_timestamps_ms, 0);
    }

    positions.sort_by_key(|point| point.timestamp_ms);
    hit_timestamps_ms.sort_unstable();

    let start_offset_ms = positions
        .first()
        .map(|point| point.timestamp_ms)
        .unwrap_or(0);
    if start_offset_ms > 0 {
        for point in &mut positions {
            point.timestamp_ms = point.timestamp_ms.saturating_sub(start_offset_ms);
        }
        hit_timestamps_ms.retain(|hit_ms| *hit_ms >= start_offset_ms);
        for hit_ms in &mut hit_timestamps_ms {
            *hit_ms = hit_ms.saturating_sub(start_offset_ms);
        }
    }

    let local_point_end_ms = positions
        .last()
        .map(|point| point.timestamp_ms)
        .unwrap_or(0);
    if local_point_end_ms > 0 {
        let local_hit_limit_ms = if target_duration_ms > 0 {
            local_point_end_ms
                .min(target_duration_ms)
                .saturating_add(250)
        } else {
            local_point_end_ms.saturating_add(250)
        };
        hit_timestamps_ms.retain(|timestamp_ms| *timestamp_ms <= local_hit_limit_ms);
    }

    let observed_duration_ms = positions
        .last()
        .map(|point| point.timestamp_ms)
        .unwrap_or(0)
        .max(hit_timestamps_ms.last().copied().unwrap_or(0));

    if target_duration_ms > 0
        && observed_duration_ms > target_duration_ms.saturating_add(1_500)
        && observed_duration_ms > target_duration_ms.saturating_mul(12) / 10
    {
        let scale = target_duration_ms as f64 / observed_duration_ms as f64;
        for point in &mut positions {
            point.timestamp_ms = ((point.timestamp_ms as f64) * scale).round().max(0.0) as u64;
        }
        for hit_ms in &mut hit_timestamps_ms {
            *hit_ms = ((*hit_ms as f64) * scale).round().max(0.0) as u64;
        }
    }

    if target_duration_ms > 0 {
        let limit_ms = target_duration_ms.saturating_add(250);
        positions.retain(|point| point.timestamp_ms <= limit_ms);
        hit_timestamps_ms.retain(|timestamp_ms| *timestamp_ms <= limit_ms);
    }

    positions.sort_by_key(|point| point.timestamp_ms);
    hit_timestamps_ms.sort_unstable();
    (positions, hit_timestamps_ms, start_offset_ms)
}

fn build_relative_hit_timestamps(
    raw_hit_event_ts: &[u64],
    replay_base_ts: Option<u64>,
) -> Vec<u64> {
    if raw_hit_event_ts.is_empty() {
        return Vec::new();
    }

    let mut raw = raw_hit_event_ts.to_vec();
    raw.sort_unstable();

    let earliest = raw[0];
    let raw_span_ms = raw
        .last()
        .copied()
        .unwrap_or(earliest)
        .saturating_sub(earliest);

    let normalize = |base_ts: u64| {
        raw.iter()
            .map(|ts_ms| ts_ms.saturating_sub(base_ts))
            .collect::<Vec<_>>()
    };

    let mut normalized = normalize(replay_base_ts.unwrap_or(earliest));
    let normalized_span_ms = normalized.last().copied().unwrap_or(0);
    let unique_count = normalized
        .iter()
        .copied()
        .collect::<std::collections::BTreeSet<_>>()
        .len();

    let clearly_pathological =
        raw_span_ms > 250 && (normalized_span_ms < raw_span_ms / 2 || unique_count <= 1);

    if clearly_pathological {
        normalized = normalize(earliest);
    }

    normalized
}

async fn try_upload_mouse_path_for_session(app: &AppHandle, session_id: &str) {
    if let Err(error) = upload_mouse_path_for_session(app, session_id).await {
        let message = verbose_transport_error(&error);
        log::warn!(
            "hub_sync: mouse path upload skipped for {}: {}",
            session_id,
            message
        );
    }
}

async fn upload_mouse_path_for_session(app: &AppHandle, session_id: &str) -> anyhow::Result<()> {
    let settings = crate::settings::load(app)?;
    let base_url = normalize_base_url(&settings.hub_api_base_url);
    let upload_token = settings.hub_upload_token.trim();
    if !settings.hub_sync_enabled || base_url.is_empty() || upload_token.is_empty() {
        return Ok(());
    }
    if !should_upload_mouse_path(app, session_id)? {
        return Ok(());
    }

    let Some(replay) = crate::replay_store::load_mouse_path_payload(app, session_id) else {
        anyhow::bail!("no mouse path payload is available for this run");
    };
    if replay.positions.is_empty() {
        anyhow::bail!("this replay has no saved mouse path data");
    }

    let persisted_run = crate::stats_db::get_run_summary(app, session_id).unwrap_or(None);
    let persisted_timeline = crate::stats_db::get_run_timeline(app, session_id).unwrap_or_default();
    let shot_telemetry = crate::stats_db::get_shot_telemetry(app, session_id).unwrap_or_default();
    let replay_base_ts = persisted_run
        .as_ref()
        .and_then(|run| run.started_at_bridge_ts_ms)
        .or_else(|| estimate_replay_bridge_base_ts(&shot_telemetry, &persisted_timeline))
        .filter(|value| *value > 0);
    let raw_hit_event_ts = shot_telemetry
        .into_iter()
        .filter(|event| event.event == "shot_hit")
        .map(|event| event.ts_ms)
        .collect::<Vec<_>>();
    let hit_timestamps_ms = build_relative_hit_timestamps(&raw_hit_event_ts, replay_base_ts);
    let target_duration_ms = persisted_run
        .as_ref()
        .and_then(|run| run.duration_secs)
        .filter(|value| value.is_finite() && *value > 0.0)
        .map(|value| (value * 1000.0).round() as u64)
        .unwrap_or_else(|| {
            replay
                .metrics
                .last()
                .map(|point| point.timestamp_ms)
                .unwrap_or_else(|| {
                    replay
                        .positions
                        .last()
                        .map(|point| point.timestamp_ms)
                        .unwrap_or(0)
                })
        });
    let (positions, hit_timestamps_ms, playback_offset_ms) =
        canonicalize_mouse_path_timeline(replay.positions, hit_timestamps_ms, target_duration_ms);
    // Where the uploaded video starts on the replay timeline. An FTHR video is
    // exported from its first segment, which starts later than 0 when the
    // clip doesn't cover the start of the run.
    let video_offset_ms = match replay.video.as_ref().and_then(|v| v.segments.first()) {
        Some(segment) => segment.replay_start_ms,
        None => replay
            .frames
            .iter()
            .map(|frame| frame.timestamp_ms)
            .min()
            .unwrap_or(0),
    };
    let points = positions
        .into_iter()
        .take(8000)
        .map(|point| MousePathUploadPoint {
            x: point.x,
            y: point.y,
            timestamp_ms: point.timestamp_ms,
            is_click: point.is_click,
        })
        .collect::<Vec<_>>();
    let hit_timestamps_ms = hit_timestamps_ms.into_iter().take(4000).collect::<Vec<_>>();
    if points.is_empty() {
        anyhow::bail!("mouse path timeline normalization produced no usable points");
    }
    let first_point_ms = points.first().map(|point| point.timestamp_ms).unwrap_or(0);
    let last_point_ms = points.last().map(|point| point.timestamp_ms).unwrap_or(0);
    let first_hit_ms = hit_timestamps_ms.first().copied().unwrap_or(0);
    let last_hit_ms = hit_timestamps_ms.last().copied().unwrap_or(0);
    log::info!(
        "hub_sync: mouse path upload payload session='{}' points={} hits={} offset_ms={} video_offset_ms={} first_point_ms={} last_point_ms={} first_hit_ms={} last_hit_ms={} replay_base_ts={:?}",
        session_id,
        points.len(),
        hit_timestamps_ms.len(),
        playback_offset_ms,
        video_offset_ms,
        first_point_ms,
        last_point_ms,
        first_hit_ms,
        last_hit_ms,
        replay_base_ts
    );

    let response = CLIENT
        .post(format!(
            "{base_url}{REPLAY_MOUSE_PATH_UPLOAD_PATH}?sessionId={}",
            urlencoding::encode(session_id)
        ))
        .header("Content-Type", "application/json")
        .header("Authorization", format!("Bearer {upload_token}"))
        .header(
            reqwest::header::USER_AGENT,
            format!("AimMod/{}", crate::app_version::raw_version()),
        )
        .json(&MousePathUploadPayload {
            points,
            hit_timestamps_ms,
            playback_offset_ms,
            video_offset_ms,
        })
        .send()
        .await
        .with_context(|| {
            format!("error sending request for url ({base_url}{REPLAY_MOUSE_PATH_UPLOAD_PATH})")
        })?;

    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        anyhow::bail!("hub mouse path upload returned {status}: {body}");
    }

    crate::stats_db::mark_mouse_path_uploaded(app, session_id, HUB_MOUSE_PATH_SCHEMA_VERSION)?;
    Ok(())
}

fn should_upload_replay_media(
    app: &AppHandle,
    settings: &crate::settings::AppSettings,
    result: &SessionResult,
    session_id: &str,
) -> anyhow::Result<bool> {
    let Some(state) = crate::stats_db::get_replay_upload_state(app, session_id)? else {
        return Ok(false);
    };
    if state.hub_media_schema_version >= HUB_REPLAY_MEDIA_SCHEMA_VERSION
        && state.hub_media_uploaded_at_unix_ms.is_some()
        && state
            .hub_media_uploaded_quality
            .as_deref()
            .map(|value| value == settings.replay_media_upload_quality.as_str())
            .unwrap_or(false)
    {
        return Ok(false);
    }

    let is_pb = crate::session_store::get_personal_best_for_scenario(app, &result.scenario)
        .map(|best| result.score.round().max(0.0) as u32 >= best)
        .unwrap_or(false);

    let should = match settings.replay_media_upload_mode.as_str() {
        "all" => true,
        "favorites_and_pb" => state.is_favorite || is_pb,
        "favorites" => state.is_favorite,
        _ => false,
    };
    Ok(should)
}

fn should_upload_mouse_path(app: &AppHandle, session_id: &str) -> anyhow::Result<bool> {
    let Some(state) = crate::stats_db::get_replay_upload_state(app, session_id)? else {
        return Ok(false);
    };
    Ok(
        state.hub_mouse_path_schema_version < HUB_MOUSE_PATH_SCHEMA_VERSION
            || state.hub_mouse_path_uploaded_at_unix_ms.is_none(),
    )
}

pub(crate) fn normalize_base_url(value: &str) -> String {
    crate::settings::normalize_hub_api_base_url(value)
}

fn played_at_iso(timestamp: &str) -> String {
    NaiveDateTime::parse_from_str(timestamp, "%Y.%m.%d-%H.%M.%S")
        .ok()
        .and_then(|naive| {
            Local
                .from_local_datetime(&naive)
                .single()
                .or_else(|| Local.from_local_datetime(&naive).earliest())
                .or_else(|| Local.from_local_datetime(&naive).latest())
        })
        .map(|local| local.with_timezone(&Utc).to_rfc3339())
        .unwrap_or_else(|| Utc::now().to_rfc3339())
}

fn secs_to_ms(value: f64) -> u64 {
    (value.max(0.0) * 1000.0).round() as u64
}

fn estimate_replay_bridge_base_ts(
    shot_telemetry: &[crate::bridge::BridgeShotTelemetryEvent],
    persisted_timeline: &[BridgeRunTimelinePoint],
) -> Option<u64> {
    fn estimate_offset_ms(
        timeline: &[BridgeRunTimelinePoint],
        field: &str,
        total: u32,
    ) -> Option<u64> {
        timeline.iter().find_map(|point| {
            let value = match field {
                "shots_fired" => point.shots_fired,
                "shots_hit" => point.shots_hit,
                _ => None,
            }?;
            (value.is_finite() && value + 0.0001 >= total as f64).then(|| point.t_sec as u64 * 1000)
        })
    }

    if shot_telemetry.is_empty() {
        return None;
    }

    let mut events = shot_telemetry.iter().collect::<Vec<_>>();
    events.sort_by_key(|event| event.ts_ms);

    let mut bases = Vec::new();
    let mut cumulative_fired = 0u32;
    let mut cumulative_hit = 0u32;

    for event in events.into_iter().take(64) {
        let weight = std::cmp::max(1, event.count.unwrap_or(1));
        let offset_ms = match event.event.as_str() {
            "shot_fired" => {
                cumulative_fired = cumulative_fired.saturating_add(weight);
                estimate_offset_ms(
                    persisted_timeline,
                    "shots_fired",
                    event.total.unwrap_or(cumulative_fired),
                )
            }
            "shot_hit" => {
                cumulative_hit = cumulative_hit.saturating_add(weight);
                estimate_offset_ms(
                    persisted_timeline,
                    "shots_hit",
                    event.total.unwrap_or(cumulative_hit),
                )
            }
            _ => None,
        };

        if let Some(offset_ms) = offset_ms {
            bases.push(event.ts_ms.saturating_sub(offset_ms));
            if bases.len() >= 12 {
                break;
            }
        }
    }

    if bases.is_empty() {
        return shot_telemetry.first().map(|event| event.ts_ms);
    }

    bases.sort_unstable();
    Some(bases[bases.len() / 2])
}

fn first_meaningful_number(values: impl IntoIterator<Item = Option<f64>>) -> Option<f64> {
    let mut first_finite = None;
    for value in values {
        let Some(value) = value.filter(|value| value.is_finite()) else {
            continue;
        };
        if first_finite.is_none() {
            first_finite = Some(value);
        }
        if value > 0.0 {
            return Some(value);
        }
    }
    first_finite
}

fn resolve_upload_score(
    result: &SessionResult,
    persisted_run: Option<&BridgeRunSnapshot>,
    persisted_timeline: &[BridgeRunTimelinePoint],
) -> f64 {
    let timeline_peak = persisted_timeline.iter().fold(None, |best, point| {
        let candidate = first_meaningful_number([point.score_total, point.score_total_derived]);
        match (best, candidate) {
            (Some(current), Some(next)) if next > current => Some(next),
            (None, Some(next)) => Some(next),
            (current, _) => current,
        }
    });

    first_meaningful_number([
        persisted_run.and_then(|run| run.score_total),
        persisted_run.and_then(|run| run.score_total_derived),
        timeline_peak,
        Some(result.score),
    ])
    .unwrap_or(0.0)
}

fn summary_number(map: &mut HashMap<String, SessionSummaryValue>, key: &str, value: Option<f64>) {
    if let Some(value) = value {
        map.insert(key.to_string(), SessionSummaryValue::number(value));
    }
}

fn summary_string(
    map: &mut HashMap<String, SessionSummaryValue>,
    key: &str,
    value: Option<String>,
) {
    if let Some(value) = value.filter(|value| !value.trim().is_empty()) {
        map.insert(key.to_string(), SessionSummaryValue::string(value));
    }
}

fn summary_bool(map: &mut HashMap<String, SessionSummaryValue>, key: &str, value: Option<bool>) {
    if let Some(value) = value {
        map.insert(key.to_string(), SessionSummaryValue::boolean(value));
    }
}

fn build_summary_map(
    input: &SessionUploadInput,
    run: Option<&BridgeRunSnapshot>,
    context_window_count: usize,
    scenario_type: &str,
) -> HashMap<String, SessionSummaryValue> {
    let mut summary = HashMap::new();

    summary_string(
        &mut summary,
        "appVersion",
        Some(crate::app_version::raw_version().to_string()),
    );
    summary_string(
        &mut summary,
        "platformOs",
        Some(std::env::consts::OS.to_string()),
    );
    summary_string(
        &mut summary,
        "platformArch",
        Some(std::env::consts::ARCH.to_string()),
    );
    summary_string(
        &mut summary,
        "buildProfile",
        Some(
            if cfg!(debug_assertions) {
                "debug"
            } else {
                "release"
            }
            .to_string(),
        ),
    );
    summary_bool(&mut summary, "hasBridgeRunSnapshot", Some(run.is_some()));
    summary_bool(
        &mut summary,
        "hasSmoothnessSnapshot",
        Some(input.smoothness.is_some()),
    );
    summary_bool(
        &mut summary,
        "hasShotTimingSnapshot",
        Some(input.shot_timing.is_some()),
    );
    summary_bool(
        &mut summary,
        "hasStatsPanelSnapshot",
        Some(input.stats_panel.is_some()),
    );

    summary_number(&mut summary, "csvScore", Some(input.result.score));
    summary_number(&mut summary, "csvAccuracy", Some(input.result.accuracy));
    summary_number(&mut summary, "csvKills", Some(input.result.kills as f64));
    summary_number(&mut summary, "csvDeaths", Some(input.result.deaths as f64));
    summary_number(
        &mut summary,
        "csvDurationSecs",
        Some(input.result.duration_secs),
    );
    summary_number(&mut summary, "csvAvgTtk", Some(input.result.avg_ttk));
    summary_number(
        &mut summary,
        "csvDamageDone",
        Some(input.result.damage_done),
    );
    summary_number(
        &mut summary,
        "contextWindowCount",
        Some(context_window_count as f64),
    );
    summary_string(
        &mut summary,
        "scenarioType",
        Some(scenario_type.to_string()),
    );

    if let Some(stats_panel) = input.stats_panel.as_ref() {
        summary_string(
            &mut summary,
            "scenarioSubtype",
            stats_panel.scenario_subtype.clone(),
        );
        summary_number(
            &mut summary,
            "panelKills",
            stats_panel.kills.map(|value| value as f64),
        );
        summary_number(
            &mut summary,
            "panelAvgKps",
            stats_panel.avg_kps.map(f64::from),
        );
        summary_number(
            &mut summary,
            "panelAccuracyPct",
            stats_panel.accuracy_pct.map(f64::from),
        );
        summary_number(
            &mut summary,
            "panelTotalDamage",
            stats_panel.total_damage.map(f64::from),
        );
        summary_number(
            &mut summary,
            "panelAvgTtkMs",
            stats_panel.avg_ttk_ms.map(f64::from),
        );
        summary_number(
            &mut summary,
            "panelBestTtkMs",
            stats_panel.best_ttk_ms.map(f64::from),
        );
        summary_number(
            &mut summary,
            "panelTtkStdMs",
            stats_panel.ttk_std_ms.map(f64::from),
        );
        summary_number(
            &mut summary,
            "panelAccuracyTrend",
            stats_panel.accuracy_trend.map(f64::from),
        );
    }

    if let Some(shot_timing) = input.shot_timing.as_ref() {
        summary_number(
            &mut summary,
            "pairedShotHits",
            Some(shot_timing.paired_shot_hits as f64),
        );
        summary_number(
            &mut summary,
            "avgFireToHitMs",
            shot_timing.avg_fire_to_hit_ms.map(f64::from),
        );
        summary_number(
            &mut summary,
            "p90FireToHitMs",
            shot_timing.p90_fire_to_hit_ms.map(f64::from),
        );
        summary_number(
            &mut summary,
            "avgShotsToHit",
            shot_timing.avg_shots_to_hit.map(f64::from),
        );
        summary_number(
            &mut summary,
            "correctiveShotRatio",
            shot_timing.corrective_shot_ratio.map(f64::from),
        );
    }

    if let Some(run) = run {
        summary_number(&mut summary, "scoreTotal", run.score_total);
        summary_number(&mut summary, "scoreTotalDerived", run.score_total_derived);
        summary_number(&mut summary, "scorePerMinute", run.score_per_minute);
        summary_number(&mut summary, "shotsFired", run.shots_fired);
        summary_number(&mut summary, "shotsHit", run.shots_hit);
        summary_number(&mut summary, "kills", run.kills);
        summary_number(&mut summary, "killsPerSecond", run.kills_per_second);
        summary_number(&mut summary, "damageDone", run.damage_done);
        summary_number(&mut summary, "damagePossible", run.damage_possible);
        summary_number(&mut summary, "damageEfficiency", run.damage_efficiency);
        summary_number(&mut summary, "accuracyPct", run.accuracy_pct);
        summary_number(
            &mut summary,
            "peakScorePerMinute",
            run.peak_score_per_minute,
        );
        summary_number(
            &mut summary,
            "peakKillsPerSecond",
            run.peak_kills_per_second,
        );
        summary_number(
            &mut summary,
            "eventShotFired",
            Some(run.event_counts.shot_fired_events as f64),
        );
        summary_number(
            &mut summary,
            "eventShotHit",
            Some(run.event_counts.shot_hit_events as f64),
        );
        summary_number(
            &mut summary,
            "eventKills",
            Some(run.event_counts.kill_events as f64),
        );
        summary_number(
            &mut summary,
            "pauseWindowCount",
            Some(run.pause_windows.len() as f64),
        );
        summary_bool(
            &mut summary,
            "hasTickStream",
            Some(run.tick_stream_v1.is_some()),
        );
    }

    summary
}

fn build_feature_map(input: &SessionUploadInput) -> HashMap<String, SessionSummaryValue> {
    let mut feature_set = HashMap::new();

    if let Some(smoothness) = input.smoothness.as_ref() {
        summary_number(
            &mut feature_set,
            "smoothnessComposite",
            Some(f64::from(smoothness.composite)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessJitter",
            Some(f64::from(smoothness.jitter)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessOvershootRate",
            Some(f64::from(smoothness.overshoot_rate)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessVelocityStd",
            Some(f64::from(smoothness.velocity_std)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessPathEfficiency",
            Some(f64::from(smoothness.path_efficiency)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessAvgSpeed",
            Some(f64::from(smoothness.avg_speed)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessClickTimingCv",
            Some(f64::from(smoothness.click_timing_cv)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessCorrectionRatio",
            Some(f64::from(smoothness.correction_ratio)),
        );
        summary_number(
            &mut feature_set,
            "smoothnessDirectionalBias",
            Some(f64::from(smoothness.directional_bias)),
        );
    }

    if let Some(shot_timing) = input.shot_timing.as_ref() {
        summary_number(
            &mut feature_set,
            "shotTimingAvgFireToHitMs",
            shot_timing.avg_fire_to_hit_ms.map(f64::from),
        );
        summary_number(
            &mut feature_set,
            "shotTimingP90FireToHitMs",
            shot_timing.p90_fire_to_hit_ms.map(f64::from),
        );
        summary_number(
            &mut feature_set,
            "shotTimingAvgShotsToHit",
            shot_timing.avg_shots_to_hit.map(f64::from),
        );
        summary_number(
            &mut feature_set,
            "shotTimingCorrectiveShotRatio",
            shot_timing.corrective_shot_ratio.map(f64::from),
        );
    }

    feature_set
}

fn build_timeline_payload(timeline: &[BridgeRunTimelinePoint]) -> Vec<TimelineSecondPayload> {
    timeline
        .iter()
        .map(|point| TimelineSecondPayload {
            t_sec: point.t_sec,
            score: point
                .score_total
                .or(point.score_total_derived)
                .unwrap_or_default(),
            accuracy: point.accuracy_pct.unwrap_or_default(),
            damage_eff: point.damage_efficiency.unwrap_or_default(),
            spm: point.score_per_minute.unwrap_or_default(),
            shots: point.shots_fired.unwrap_or_default().max(0.0).round() as u32,
            hits: point.shots_hit.unwrap_or_default().max(0.0).round() as u32,
            kills: point.kills.unwrap_or_default().max(0.0).round() as u32,
            paused: false,
        })
        .collect()
}

fn build_context_windows_payload(
    windows: &[crate::stats_db::SessionReplayContextWindow],
    session_spm: Option<f64>,
    session_accuracy: Option<f64>,
    session_kps: Option<f64>,
) -> Vec<ContextWindowPayload> {
    windows
        .iter()
        .map(|window| {
            let mut feature_summary = HashMap::new();
            summary_number(
                &mut feature_summary,
                "shotEventCount",
                Some(window.shot_event_count as f64),
            );
            summary_number(
                &mut feature_summary,
                "firedCount",
                Some(window.fired_count as f64),
            );
            summary_number(
                &mut feature_summary,
                "hitCount",
                Some(window.hit_count as f64),
            );
            summary_number(&mut feature_summary, "accuracyPct", window.accuracy_pct);
            summary_number(&mut feature_summary, "avgBotCount", window.avg_bot_count);
            summary_number(
                &mut feature_summary,
                "primaryTargetShare",
                window.primary_target_share,
            );
            summary_number(
                &mut feature_summary,
                "avgNearestDistance",
                window.avg_nearest_distance,
            );
            summary_number(
                &mut feature_summary,
                "avgNearestYawErrorDeg",
                window.avg_nearest_yaw_error_deg,
            );
            summary_number(
                &mut feature_summary,
                "avgNearestPitchErrorDeg",
                window.avg_nearest_pitch_error_deg,
            );
            summary_number(
                &mut feature_summary,
                "avgScorePerMinute",
                window.avg_score_per_minute,
            );
            summary_number(
                &mut feature_summary,
                "avgKillsPerSecond",
                window.avg_kills_per_second,
            );
            summary_number(
                &mut feature_summary,
                "avgTimelineAccuracyPct",
                window.avg_timeline_accuracy_pct,
            );
            summary_number(
                &mut feature_summary,
                "avgDamageEfficiency",
                window.avg_damage_efficiency,
            );
            summary_string(&mut feature_summary, "phase", window.phase.clone());
            summary_string(
                &mut feature_summary,
                "primaryTargetLabel",
                window.primary_target_label.clone(),
            );
            summary_string(
                &mut feature_summary,
                "primaryTargetProfile",
                window.primary_target_profile.clone(),
            );
            summary_string(
                &mut feature_summary,
                "primaryTargetEntityId",
                window.primary_target_entity_id.clone(),
            );

            let mut coaching_tags = Vec::new();

            // Phase tag (opening / mid / closing)
            if let Some(phase) = window.phase.clone().filter(|v| !v.trim().is_empty()) {
                coaching_tags.push(phase);
            }

            // SPM-based pace signals: compare window average to session average
            let win_spm = window.avg_score_per_minute;
            if let (Some(win), Some(ses)) = (win_spm, session_spm) {
                if ses > 0.0 {
                    let ratio = win / ses;
                    if ratio < 0.88 {
                        coaching_tags.push("pace_fade".to_string());
                    } else if ratio > 1.12 {
                        coaching_tags.push("pace_peak".to_string());
                    }
                }
            }

            // Accuracy signals: compare window accuracy to session accuracy
            let win_acc = window.avg_timeline_accuracy_pct.or(window.accuracy_pct);
            if let (Some(win), Some(ses)) = (win_acc, session_accuracy) {
                let diff = win - ses;
                if diff > 5.0 {
                    coaching_tags.push("accuracy_build".to_string());
                } else if diff < -5.0 {
                    coaching_tags.push("accuracy_drop".to_string());
                }
            }

            // KPS pace signals for kill-based scenarios
            if let (Some(win), Some(ses)) = (window.avg_kills_per_second, session_kps) {
                if ses > 0.0 && win / ses > 1.15 {
                    coaching_tags.push("peak_tempo".to_string());
                }
            }

            // Context kind tag when it carries meaning beyond phase
            if window.context_kind == "metric_shift" {
                coaching_tags.push("pace_shift".to_string());
            }

            ContextWindowPayload {
                start_ms: window.start_ms,
                end_ms: window.end_ms,
                window_type: window.context_kind.clone(),
                label: window.label.clone(),
                feature_summary,
                coaching_tags,
            }
        })
        .collect()
}

impl SessionSummaryValue {
    fn string(value: impl Into<String>) -> Self {
        Self {
            string_value: Some(value.into()),
            number_value: None,
            bool_value: None,
        }
    }

    fn number(value: f64) -> Self {
        Self {
            string_value: None,
            number_value: Some(value),
            bool_value: None,
        }
    }

    fn boolean(value: bool) -> Self {
        Self {
            string_value: None,
            number_value: None,
            bool_value: Some(value),
        }
    }
}
