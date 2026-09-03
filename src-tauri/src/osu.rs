use chrono::{DateTime, SecondsFormat, Utc};
use futures_util::stream::{self, StreamExt};
use once_cell::sync::Lazy;
use reqwest::{Client, Url};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::env;
use std::ffi::OsStr;
use std::fs::{self, File};
use std::io::{self, BufReader, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;
use tauri::{AppHandle, Manager};

const MAX_IMPORT_FILES: usize = 100;
const MAX_REPLAY_FILE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_REPLAY_STRING_BYTES: usize = 1024 * 1024;
const SUPPORTED_IMPORT_EXTENSIONS: [&str; 2] = ["osz", "osr"];
const WINDOWS_TICKS_AT_UNIX_EPOCH: i64 = 621_355_968_000_000_000;
const WINDOWS_TICKS_PER_SECOND: i64 = 10_000_000;
const MAX_STORAGE_CONFIG_BYTES: u64 = 64 * 1024;
const MAX_MEDIA_FILE_BYTES: u64 = 512 * 1024 * 1024;
const REPLAY_HANDOFF_MAX_AGE_SECONDS: u64 = 24 * 60 * 60;
const GET_PROVIDER_STATUS_PATH: &str = "/aimmod.osu.v1.OsuService/GetProviderStatus";
const SEARCH_BEATMAP_ITEMS_PATH: &str = "/aimmod.osu.v1.OsuService/SearchBeatmapItems";
const GET_BEATMAP_ITEM_PATH: &str = "/aimmod.osu.v1.OsuService/GetBeatmapItem";
const GET_DOWNLOAD_HANDOFF_PATH: &str = "/aimmod.osu.v1.OsuService/GetDownloadHandoff";
const COLLECTOR_BASE_URL: &str = "https://osucollector.com";
const COLLECTOR_HYDRATION_CONCURRENCY: usize = 4;
const COLLECTOR_HYDRATION_ITEMS: usize = 4;
const COLLECTOR_DIFFICULTY_LIMIT: usize = 50;
static HANDOFF_SEQUENCE: AtomicU64 = AtomicU64::new(0);
static COLLECTOR_CLIENT: Lazy<Client> = Lazy::new(|| {
    Client::builder()
        .timeout(Duration::from_secs(12))
        .user_agent("AimMod/1.8 (https://aimmod.app)")
        .build()
        .expect("failed to build osu!Collector client")
});

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLazerInstallation {
    pub data_path: String,
    pub has_database: bool,
    pub has_file_store: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLazerStatus {
    pub detected: bool,
    pub installations: Vec<OsuLazerInstallation>,
    pub supported_import_extensions: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuImportResult {
    pub path: String,
    pub file_name: String,
    pub kind: String,
    pub status: String,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayHitCounts {
    pub count_300: u16,
    pub count_100: u16,
    pub count_50: u16,
    pub count_geki: u16,
    pub count_katu: u16,
    pub count_miss: u16,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayMods {
    pub bitmask: u32,
    pub names: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayInspection {
    pub path: String,
    pub file_name: String,
    pub mode: Option<String>,
    pub game_version: Option<u32>,
    pub beatmap_hash: Option<String>,
    pub player_name: Option<String>,
    pub replay_hash: Option<String>,
    pub counts: Option<OsuReplayHitCounts>,
    pub score: Option<u32>,
    pub max_combo: Option<u16>,
    pub perfect: Option<bool>,
    pub mods: Option<OsuReplayMods>,
    pub played_at: Option<String>,
    pub parse_error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapProvider {
    pub id: String,
    pub name: String,
    pub status: String,
    pub capabilities: Vec<String>,
    pub message: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapSearchFilters {
    pub mode: Option<String>,
    pub status: Option<String>,
    pub min_star_rating: Option<f64>,
    pub max_star_rating: Option<f64>,
    pub min_bpm: Option<f64>,
    pub max_bpm: Option<f64>,
    pub min_length_seconds: Option<u32>,
    pub max_length_seconds: Option<u32>,
    pub min_approach_rate: Option<f64>,
    pub max_approach_rate: Option<f64>,
    pub min_circle_size: Option<f64>,
    pub max_circle_size: Option<f64>,
    pub min_overall_difficulty: Option<f64>,
    pub max_overall_difficulty: Option<f64>,
    pub sort: Option<String>,
    pub descending: Option<bool>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapSearchRequest {
    pub provider: String,
    pub query: String,
    #[serde(default)]
    pub filters: OsuBeatmapSearchFilters,
    pub offset: Option<u32>,
    pub limit: Option<u32>,
    #[serde(default)]
    pub page_token: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapSearchItem {
    pub provider: String,
    pub source_id: String,
    pub item_kind: String,
    pub beatmapset_id: String,
    pub beatmap_id: Option<String>,
    pub artist: String,
    pub title: String,
    pub creator: String,
    pub difficulty_name: Option<String>,
    pub mode: Option<String>,
    pub star_rating: Option<f64>,
    pub bpm: Option<f64>,
    pub length_seconds: Option<u32>,
    pub status: Option<String>,
    pub cover_image_url: Option<String>,
    pub play_count: Option<u32>,
    pub favourite_count: Option<u32>,
    pub approach_rate: Option<f64>,
    pub circle_size: Option<f64>,
    pub overall_difficulty: Option<f64>,
    pub hp_drain: Option<f64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapSearchResponse {
    pub provider: String,
    pub items: Vec<OsuBeatmapSearchItem>,
    pub total: Option<u32>,
    pub next_offset: Option<u32>,
    pub next_page_token: Option<String>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapDownloadRequest {
    pub provider: String,
    #[serde(default)]
    pub source_id: Option<String>,
    pub beatmapset_id: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapDownloadResult {
    pub provider: String,
    pub beatmapset_id: String,
    pub status: String,
    pub message: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapItemRequest {
    pub provider: String,
    pub source_id: String,
    pub page_token: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuBeatmapItemResponse {
    pub provider: String,
    pub items: Vec<OsuBeatmapSearchItem>,
    pub next_page_token: Option<String>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubProviderStatus {
    provider: String,
    configured: bool,
    available: bool,
    supports_search: bool,
    supports_detail: bool,
    supports_download_handoff: bool,
    message: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubGetProviderStatusResponse {
    providers: Vec<HubProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubProviderCursor {
    provider: String,
    page_token: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubBeatmapDifficulty {
    beatmap_id: String,
    beatmapset_id: String,
    name: String,
    ruleset: String,
    status: String,
    stars: f64,
    bpm: f64,
    approach_rate: Option<f64>,
    circle_size: Option<f64>,
    overall_difficulty: Option<f64>,
    drain_rate: Option<f64>,
    length_seconds: u32,
    title: String,
    artist: String,
    creator: String,
    cover_url: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubBeatmapItem {
    provider: String,
    kind: String,
    source_id: String,
    title: String,
    artist: String,
    creator: String,
    status: String,
    cover_url: String,
    minimum_stars: f64,
    maximum_stars: f64,
    minimum_bpm: f64,
    maximum_bpm: f64,
    favourite_count: Option<u32>,
    play_count: Option<u32>,
    difficulties: Vec<HubBeatmapDifficulty>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubSearchBeatmapItemsResponse {
    items: Vec<HubBeatmapItem>,
    next_page_tokens: Vec<HubProviderCursor>,
    providers: Vec<HubProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubGetBeatmapItemResponse {
    item: Option<HubBeatmapItem>,
    next_page_token: String,
    provider: Option<HubProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubDownloadHandoff {
    kind: String,
    available: bool,
    uri: String,
    beatmapset_id: String,
    requires_osu_lazer: bool,
    requires_user_confirmation: bool,
    message: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubGetDownloadHandoffResponse {
    handoff: Option<HubDownloadHandoff>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct CollectorSearchResponse {
    next_page_cursor: u64,
    has_more: bool,
    results: Option<u32>,
    collections: Vec<CollectorCollection>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct CollectorCollection {
    id: u64,
    name: String,
    uploader: CollectorUploader,
    favourites: u64,
    difficulty_spread: HashMap<String, u64>,
    bpm_spread: HashMap<String, u64>,
    modes: HashMap<String, u64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct CollectorUploader {
    username: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct CollectorBeatmapsResponse {
    beatmaps: Vec<CollectorBeatmap>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct CollectorBeatmap {
    id: u64,
    beatmapset_id: u64,
    version: String,
    mode: String,
    status: String,
    difficulty_rating: f64,
    accuracy: f64,
    drain: f64,
    bpm: f64,
    cs: f64,
    ar: f64,
    hit_length: u32,
    beatmapset: CollectorBeatmapset,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct CollectorBeatmapset {
    creator: String,
    artist: String,
    title: String,
    covers: CollectorCovers,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct CollectorCovers {
    card: String,
    cover: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct ParsedReplayHeader {
    mode: String,
    game_version: u32,
    beatmap_hash: String,
    player_name: String,
    replay_hash: String,
    counts: OsuReplayHitCounts,
    score: u32,
    max_combo: u16,
    perfect: bool,
    mods: OsuReplayMods,
    played_at: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum LazerLauncher {
    Executable(PathBuf),
    #[cfg(target_os = "linux")]
    Flatpak,
    #[cfg(target_os = "macos")]
    MacApplication(PathBuf),
}

fn push_candidate(candidates: &mut Vec<PathBuf>, path: Option<PathBuf>) {
    if let Some(path) = path {
        candidates.push(path);
    }
}

pub(crate) fn lazer_data_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();

    push_candidate(
        &mut candidates,
        env::var_os("AIMMOD_OSU_LAZER_DATA_DIR").map(PathBuf::from),
    );

    #[cfg(target_os = "windows")]
    {
        push_candidate(
            &mut candidates,
            env::var_os("APPDATA").map(|path| PathBuf::from(path).join("osu")),
        );
    }

    #[cfg(target_os = "linux")]
    {
        push_candidate(
            &mut candidates,
            env::var_os("XDG_DATA_HOME").map(|path| PathBuf::from(path).join("osu")),
        );
        push_candidate(
            &mut candidates,
            env::var_os("HOME").map(|path| PathBuf::from(path).join(".local/share/osu")),
        );
        push_candidate(
            &mut candidates,
            env::var_os("HOME")
                .map(|path| PathBuf::from(path).join(".var/app/sh.ppy.osu/data/osu")),
        );
    }

    #[cfg(target_os = "macos")]
    {
        push_candidate(
            &mut candidates,
            env::var_os("HOME")
                .map(|path| PathBuf::from(path).join("Library/Application Support/osu")),
        );
    }

    let custom_paths: Vec<_> = candidates
        .iter()
        .filter_map(|path| custom_storage_path(path))
        .collect();
    candidates.extend(custom_paths);

    let mut seen = HashSet::new();
    candidates
        .into_iter()
        .filter(|path| seen.insert(path.clone()))
        .collect()
}

pub(crate) fn allow_lazer_file_store_assets(app: &AppHandle) {
    let scope = app.asset_protocol_scope();
    for root in lazer_data_candidates() {
        let Ok(store) = root.join("files").canonicalize() else {
            continue;
        };
        if !store.is_dir() {
            continue;
        }
        if let Err(error) = scope.allow_directory(&store, true) {
            log::warn!(
                "osu: could not allow read-only media access for {}: {error}",
                store.display()
            );
        }
    }
}

pub(crate) fn media_protocol_response(
    request: tauri::http::Request<Vec<u8>>,
) -> tauri::http::Response<Vec<u8>> {
    use tauri::http::{Method, Response, StatusCode, header};

    if request.method() != Method::GET && request.method() != Method::HEAD {
        return Response::builder()
            .status(StatusCode::METHOD_NOT_ALLOWED)
            .body(Vec::new())
            .expect("static media response");
    }
    let hash = request.uri().path().trim_start_matches('/');
    if hash.len() != 64 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Response::builder()
            .status(StatusCode::BAD_REQUEST)
            .body(Vec::new())
            .expect("static media response");
    }
    let hash = hash.to_ascii_lowercase();
    let Some(path) = lazer_data_candidates().into_iter().find_map(|root| {
        let store = root.join("files").canonicalize().ok()?;
        let candidate = store
            .join(&hash[..1])
            .join(&hash[..2])
            .join(&hash)
            .canonicalize()
            .ok()?;
        candidate
            .starts_with(&store)
            .then_some(candidate)
            .filter(|path| path.is_file())
    }) else {
        return Response::builder()
            .status(StatusCode::NOT_FOUND)
            .body(Vec::new())
            .expect("static media response");
    };

    let Ok(mut file) = File::open(&path) else {
        return Response::builder()
            .status(StatusCode::NOT_FOUND)
            .body(Vec::new())
            .expect("static media response");
    };
    let Ok(length) = file.metadata().map(|metadata| metadata.len()) else {
        return Response::builder()
            .status(StatusCode::INTERNAL_SERVER_ERROR)
            .body(Vec::new())
            .expect("static media response");
    };
    if length == 0 || length > MAX_MEDIA_FILE_BYTES {
        return Response::builder()
            .status(StatusCode::PAYLOAD_TOO_LARGE)
            .body(Vec::new())
            .expect("static media response");
    }
    let mut magic = [0_u8; 16];
    let magic_length = file.read(&mut magic).unwrap_or(0);
    let Some(content_type) = media_content_type(&magic[..magic_length]) else {
        return Response::builder()
            .status(StatusCode::UNSUPPORTED_MEDIA_TYPE)
            .body(Vec::new())
            .expect("static media response");
    };

    let requested_range = request
        .headers()
        .get(header::RANGE)
        .and_then(|value| value.to_str().ok());
    let range = match requested_range {
        Some(value) => match parse_single_byte_range(value, length) {
            Some(range) => Some(range),
            None => {
                return Response::builder()
                    .status(StatusCode::RANGE_NOT_SATISFIABLE)
                    .header(header::CONTENT_RANGE, format!("bytes */{length}"))
                    .body(Vec::new())
                    .expect("static media response");
            }
        },
        None => None,
    };
    let (start, end, status) = range
        .map(|(start, end)| (start, end, StatusCode::PARTIAL_CONTENT))
        .unwrap_or((0, length - 1, StatusCode::OK));
    let response_length = end - start + 1;
    let mut builder = Response::builder()
        .status(status)
        .header(header::CONTENT_TYPE, content_type)
        .header(header::CONTENT_LENGTH, response_length)
        .header(header::ACCEPT_RANGES, "bytes")
        .header(
            header::CACHE_CONTROL,
            "private, max-age=31536000, immutable",
        )
        .header(header::ACCESS_CONTROL_ALLOW_ORIGIN, "*")
        .header(
            header::ACCESS_CONTROL_EXPOSE_HEADERS,
            "content-range, accept-ranges",
        );
    if range.is_some() {
        builder = builder.header(
            header::CONTENT_RANGE,
            format!("bytes {start}-{end}/{length}"),
        );
    }
    if request.method() == Method::HEAD {
        return builder.body(Vec::new()).expect("static media response");
    }
    if file.seek(SeekFrom::Start(start)).is_err() {
        return Response::builder()
            .status(StatusCode::INTERNAL_SERVER_ERROR)
            .body(Vec::new())
            .expect("static media response");
    }
    let Ok(body_capacity) = usize::try_from(response_length) else {
        return Response::builder()
            .status(StatusCode::PAYLOAD_TOO_LARGE)
            .body(Vec::new())
            .expect("static media response");
    };
    let mut body = Vec::with_capacity(body_capacity);
    if file
        .by_ref()
        .take(response_length)
        .read_to_end(&mut body)
        .is_err()
    {
        return Response::builder()
            .status(StatusCode::INTERNAL_SERVER_ERROR)
            .body(Vec::new())
            .expect("static media response");
    }
    if body.len() as u64 != response_length {
        return Response::builder()
            .status(StatusCode::INTERNAL_SERVER_ERROR)
            .body(Vec::new())
            .expect("static media response");
    }
    builder.body(body).expect("static media response")
}

fn media_content_type(magic: &[u8]) -> Option<&'static str> {
    if magic.starts_with(b"ID3")
        || magic
            .windows(2)
            .next()
            .is_some_and(|bytes| bytes[0] == 0xff && bytes[1] & 0xe0 == 0xe0)
    {
        Some("audio/mpeg")
    } else if magic.starts_with(b"OggS") {
        Some("audio/ogg")
    } else if magic.starts_with(b"fLaC") {
        Some("audio/flac")
    } else if magic.starts_with(b"RIFF") && magic.get(8..12) == Some(b"WAVE") {
        Some("audio/wav")
    } else if magic.get(4..8) == Some(b"ftyp") {
        Some("audio/mp4")
    } else if magic.starts_with(b"\x89PNG\r\n\x1a\n") {
        Some("image/png")
    } else if magic.starts_with(b"\xff\xd8\xff") {
        Some("image/jpeg")
    } else if magic.starts_with(b"GIF87a") || magic.starts_with(b"GIF89a") {
        Some("image/gif")
    } else if magic.starts_with(b"RIFF") && magic.get(8..12) == Some(b"WEBP") {
        Some("image/webp")
    } else {
        None
    }
}

fn parse_single_byte_range(value: &str, length: u64) -> Option<(u64, u64)> {
    let value = value.strip_prefix("bytes=")?;
    if value.contains(',') || length == 0 {
        return None;
    }
    let (start, end) = value.split_once('-')?;
    if start.is_empty() {
        let suffix = end.parse::<u64>().ok()?.min(length);
        return (suffix > 0).then_some((length - suffix, length - 1));
    }
    let start = start.parse::<u64>().ok()?;
    if start >= length {
        return None;
    }
    let end = if end.is_empty() {
        length - 1
    } else {
        end.parse::<u64>().ok()?.min(length - 1)
    };
    (end >= start).then_some((start, end))
}

fn custom_storage_path(default_data_path: &Path) -> Option<PathBuf> {
    let config_path = default_data_path.join("storage.ini");
    let metadata = config_path.metadata().ok()?;
    if !metadata.is_file() || metadata.len() > MAX_STORAGE_CONFIG_BYTES {
        return None;
    }

    let contents = fs::read_to_string(config_path).ok()?;
    parse_storage_full_path(&contents)
}

fn parse_storage_full_path(contents: &str) -> Option<PathBuf> {
    contents.lines().find_map(|line| {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with(';') {
            return None;
        }

        let (key, value) = line.split_once('=')?;
        if !key.trim().eq_ignore_ascii_case("FullPath") {
            return None;
        }

        let value = value.trim().trim_matches('"');
        let path = PathBuf::from(value);
        (!value.is_empty() && path.is_absolute()).then_some(path)
    })
}

fn inspect_lazer_installation(path: &Path) -> Option<OsuLazerInstallation> {
    if !path.is_dir() {
        return None;
    }

    let database_path = path.join("client.realm");
    let has_database = database_path
        .metadata()
        .map(|metadata| metadata.is_file() && metadata.len() > 0)
        .unwrap_or(false);
    let has_file_store = path.join("files").is_dir();
    if !has_database && !has_file_store {
        return None;
    }

    Some(OsuLazerInstallation {
        data_path: path.to_string_lossy().into_owned(),
        has_database,
        has_file_store,
    })
}

pub fn get_lazer_status() -> OsuLazerStatus {
    let installations: Vec<_> = lazer_data_candidates()
        .iter()
        .filter_map(|path| inspect_lazer_installation(path))
        .collect();

    OsuLazerStatus {
        detected: !installations.is_empty(),
        installations,
        supported_import_extensions: SUPPORTED_IMPORT_EXTENSIONS
            .iter()
            .map(|extension| (*extension).to_string())
            .collect(),
    }
}

fn hub_provider(provider: &str) -> Result<&'static str, String> {
    match provider {
        "official" => Ok("PROVIDER_OSU_OFFICIAL"),
        "osuCollector" => Ok("PROVIDER_OSU_COLLECTOR"),
        _ => Err("AimMod does not recognize this beatmap provider.".to_string()),
    }
}

fn desktop_provider(provider: &str) -> Option<&'static str> {
    match provider {
        "PROVIDER_OSU_OFFICIAL" => Some("official"),
        "PROVIDER_OSU_COLLECTOR" => Some("osuCollector"),
        _ => None,
    }
}

fn provider_name(provider: &str) -> &'static str {
    match provider {
        "official" => "osu!",
        "osuCollector" => "osu!Collector",
        _ => "Unknown provider",
    }
}

fn map_provider_status(status: HubProviderStatus) -> Option<OsuBeatmapProvider> {
    let id = desktop_provider(&status.provider)?.to_string();
    let mut capabilities = Vec::new();
    if status.supports_search {
        capabilities.extend(["search".to_string(), "filter".to_string()]);
    }
    if status.supports_detail {
        capabilities.push("detail".to_string());
    }
    if status.supports_download_handoff {
        capabilities.extend(["download".to_string(), "import".to_string()]);
    }

    Some(OsuBeatmapProvider {
        name: provider_name(&id).to_string(),
        id,
        status: if status.available {
            "available"
        } else if !status.configured {
            "configurationRequired"
        } else {
            "unavailable"
        }
        .to_string(),
        capabilities,
        message: status.message,
    })
}

fn hub_ruleset(mode: Option<&str>) -> Result<Option<&'static str>, String> {
    match mode {
        None | Some("") | Some("all") => Ok(None),
        Some("osu") => Ok(Some("RULESET_OSU")),
        Some("taiko") => Ok(Some("RULESET_TAIKO")),
        Some("catch") => Ok(Some("RULESET_CATCH")),
        Some("mania") => Ok(Some("RULESET_MANIA")),
        Some(_) => Err("AimMod does not recognize this osu! ruleset.".to_string()),
    }
}

fn desktop_ruleset(ruleset: &str) -> Option<String> {
    match ruleset {
        "RULESET_OSU" => Some("osu"),
        "RULESET_TAIKO" => Some("taiko"),
        "RULESET_CATCH" => Some("catch"),
        "RULESET_MANIA" => Some("mania"),
        _ => None,
    }
    .map(str::to_string)
}

fn hub_sort(filters: &OsuBeatmapSearchFilters) -> &'static str {
    match (filters.sort.as_deref(), filters.descending) {
        (Some("stars-high"), _) => "difficulty_desc",
        (Some("stars-low"), _) => "difficulty_asc",
        (Some("title"), Some(false)) => "title_asc",
        (Some("title"), _) => "title_desc",
        (Some("updated"), Some(false)) => "updated_asc",
        (Some("updated"), _) => "updated_desc",
        (Some("relevance"), Some(false)) => "relevance_asc",
        _ => "relevance_desc",
    }
}

fn optional_range(
    minimum: Option<impl Serialize>,
    maximum: Option<impl Serialize>,
) -> serde_json::Value {
    let mut range = serde_json::Map::new();
    if let Some(value) = minimum {
        range.insert("minimum".to_string(), serde_json::to_value(value).unwrap());
    }
    if let Some(value) = maximum {
        range.insert("maximum".to_string(), serde_json::to_value(value).unwrap());
    }
    serde_json::Value::Object(range)
}

fn search_payload(
    request: &OsuBeatmapSearchRequest,
    provider: &str,
) -> Result<serde_json::Value, String> {
    if request.offset.unwrap_or(0) != 0
        && request.page_token.as_deref().unwrap_or_default().is_empty()
    {
        return Err(
            "This provider uses page tokens. Request the next page with nextPageToken.".to_string(),
        );
    }

    let mut filters = serde_json::Map::new();
    if let Some(ruleset) = hub_ruleset(request.filters.mode.as_deref())? {
        filters.insert("ruleset".to_string(), ruleset.into());
    }
    if let Some(status) = request
        .filters
        .status
        .as_deref()
        .filter(|status| !status.is_empty() && *status != "all")
    {
        filters.insert("status".to_string(), status.into());
    }
    if request.filters.min_star_rating.is_some() || request.filters.max_star_rating.is_some() {
        filters.insert(
            "stars".to_string(),
            optional_range(
                request.filters.min_star_rating,
                request.filters.max_star_rating,
            ),
        );
    }
    if request.filters.min_bpm.is_some() || request.filters.max_bpm.is_some() {
        filters.insert(
            "bpm".to_string(),
            optional_range(request.filters.min_bpm, request.filters.max_bpm),
        );
    }
    if request.filters.min_length_seconds.is_some() || request.filters.max_length_seconds.is_some()
    {
        filters.insert(
            "lengthSeconds".to_string(),
            optional_range(
                request.filters.min_length_seconds,
                request.filters.max_length_seconds,
            ),
        );
    }
    if request.filters.min_approach_rate.is_some() || request.filters.max_approach_rate.is_some() {
        filters.insert(
            "approachRate".to_string(),
            optional_range(
                request.filters.min_approach_rate,
                request.filters.max_approach_rate,
            ),
        );
    }
    if request.filters.min_circle_size.is_some() || request.filters.max_circle_size.is_some() {
        filters.insert(
            "circleSize".to_string(),
            optional_range(
                request.filters.min_circle_size,
                request.filters.max_circle_size,
            ),
        );
    }
    if request.filters.min_overall_difficulty.is_some()
        || request.filters.max_overall_difficulty.is_some()
    {
        filters.insert(
            "overallDifficulty".to_string(),
            optional_range(
                request.filters.min_overall_difficulty,
                request.filters.max_overall_difficulty,
            ),
        );
    }

    let mut payload = serde_json::json!({
        "query": request.query,
        "providers": [provider],
        "filters": filters,
        "sort": hub_sort(&request.filters),
    });
    if let Some(page_token) = request
        .page_token
        .as_deref()
        .filter(|token| !token.is_empty())
    {
        payload["pageTokens"] = serde_json::json!([{
            "provider": provider,
            "pageToken": page_token,
        }]);
    }
    Ok(payload)
}

fn summary_value(minimum: f64, maximum: f64) -> Option<f64> {
    if maximum > 0.0 {
        Some(maximum)
    } else if minimum > 0.0 {
        Some(minimum)
    } else {
        None
    }
}

fn map_hub_item(item: HubBeatmapItem) -> Vec<OsuBeatmapSearchItem> {
    let provider = desktop_provider(&item.provider)
        .unwrap_or("unknown")
        .to_string();
    if item.difficulties.is_empty() {
        let is_beatmapset = item.kind == "ITEM_KIND_BEATMAPSET";
        return vec![OsuBeatmapSearchItem {
            provider,
            source_id: item.source_id.clone(),
            item_kind: item.kind,
            beatmapset_id: if is_beatmapset {
                item.source_id
            } else {
                String::new()
            },
            beatmap_id: None,
            artist: item.artist,
            title: item.title,
            creator: item.creator,
            difficulty_name: None,
            mode: None,
            star_rating: summary_value(item.minimum_stars, item.maximum_stars),
            bpm: summary_value(item.minimum_bpm, item.maximum_bpm),
            length_seconds: None,
            status: (!item.status.is_empty()).then_some(item.status),
            cover_image_url: (!item.cover_url.is_empty()).then_some(item.cover_url),
            play_count: item.play_count,
            favourite_count: item.favourite_count,
            approach_rate: None,
            circle_size: None,
            overall_difficulty: None,
            hp_drain: None,
        }];
    }

    item.difficulties
        .into_iter()
        .map(|difficulty| OsuBeatmapSearchItem {
            provider: provider.clone(),
            source_id: item.source_id.clone(),
            item_kind: item.kind.clone(),
            beatmapset_id: difficulty.beatmapset_id,
            beatmap_id: (!difficulty.beatmap_id.is_empty()).then_some(difficulty.beatmap_id),
            artist: if difficulty.artist.is_empty() {
                item.artist.clone()
            } else {
                difficulty.artist
            },
            title: if difficulty.title.is_empty() {
                item.title.clone()
            } else {
                difficulty.title
            },
            creator: if difficulty.creator.is_empty() {
                item.creator.clone()
            } else {
                difficulty.creator
            },
            difficulty_name: (!difficulty.name.is_empty()).then_some(difficulty.name),
            mode: desktop_ruleset(&difficulty.ruleset),
            star_rating: (difficulty.stars > 0.0).then_some(difficulty.stars),
            bpm: (difficulty.bpm > 0.0).then_some(difficulty.bpm),
            length_seconds: (difficulty.length_seconds > 0).then_some(difficulty.length_seconds),
            status: if difficulty.status.is_empty() {
                (!item.status.is_empty()).then_some(item.status.clone())
            } else {
                Some(difficulty.status)
            },
            cover_image_url: if difficulty.cover_url.is_empty() {
                (!item.cover_url.is_empty()).then_some(item.cover_url.clone())
            } else {
                Some(difficulty.cover_url)
            },
            play_count: item.play_count,
            favourite_count: item.favourite_count,
            approach_rate: difficulty.approach_rate,
            circle_size: difficulty.circle_size,
            overall_difficulty: difficulty.overall_difficulty,
            hp_drain: difficulty.drain_rate,
        })
        .collect()
}

fn collector_spread_range(spread: &HashMap<String, u64>) -> Option<(f64, f64)> {
    let mut values: Vec<_> = spread
        .iter()
        .filter(|(_, count)| **count > 0)
        .filter_map(|(value, _)| value.parse::<f64>().ok())
        .filter(|value| value.is_finite())
        .collect();
    values.sort_by(f64::total_cmp);
    Some((*values.first()?, *values.last()?))
}

fn collector_range_matches(
    value: Option<(f64, f64)>,
    minimum: Option<f64>,
    maximum: Option<f64>,
) -> bool {
    let Some((item_minimum, item_maximum)) = value else {
        return minimum.is_none() && maximum.is_none();
    };
    minimum.is_none_or(|minimum| item_maximum >= minimum)
        && maximum.is_none_or(|maximum| item_minimum <= maximum)
}

fn collector_collection_matches(
    collection: &CollectorCollection,
    filters: &OsuBeatmapSearchFilters,
) -> bool {
    if filters
        .mode
        .as_deref()
        .is_some_and(|mode| mode != "all" && collection.modes.get(mode).copied().unwrap_or(0) == 0)
    {
        return false;
    }
    collector_range_matches(
        collector_spread_range(&collection.difficulty_spread),
        filters.min_star_rating,
        filters.max_star_rating,
    ) && collector_range_matches(
        collector_spread_range(&collection.bpm_spread),
        filters.min_bpm,
        filters.max_bpm,
    )
}

fn unsupported_collector_filters(filters: &OsuBeatmapSearchFilters) -> Vec<&'static str> {
    let mut unsupported = Vec::new();
    if filters.status.is_some() {
        unsupported.push("ranked status");
    }
    if filters.min_length_seconds.is_some() || filters.max_length_seconds.is_some() {
        unsupported.push("length");
    }
    if filters.min_approach_rate.is_some() || filters.max_approach_rate.is_some() {
        unsupported.push("approach rate");
    }
    if filters.min_circle_size.is_some() || filters.max_circle_size.is_some() {
        unsupported.push("circle size");
    }
    if filters.min_overall_difficulty.is_some() || filters.max_overall_difficulty.is_some() {
        unsupported.push("overall difficulty");
    }
    unsupported
}

fn map_collector_response(
    request: &OsuBeatmapSearchRequest,
    response: CollectorSearchResponse,
    hydrated: &HashMap<u64, Vec<CollectorBeatmap>>,
) -> OsuBeatmapSearchResponse {
    let limit = request.limit.unwrap_or(50).clamp(1, 100) as usize;
    let mut items: Vec<_> = response
        .collections
        .into_iter()
        .filter(|collection| collector_collection_matches(collection, &request.filters))
        .flat_map(|collection| {
            let source_id = collection.id.to_string();
            let favourite_count = Some(collection.favourites.min(u32::MAX as u64) as u32);
            if let Some(difficulties) = hydrated
                .get(&collection.id)
                .filter(|items| !items.is_empty())
            {
                return difficulties
                    .iter()
                    .take(COLLECTOR_DIFFICULTY_LIMIT)
                    .map(|difficulty| {
                        map_collector_difficulty(&source_id, favourite_count, difficulty)
                    })
                    .collect::<Vec<_>>();
            }
            let stars = collector_spread_range(&collection.difficulty_spread);
            let bpm = collector_spread_range(&collection.bpm_spread);
            vec![OsuBeatmapSearchItem {
                provider: "osuCollector".to_string(),
                source_id,
                item_kind: "ITEM_KIND_COLLECTION".to_string(),
                beatmapset_id: String::new(),
                beatmap_id: None,
                artist: String::new(),
                title: collection.name,
                creator: collection.uploader.username,
                difficulty_name: None,
                mode: None,
                star_rating: stars.map(|(_, maximum)| maximum),
                bpm: bpm.map(|(_, maximum)| maximum),
                length_seconds: None,
                status: None,
                cover_image_url: None,
                play_count: None,
                favourite_count,
                approach_rate: None,
                circle_size: None,
                overall_difficulty: None,
                hp_drain: None,
            }]
        })
        .take(limit)
        .collect();
    let next_page_token = (response.has_more && response.next_page_cursor > 0)
        .then(|| response.next_page_cursor.to_string());
    let total = response.results.or(Some(items.len() as u32));
    items.shrink_to_fit();
    OsuBeatmapSearchResponse {
        provider: "osuCollector".to_string(),
        items,
        total,
        next_offset: None,
        next_page_token,
        error: None,
    }
}

fn collector_optional_number(value: f64) -> Option<f64> {
    (value.is_finite() && value > 0.0).then_some(value)
}

fn map_collector_difficulty(
    source_id: &str,
    favourite_count: Option<u32>,
    difficulty: &CollectorBeatmap,
) -> OsuBeatmapSearchItem {
    OsuBeatmapSearchItem {
        provider: "osuCollector".to_string(),
        source_id: source_id.to_string(),
        item_kind: "ITEM_KIND_COLLECTION".to_string(),
        beatmapset_id: difficulty.beatmapset_id.to_string(),
        beatmap_id: (difficulty.id > 0).then(|| difficulty.id.to_string()),
        artist: difficulty.beatmapset.artist.clone(),
        title: difficulty.beatmapset.title.clone(),
        creator: difficulty.beatmapset.creator.clone(),
        difficulty_name: (!difficulty.version.is_empty()).then(|| difficulty.version.clone()),
        mode: (!difficulty.mode.is_empty()).then(|| difficulty.mode.clone()),
        star_rating: collector_optional_number(difficulty.difficulty_rating),
        bpm: collector_optional_number(difficulty.bpm),
        length_seconds: (difficulty.hit_length > 0).then_some(difficulty.hit_length),
        status: (!difficulty.status.is_empty()).then(|| difficulty.status.clone()),
        cover_image_url: [
            difficulty.beatmapset.covers.card.as_str(),
            difficulty.beatmapset.covers.cover.as_str(),
        ]
        .into_iter()
        .find(|value| !value.is_empty())
        .map(str::to_string),
        play_count: None,
        favourite_count,
        approach_rate: collector_optional_number(difficulty.ar),
        circle_size: collector_optional_number(difficulty.cs),
        overall_difficulty: collector_optional_number(difficulty.accuracy),
        hp_drain: collector_optional_number(difficulty.drain),
    }
}

async fn hydrate_collector_direct(
    collections: &[CollectorCollection],
) -> HashMap<u64, Vec<CollectorBeatmap>> {
    let collection_ids: Vec<_> = collections
        .iter()
        .take(COLLECTOR_HYDRATION_ITEMS)
        .map(|collection| collection.id)
        .collect();
    stream::iter(collection_ids.into_iter().map(|collection_id| async move {
        let result = COLLECTOR_CLIENT
            .get(format!(
                "{COLLECTOR_BASE_URL}/api/collections/{collection_id}/beatmapsv2"
            ))
            .send()
            .await
            .map_err(|error| error.to_string())?
            .error_for_status()
            .map_err(|error| error.to_string())?
            .json::<CollectorBeatmapsResponse>()
            .await
            .map_err(|error| error.to_string())?;
        Ok::<_, String>((collection_id, result.beatmaps))
    }))
    .buffer_unordered(COLLECTOR_HYDRATION_CONCURRENCY)
    .filter_map(|result| async move { result.ok() })
    .collect()
    .await
}

async fn search_collector_direct(
    request: &OsuBeatmapSearchRequest,
) -> Result<OsuBeatmapSearchResponse, String> {
    let unsupported = unsupported_collector_filters(&request.filters);
    if !unsupported.is_empty() {
        return Err(format!(
            "osu!Collector collection search cannot apply {}. Clear those filters or use osu!.",
            unsupported.join(", ")
        ));
    }
    let mut url = Url::parse(&format!("{COLLECTOR_BASE_URL}/api/collections/search"))
        .map_err(|error| error.to_string())?;
    {
        let mut query = url.query_pairs_mut();
        query.append_pair("search", request.query.trim());
        query.append_pair(
            "sortBy",
            if request.query.trim().is_empty() {
                "dateUploaded"
            } else {
                "_text_match"
            },
        );
        query.append_pair("orderBy", "desc");
        if let Some(page_token) = request
            .page_token
            .as_deref()
            .filter(|token| !token.is_empty())
        {
            if !page_token
                .chars()
                .all(|character| character.is_ascii_digit())
            {
                return Err("osu!Collector returned an invalid page token.".to_string());
            }
            query.append_pair("cursor", page_token);
        }
    }
    let response = COLLECTOR_CLIENT
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Could not reach osu!Collector: {error}"))?
        .error_for_status()
        .map_err(|error| format!("osu!Collector returned an error: {error}"))?
        .json::<CollectorSearchResponse>()
        .await
        .map_err(|error| format!("osu!Collector returned an unreadable response: {error}"))?;
    let hydration_candidates: Vec<_> = response
        .collections
        .iter()
        .filter(|collection| collector_collection_matches(collection, &request.filters))
        .take(COLLECTOR_HYDRATION_ITEMS)
        .cloned()
        .collect();
    let hydrated = hydrate_collector_direct(&hydration_candidates).await;
    Ok(map_collector_response(request, response, &hydrated))
}

async fn collector_direct_status(hub_error: &str) -> OsuBeatmapProvider {
    let probe = COLLECTOR_CLIENT
        .get(format!("{COLLECTOR_BASE_URL}/api/collections/recent"))
        .send()
        .await
        .and_then(reqwest::Response::error_for_status);
    let (status, message) = match probe {
        Ok(_) => (
            "available",
            format!(
                "osu!Collector is available directly. AimMod Hub status was unavailable: {hub_error}"
            ),
        ),
        Err(error) => (
            "unavailable",
            format!("osu!Collector and AimMod Hub are unavailable: {error}"),
        ),
    };
    OsuBeatmapProvider {
        id: "osuCollector".to_string(),
        name: "osu!Collector".to_string(),
        status: status.to_string(),
        capabilities: vec!["search".to_string(), "filter".to_string()],
        message,
    }
}

pub async fn get_beatmap_providers(app: &AppHandle) -> Result<Vec<OsuBeatmapProvider>, String> {
    match crate::hub_api::post_connect_json::<_, HubGetProviderStatusResponse>(
        app,
        GET_PROVIDER_STATUS_PATH,
        &serde_json::json!({}),
    )
    .await
    {
        Ok(response) => Ok(response
            .providers
            .into_iter()
            .filter_map(map_provider_status)
            .collect()),
        Err(error) => {
            let hub_error = error.to_string();
            Ok(vec![
                OsuBeatmapProvider {
                    id: "official".to_string(),
                    name: "osu!".to_string(),
                    status: "unavailable".to_string(),
                    capabilities: Vec::new(),
                    message: "Official osu! search needs AimMod Hub OAuth client credentials. AimMod does not read osu!lazer bearer tokens.".to_string(),
                },
                collector_direct_status(&hub_error).await,
            ])
        }
    }
}

pub async fn search_beatmaps(
    app: &AppHandle,
    request: OsuBeatmapSearchRequest,
) -> OsuBeatmapSearchResponse {
    let provider = request.provider.clone();
    let hub_provider = match hub_provider(&provider) {
        Ok(provider) => provider,
        Err(error) => return search_error(provider, error),
    };
    let payload = match search_payload(&request, hub_provider) {
        Ok(payload) => payload,
        Err(error) => return search_error(provider, error),
    };
    let response: HubSearchBeatmapItemsResponse = match crate::hub_api::post_connect_json(
        app,
        SEARCH_BEATMAP_ITEMS_PATH,
        &payload,
    )
    .await
    {
        Ok(response) => response,
        Err(error) => {
            if provider == "osuCollector" {
                return search_collector_direct(&request)
                        .await
                        .unwrap_or_else(|collector_error| {
                            search_error(
                                provider,
                                format!(
                                    "osu!Collector search failed: {collector_error} AimMod Hub was also unavailable: {error}"
                                ),
                            )
                        });
            }
            return search_error(provider, format!("AimMod Hub search failed: {error}"));
        }
    };

    if provider == "osuCollector"
        && response.items.is_empty()
        && response
            .providers
            .iter()
            .any(|status| status.provider == hub_provider && !status.available)
    {
        return search_collector_direct(&request)
            .await
            .unwrap_or_else(|error| search_error(provider, error));
    }

    let limit = request.limit.unwrap_or(50).clamp(1, 100) as usize;
    let mut items: Vec<_> = response.items.into_iter().flat_map(map_hub_item).collect();
    items.truncate(limit);
    let next_page_token = response
        .next_page_tokens
        .into_iter()
        .find(|cursor| cursor.provider == hub_provider)
        .map(|cursor| cursor.page_token)
        .filter(|token| !token.is_empty());
    let provider_error = response
        .providers
        .into_iter()
        .find(|status| status.provider == hub_provider && !status.available)
        .and_then(|status| (!status.message.is_empty()).then_some(status.message));

    OsuBeatmapSearchResponse {
        provider,
        total: Some(items.len() as u32),
        items,
        next_offset: None,
        next_page_token,
        error: provider_error,
    }
}

fn search_error(provider: String, error: impl Into<String>) -> OsuBeatmapSearchResponse {
    OsuBeatmapSearchResponse {
        provider,
        items: Vec::new(),
        total: None,
        next_offset: None,
        next_page_token: None,
        error: Some(error.into()),
    }
}

pub async fn get_beatmap_item(
    app: &AppHandle,
    request: OsuBeatmapItemRequest,
) -> OsuBeatmapItemResponse {
    let provider = request.provider.clone();
    let hub_provider = match hub_provider(&provider) {
        Ok(provider) => provider,
        Err(error) => return item_error(provider, error),
    };
    if request.source_id.trim().is_empty() {
        return item_error(provider, "A beatmap or collection source ID is required.");
    }
    let payload = serde_json::json!({
        "provider": hub_provider,
        "sourceId": request.source_id,
        "pageToken": request.page_token.unwrap_or_default(),
    });
    let response: HubGetBeatmapItemResponse =
        match crate::hub_api::post_connect_json(app, GET_BEATMAP_ITEM_PATH, &payload).await {
            Ok(response) => response,
            Err(error) => {
                return item_error(
                    provider,
                    format!("AimMod Hub detail request failed: {error}"),
                );
            }
        };
    let error = response
        .provider
        .filter(|status| !status.available)
        .and_then(|status| (!status.message.is_empty()).then_some(status.message));
    OsuBeatmapItemResponse {
        provider,
        items: response.item.map(map_hub_item).unwrap_or_default(),
        next_page_token: (!response.next_page_token.is_empty()).then_some(response.next_page_token),
        error,
    }
}

fn item_error(provider: String, error: impl Into<String>) -> OsuBeatmapItemResponse {
    OsuBeatmapItemResponse {
        provider,
        items: Vec::new(),
        next_page_token: None,
        error: Some(error.into()),
    }
}

fn canonical_beatmapset_id(value: &str) -> Option<String> {
    let parsed = value.parse::<u64>().ok()?;
    (parsed > 0 && parsed.to_string() == value).then(|| value.to_string())
}

fn validate_download_handoff(
    handoff: &HubDownloadHandoff,
    expected_beatmapset_id: &str,
) -> Result<(), String> {
    if canonical_beatmapset_id(expected_beatmapset_id).as_deref() != Some(expected_beatmapset_id) {
        return Err("AimMod received an invalid beatmapset ID.".to_string());
    }
    if !handoff.available {
        return Err(if handoff.message.is_empty() {
            "AimMod Hub did not provide a download handoff.".to_string()
        } else {
            handoff.message.clone()
        });
    }
    let expected_uri = format!("osu://dl/{expected_beatmapset_id}");
    if handoff.kind != "DOWNLOAD_HANDOFF_KIND_LAZER_URI"
        || !handoff.requires_osu_lazer
        || !handoff.requires_user_confirmation
        || handoff.beatmapset_id != expected_beatmapset_id
        || handoff.uri != expected_uri
    {
        return Err("AimMod Hub returned an invalid osu!lazer download handoff.".to_string());
    }
    Ok(())
}

pub async fn download_beatmap(
    app: &AppHandle,
    request: OsuBeatmapDownloadRequest,
) -> OsuBeatmapDownloadResult {
    let provider = request.provider.clone();
    let beatmapset_id = match canonical_beatmapset_id(&request.beatmapset_id) {
        Some(id) => id,
        None => {
            return download_result(
                provider,
                request.beatmapset_id,
                "rejected",
                "A positive canonical beatmapset ID is required.",
            );
        }
    };
    let hub_provider = match hub_provider(&provider) {
        Ok(provider) => provider,
        Err(error) => return download_result(provider, beatmapset_id, "rejected", error),
    };
    let source_id = request
        .source_id
        .as_deref()
        .filter(|value| !value.trim().is_empty());
    if provider == "osuCollector" && source_id.is_none() {
        return download_result(
            provider,
            beatmapset_id,
            "rejected",
            "Open the collection and select a beatmap before downloading it.",
        );
    }
    let payload = serde_json::json!({
        "provider": hub_provider,
        "sourceId": source_id.unwrap_or(&beatmapset_id),
        "beatmapsetId": beatmapset_id,
    });
    let response: HubGetDownloadHandoffResponse =
        match crate::hub_api::post_connect_json(app, GET_DOWNLOAD_HANDOFF_PATH, &payload).await {
            Ok(response) => response,
            Err(error) => {
                return download_result(
                    provider,
                    beatmapset_id,
                    "error",
                    format!("AimMod Hub download request failed: {error}"),
                );
            }
        };
    let Some(handoff) = response.handoff else {
        return download_result(
            provider,
            beatmapset_id,
            "unavailable",
            "AimMod Hub did not provide a download handoff.",
        );
    };
    if let Err(error) = validate_download_handoff(&handoff, &beatmapset_id) {
        return download_result(
            provider,
            beatmapset_id,
            if handoff.available {
                "rejected"
            } else {
                "unavailable"
            },
            error,
        );
    }
    let Some(launcher) = find_lazer_launcher() else {
        return download_result(
            provider,
            beatmapset_id,
            "error",
            "AimMod could not find an osu!lazer executable. Set AIMMOD_OSU_LAZER_EXECUTABLE to its path.",
        );
    };
    match launch_lazer_argument(&launcher, OsStr::new(&handoff.uri)) {
        Ok(()) => download_result(
            provider,
            beatmapset_id.clone(),
            "opened",
            format!("Asked osu!lazer to download beatmapset {beatmapset_id}."),
        ),
        Err(error) => download_result(
            provider,
            beatmapset_id,
            "error",
            format!("Could not open the download in osu!lazer: {error}"),
        ),
    }
}

fn download_result(
    provider: String,
    beatmapset_id: String,
    status: &str,
    message: impl Into<String>,
) -> OsuBeatmapDownloadResult {
    OsuBeatmapDownloadResult {
        provider,
        beatmapset_id,
        status: status.to_string(),
        message: message.into(),
    }
}

fn supported_import_kind(path: &Path) -> Option<&'static str> {
    match path
        .extension()
        .and_then(|extension| extension.to_str())
        .map(str::to_ascii_lowercase)
        .as_deref()
    {
        Some("osz") => Some("beatmap"),
        Some("osr") => Some("replay"),
        _ => None,
    }
}

fn import_result(
    path: &Path,
    kind: &str,
    status: &str,
    message: impl Into<String>,
) -> OsuImportResult {
    OsuImportResult {
        path: path.to_string_lossy().into_owned(),
        file_name: display_file_name(path),
        kind: kind.to_string(),
        status: status.to_string(),
        message: message.into(),
    }
}

fn display_file_name(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| "Unknown file".to_string())
}

fn validate_beatmap_archive(path: &Path) -> Result<(), String> {
    let file = File::open(path).map_err(|error| format!("Could not read the beatmap: {error}"))?;
    let archive = zip::ZipArchive::new(file)
        .map_err(|_| "The selected .osz file is not a valid ZIP archive.".to_string())?;

    let contains_beatmap = archive
        .file_names()
        .any(|name| name.to_ascii_lowercase().ends_with(".osu"));
    if !contains_beatmap {
        return Err("The selected .osz archive does not contain a beatmap file.".to_string());
    }

    Ok(())
}

fn validate_import_file(path: &Path, kind: &str) -> Result<(), String> {
    match kind {
        "beatmap" => validate_beatmap_archive(path),
        "replay" => parse_replay_file(path).map(|_| ()),
        _ => Err("AimMod does not support this file type.".to_string()),
    }
}

fn executable_file(path: PathBuf) -> Option<PathBuf> {
    if !path.is_file() {
        return None;
    }

    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mode = path.metadata().ok()?.permissions().mode();
        if mode & 0o111 == 0 {
            return None;
        }
    }

    path.canonicalize().ok()
}

pub(crate) fn find_lazer_launcher() -> Option<LazerLauncher> {
    if let Some(path) = env::var_os("AIMMOD_OSU_LAZER_EXECUTABLE")
        .map(PathBuf::from)
        .and_then(executable_file)
    {
        return Some(LazerLauncher::Executable(path));
    }

    #[cfg(target_os = "windows")]
    {
        let root = env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)?
            .join("osulazer");
        for path in [root.join("current/osu!.exe"), root.join("osu!.exe")] {
            if let Some(path) = executable_file(path) {
                return Some(LazerLauncher::Executable(path));
            }
        }
        return None;
    }

    #[cfg(target_os = "linux")]
    {
        let home = env::var_os("HOME").map(PathBuf::from);
        let candidates = [
            home.as_ref()
                .map(|path| path.join(".local/bin/osu.AppImage")),
            home.as_ref()
                .map(|path| path.join("Applications/osu.AppImage")),
            home.as_ref()
                .map(|path| path.join("Games/osu-lazer/osu.AppImage")),
            Some(PathBuf::from("/opt/osu-lazer/osu.AppImage")),
        ];

        for candidate in candidates.into_iter().flatten() {
            if let Some(path) = executable_file(candidate) {
                return Some(LazerLauncher::Executable(path));
            }
        }

        let has_flatpak_install = home
            .as_ref()
            .is_some_and(|path| path.join(".local/share/flatpak/app/sh.ppy.osu").is_dir())
            || Path::new("/var/lib/flatpak/app/sh.ppy.osu").is_dir();
        if has_flatpak_install && executable_file(PathBuf::from("/usr/bin/flatpak")).is_some() {
            return Some(LazerLauncher::Flatpak);
        }
    }

    #[cfg(target_os = "macos")]
    {
        for application in [
            PathBuf::from("/Applications/osu!.app"),
            env::var_os("HOME")
                .map(PathBuf::from)
                .unwrap_or_default()
                .join("Applications/osu!.app"),
        ] {
            if application.join("Contents/MacOS/osu!").is_file() {
                return Some(LazerLauncher::MacApplication(application));
            }
        }
    }

    None
}

pub(crate) fn launch_lazer_argument(launcher: &LazerLauncher, argument: &OsStr) -> io::Result<()> {
    let mut command = match launcher {
        LazerLauncher::Executable(executable) => {
            let mut command = Command::new(executable);
            command.arg(argument);
            command
        }
        #[cfg(target_os = "linux")]
        LazerLauncher::Flatpak => {
            let mut command = Command::new("/usr/bin/flatpak");
            command.args(["run", "sh.ppy.osu"]).arg(argument);
            command
        }
        #[cfg(target_os = "macos")]
        LazerLauncher::MacApplication(application) => {
            let mut command = Command::new("/usr/bin/open");
            command.arg("-a").arg(application).arg(argument);
            command
        }
    };

    let mut child = command.spawn()?;
    std::thread::spawn(move || {
        let _ = child.wait();
    });
    Ok(())
}

fn create_beatmap_handoff(app: &AppHandle, source: &Path) -> Result<PathBuf, String> {
    let handoff_dir = app
        .path()
        .app_cache_dir()
        .map_err(|error| format!("Could not locate AimMod's cache folder: {error}"))?
        .join("osu-import-handoff");
    fs::create_dir_all(&handoff_dir)
        .map_err(|error| format!("Could not create AimMod's import handoff folder: {error}"))?;

    let sequence = HANDOFF_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let handoff_path = handoff_dir.join(format!("import-{}-{sequence}.osz", std::process::id()));
    fs::copy(source, &handoff_path)
        .map_err(|error| format!("Could not copy the file for safe import: {error}"))?;
    Ok(handoff_path)
}

pub fn import_files(app: &AppHandle, paths: Vec<String>) -> Vec<OsuImportResult> {
    if paths.len() > MAX_IMPORT_FILES {
        return vec![OsuImportResult {
            path: String::new(),
            file_name: "Import selection".to_string(),
            kind: "unknown".to_string(),
            status: "rejected".to_string(),
            message: format!("Select at most {MAX_IMPORT_FILES} files at once."),
        }];
    }

    let launcher = find_lazer_launcher();

    paths
        .into_iter()
        .map(|raw_path| {
            let path = PathBuf::from(raw_path);
            let Some(kind) = supported_import_kind(&path) else {
                return import_result(
                    &path,
                    "unknown",
                    "rejected",
                    "AimMod currently imports .osz beatmaps and .osr replays into osu!lazer.",
                );
            };

            let canonical = match path.canonicalize() {
                Ok(path) if path.is_file() => path,
                _ => {
                    return import_result(
                        &path,
                        kind,
                        "error",
                        "The selected file no longer exists or cannot be read.",
                    );
                }
            };

            if let Err(error) = validate_import_file(&canonical, kind) {
                return import_result(&canonical, kind, "rejected", error);
            }

            let Some(launcher) = launcher.as_ref() else {
                return import_result(
                    &canonical,
                    kind,
                    "error",
                    "AimMod could not find an osu!lazer executable. Set AIMMOD_OSU_LAZER_EXECUTABLE to its path.",
                );
            };
            let handoff_path = match kind {
                "beatmap" => match create_beatmap_handoff(app, &canonical) {
                    Ok(path) => Some(path),
                    Err(error) => return import_result(&canonical, kind, "error", error),
                },
                "replay" => None,
                _ => unreachable!("kind was validated above"),
            };
            let launch_path = handoff_path.as_deref().unwrap_or(&canonical);

            match launch_lazer_argument(launcher, launch_path.as_os_str()) {
                Ok(()) => import_result(
                    &canonical,
                    kind,
                    "opened",
                    if kind == "beatmap" {
                        "Handed a temporary copy to osu!lazer. The selected source file was preserved."
                    } else {
                        "Asked osu!lazer to open the validated replay. The replay file remains in place."
                    },
                ),
                Err(error) => {
                    if let Some(handoff_path) = handoff_path {
                        let _ = fs::remove_file(handoff_path);
                    }
                    import_result(
                        &canonical,
                        kind,
                        "error",
                        format!("Could not open the file in osu!lazer: {error}"),
                    )
                }
            }
        })
        .collect()
}

pub fn inspect_replay_files(paths: Vec<String>) -> Vec<OsuReplayInspection> {
    if paths.len() > MAX_IMPORT_FILES {
        return vec![replay_parse_error(
            Path::new(""),
            format!("Select at most {MAX_IMPORT_FILES} replay files at once."),
        )];
    }

    paths
        .into_iter()
        .map(|raw_path| {
            let path = PathBuf::from(raw_path);
            if supported_import_kind(&path) != Some("replay") {
                return replay_parse_error(&path, "Select an .osr replay file.");
            }

            let canonical = match path.canonicalize() {
                Ok(path) if path.is_file() => path,
                _ => {
                    return replay_parse_error(
                        &path,
                        "The selected replay no longer exists or cannot be read.",
                    );
                }
            };

            match parse_replay_file(&canonical) {
                Ok(header) => OsuReplayInspection {
                    path: canonical.to_string_lossy().into_owned(),
                    file_name: display_file_name(&canonical),
                    mode: Some(header.mode),
                    game_version: Some(header.game_version),
                    beatmap_hash: Some(header.beatmap_hash),
                    player_name: Some(header.player_name),
                    replay_hash: Some(header.replay_hash),
                    counts: Some(header.counts),
                    score: Some(header.score),
                    max_combo: Some(header.max_combo),
                    perfect: Some(header.perfect),
                    mods: Some(header.mods),
                    played_at: Some(header.played_at),
                    parse_error: None,
                },
                Err(error) => replay_parse_error(&canonical, error),
            }
        })
        .collect()
}

pub fn open_local_replay(app: &AppHandle, raw_path: String) -> OsuImportResult {
    let path = PathBuf::from(raw_path);
    let canonical = match path.canonicalize() {
        Ok(path) if path.is_file() => path,
        _ => {
            return import_result(
                &path,
                "replay",
                "error",
                "The local replay no longer exists or cannot be read.",
            );
        }
    };
    let source_kind = lazer_replay_source(&canonical);
    if source_kind.is_none() {
        return import_result(
            &canonical,
            "replay",
            "rejected",
            "AimMod only opens replays from a detected osu!lazer store or exports folder.",
        );
    }
    let is_store_replay = source_kind == Some("store");
    let valid = if is_store_replay {
        inspect_lazer_store_replay(&canonical).is_some()
    } else {
        parse_replay_file(&canonical).is_ok()
    };
    if !valid {
        return import_result(
            &canonical,
            "replay",
            "rejected",
            "The selected local file is not a valid osu!lazer replay.",
        );
    }
    let Some(launcher) = find_lazer_launcher() else {
        return import_result(
            &canonical,
            "replay",
            "error",
            "AimMod could not find an osu!lazer executable. Set AIMMOD_OSU_LAZER_EXECUTABLE to its path.",
        );
    };

    let handoff = if is_store_replay {
        match create_replay_handoff(app, &canonical) {
            Ok(path) => Some(path),
            Err(error) => return import_result(&canonical, "replay", "error", error),
        }
    } else {
        None
    };
    let launch_path = handoff.as_deref().unwrap_or(&canonical);
    match launch_lazer_argument(&launcher, launch_path.as_os_str()) {
        Ok(()) => import_result(
            &canonical,
            "replay",
            "opened",
            if is_store_replay {
                "Asked osu!lazer to open an AimMod-managed replay handoff. The lazer store was not changed."
            } else {
                "Asked osu!lazer to open the exported replay. The replay file remains in place."
            },
        ),
        Err(error) => import_result(
            &canonical,
            "replay",
            "error",
            format!("Could not open the replay in osu!lazer: {error}"),
        ),
    }
}

fn lazer_replay_source(path: &Path) -> Option<&'static str> {
    let roots: Vec<_> = lazer_data_candidates()
        .into_iter()
        .filter_map(|root| root.canonicalize().ok())
        .collect();
    lazer_replay_source_in_roots(path, &roots)
}

fn lazer_replay_source_in_roots(path: &Path, roots: &[PathBuf]) -> Option<&'static str> {
    for root in roots {
        if root
            .join("exports")
            .canonicalize()
            .ok()
            .is_some_and(|exports| path.parent() == Some(exports.as_path()))
            && path
                .extension()
                .and_then(|extension| extension.to_str())
                .is_some_and(|extension| extension.eq_ignore_ascii_case("osr"))
        {
            return Some("export");
        }
        let Ok(relative) = path.strip_prefix(root.join("files")) else {
            continue;
        };
        let components: Vec<_> = relative
            .components()
            .filter_map(|component| component.as_os_str().to_str())
            .collect();
        if components.len() == 3
            && components[0].len() == 1
            && components[1].len() == 2
            && components[2].len() == 64
            && components[1].starts_with(components[0])
            && components[2].starts_with(components[1])
            && components
                .iter()
                .all(|component| is_lower_hex(component, component.len()))
        {
            return Some("store");
        }
    }
    None
}

fn create_replay_handoff(app: &AppHandle, source: &Path) -> Result<PathBuf, String> {
    let directory = app
        .path()
        .app_cache_dir()
        .map_err(|error| format!("Could not locate AimMod's cache folder: {error}"))?
        .join("osu-replay-handoff");
    fs::create_dir_all(&directory)
        .map_err(|error| format!("Could not create AimMod's replay handoff folder: {error}"))?;
    cleanup_replay_handoffs(&directory);

    let source_name = source
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| is_lower_hex(name, 64))
        .ok_or_else(|| "The lazer replay has an invalid content-store path.".to_string())?;
    let handoff = directory.join(format!("replay-{source_name}.osr"));
    let source_length = source
        .metadata()
        .map_err(|error| format!("Could not inspect the local replay: {error}"))?
        .len();
    let current_matches = handoff
        .symlink_metadata()
        .map(|metadata| metadata.file_type().is_file() && metadata.len() == source_length)
        .unwrap_or(false);
    if !current_matches {
        if let Ok(metadata) = handoff.symlink_metadata() {
            if !metadata.file_type().is_file() {
                return Err("AimMod's replay handoff path is not a regular file.".to_string());
            }
            fs::remove_file(&handoff)
                .map_err(|error| format!("Could not replace the replay handoff: {error}"))?;
        }
        let temporary = directory.join(format!(
            ".replay-{}-{}.tmp",
            std::process::id(),
            HANDOFF_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::copy(source, &temporary)
            .map_err(|error| format!("Could not cache the local replay: {error}"))?;
        fs::rename(&temporary, &handoff).map_err(|error| {
            let _ = fs::remove_file(&temporary);
            format!("Could not finish the local replay handoff: {error}")
        })?;
    }
    Ok(handoff)
}

fn cleanup_replay_handoffs(directory: &Path) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    let now = std::time::SystemTime::now();
    for entry in entries.flatten() {
        let path = entry.path();
        let old_regular_file = path
            .symlink_metadata()
            .ok()
            .filter(|metadata| metadata.file_type().is_file())
            .and_then(|metadata| metadata.modified().ok())
            .and_then(|modified| now.duration_since(modified).ok())
            .is_some_and(|age| age.as_secs() > REPLAY_HANDOFF_MAX_AGE_SECONDS);
        if old_regular_file {
            let _ = fs::remove_file(path);
        }
    }
}

fn replay_parse_error(path: &Path, error: impl Into<String>) -> OsuReplayInspection {
    OsuReplayInspection {
        path: path.to_string_lossy().into_owned(),
        file_name: display_file_name(path),
        mode: None,
        game_version: None,
        beatmap_hash: None,
        player_name: None,
        replay_hash: None,
        counts: None,
        score: None,
        max_combo: None,
        perfect: None,
        mods: None,
        played_at: None,
        parse_error: Some(error.into()),
    }
}

fn parse_replay_file(path: &Path) -> Result<ParsedReplayHeader, String> {
    let metadata = path
        .metadata()
        .map_err(|error| format!("Could not inspect the replay: {error}"))?;
    if metadata.len() > MAX_REPLAY_FILE_BYTES {
        return Err(format!(
            "The replay is larger than AimMod's {} MiB inspection limit.",
            MAX_REPLAY_FILE_BYTES / 1024 / 1024
        ));
    }

    let file = File::open(path).map_err(|error| format!("Could not read the replay: {error}"))?;
    parse_replay_header(BufReader::new(file))
}

pub(crate) fn inspect_lazer_store_replay(path: &Path) -> Option<OsuReplayInspection> {
    let metadata = path.metadata().ok()?;
    if !metadata.is_file() || metadata.len() < 96 || metadata.len() > MAX_REPLAY_FILE_BYTES {
        return None;
    }

    let bytes = fs::read(path).ok()?;
    if let Some(expected_hash) = path
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| is_lower_hex(name, 64))
    {
        if format!("{:x}", Sha256::digest(&bytes)) != expected_hash {
            return None;
        }
    }
    let mut cursor = io::Cursor::new(bytes.as_slice());
    let header = parse_replay_header(&mut cursor).ok()?;

    if header.game_version < 30_000_000
        || !is_lower_hex(&header.beatmap_hash, 32)
        || !is_lower_hex(&header.replay_hash, 32)
        || header.player_name.is_empty()
        || header.player_name.len() > 64
    {
        return None;
    }

    let replay_length = read_i32(&mut cursor).ok()?;
    if replay_length < 13 || !skip_bounded_bytes(&mut cursor, replay_length as usize) {
        return None;
    }
    if read_i64(&mut cursor).is_err() {
        return None;
    }

    let score_info_length = read_i32(&mut cursor).ok()?;
    if score_info_length < 13 || !skip_bounded_bytes(&mut cursor, score_info_length as usize) {
        return None;
    }
    if cursor.position() != bytes.len() as u64 {
        return None;
    }

    Some(OsuReplayInspection {
        path: path.to_string_lossy().into_owned(),
        file_name: display_file_name(path),
        mode: Some(header.mode),
        game_version: Some(header.game_version),
        beatmap_hash: Some(header.beatmap_hash),
        player_name: Some(header.player_name),
        replay_hash: Some(header.replay_hash),
        counts: Some(header.counts),
        score: Some(header.score),
        max_combo: Some(header.max_combo),
        perfect: Some(header.perfect),
        mods: Some(header.mods),
        played_at: Some(header.played_at),
        parse_error: None,
    })
}

fn is_lower_hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn skip_bounded_bytes(cursor: &mut io::Cursor<&[u8]>, length: usize) -> bool {
    let Ok(length) = u64::try_from(length) else {
        return false;
    };
    let Some(end) = cursor.position().checked_add(length) else {
        return false;
    };
    if end > cursor.get_ref().len() as u64 {
        return false;
    }
    cursor.set_position(end);
    true
}

fn parse_replay_header(mut reader: impl Read) -> Result<ParsedReplayHeader, String> {
    let mode = match read_u8(&mut reader)? {
        0 => "osu",
        1 => "taiko",
        2 => "catch",
        3 => "mania",
        other => return Err(format!("The replay uses unknown game mode {other}.")),
    }
    .to_string();
    let game_version = read_u32(&mut reader)?;
    let beatmap_hash = read_osu_string(&mut reader, "beatmap hash")?;
    let player_name = read_osu_string(&mut reader, "player name")?;
    let replay_hash = read_osu_string(&mut reader, "replay hash")?;
    let counts = OsuReplayHitCounts {
        count_300: read_u16(&mut reader)?,
        count_100: read_u16(&mut reader)?,
        count_50: read_u16(&mut reader)?,
        count_geki: read_u16(&mut reader)?,
        count_katu: read_u16(&mut reader)?,
        count_miss: read_u16(&mut reader)?,
    };
    let score = read_u32(&mut reader)?;
    let max_combo = read_u16(&mut reader)?;
    let perfect = read_u8(&mut reader)? != 0;
    let mod_bitmask = read_u32(&mut reader)?;
    let _life_graph = read_osu_string(&mut reader, "life graph")?;
    let ticks = read_i64(&mut reader)?;
    let played_at = windows_ticks_to_rfc3339(ticks)?;

    Ok(ParsedReplayHeader {
        mode,
        game_version,
        beatmap_hash,
        player_name,
        replay_hash,
        counts,
        score,
        max_combo,
        perfect,
        mods: OsuReplayMods {
            bitmask: mod_bitmask,
            names: legacy_mod_names(mod_bitmask),
        },
        played_at,
    })
}

fn read_osu_string(reader: &mut impl Read, field: &str) -> Result<String, String> {
    match read_u8(reader)? {
        0x00 => Ok(String::new()),
        0x0b => {
            let length = read_uleb128(reader)?;
            if length > MAX_REPLAY_STRING_BYTES as u64 {
                return Err(format!("The replay {field} is too large."));
            }

            let mut bytes = vec![0_u8; length as usize];
            reader
                .read_exact(&mut bytes)
                .map_err(|_| format!("The replay ended inside the {field}."))?;
            String::from_utf8(bytes).map_err(|_| format!("The replay {field} is not valid UTF-8."))
        }
        marker => Err(format!(
            "The replay has an invalid string marker 0x{marker:02x} for {field}."
        )),
    }
}

fn read_uleb128(reader: &mut impl Read) -> Result<u64, String> {
    let mut value = 0_u64;
    for shift in (0..=63).step_by(7) {
        let byte = read_u8(reader)?;
        let chunk = u64::from(byte & 0x7f);
        if shift == 63 && chunk > 1 {
            return Err("The replay contains an oversized string length.".to_string());
        }
        value |= chunk << shift;
        if byte & 0x80 == 0 {
            return Ok(value);
        }
    }
    Err("The replay contains an invalid string length.".to_string())
}

fn read_u8(reader: &mut impl Read) -> Result<u8, String> {
    let mut bytes = [0_u8; 1];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(bytes[0])
}

fn read_u16(reader: &mut impl Read) -> Result<u16, String> {
    let mut bytes = [0_u8; 2];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(u16::from_le_bytes(bytes))
}

fn read_u32(reader: &mut impl Read) -> Result<u32, String> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(u32::from_le_bytes(bytes))
}

fn read_i32(reader: &mut impl Read) -> Result<i32, String> {
    let mut bytes = [0_u8; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(i32::from_le_bytes(bytes))
}

fn read_i64(reader: &mut impl Read) -> Result<i64, String> {
    let mut bytes = [0_u8; 8];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(i64::from_le_bytes(bytes))
}

fn windows_ticks_to_rfc3339(ticks: i64) -> Result<String, String> {
    let unix_ticks = ticks
        .checked_sub(WINDOWS_TICKS_AT_UNIX_EPOCH)
        .ok_or_else(|| "The replay timestamp is outside the supported range.".to_string())?;
    let seconds = unix_ticks.div_euclid(WINDOWS_TICKS_PER_SECOND);
    let nanoseconds = unix_ticks.rem_euclid(WINDOWS_TICKS_PER_SECOND) as u32 * 100;
    let timestamp = DateTime::<Utc>::from_timestamp(seconds, nanoseconds)
        .ok_or_else(|| "The replay timestamp is outside the supported range.".to_string())?;
    Ok(timestamp.to_rfc3339_opts(SecondsFormat::AutoSi, true))
}

fn legacy_mod_names(bitmask: u32) -> Vec<String> {
    const MODS: &[(u32, &str)] = &[
        (1 << 0, "NoFail"),
        (1 << 1, "Easy"),
        (1 << 2, "TouchDevice"),
        (1 << 3, "Hidden"),
        (1 << 4, "HardRock"),
        (1 << 5, "SuddenDeath"),
        (1 << 6, "DoubleTime"),
        (1 << 7, "Relax"),
        (1 << 8, "HalfTime"),
        (1 << 9, "Nightcore"),
        (1 << 10, "Flashlight"),
        (1 << 11, "Autoplay"),
        (1 << 12, "SpunOut"),
        (1 << 13, "Autopilot"),
        (1 << 14, "Perfect"),
        (1 << 15, "Key4"),
        (1 << 16, "Key5"),
        (1 << 17, "Key6"),
        (1 << 18, "Key7"),
        (1 << 19, "Key8"),
        (1 << 20, "FadeIn"),
        (1 << 21, "Random"),
        (1 << 22, "Cinema"),
        (1 << 23, "TargetPractice"),
        (1 << 24, "Key9"),
        (1 << 25, "Coop"),
        (1 << 26, "Key1"),
        (1 << 27, "Key3"),
        (1 << 28, "Key2"),
        (1 << 30, "Mirror"),
    ];

    let mut names: Vec<String> = MODS
        .iter()
        .filter(|(flag, _)| bitmask & flag != 0)
        .map(|(_, name)| (*name).to_string())
        .collect();
    let known = MODS.iter().fold(0_u32, |known, (flag, _)| known | flag);
    let unknown = bitmask & !known;
    if unknown != 0 {
        names.push(format!("Unknown(0x{unknown:08x})"));
    }
    names
}

#[cfg(test)]
mod tests {
    use super::{
        CollectorBeatmap, CollectorSearchResponse, HubBeatmapItem, HubDownloadHandoff,
        HubSearchBeatmapItemsResponse, MAX_REPLAY_STRING_BYTES, OsuBeatmapSearchFilters,
        OsuBeatmapSearchRequest, ParsedReplayHeader, canonical_beatmapset_id,
        inspect_lazer_store_replay, lazer_replay_source_in_roots, legacy_mod_names,
        map_collector_response, map_hub_item, map_provider_status, media_content_type,
        parse_replay_file, parse_replay_header, parse_single_byte_range, parse_storage_full_path,
        read_uleb128, search_payload, supported_import_kind, validate_download_handoff,
        windows_ticks_to_rfc3339,
    };
    use std::collections::HashMap;
    use std::io::Cursor;
    use std::path::Path;

    fn write_osu_string(output: &mut Vec<u8>, value: &str) {
        if value.is_empty() {
            output.push(0);
            return;
        }

        output.push(0x0b);
        let mut remaining = value.len() as u64;
        loop {
            let mut byte = (remaining & 0x7f) as u8;
            remaining >>= 7;
            if remaining != 0 {
                byte |= 0x80;
            }
            output.push(byte);
            if remaining == 0 {
                break;
            }
        }
        output.extend_from_slice(value.as_bytes());
    }

    #[test]
    fn detects_supported_extensionless_media_containers() {
        assert_eq!(media_content_type(b"ID3\x04\0\0"), Some("audio/mpeg"));
        assert_eq!(media_content_type(b"\xff\xfb\x90\x64"), Some("audio/mpeg"));
        assert_eq!(media_content_type(b"OggS\0\x02"), Some("audio/ogg"));
        assert_eq!(media_content_type(b"fLaC\0\0"), Some("audio/flac"));
        assert_eq!(media_content_type(b"RIFF1234WAVE"), Some("audio/wav"));
        assert_eq!(media_content_type(b"\x89PNG\r\n\x1a\n"), Some("image/png"));
        assert_eq!(media_content_type(b"\xff\xd8\xff\xe0"), Some("image/jpeg"));
        assert_eq!(media_content_type(b"RIFF1234WEBP"), Some("image/webp"));
        assert_eq!(media_content_type(b"osu file format v14"), None);
    }

    #[test]
    fn parses_single_media_ranges_without_escaping_file_bounds() {
        assert_eq!(parse_single_byte_range("bytes=0-99", 1000), Some((0, 99)));
        assert_eq!(
            parse_single_byte_range("bytes=900-", 1000),
            Some((900, 999))
        );
        assert_eq!(
            parse_single_byte_range("bytes=-100", 1000),
            Some((900, 999))
        );
        assert_eq!(
            parse_single_byte_range("bytes=900-2000", 1000),
            Some((900, 999))
        );
        assert_eq!(parse_single_byte_range("bytes=1000-", 1000), None);
        assert_eq!(parse_single_byte_range("bytes=0-1,4-5", 1000), None);
    }

    fn replay_fixture() -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.push(0);
        bytes.extend_from_slice(&202_608_01_u32.to_le_bytes());
        write_osu_string(&mut bytes, "0123456789abcdef0123456789abcdef");
        write_osu_string(&mut bytes, "veryCrunchy");
        write_osu_string(&mut bytes, "fedcba9876543210fedcba9876543210");
        for count in [500_u16, 12, 3, 8, 4, 1] {
            bytes.extend_from_slice(&count.to_le_bytes());
        }
        bytes.extend_from_slice(&1_234_567_u32.to_le_bytes());
        bytes.extend_from_slice(&777_u16.to_le_bytes());
        bytes.push(1);
        bytes.extend_from_slice(&((1_u32 << 3) | (1_u32 << 4)).to_le_bytes());
        write_osu_string(&mut bytes, "0|1,1000|0.5");
        bytes.extend_from_slice(&638_712_864_000_000_000_i64.to_le_bytes());
        bytes
    }

    fn complete_lazer_replay_fixture() -> Vec<u8> {
        let mut bytes = replay_fixture();
        bytes[1..5].copy_from_slice(&30_000_019_u32.to_le_bytes());
        let compressed_replay = [0_u8; 13];
        bytes.extend_from_slice(&(compressed_replay.len() as i32).to_le_bytes());
        bytes.extend_from_slice(&compressed_replay);
        bytes.extend_from_slice(&(-1_i64).to_le_bytes());
        let compressed_score = [0_u8; 13];
        bytes.extend_from_slice(&(compressed_score.len() as i32).to_le_bytes());
        bytes.extend_from_slice(&compressed_score);
        bytes
    }

    #[test]
    fn accepts_lazer_beatmaps_and_replays_case_insensitively() {
        assert_eq!(supported_import_kind(Path::new("map.osz")), Some("beatmap"));
        assert_eq!(supported_import_kind(Path::new("play.OSR")), Some("replay"));
    }

    #[test]
    fn rejects_stable_databases_and_unrelated_files() {
        assert_eq!(supported_import_kind(Path::new("collection.db")), None);
        assert_eq!(supported_import_kind(Path::new("notes.txt")), None);
    }

    #[test]
    fn parses_legacy_replay_header_without_reading_frame_data() {
        let parsed = parse_replay_header(Cursor::new(replay_fixture())).unwrap();
        assert_eq!(
            parsed,
            ParsedReplayHeader {
                mode: "osu".to_string(),
                game_version: 202_608_01,
                beatmap_hash: "0123456789abcdef0123456789abcdef".to_string(),
                player_name: "veryCrunchy".to_string(),
                replay_hash: "fedcba9876543210fedcba9876543210".to_string(),
                counts: super::OsuReplayHitCounts {
                    count_300: 500,
                    count_100: 12,
                    count_50: 3,
                    count_geki: 8,
                    count_katu: 4,
                    count_miss: 1,
                },
                score: 1_234_567,
                max_combo: 777,
                perfect: true,
                mods: super::OsuReplayMods {
                    bitmask: (1 << 3) | (1 << 4),
                    names: vec!["Hidden".to_string(), "HardRock".to_string()],
                },
                played_at: "2025-01-01T00:00:00Z".to_string(),
            }
        );
    }

    #[test]
    fn rejects_truncated_and_invalid_replay_headers() {
        let truncated = parse_replay_header(Cursor::new([0_u8, 1, 2])).unwrap_err();
        assert_eq!(truncated, "The replay header is truncated.");

        let mut invalid_mode = replay_fixture();
        invalid_mode[0] = 4;
        assert_eq!(
            parse_replay_header(Cursor::new(invalid_mode)).unwrap_err(),
            "The replay uses unknown game mode 4."
        );
    }

    #[test]
    fn identifies_complete_lazer_replays_in_the_content_store() {
        let replay = tempfile::NamedTempFile::new().unwrap();
        std::fs::write(replay.path(), complete_lazer_replay_fixture()).unwrap();
        let inspected = inspect_lazer_store_replay(replay.path()).unwrap();
        assert_eq!(inspected.player_name.as_deref(), Some("veryCrunchy"));
        assert_eq!(inspected.score, Some(1_234_567));

        let truncated = tempfile::NamedTempFile::new().unwrap();
        std::fs::write(truncated.path(), replay_fixture()).unwrap();
        assert!(inspect_lazer_store_replay(truncated.path()).is_none());
    }

    #[test]
    fn authorizes_only_exact_lazer_replay_locations() {
        let root = tempfile::tempdir().unwrap();
        let exports = root.path().join("exports");
        std::fs::create_dir_all(&exports).unwrap();
        let exported = exports.join("play.osr");
        std::fs::write(&exported, b"fixture").unwrap();

        let hash = "a".repeat(64);
        let stored = root
            .path()
            .join("files")
            .join(&hash[..1])
            .join(&hash[..2])
            .join(&hash);
        std::fs::create_dir_all(stored.parent().unwrap()).unwrap();
        std::fs::write(&stored, b"fixture").unwrap();
        let unrelated = root.path().join("play.osr");
        std::fs::write(&unrelated, b"fixture").unwrap();

        let roots = [root.path().canonicalize().unwrap()];
        assert_eq!(
            lazer_replay_source_in_roots(&exported.canonicalize().unwrap(), &roots),
            Some("export")
        );
        assert_eq!(
            lazer_replay_source_in_roots(&stored.canonicalize().unwrap(), &roots),
            Some("store")
        );
        assert_eq!(
            lazer_replay_source_in_roots(&unrelated.canonicalize().unwrap(), &roots),
            None
        );
    }

    #[test]
    fn rejects_oversized_and_overflowing_string_lengths() {
        let oversized = [0x81, 0x80, 0x40];
        assert!(
            read_uleb128(&mut Cursor::new(oversized)).unwrap() > MAX_REPLAY_STRING_BYTES as u64
        );

        let overflow = [0xff; 10];
        assert_eq!(
            read_uleb128(&mut Cursor::new(overflow)).unwrap_err(),
            "The replay contains an oversized string length."
        );
    }

    #[test]
    fn converts_windows_ticks_and_preserves_unknown_mod_bits() {
        assert_eq!(
            windows_ticks_to_rfc3339(621_355_968_000_000_000).unwrap(),
            "1970-01-01T00:00:00Z"
        );
        assert_eq!(
            legacy_mod_names((1 << 3) | (1 << 29)),
            vec!["Hidden".to_string(), "Unknown(0x20000000)".to_string()]
        );
    }

    #[test]
    fn reads_only_absolute_custom_storage_paths() {
        let absolute = if cfg!(windows) {
            Path::new(r"C:\games\osu-data")
        } else {
            Path::new("/mnt/games/osu-data")
        };
        assert_eq!(
            parse_storage_full_path(&format!("[Storage]\nFullPath = {}\n", absolute.display())),
            Some(absolute.to_path_buf())
        );
        assert_eq!(parse_storage_full_path("FullPath = ../osu-data\n"), None);
        assert_eq!(parse_storage_full_path("OtherPath = /tmp/not-osu\n"), None);
    }

    #[test]
    fn maps_search_request_to_connect_json() {
        let request = OsuBeatmapSearchRequest {
            provider: "official".to_string(),
            query: "stream practice".to_string(),
            filters: OsuBeatmapSearchFilters {
                mode: Some("osu".to_string()),
                status: Some("ranked".to_string()),
                min_star_rating: Some(4.0),
                max_star_rating: Some(6.5),
                min_bpm: None,
                max_bpm: Some(220.0),
                min_length_seconds: Some(60),
                max_length_seconds: Some(180),
                min_approach_rate: Some(8.0),
                max_approach_rate: Some(9.7),
                min_circle_size: Some(3.5),
                max_circle_size: Some(5.0),
                min_overall_difficulty: Some(7.0),
                max_overall_difficulty: Some(9.5),
                sort: Some("stars-high".to_string()),
                descending: Some(true),
            },
            offset: Some(0),
            limit: Some(20),
            page_token: Some("next-1".to_string()),
        };
        let payload = search_payload(&request, "PROVIDER_OSU_OFFICIAL").unwrap();
        assert_eq!(payload["providers"][0], "PROVIDER_OSU_OFFICIAL");
        assert_eq!(payload["filters"]["ruleset"], "RULESET_OSU");
        assert_eq!(payload["filters"]["stars"]["minimum"], 4.0);
        assert_eq!(payload["filters"]["lengthSeconds"]["maximum"], 180);
        assert_eq!(payload["filters"]["approachRate"]["minimum"], 8.0);
        assert_eq!(payload["filters"]["approachRate"]["maximum"], 9.7);
        assert_eq!(payload["filters"]["circleSize"]["minimum"], 3.5);
        assert_eq!(payload["filters"]["circleSize"]["maximum"], 5.0);
        assert_eq!(payload["filters"]["overallDifficulty"]["minimum"], 7.0);
        assert_eq!(payload["filters"]["overallDifficulty"]["maximum"], 9.5);
        assert_eq!(payload["sort"], "difficulty_desc");
        assert_eq!(payload["pageTokens"][0]["pageToken"], "next-1");
    }

    #[test]
    fn deserializes_and_maps_advanced_difficulty_ranges() {
        let request: OsuBeatmapSearchRequest = serde_json::from_value(serde_json::json!({
            "provider": "collector",
            "query": "aim control",
            "filters": {
                "minApproachRate": 7.5,
                "maxApproachRate": 10.0,
                "minCircleSize": 3.0,
                "maxCircleSize": 6.5,
                "minOverallDifficulty": 6.0,
                "maxOverallDifficulty": 9.25
            }
        }))
        .unwrap();

        let payload = search_payload(&request, "PROVIDER_COLLECTOR").unwrap();
        assert_eq!(payload["filters"]["approachRate"]["minimum"], 7.5);
        assert_eq!(payload["filters"]["approachRate"]["maximum"], 10.0);
        assert_eq!(payload["filters"]["circleSize"]["minimum"], 3.0);
        assert_eq!(payload["filters"]["circleSize"]["maximum"], 6.5);
        assert_eq!(payload["filters"]["overallDifficulty"]["minimum"], 6.0);
        assert_eq!(payload["filters"]["overallDifficulty"]["maximum"], 9.25);
    }

    #[test]
    fn maps_hub_status_and_difficulties_to_stable_desktop_shapes() {
        let response: HubSearchBeatmapItemsResponse = serde_json::from_value(serde_json::json!({
            "providers": [{
                "provider": "PROVIDER_OSU_OFFICIAL",
                "configured": true,
                "available": true,
                "supportsSearch": true,
                "supportsDetail": true,
                "supportsDownloadHandoff": true,
                "message": "Ready"
            }],
            "items": [{
                "provider": "PROVIDER_OSU_OFFICIAL",
                "kind": "ITEM_KIND_BEATMAPSET",
                "sourceId": "9001",
                "title": "Test map",
                "artist": "Test artist",
                "creator": "Mapper",
                "coverUrl": "https://assets.ppy.sh/cover.jpg",
                "playCount": 123456,
                "favouriteCount": 789,
                "difficulties": [{
                    "beatmapId": "123",
                    "beatmapsetId": "9001",
                    "name": "Insane",
                    "ruleset": "RULESET_OSU",
                    "status": "ranked",
                    "stars": 5.25,
                    "bpm": 180.0,
                    "approachRate": 9.4,
                    "circleSize": 4.0,
                    "overallDifficulty": 8.7,
                    "drainRate": 6.5,
                    "lengthSeconds": 95
                }]
            }]
        }))
        .unwrap();
        let provider = map_provider_status(response.providers.into_iter().next().unwrap()).unwrap();
        assert_eq!(provider.id, "official");
        assert_eq!(provider.status, "available");
        assert_eq!(
            provider.capabilities,
            ["search", "filter", "detail", "download", "import"]
        );

        let items = map_hub_item(response.items.into_iter().next().unwrap());
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].source_id, "9001");
        assert_eq!(items[0].beatmapset_id, "9001");
        assert_eq!(items[0].beatmap_id.as_deref(), Some("123"));
        assert_eq!(items[0].mode.as_deref(), Some("osu"));
        assert_eq!(items[0].star_rating, Some(5.25));
        assert_eq!(items[0].play_count, Some(123456));
        assert_eq!(items[0].favourite_count, Some(789));
        assert_eq!(items[0].approach_rate, Some(9.4));
        assert_eq!(items[0].circle_size, Some(4.0));
        assert_eq!(items[0].overall_difficulty, Some(8.7));
        assert_eq!(items[0].hp_drain, Some(6.5));

        let desktop_json = serde_json::to_value(&items[0]).unwrap();
        assert_eq!(desktop_json["playCount"], 123456);
        assert_eq!(desktop_json["favouriteCount"], 789);
        assert_eq!(desktop_json["approachRate"], 9.4);
        assert_eq!(desktop_json["circleSize"], 4.0);
        assert_eq!(desktop_json["overallDifficulty"], 8.7);
        assert_eq!(desktop_json["hpDrain"], 6.5);
    }

    #[test]
    fn maps_live_collector_collection_search_without_oauth() {
        let response: CollectorSearchResponse = serde_json::from_value(serde_json::json!({
            "nextPageCursor": 2,
            "hasMore": true,
            "results": 1334,
            "collections": [{
                "id": 2213,
                "name": "Aim Control",
                "uploader": { "username": "Dimension Shift" },
                "favourites": 587,
                "difficultySpread": { "1": 35, "5": 186, "7": 26 },
                "bpmSpread": { "150": 206, "200": 40, "270": 7 },
                "modes": { "osu": 647, "taiko": 7 }
            }]
        }))
        .unwrap();
        let request = OsuBeatmapSearchRequest {
            provider: "osuCollector".to_string(),
            query: "aim".to_string(),
            filters: OsuBeatmapSearchFilters {
                mode: Some("osu".to_string()),
                min_star_rating: Some(4.0),
                max_star_rating: Some(8.0),
                min_bpm: Some(140.0),
                max_bpm: Some(300.0),
                ..OsuBeatmapSearchFilters::default()
            },
            offset: Some(0),
            limit: Some(50),
            page_token: None,
        };

        let difficulties: Vec<CollectorBeatmap> = serde_json::from_value(serde_json::json!([{
            "id": 456,
            "beatmapset_id": 123,
            "version": "Another",
            "mode": "osu",
            "status": "ranked",
            "difficulty_rating": 6.25,
            "accuracy": 9.1,
            "drain": 6.5,
            "bpm": 222.0,
            "cs": 4.0,
            "ar": 9.6,
            "hit_length": 95,
            "beatmapset": {
                "creator": "Mapper",
                "artist": "Artist",
                "title": "Freedom Dive",
                "covers": { "card": "https://img.example/card.jpg" }
            }
        }]))
        .unwrap();
        let hydrated = HashMap::from([(2213, difficulties)]);
        let mapped = map_collector_response(&request, response, &hydrated);
        assert_eq!(mapped.items.len(), 1);
        assert_eq!(mapped.items[0].source_id, "2213");
        assert_eq!(mapped.items[0].item_kind, "ITEM_KIND_COLLECTION");
        assert_eq!(mapped.items[0].beatmap_id.as_deref(), Some("456"));
        assert_eq!(mapped.items[0].beatmapset_id, "123");
        assert_eq!(mapped.items[0].title, "Freedom Dive");
        assert_eq!(mapped.items[0].creator, "Mapper");
        assert_eq!(mapped.items[0].difficulty_name.as_deref(), Some("Another"));
        assert_eq!(mapped.items[0].star_rating, Some(6.25));
        assert_eq!(mapped.items[0].bpm, Some(222.0));
        assert_eq!(mapped.items[0].approach_rate, Some(9.6));
        assert_eq!(mapped.items[0].circle_size, Some(4.0));
        assert_eq!(mapped.items[0].overall_difficulty, Some(9.1));
        assert_eq!(mapped.items[0].hp_drain, Some(6.5));
        assert_eq!(
            mapped.items[0].cover_image_url.as_deref(),
            Some("https://img.example/card.jpg")
        );
        assert_eq!(mapped.items[0].favourite_count, Some(587));
        assert_eq!(mapped.next_page_token.as_deref(), Some("2"));
        assert_eq!(mapped.total, Some(1334));
        assert!(mapped.error.is_none());
    }

    #[test]
    fn keeps_collection_ids_out_of_beatmapset_handoffs() {
        let items = map_hub_item(HubBeatmapItem {
            provider: "PROVIDER_OSU_COLLECTOR".to_string(),
            kind: "ITEM_KIND_COLLECTION".to_string(),
            source_id: "collection-42".to_string(),
            title: "Aim training".to_string(),
            ..HubBeatmapItem::default()
        });
        assert_eq!(items[0].source_id, "collection-42");
        assert!(items[0].beatmapset_id.is_empty());
    }

    #[test]
    fn rejects_noncanonical_ids_and_unverified_download_handoffs() {
        assert_eq!(canonical_beatmapset_id("123"), Some("123".to_string()));
        assert_eq!(canonical_beatmapset_id("0123"), None);
        assert_eq!(canonical_beatmapset_id("0"), None);
        assert_eq!(canonical_beatmapset_id("123?web=1"), None);

        let valid = HubDownloadHandoff {
            kind: "DOWNLOAD_HANDOFF_KIND_LAZER_URI".to_string(),
            available: true,
            uri: "osu://dl/123".to_string(),
            beatmapset_id: "123".to_string(),
            requires_osu_lazer: true,
            requires_user_confirmation: true,
            message: String::new(),
        };
        assert!(validate_download_handoff(&valid, "123").is_ok());

        for invalid in [
            HubDownloadHandoff {
                uri: "https://osu.ppy.sh/beatmapsets/123".to_string(),
                ..valid.clone()
            },
            HubDownloadHandoff {
                uri: "osu://dl/124".to_string(),
                ..valid.clone()
            },
            HubDownloadHandoff {
                beatmapset_id: "124".to_string(),
                ..valid.clone()
            },
            HubDownloadHandoff {
                requires_osu_lazer: false,
                ..valid.clone()
            },
            HubDownloadHandoff {
                requires_user_confirmation: false,
                ..valid.clone()
            },
            HubDownloadHandoff {
                available: false,
                ..valid.clone()
            },
        ] {
            assert!(validate_download_handoff(&invalid, "123").is_err());
        }
    }

    #[test]
    fn parses_external_replay_fixture_when_requested() {
        let Some(path) = std::env::var_os("AIMMOD_OSU_TEST_REPLAY") else {
            return;
        };
        let parsed = parse_replay_file(Path::new(&path)).unwrap();
        assert_eq!(parsed.player_name, "verycrunchy");
        assert_eq!(parsed.game_version, 30_000_019);
        assert_eq!(parsed.beatmap_hash, "26e47e7ed9ca09553cf2b51fd064786d");
        assert_eq!(parsed.replay_hash, "a716eb5190e59becb712587d5500b672");
        assert_eq!(parsed.score, 556_291);
        assert_eq!(parsed.max_combo, 116);
        assert!(parsed.played_at.starts_with("2026-09-01T15:49:18."));
        assert!(inspect_lazer_store_replay(Path::new(&path)).is_some());
        eprintln!("external replay metadata: {parsed:?}");
    }
}
