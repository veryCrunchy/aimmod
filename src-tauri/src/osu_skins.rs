use chrono::{SecondsFormat, Utc};
use futures_util::StreamExt;
use once_cell::sync::Lazy;
use reqwest::{Client, Url, redirect::Policy};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::ffi::OsStr;
use std::fs::{self, File};
use std::io::{BufReader, Read};
use std::net::IpAddr;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime};
use tauri::{AppHandle, Manager};
use tokio::io::AsyncWriteExt;

const MAX_SKIN_FILES: usize = 20_000;
const MAX_SKIN_PACKAGE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_SKIN_ENTRY_BYTES: u64 = 256 * 1024 * 1024;
const MAX_SKIN_UNCOMPRESSED_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_SKIN_INI_BYTES: u64 = 1024 * 1024;
const MAX_SKIN_METADATA_BYTES: u64 = 64 * 1024;
const MAX_ARCHIVE_PATH_BYTES: usize = 1024;
const MAX_COMPRESSION_RATIO: u64 = 500;
const MAX_LOCAL_IMPORT_FILES: usize = 20;
const STALE_HANDOFF_AGE: Duration = Duration::from_secs(24 * 60 * 60);
const OSUCK_BASE_URL: &str = "https://skins.osuck.net";
const OSUCK_MAX_RESPONSE_BYTES: usize = 4 * 1024 * 1024;
const OSUCK_MIN_REQUEST_INTERVAL: Duration = Duration::from_millis(350);
static SKIN_FILE_SEQUENCE: AtomicU64 = AtomicU64::new(0);
static OSUCK_REQUEST_GATE: Lazy<tokio::sync::Mutex<Option<Instant>>> =
    Lazy::new(|| tokio::sync::Mutex::new(None));
static SKIN_DOWNLOAD_CLIENT: Lazy<Client> = Lazy::new(|| {
    Client::builder()
        .timeout(Duration::from_secs(90))
        .redirect(Policy::none())
        .build()
        .expect("failed to build the osu skin download client")
});
static OSUCK_CLIENT: Lazy<Client> = Lazy::new(|| {
    Client::builder()
        .timeout(Duration::from_secs(30))
        .redirect(Policy::none())
        .user_agent("AimMod/1.8 (+https://aimmod.app)")
        .build()
        .expect("failed to build the skins.osuck.net client")
});
const GET_SKIN_PROVIDER_STATUS_PATH: &str = "/aimmod.osu.v1.OsuService/GetSkinProviderStatus";
const SEARCH_SKINS_PATH: &str = "/aimmod.osu.v1.OsuService/SearchSkins";
const GET_SKIN_PATH: &str = "/aimmod.osu.v1.OsuService/GetSkin";
const GET_SKIN_DOWNLOAD_HANDOFF_PATH: &str = "/aimmod.osu.v1.OsuService/GetSkinDownloadHandoff";

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuSkinSearchFilters {
    pub rulesets: Vec<String>,
    pub aspect_ratio: Option<String>,
    pub creator: Option<String>,
    pub player: Option<String>,
    pub tag: Option<String>,
    pub include_sensitive: Option<bool>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinSearchRequest {
    pub provider: String,
    pub query: String,
    pub page_token: Option<String>,
    pub limit: Option<u32>,
    #[serde(default)]
    pub filters: OsuSkinSearchFilters,
    pub sort: Option<String>,
    pub direction: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinDetailRequest {
    pub provider: String,
    pub source_id: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinInstallRequest {
    pub provider: String,
    pub source_id: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinProvider {
    pub id: String,
    pub name: String,
    pub status: String,
    pub capabilities: Vec<String>,
    pub message: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuSkinSearchItem {
    pub provider: String,
    pub source_id: String,
    pub name: String,
    pub creator: String,
    pub players: Vec<String>,
    pub rulesets: Vec<String>,
    pub aspect_ratios: Vec<String>,
    pub tags: Vec<String>,
    pub sensitive: Option<bool>,
    pub thumbnail_url: String,
    pub view_count: u64,
    pub download_count: u64,
    pub file_size_bytes: u64,
    pub counts_are_approximate: bool,
    pub file_size_is_approximate: bool,
    pub submitted_at: String,
    pub updated_at: String,
    pub screenshots: Vec<OsuSkinScreenshot>,
    pub download_available: bool,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuSkinScreenshot {
    pub label: String,
    pub image_url: String,
    pub width: u32,
    pub height: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinSearchResponse {
    pub provider: String,
    pub items: Vec<OsuSkinSearchItem>,
    pub next_page_token: Option<String>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinDetailResponse {
    pub provider: String,
    pub item: Option<OsuSkinSearchItem>,
    pub error: Option<String>,
}

fn deserialize_u64ish<'de, D>(deserializer: D) -> Result<u64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = serde_json::Value::deserialize(deserializer)?;
    match value {
        serde_json::Value::Number(number) => number
            .as_u64()
            .ok_or_else(|| serde::de::Error::custom("expected an unsigned integer")),
        serde_json::Value::String(value) => value
            .parse::<u64>()
            .map_err(|_| serde::de::Error::custom("expected an unsigned integer string")),
        _ => Err(serde::de::Error::custom("expected an unsigned integer")),
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubSkinProviderStatus {
    provider: String,
    available: bool,
    supports_search: bool,
    supports_detail: bool,
    supports_screenshots: bool,
    supports_direct_download: bool,
    requires_interactive_download_verification: bool,
    message: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubSkinProviderCursor {
    provider: String,
    page_token: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubSkinDownloadHandoff {
    kind: String,
    available: bool,
    uri: String,
    file_name: String,
    #[serde(deserialize_with = "deserialize_u64ish")]
    expected_size_bytes: u64,
    sha256: String,
    #[serde(deserialize_with = "deserialize_u64ish")]
    max_download_bytes: u64,
    requires_interactive_verification: bool,
    expires_at_iso: String,
    message: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubSkinItem {
    provider: String,
    source_id: String,
    name: String,
    creator: String,
    players: Vec<String>,
    rulesets: Vec<String>,
    aspect_ratios: Vec<String>,
    tags: Vec<String>,
    sensitive: Option<bool>,
    thumbnail_url: String,
    #[serde(deserialize_with = "deserialize_u64ish")]
    view_count: u64,
    #[serde(deserialize_with = "deserialize_u64ish")]
    download_count: u64,
    #[serde(deserialize_with = "deserialize_u64ish")]
    file_size_bytes: u64,
    counts_are_approximate: bool,
    file_size_is_approximate: bool,
    submitted_at_iso: String,
    updated_at_iso: String,
    screenshots: Vec<OsuSkinScreenshot>,
    download_handoff: Option<HubSkinDownloadHandoff>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubGetSkinProviderStatusResponse {
    providers: Vec<HubSkinProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubSearchSkinsResponse {
    items: Vec<HubSkinItem>,
    next_page_tokens: Vec<HubSkinProviderCursor>,
    providers: Vec<HubSkinProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubGetSkinResponse {
    item: Option<HubSkinItem>,
    provider: Option<HubSkinProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubGetSkinDownloadHandoffResponse {
    handoff: Option<HubSkinDownloadHandoff>,
    provider: Option<HubSkinProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkinStats {
    views: u64,
    downloads: u64,
    size_max: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkinCreator {
    name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkinScreenshot {
    checksum: String,
    category_id: i32,
    title: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkinFileStats {
    google: u64,
    mega: u64,
    mediafire: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkinFileSizes {
    osk: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkinFile {
    checksum: String,
    name: String,
    stats: OsuckSkinFileStats,
    size: OsuckSkinFileSizes,
    google: Vec<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuckSkin {
    id: u64,
    name: String,
    version: String,
    stats: OsuckSkinStats,
    creators: Vec<OsuckSkinCreator>,
    screenshots: Vec<OsuckSkinScreenshot>,
    #[serde(alias = "metadata_modes")]
    modes: Vec<u8>,
    #[serde(alias = "metadata_ratios")]
    ratios: Vec<u8>,
    keywords: Vec<String>,
    files: Vec<OsuckSkinFile>,
    created_at: String,
    released_at: String,
    updated_at: String,
    #[serde(rename = "_warning_type")]
    warning_type: i32,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagedOsuSkin {
    pub id: String,
    pub name: String,
    pub author: String,
    pub file_name: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub provider: Option<String>,
    pub source_id: Option<String>,
    pub installed_at: String,
    pub import_status: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinInstallResult {
    pub id: Option<String>,
    pub name: Option<String>,
    pub author: Option<String>,
    pub file_name: String,
    pub size_bytes: Option<u64>,
    pub sha256: Option<String>,
    pub provider: Option<String>,
    pub source_id: Option<String>,
    pub status: String,
    pub message: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuSkinRemoveResult {
    pub id: String,
    pub status: String,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct SkinArchiveMetadata {
    name: String,
    author: String,
    file_count: usize,
    uncompressed_bytes: u64,
}

fn hub_skin_provider(provider: &str) -> Result<&'static str, String> {
    match provider {
        "osuSkins" => Ok("SKIN_PROVIDER_OSU_SKINS"),
        "osuCK" => Ok("SKIN_PROVIDER_OSUCK"),
        _ => Err("AimMod does not recognize this skin provider.".to_string()),
    }
}

fn desktop_skin_provider(provider: &str) -> Option<&'static str> {
    match provider {
        "SKIN_PROVIDER_OSU_SKINS" => Some("osuSkins"),
        "SKIN_PROVIDER_OSUCK" => Some("osuCK"),
        _ => None,
    }
}

fn skin_provider_name(provider: &str) -> &'static str {
    match provider {
        "osuSkins" => "osuskins.net",
        "osuCK" => "skins.osuck.net",
        _ => "Unknown provider",
    }
}

fn map_skin_provider_status(status: HubSkinProviderStatus) -> Option<OsuSkinProvider> {
    let id = desktop_skin_provider(&status.provider)?.to_string();
    let mut capabilities = Vec::new();
    if status.supports_search {
        capabilities.push("search".to_string());
    }
    if status.supports_detail {
        capabilities.push("detail".to_string());
    }
    if status.supports_screenshots {
        capabilities.push("screenshots".to_string());
    }
    if status.supports_direct_download && !status.requires_interactive_download_verification {
        capabilities.push("download".to_string());
        capabilities.push("install".to_string());
    }
    Some(OsuSkinProvider {
        name: skin_provider_name(&id).to_string(),
        id,
        status: if status.available {
            "available"
        } else {
            "unavailable"
        }
        .to_string(),
        capabilities,
        message: status.message,
    })
}

fn desktop_ruleset(value: &str) -> Option<String> {
    match value {
        "RULESET_OSU" => Some("osu"),
        "RULESET_TAIKO" => Some("taiko"),
        "RULESET_CATCH" => Some("catch"),
        "RULESET_MANIA" => Some("mania"),
        _ => None,
    }
    .map(str::to_string)
}

fn hub_ruleset(value: &str) -> Result<&'static str, String> {
    match value {
        "osu" => Ok("RULESET_OSU"),
        "taiko" => Ok("RULESET_TAIKO"),
        "catch" => Ok("RULESET_CATCH"),
        "mania" => Ok("RULESET_MANIA"),
        _ => Err("AimMod does not recognize this osu! ruleset.".to_string()),
    }
}

fn hub_skin_sort(value: Option<&str>) -> Result<&'static str, String> {
    match value.unwrap_or("relevance") {
        "relevance" => Ok("SKIN_SORT_RELEVANCE"),
        "newest" => Ok("SKIN_SORT_NEWEST"),
        "mostViewed" => Ok("SKIN_SORT_MOST_VIEWED"),
        "mostDownloaded" => Ok("SKIN_SORT_MOST_DOWNLOADED"),
        "name" => Ok("SKIN_SORT_NAME"),
        "random" => Ok("SKIN_SORT_RANDOM"),
        _ => Err("AimMod does not recognize this skin sort order.".to_string()),
    }
}

fn hub_sort_direction(value: Option<&str>) -> Result<&'static str, String> {
    match value.unwrap_or("descending") {
        "ascending" => Ok("SORT_DIRECTION_ASCENDING"),
        "descending" => Ok("SORT_DIRECTION_DESCENDING"),
        _ => Err("AimMod does not recognize this sort direction.".to_string()),
    }
}

fn map_skin_item(item: HubSkinItem) -> OsuSkinSearchItem {
    OsuSkinSearchItem {
        provider: desktop_skin_provider(&item.provider)
            .unwrap_or("unknown")
            .to_string(),
        source_id: item.source_id,
        name: item.name,
        creator: item.creator,
        players: item.players,
        rulesets: item
            .rulesets
            .into_iter()
            .filter_map(|ruleset| desktop_ruleset(&ruleset))
            .collect(),
        aspect_ratios: item.aspect_ratios,
        tags: item.tags,
        sensitive: item.sensitive,
        thumbnail_url: item.thumbnail_url,
        view_count: item.view_count,
        download_count: item.download_count,
        file_size_bytes: item.file_size_bytes,
        counts_are_approximate: item.counts_are_approximate,
        file_size_is_approximate: item.file_size_is_approximate,
        submitted_at: item.submitted_at_iso,
        updated_at: item.updated_at_iso,
        screenshots: item.screenshots,
        download_available: item
            .download_handoff
            .is_some_and(|handoff| handoff.available && !handoff.requires_interactive_verification),
    }
}

fn skin_search_error(provider: String, error: impl Into<String>) -> OsuSkinSearchResponse {
    OsuSkinSearchResponse {
        provider,
        items: Vec::new(),
        next_page_token: None,
        error: Some(error.into()),
    }
}

fn osuck_mode(mode: u8) -> Option<String> {
    match mode {
        0 => Some("osu"),
        1 => Some("catch"),
        2 => Some("mania"),
        3 => Some("taiko"),
        _ => None,
    }
    .map(str::to_string)
}

fn osuck_ratio(ratio: u8) -> Option<String> {
    [
        "43:18",
        "32:9",
        "21:9",
        "16:10",
        "16:9",
        "5:4",
        "4:3",
        "3:4",
        "Universal",
    ]
    .get(ratio as usize)
    .map(|value| (*value).to_string())
}

fn valid_osuck_checksum(value: &str) -> bool {
    value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn osuck_image_url(checksum: &str, suffix: &str) -> String {
    if !valid_osuck_checksum(checksum) {
        return String::new();
    }
    format!("{OSUCK_BASE_URL}/images/screenshots/{checksum}_{suffix}.webp")
}

fn preferred_osuck_package(skin: &OsuckSkin) -> Option<&OsuckSkinFile> {
    skin.files
        .iter()
        .filter(|file| {
            file.name.to_ascii_lowercase().ends_with(".osk")
                && valid_osuck_checksum(&file.checksum)
                && file.size.osk > 0
                && file.google.first().copied().unwrap_or(false)
        })
        .max_by_key(|file| {
            file.stats
                .google
                .saturating_add(file.stats.mega)
                .saturating_add(file.stats.mediafire)
        })
}

fn map_osuck_skin(mut skin: OsuckSkin, detail: bool) -> OsuSkinSearchItem {
    skin.screenshots.sort_by_key(|shot| match shot.category_id {
        6..=9 => 0,
        2 => 1,
        17 => 2,
        _ => 3,
    });
    let screenshots: Vec<_> = skin
        .screenshots
        .iter()
        .filter(|shot| valid_osuck_checksum(&shot.checksum))
        .take(if detail { 24 } else { 6 })
        .map(|shot| OsuSkinScreenshot {
            label: shot.title.clone(),
            image_url: osuck_image_url(&shot.checksum, "md"),
            width: 0,
            height: 0,
        })
        .collect();
    let thumbnail_url = skin
        .screenshots
        .first()
        .map(|shot| osuck_image_url(&shot.checksum, "xs"))
        .unwrap_or_default();
    let package_size = preferred_osuck_package(&skin).map(|file| file.size.osk);
    let size_from_catalog = (skin.stats.size_max * 1024.0 * 1024.0).round();
    let file_size_bytes = package_size.unwrap_or_else(|| {
        if size_from_catalog.is_finite() && size_from_catalog > 0.0 {
            size_from_catalog as u64
        } else {
            0
        }
    });
    let name = if skin.version.trim().is_empty() {
        skin.name
    } else {
        format!("{} {}", skin.name, skin.version)
    };
    OsuSkinSearchItem {
        provider: "osuCK".to_string(),
        source_id: skin.id.to_string(),
        name,
        creator: skin
            .creators
            .iter()
            .map(|creator| creator.name.trim())
            .filter(|name| !name.is_empty())
            .collect::<Vec<_>>()
            .join(", "),
        players: Vec::new(),
        rulesets: skin.modes.into_iter().filter_map(osuck_mode).collect(),
        aspect_ratios: skin.ratios.into_iter().filter_map(osuck_ratio).collect(),
        tags: skin.keywords,
        sensitive: Some(skin.warning_type > 0),
        thumbnail_url,
        view_count: skin.stats.views,
        download_count: skin.stats.downloads,
        file_size_bytes,
        counts_are_approximate: false,
        file_size_is_approximate: package_size.is_none(),
        submitted_at: if skin.released_at.is_empty() {
            skin.created_at
        } else {
            skin.released_at
        },
        updated_at: skin.updated_at,
        screenshots,
        download_available: package_size.is_some(),
    }
}

async fn osuck_rate_limit() {
    let mut last = OSUCK_REQUEST_GATE.lock().await;
    if let Some(previous) = *last {
        let elapsed = previous.elapsed();
        if elapsed < OSUCK_MIN_REQUEST_INTERVAL {
            tokio::time::sleep(OSUCK_MIN_REQUEST_INTERVAL - elapsed).await;
        }
    }
    *last = Some(Instant::now());
}

async fn osuck_json(
    location: &str,
) -> Result<(serde_json::Value, reqwest::header::HeaderMap), String> {
    if !location.starts_with('/') || location.contains('\n') || location.contains('\r') {
        return Err("AimMod refused an invalid skins.osuck.net request path.".to_string());
    }
    osuck_rate_limit().await;
    let response = OSUCK_CLIENT
        .post(format!("{OSUCK_BASE_URL}/api/details"))
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .header("X-Request-Location", location)
        .send()
        .await
        .map_err(|error| format!("skins.osuck.net could not be reached: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "skins.osuck.net returned HTTP {}.",
            response.status()
        ));
    }
    if response
        .content_length()
        .is_some_and(|length| length > OSUCK_MAX_RESPONSE_BYTES as u64)
    {
        return Err("skins.osuck.net returned an unexpectedly large response.".to_string());
    }
    let headers = response.headers().clone();
    let bytes = response
        .bytes()
        .await
        .map_err(|error| format!("Could not read skins.osuck.net: {error}"))?;
    if bytes.len() > OSUCK_MAX_RESPONSE_BYTES {
        return Err("skins.osuck.net returned an unexpectedly large response.".to_string());
    }
    let value = serde_json::from_slice(&bytes)
        .map_err(|_| "skins.osuck.net returned an unsupported catalog response.".to_string())?;
    Ok((value, headers))
}

fn osuck_search_location(request: &OsuSkinSearchRequest) -> Result<String, String> {
    let mut url = Url::parse("https://skins.osuck.net/search")
        .map_err(|_| "Could not prepare the skins.osuck.net search.".to_string())?;
    {
        let mut query = url.query_pairs_mut();
        let text = [
            request.query.trim(),
            request.filters.tag.as_deref().unwrap_or("").trim(),
        ]
        .into_iter()
        .filter(|value| !value.is_empty())
        .collect::<Vec<_>>()
        .join(" ");
        if !text.is_empty() {
            query.append_pair("query", &text);
        }
        if !request.filters.rulesets.is_empty() {
            let modes = request
                .filters
                .rulesets
                .iter()
                .map(|value| {
                    if value == "catch" {
                        "ctb"
                    } else {
                        value.as_str()
                    }
                })
                .collect::<Vec<_>>()
                .join(",");
            query.append_pair("mode", &modes);
        }
        if let Some(ratio) = request
            .filters
            .aspect_ratio
            .as_deref()
            .filter(|value| !value.is_empty())
        {
            query.append_pair("ratio", ratio);
        }
        if let Some(sort) = match request.sort.as_deref().unwrap_or("relevance") {
            "mostDownloaded" => Some("0"),
            "mostViewed" => Some("1"),
            "newest" => Some("5"),
            "name" => Some("6"),
            "relevance" => None,
            _ => return Err("skins.osuck.net does not support this sort order.".to_string()),
        } {
            query.append_pair("sort", sort);
        }
        if request.direction.as_deref() == Some("ascending") {
            query.append_pair("order", "asc");
        }
    }
    Ok(match url.query() {
        Some(query) => format!("/search?{query}"),
        None => "/search".to_string(),
    })
}

fn parse_osuck_search(value: serde_json::Value) -> Result<Vec<OsuckSkin>, String> {
    let items = if value.get(0).is_some_and(serde_json::Value::is_array)
        && value.get(1).is_some_and(serde_json::Value::is_number)
    {
        value.get(0).cloned().unwrap_or_default()
    } else {
        value
    };
    serde_json::from_value(items)
        .map_err(|_| "skins.osuck.net returned an unsupported skin list.".to_string())
}

async fn search_osuck_direct(request: &OsuSkinSearchRequest) -> OsuSkinSearchResponse {
    let location = match osuck_search_location(request) {
        Ok(value) => value,
        Err(error) => return skin_search_error("osuCK".to_string(), error),
    };
    let value = match osuck_json(&location).await {
        Ok((value, _)) => value,
        Err(error) => return skin_search_error("osuCK".to_string(), error),
    };
    let mut items = match parse_osuck_search(value) {
        Ok(items) => items,
        Err(error) => return skin_search_error("osuCK".to_string(), error),
    };
    if request.filters.include_sensitive != Some(true) {
        items.retain(|item| item.warning_type <= 0);
    }
    let limit = request.limit.unwrap_or(40).clamp(1, 100) as usize;
    OsuSkinSearchResponse {
        provider: "osuCK".to_string(),
        items: items
            .into_iter()
            .take(limit)
            .map(|skin| map_osuck_skin(skin, false))
            .collect(),
        next_page_token: None,
        error: None,
    }
}

async fn get_osuck_direct(source_id: &str) -> Result<(OsuckSkin, OsuSkinSearchItem), String> {
    let id = source_id
        .parse::<u64>()
        .ok()
        .filter(|id| *id > 0)
        .ok_or_else(|| "AimMod does not recognize this skins.osuck.net skin ID.".to_string())?;
    let (value, _) = osuck_json(&format!("/skins/{id}")).await?;
    let skin: OsuckSkin = serde_json::from_value(value)
        .map_err(|_| "skins.osuck.net returned unsupported skin details.".to_string())?;
    if skin.id != id {
        return Err("skins.osuck.net returned mismatched skin details.".to_string());
    }
    let item = map_osuck_skin(skin.clone(), true);
    Ok((skin, item))
}

fn collect_osuck_cookies(
    headers: &reqwest::header::HeaderMap,
    cookies: &mut BTreeMap<String, String>,
) {
    for value in headers.get_all(reqwest::header::SET_COOKIE) {
        let Ok(value) = value.to_str() else { continue };
        let Some((name, rest)) = value.split_once('=') else {
            continue;
        };
        let name = name.trim();
        if !matches!(name, "facere" | "mollitia") {
            continue;
        }
        let value = rest.split(';').next().unwrap_or_default().trim();
        if !value.is_empty()
            && value.len() <= 256
            && value
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
        {
            cookies.insert(name.to_string(), value.to_string());
        }
    }
}

fn osuck_cookie_header(cookies: &BTreeMap<String, String>) -> String {
    cookies
        .iter()
        .map(|(name, value)| format!("{name}={value}"))
        .collect::<Vec<_>>()
        .join("; ")
}

fn osuck_google_file_id(location: &Url) -> Option<&str> {
    if location.scheme() != "https" || location.host_str()? != "drive.google.com" {
        return None;
    }
    let segments: Vec<_> = location.path_segments()?.collect();
    if segments.len() < 4 || segments[0] != "file" || segments[1] != "d" || segments[3] != "view" {
        return None;
    }
    let id = segments[2];
    (!id.is_empty()
        && id.len() <= 128
        && id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_')))
    .then_some(id)
}

async fn resolve_osuck_download(skin: &OsuckSkin, package: &OsuckSkinFile) -> Result<Url, String> {
    let location = format!("/skins/{}?tab=downloads", skin.id);
    let mut cookies = BTreeMap::new();
    osuck_rate_limit().await;
    let page = OSUCK_CLIENT
        .get(format!("{OSUCK_BASE_URL}{location}"))
        .header(reqwest::header::ACCEPT, "text/html")
        .send()
        .await
        .map_err(|error| format!("Could not prepare the skins.osuck.net download: {error}"))?;
    if !page.status().is_success() {
        return Err(format!(
            "skins.osuck.net returned HTTP {} while preparing the download.",
            page.status()
        ));
    }
    collect_osuck_cookies(page.headers(), &mut cookies);

    osuck_rate_limit().await;
    let view = OSUCK_CLIENT
        .post(format!("{OSUCK_BASE_URL}/api/views"))
        .header("X-Request-Location", &location)
        .header(
            "X-Request-Params",
            "1:_:true:::1920:::1080:::1.7777777777777777:::0:::0:::-1",
        )
        .header(reqwest::header::COOKIE, osuck_cookie_header(&cookies))
        .send()
        .await
        .map_err(|error| format!("Could not prepare the skins.osuck.net mirror: {error}"))?;
    if !view.status().is_success() {
        return Err(format!(
            "skins.osuck.net returned HTTP {} while preparing the mirror.",
            view.status()
        ));
    }
    collect_osuck_cookies(view.headers(), &mut cookies);
    let server_time = view
        .headers()
        .get("d")
        .and_then(|value| value.to_str().ok())
        .filter(|value| value.len() <= 20 && value.bytes().all(|byte| byte.is_ascii_digit()))
        .ok_or_else(|| "skins.osuck.net did not return its download timing marker.".to_string())?;
    cookies.insert(
        "autem".to_string(),
        format!("1:_:true:::1920:::1080:::1.7777777777777777:::{server_time}:::-1:::-1"),
    );
    cookies.insert("lorem2".to_string(), location.clone());

    osuck_rate_limit().await;
    let redirect = OSUCK_CLIENT
        .get(format!(
            "{OSUCK_BASE_URL}/downloads/skin-0{}0",
            package.checksum
        ))
        .header(
            reqwest::header::ACCEPT,
            "text/html,application/octet-stream",
        )
        .header(
            reqwest::header::REFERER,
            format!("{OSUCK_BASE_URL}{location}"),
        )
        .header(reqwest::header::COOKIE, osuck_cookie_header(&cookies))
        .send()
        .await
        .map_err(|error| format!("Could not resolve the skins.osuck.net download: {error}"))?;
    if !redirect.status().is_redirection() {
        return Err(format!(
            "skins.osuck.net returned HTTP {} instead of a download mirror.",
            redirect.status()
        ));
    }
    let mirror = redirect
        .headers()
        .get(reqwest::header::LOCATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| Url::parse(value).ok())
        .ok_or_else(|| "skins.osuck.net returned an invalid download mirror.".to_string())?;
    let file_id = osuck_google_file_id(&mirror).ok_or_else(|| {
        "This skins.osuck.net package does not have a supported in-app download mirror.".to_string()
    })?;
    Url::parse_with_params(
        "https://drive.usercontent.google.com/download",
        &[("id", file_id), ("export", "download"), ("confirm", "t")],
    )
    .map_err(|_| "Could not prepare the in-app skin download.".to_string())
}

pub async fn get_skin_providers(app: &AppHandle) -> Result<Vec<OsuSkinProvider>, String> {
    let response: Result<HubGetSkinProviderStatusResponse, _> = crate::hub_api::post_connect_json(
        app,
        GET_SKIN_PROVIDER_STATUS_PATH,
        &serde_json::json!({}),
    )
    .await;
    let mut providers: Vec<_> = response
        .ok()
        .into_iter()
        .flat_map(|response| response.providers)
        .filter_map(map_skin_provider_status)
        .filter(|provider| {
            provider
                .capabilities
                .iter()
                .any(|capability| capability == "install")
        })
        .collect();
    if let Some(osuck) = providers.iter_mut().find(|provider| provider.id == "osuCK") {
        osuck.status = "available".to_string();
        osuck.capabilities = vec![
            "search".to_string(),
            "detail".to_string(),
            "screenshots".to_string(),
            "download".to_string(),
            "install".to_string(),
        ];
        osuck.message =
            "Direct desktop catalog connection; requests are rate-limited and stay inside AimMod."
                .to_string();
    } else {
        providers.push(OsuSkinProvider {
            id: "osuCK".to_string(),
            name: "skins.osuck.net".to_string(),
            status: "available".to_string(),
            capabilities: vec![
                "search".to_string(),
                "detail".to_string(),
                "screenshots".to_string(),
                "download".to_string(),
                "install".to_string(),
            ],
            message: "Direct desktop catalog connection; requests are rate-limited and stay inside AimMod.".to_string(),
        });
    }
    Ok(providers)
}

pub async fn search_skins(app: &AppHandle, request: OsuSkinSearchRequest) -> OsuSkinSearchResponse {
    let provider = request.provider.clone();
    if provider == "osuCK" {
        return search_osuck_direct(&request).await;
    }
    let hub_provider = match hub_skin_provider(&provider) {
        Ok(provider) => provider,
        Err(error) => return skin_search_error(provider, error),
    };
    let rulesets: Result<Vec<_>, _> = request
        .filters
        .rulesets
        .iter()
        .map(|ruleset| hub_ruleset(ruleset))
        .collect();
    let rulesets = match rulesets {
        Ok(rulesets) => rulesets,
        Err(error) => return skin_search_error(provider, error),
    };
    let sort = match hub_skin_sort(request.sort.as_deref()) {
        Ok(sort) => sort,
        Err(error) => return skin_search_error(provider, error),
    };
    let direction = match hub_sort_direction(request.direction.as_deref()) {
        Ok(direction) => direction,
        Err(error) => return skin_search_error(provider, error),
    };
    let mut payload = serde_json::json!({
        "query": request.query,
        "providers": [hub_provider],
        "filters": {
            "rulesets": rulesets,
            "aspectRatio": request.filters.aspect_ratio.unwrap_or_default(),
            "creator": request.filters.creator.unwrap_or_default(),
            "player": request.filters.player.unwrap_or_default(),
            "tag": request.filters.tag.unwrap_or_default(),
            "includeSensitive": request.filters.include_sensitive,
        },
        "sort": sort,
        "direction": direction,
    });
    if let Some(page_token) = request
        .page_token
        .as_deref()
        .filter(|value| !value.is_empty())
    {
        payload["pageTokens"] = serde_json::json!([{
            "provider": hub_provider,
            "pageToken": page_token,
        }]);
    }
    let response: HubSearchSkinsResponse =
        match crate::hub_api::post_connect_json(app, SEARCH_SKINS_PATH, &payload).await {
            Ok(response) => response,
            Err(error) => {
                return skin_search_error(
                    provider,
                    format!("AimMod Hub skin search failed: {error}"),
                );
            }
        };
    let limit = request.limit.unwrap_or(40).clamp(1, 100) as usize;
    let items = response
        .items
        .into_iter()
        .take(limit)
        .map(map_skin_item)
        .collect();
    let next_page_token = response
        .next_page_tokens
        .into_iter()
        .find(|cursor| cursor.provider == hub_provider)
        .map(|cursor| cursor.page_token)
        .filter(|value| !value.is_empty());
    let error = response
        .providers
        .into_iter()
        .find(|status| status.provider == hub_provider && !status.available)
        .and_then(|status| (!status.message.is_empty()).then_some(status.message));
    OsuSkinSearchResponse {
        provider,
        items,
        next_page_token,
        error,
    }
}

pub async fn get_skin(app: &AppHandle, request: OsuSkinDetailRequest) -> OsuSkinDetailResponse {
    let provider = request.provider.clone();
    if provider == "osuCK" {
        return match get_osuck_direct(&request.source_id).await {
            Ok((_, item)) => OsuSkinDetailResponse {
                provider,
                item: Some(item),
                error: None,
            },
            Err(error) => OsuSkinDetailResponse {
                provider,
                item: None,
                error: Some(error),
            },
        };
    }
    let hub_provider = match hub_skin_provider(&provider) {
        Ok(provider) => provider,
        Err(error) => {
            return OsuSkinDetailResponse {
                provider,
                item: None,
                error: Some(error),
            };
        }
    };
    if request.source_id.trim().is_empty() {
        return OsuSkinDetailResponse {
            provider,
            item: None,
            error: Some("A skin source ID is required.".to_string()),
        };
    }
    let response: HubGetSkinResponse = match crate::hub_api::post_connect_json(
        app,
        GET_SKIN_PATH,
        &serde_json::json!({"provider": hub_provider, "sourceId": request.source_id}),
    )
    .await
    {
        Ok(response) => response,
        Err(error) => {
            return OsuSkinDetailResponse {
                provider,
                item: None,
                error: Some(format!("AimMod Hub skin detail failed: {error}")),
            };
        }
    };
    let error = response
        .provider
        .filter(|status| !status.available)
        .and_then(|status| (!status.message.is_empty()).then_some(status.message));
    OsuSkinDetailResponse {
        provider,
        item: response.item.map(map_skin_item),
        error,
    }
}

fn display_file_name(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| "Unknown skin".to_string())
}

fn has_osk_extension(path: &Path) -> bool {
    path.extension()
        .and_then(OsStr::to_str)
        .is_some_and(|extension| extension.eq_ignore_ascii_case("osk"))
}

fn safe_archive_name(name: &str) -> bool {
    if name.is_empty() || name.len() > MAX_ARCHIVE_PATH_BYTES || name.contains('\0') {
        return false;
    }
    let normalized = name.replace('\\', "/");
    if normalized.starts_with('/') {
        return false;
    }
    let mut segments = normalized.split('/');
    let Some(first) = segments.next() else {
        return false;
    };
    if first.ends_with(':') || first == ".." {
        return false;
    }
    segments.all(|segment| segment != "..")
}

fn parse_skin_ini(contents: &[u8]) -> (Option<String>, Option<String>) {
    let text = String::from_utf8_lossy(contents);
    let mut name = None;
    let mut author = None;
    for raw_line in text.trim_start_matches('\u{feff}').lines() {
        let line = raw_line.trim();
        if line.is_empty() || line.starts_with("//") || line.starts_with(';') {
            continue;
        }
        let Some((key, value)) = line.split_once(':') else {
            continue;
        };
        let value = value.trim();
        if value.is_empty() {
            continue;
        }
        if key.trim().eq_ignore_ascii_case("Name") {
            name = Some(value.chars().take(200).collect());
        } else if key.trim().eq_ignore_ascii_case("Author") {
            author = Some(value.chars().take(200).collect());
        }
    }
    (name, author)
}

fn validate_skin_archive(path: &Path) -> Result<SkinArchiveMetadata, String> {
    if !has_osk_extension(path) {
        return Err("Select an .osk skin package.".to_string());
    }
    let metadata = path
        .metadata()
        .map_err(|error| format!("Could not inspect the skin package: {error}"))?;
    if !metadata.is_file() {
        return Err("The selected skin package is not a file.".to_string());
    }
    if metadata.len() == 0 || metadata.len() > MAX_SKIN_PACKAGE_BYTES {
        return Err(format!(
            "Skin packages must be between 1 byte and {} MiB.",
            MAX_SKIN_PACKAGE_BYTES / 1024 / 1024
        ));
    }

    let file =
        File::open(path).map_err(|error| format!("Could not read the skin package: {error}"))?;
    let mut archive = zip::ZipArchive::new(file)
        .map_err(|_| "The selected .osk file is not a valid ZIP archive.".to_string())?;
    if archive.len() == 0 || archive.len() > MAX_SKIN_FILES {
        return Err(format!(
            "Skin packages must contain between 1 and {MAX_SKIN_FILES} entries."
        ));
    }

    let fallback_name = path
        .file_stem()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| "Unnamed skin".to_string());
    let mut parsed_name = None;
    let mut parsed_author = None;
    let mut file_count = 0_usize;
    let mut uncompressed_bytes = 0_u64;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| format!("Could not inspect the skin archive: {error}"))?;
        if !safe_archive_name(entry.name()) || entry.enclosed_name().is_none() {
            return Err("The skin archive contains an unsafe file path.".to_string());
        }
        if entry
            .unix_mode()
            .is_some_and(|mode| mode & 0o170000 == 0o120000)
        {
            return Err("The skin archive contains a symbolic link.".to_string());
        }
        if entry.encrypted() {
            return Err("AimMod does not import encrypted skin archives.".to_string());
        }
        if entry.is_dir() {
            continue;
        }
        file_count += 1;
        if entry.size() > MAX_SKIN_ENTRY_BYTES {
            return Err("The skin archive contains a file that is too large.".to_string());
        }
        uncompressed_bytes = uncompressed_bytes
            .checked_add(entry.size())
            .ok_or_else(|| "The skin archive size is invalid.".to_string())?;
        if uncompressed_bytes > MAX_SKIN_UNCOMPRESSED_BYTES {
            return Err(format!(
                "The unpacked skin exceeds {} MiB.",
                MAX_SKIN_UNCOMPRESSED_BYTES / 1024 / 1024
            ));
        }
        let compressed = entry.compressed_size();
        if entry.size() > 1024 * 1024
            && (compressed == 0 || entry.size() / compressed.max(1) > MAX_COMPRESSION_RATIO)
        {
            return Err("The skin archive has an unsafe compression ratio.".to_string());
        }

        let normalized_name = entry.name().replace('\\', "/");
        if normalized_name.eq_ignore_ascii_case("skin.ini") {
            if entry.size() > MAX_SKIN_INI_BYTES {
                return Err("The skin.ini file is too large.".to_string());
            }
            let mut contents = Vec::with_capacity(entry.size() as usize);
            entry
                .read_to_end(&mut contents)
                .map_err(|error| format!("Could not read skin.ini: {error}"))?;
            (parsed_name, parsed_author) = parse_skin_ini(&contents);
        }
    }
    if file_count == 0 {
        return Err("The skin archive contains no files.".to_string());
    }

    Ok(SkinArchiveMetadata {
        name: parsed_name.unwrap_or(fallback_name),
        author: parsed_author.unwrap_or_else(|| "Unknown".to_string()),
        file_count,
        uncompressed_bytes,
    })
}

fn managed_skin_dir(app: &AppHandle) -> Result<PathBuf, String> {
    let path = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Could not locate AimMod's data folder: {error}"))?
        .join("osu")
        .join("skins");
    fs::create_dir_all(&path)
        .map_err(|error| format!("Could not create AimMod's skin folder: {error}"))?;
    path.canonicalize()
        .map_err(|error| format!("Could not open AimMod's skin folder: {error}"))
}

fn skin_handoff_dir(app: &AppHandle) -> Result<PathBuf, String> {
    let path = app
        .path()
        .app_cache_dir()
        .map_err(|error| format!("Could not locate AimMod's cache folder: {error}"))?
        .join("osu-skin-import-handoff");
    fs::create_dir_all(&path)
        .map_err(|error| format!("Could not create AimMod's skin handoff folder: {error}"))?;
    Ok(path)
}

fn sha256_file(path: &Path) -> Result<String, String> {
    let file =
        File::open(path).map_err(|error| format!("Could not read the skin package: {error}"))?;
    let mut reader = BufReader::new(file);
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = reader
            .read(&mut buffer)
            .map_err(|error| format!("Could not hash the skin package: {error}"))?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn public_download_host(url: &Url) -> bool {
    let Some(host) = url.host_str() else {
        return false;
    };
    if host.eq_ignore_ascii_case("localhost")
        || host.ends_with(".localhost")
        || host.ends_with(".local")
    {
        return false;
    }
    match host.parse::<IpAddr>() {
        Ok(IpAddr::V4(address)) => {
            !(address.is_private()
                || address.is_loopback()
                || address.is_link_local()
                || address.is_multicast()
                || address.is_unspecified())
        }
        Ok(IpAddr::V6(address)) => {
            !(address.is_loopback()
                || address.is_multicast()
                || address.is_unspecified()
                || address.is_unique_local()
                || address.is_unicast_link_local())
        }
        Err(_) => true,
    }
}

fn validate_skin_download_handoff(handoff: &HubSkinDownloadHandoff) -> Result<(Url, u64), String> {
    if !handoff.available {
        return Err(if handoff.message.is_empty() {
            "This provider did not offer a skin download.".to_string()
        } else {
            handoff.message.clone()
        });
    }
    if handoff.requires_interactive_verification {
        return Err("This provider requires interactive verification. AimMod will not bypass it or open a browser fallback.".to_string());
    }
    if handoff.kind != "SKIN_DOWNLOAD_HANDOFF_KIND_DIRECT_URL" {
        return Err("AimMod Hub returned an unsupported skin download handoff.".to_string());
    }
    if handoff.file_name.contains('/')
        || handoff.file_name.contains('\\')
        || !has_osk_extension(Path::new(&handoff.file_name))
    {
        return Err("AimMod Hub returned an invalid skin file name.".to_string());
    }
    if handoff.max_download_bytes == 0
        || handoff.max_download_bytes > MAX_SKIN_PACKAGE_BYTES
        || handoff.expected_size_bytes == 0
        || handoff.expected_size_bytes > handoff.max_download_bytes
    {
        return Err("AimMod Hub returned invalid skin download limits.".to_string());
    }
    if !valid_managed_id(&handoff.sha256) {
        return Err("AimMod Hub did not provide a valid SHA-256 digest for the skin.".to_string());
    }
    let expires_at = chrono::DateTime::parse_from_rfc3339(&handoff.expires_at_iso)
        .map_err(|_| "AimMod Hub returned an invalid skin download expiry.".to_string())?;
    if expires_at <= Utc::now() {
        return Err("The skin download handoff has expired.".to_string());
    }
    let url = Url::parse(&handoff.uri)
        .map_err(|_| "AimMod Hub returned an invalid skin download URL.".to_string())?;
    if url.scheme() != "https"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
        || url.port().is_some_and(|port| port != 443)
        || !public_download_host(&url)
    {
        return Err("AimMod Hub returned an unsafe skin download URL.".to_string());
    }
    Ok((url, handoff.max_download_bytes))
}

async fn download_skin_package(
    url: Url,
    destination: &Path,
    maximum_bytes: u64,
    expected_bytes: u64,
    expected_sha256: &str,
) -> Result<(), String> {
    let response = SKIN_DOWNLOAD_CLIENT
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Could not download the skin: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The skin provider returned HTTP {}.",
            response.status()
        ));
    }
    if response
        .content_length()
        .is_some_and(|length| length > maximum_bytes || length != expected_bytes)
    {
        return Err("The skin provider returned an unexpected package size.".to_string());
    }

    let mut file = tokio::fs::File::create(destination)
        .await
        .map_err(|error| format!("Could not create the skin download file: {error}"))?;
    let mut stream = response.bytes_stream();
    let mut digest = Sha256::new();
    let mut downloaded = 0_u64;
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|error| format!("The skin download stopped early: {error}"))?;
        downloaded = downloaded
            .checked_add(chunk.len() as u64)
            .ok_or_else(|| "The skin download size overflowed.".to_string())?;
        if downloaded > maximum_bytes {
            drop(file);
            let _ = tokio::fs::remove_file(destination).await;
            return Err("The skin download exceeded the provider's byte limit.".to_string());
        }
        digest.update(&chunk);
        file.write_all(&chunk)
            .await
            .map_err(|error| format!("Could not save the skin download: {error}"))?;
    }
    file.flush()
        .await
        .map_err(|error| format!("Could not finish the skin download: {error}"))?;
    drop(file);
    if downloaded != expected_bytes {
        let _ = tokio::fs::remove_file(destination).await;
        return Err("The skin download size did not match the Hub handoff.".to_string());
    }
    let actual_sha256 = format!("{:x}", digest.finalize());
    if actual_sha256 != expected_sha256 {
        let _ = tokio::fs::remove_file(destination).await;
        return Err("The skin download failed its SHA-256 check.".to_string());
    }
    Ok(())
}

async fn download_direct_skin_package(
    url: Url,
    destination: &Path,
    maximum_bytes: u64,
) -> Result<(), String> {
    if url.scheme() != "https" || url.host_str() != Some("drive.usercontent.google.com") {
        return Err("AimMod refused an unsupported skin download host.".to_string());
    }
    let response = SKIN_DOWNLOAD_CLIENT
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Could not download the skin: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The skin mirror returned HTTP {}.",
            response.status()
        ));
    }
    if response
        .content_length()
        .is_some_and(|length| length == 0 || length > maximum_bytes)
    {
        return Err("The skin mirror returned an invalid package size.".to_string());
    }
    let mut file = tokio::fs::File::create(destination)
        .await
        .map_err(|error| format!("Could not create the skin download file: {error}"))?;
    let mut stream = response.bytes_stream();
    let mut downloaded = 0_u64;
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|error| format!("The skin download stopped early: {error}"))?;
        downloaded = downloaded
            .checked_add(chunk.len() as u64)
            .ok_or_else(|| "The skin download size overflowed.".to_string())?;
        if downloaded > maximum_bytes {
            drop(file);
            let _ = tokio::fs::remove_file(destination).await;
            return Err("The skin download exceeded AimMod's byte limit.".to_string());
        }
        file.write_all(&chunk)
            .await
            .map_err(|error| format!("Could not save the skin download: {error}"))?;
    }
    file.flush()
        .await
        .map_err(|error| format!("Could not finish the skin download: {error}"))?;
    if downloaded == 0 {
        let _ = tokio::fs::remove_file(destination).await;
        return Err("The skin mirror returned an empty package.".to_string());
    }
    Ok(())
}

fn valid_managed_id(id: &str) -> bool {
    id.len() == 64
        && id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn write_record(directory: &Path, record: &ManagedOsuSkin) -> Result<(), String> {
    if !valid_managed_id(&record.id) {
        return Err("AimMod generated an invalid skin ID.".to_string());
    }
    let sequence = SKIN_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let temporary = directory.join(format!("{}.json.part-{sequence}", record.id));
    let destination = directory.join(format!("{}.json", record.id));
    let contents = serde_json::to_vec_pretty(record)
        .map_err(|error| format!("Could not encode the skin inventory record: {error}"))?;
    fs::write(&temporary, contents)
        .map_err(|error| format!("Could not save the skin inventory record: {error}"))?;
    fs::rename(&temporary, &destination)
        .map_err(|error| format!("Could not finish the skin inventory record: {error}"))
}

fn cleanup_stale_handoffs(directory: &Path) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    let now = SystemTime::now();
    for entry in entries.flatten() {
        let path = entry.path();
        let is_stale_osk = has_osk_extension(&path)
            && entry
                .metadata()
                .ok()
                .and_then(|metadata| metadata.modified().ok())
                .and_then(|modified| now.duration_since(modified).ok())
                .is_some_and(|age| age >= STALE_HANDOFF_AGE);
        if is_stale_osk {
            let _ = fs::remove_file(path);
        }
    }
}

fn handoff_skin_to_lazer(app: &AppHandle, record: &ManagedOsuSkin) -> Result<(), String> {
    let directory = managed_skin_dir(app)?;
    let package = directory.join(format!("{}.osk", record.id));
    let handoff_dir = skin_handoff_dir(app)?;
    cleanup_stale_handoffs(&handoff_dir);
    let sequence = SKIN_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let handoff = handoff_dir.join(format!("skin-{}-{sequence}.osk", std::process::id()));
    fs::copy(&package, &handoff)
        .map_err(|error| format!("Could not copy the skin for safe import: {error}"))?;

    let Some(launcher) = crate::osu::find_lazer_launcher() else {
        let _ = fs::remove_file(&handoff);
        return Err("AimMod could not find an osu!lazer executable. Set AIMMOD_OSU_LAZER_EXECUTABLE to its path.".to_string());
    };
    if let Err(error) = crate::osu::launch_lazer_argument(&launcher, handoff.as_os_str()) {
        let _ = fs::remove_file(&handoff);
        return Err(format!("Could not open the skin in osu!lazer: {error}"));
    }
    Ok(())
}

fn cache_skin_package(
    app: &AppHandle,
    source: &Path,
    provider: Option<String>,
    source_id: Option<String>,
) -> Result<(ManagedOsuSkin, bool), String> {
    let canonical = source
        .canonicalize()
        .map_err(|_| "The selected skin package no longer exists or cannot be read.".to_string())?;
    if !canonical.is_file() || !has_osk_extension(&canonical) {
        return Err("Select an .osk skin package.".to_string());
    }

    let directory = managed_skin_dir(app)?;
    let sequence = SKIN_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let incoming = directory.join(format!("incoming-{}-{sequence}.osk", std::process::id()));
    fs::copy(&canonical, &incoming)
        .map_err(|error| format!("Could not copy the skin into AimMod: {error}"))?;
    let archive = match validate_skin_archive(&incoming) {
        Ok(metadata) => metadata,
        Err(error) => {
            let _ = fs::remove_file(&incoming);
            return Err(error);
        }
    };
    let sha256 = match sha256_file(&incoming) {
        Ok(value) => value,
        Err(error) => {
            let _ = fs::remove_file(&incoming);
            return Err(error);
        }
    };
    let package = directory.join(format!("{sha256}.osk"));
    let already_installed = package.exists();
    if already_installed {
        let existing = fs::symlink_metadata(&package)
            .map_err(|error| format!("Could not inspect AimMod's cached skin: {error}"))?;
        if !existing.is_file() || existing.file_type().is_symlink() {
            let _ = fs::remove_file(&incoming);
            return Err("AimMod's cached skin path is not a regular file.".to_string());
        }
        if sha256_file(&package)? != sha256 {
            let _ = fs::remove_file(&incoming);
            return Err("AimMod's cached skin failed its SHA-256 check.".to_string());
        }
        fs::remove_file(&incoming)
            .map_err(|error| format!("Could not discard the duplicate skin copy: {error}"))?;
    } else {
        fs::rename(&incoming, &package)
            .map_err(|error| format!("Could not finish caching the skin: {error}"))?;
    }

    let record = ManagedOsuSkin {
        id: sha256.clone(),
        name: archive.name,
        author: archive.author,
        file_name: display_file_name(&canonical),
        size_bytes: package
            .metadata()
            .map(|metadata| metadata.len())
            .unwrap_or(0),
        sha256,
        provider,
        source_id,
        installed_at: Utc::now().to_rfc3339_opts(SecondsFormat::Secs, true),
        import_status: "cached".to_string(),
    };
    write_record(&directory, &record)?;
    Ok((record, already_installed))
}

fn result_from_record(
    record: &ManagedOsuSkin,
    status: &str,
    message: impl Into<String>,
) -> OsuSkinInstallResult {
    OsuSkinInstallResult {
        id: Some(record.id.clone()),
        name: Some(record.name.clone()),
        author: Some(record.author.clone()),
        file_name: record.file_name.clone(),
        size_bytes: Some(record.size_bytes),
        sha256: Some(record.sha256.clone()),
        provider: record.provider.clone(),
        source_id: record.source_id.clone(),
        status: status.to_string(),
        message: message.into(),
    }
}

fn error_result(path: &Path, status: &str, message: impl Into<String>) -> OsuSkinInstallResult {
    OsuSkinInstallResult {
        id: None,
        name: None,
        author: None,
        file_name: display_file_name(path),
        size_bytes: None,
        sha256: None,
        provider: None,
        source_id: None,
        status: status.to_string(),
        message: message.into(),
    }
}

fn install_local_skin(app: &AppHandle, path: &Path) -> OsuSkinInstallResult {
    let (mut record, already_installed) = match cache_skin_package(app, path, None, None) {
        Ok(value) => value,
        Err(error) => return error_result(path, "rejected", error),
    };
    let directory = match managed_skin_dir(app) {
        Ok(directory) => directory,
        Err(error) => return result_from_record(&record, "cached", error),
    };
    match handoff_skin_to_lazer(app, &record) {
        Ok(()) => {
            record.import_status = "handoffRequested".to_string();
            let _ = write_record(&directory, &record);
            result_from_record(
                &record,
                if already_installed {
                    "alreadyInstalled"
                } else {
                    "installed"
                },
                "Asked osu!lazer to import and select an AimMod-owned copy of the skin.",
            )
        }
        Err(error) => result_from_record(&record, "cached", error),
    }
}

fn remote_error_result(
    request: &OsuSkinInstallRequest,
    status: &str,
    message: impl Into<String>,
) -> OsuSkinInstallResult {
    OsuSkinInstallResult {
        id: None,
        name: None,
        author: None,
        file_name: String::new(),
        size_bytes: None,
        sha256: None,
        provider: Some(request.provider.clone()),
        source_id: Some(request.source_id.clone()),
        status: status.to_string(),
        message: message.into(),
    }
}

async fn install_osuck_skin(
    app: &AppHandle,
    request: &OsuSkinInstallRequest,
) -> OsuSkinInstallResult {
    let (skin, item) = match get_osuck_direct(&request.source_id).await {
        Ok(value) => value,
        Err(error) => return remote_error_result(request, "unavailable", error),
    };
    let Some(package) = preferred_osuck_package(&skin).cloned() else {
        return remote_error_result(
            request,
            "unavailable",
            "skins.osuck.net did not provide a supported .osk package mirror for this skin.",
        );
    };
    let url = match resolve_osuck_download(&skin, &package).await {
        Ok(url) => url,
        Err(error) => return remote_error_result(request, "unavailable", error),
    };
    let directory = match managed_skin_dir(app) {
        Ok(directory) => directory,
        Err(error) => return remote_error_result(request, "error", error),
    };
    let sequence = SKIN_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let incoming = directory.join(format!(
        "osuck-download-{}-{sequence}.osk",
        std::process::id()
    ));
    if let Err(error) = download_direct_skin_package(url, &incoming, MAX_SKIN_PACKAGE_BYTES).await {
        let _ = fs::remove_file(&incoming);
        return remote_error_result(request, "error", error);
    }
    let (mut record, already_installed) = match cache_skin_package(
        app,
        &incoming,
        Some("osuCK".to_string()),
        Some(request.source_id.clone()),
    ) {
        Ok(value) => value,
        Err(error) => {
            let _ = fs::remove_file(&incoming);
            return remote_error_result(request, "rejected", error);
        }
    };
    let _ = fs::remove_file(&incoming);
    record.name = item.name;
    if !item.creator.trim().is_empty() {
        record.author = item.creator;
    }
    record.file_name = package.name;
    if let Err(error) = write_record(&directory, &record) {
        return result_from_record(&record, "cached", error);
    }
    match handoff_skin_to_lazer(app, &record) {
        Ok(()) => {
            record.import_status = "handoffRequested".to_string();
            let _ = write_record(&directory, &record);
            result_from_record(
                &record,
                if already_installed {
                    "alreadyInstalled"
                } else {
                    "installed"
                },
                "Downloaded the verified .osk package in AimMod and asked osu!lazer to import and select it.",
            )
        }
        Err(error) => result_from_record(&record, "cached", error),
    }
}

fn finish_downloaded_skin(
    app: &AppHandle,
    incoming: &Path,
    item: &HubSkinItem,
    handoff: &HubSkinDownloadHandoff,
) -> Result<(ManagedOsuSkin, bool), String> {
    let archive = validate_skin_archive(incoming)?;
    let directory = managed_skin_dir(app)?;
    let package = directory.join(format!("{}.osk", handoff.sha256));
    let already_installed = package.exists();
    if already_installed {
        let existing = fs::symlink_metadata(&package)
            .map_err(|error| format!("Could not inspect AimMod's cached skin: {error}"))?;
        if !existing.is_file() || existing.file_type().is_symlink() {
            return Err("AimMod's cached skin path is not a regular file.".to_string());
        }
        if sha256_file(&package)? != handoff.sha256 {
            return Err("AimMod's cached skin failed its SHA-256 check.".to_string());
        }
        fs::remove_file(incoming)
            .map_err(|error| format!("Could not discard the duplicate skin copy: {error}"))?;
    } else {
        fs::rename(incoming, &package)
            .map_err(|error| format!("Could not finish caching the downloaded skin: {error}"))?;
    }
    let provider = desktop_skin_provider(&item.provider)
        .ok_or_else(|| "AimMod Hub returned an unknown skin provider.".to_string())?
        .to_string();
    let record = ManagedOsuSkin {
        id: handoff.sha256.clone(),
        name: if item.name.trim().is_empty() {
            archive.name
        } else {
            item.name.clone()
        },
        author: if item.creator.trim().is_empty() {
            archive.author
        } else {
            item.creator.clone()
        },
        file_name: handoff.file_name.clone(),
        size_bytes: package
            .metadata()
            .map_err(|error| format!("Could not inspect the cached skin: {error}"))?
            .len(),
        sha256: handoff.sha256.clone(),
        provider: Some(provider),
        source_id: Some(item.source_id.clone()),
        installed_at: Utc::now().to_rfc3339_opts(SecondsFormat::Secs, true),
        import_status: "cached".to_string(),
    };
    write_record(&directory, &record)?;
    Ok((record, already_installed))
}

pub async fn install_skin(app: &AppHandle, request: OsuSkinInstallRequest) -> OsuSkinInstallResult {
    if request.provider == "osuCK" {
        return install_osuck_skin(app, &request).await;
    }
    let hub_provider = match hub_skin_provider(&request.provider) {
        Ok(provider) => provider,
        Err(error) => return remote_error_result(&request, "rejected", error),
    };
    if request.source_id.trim().is_empty() || request.source_id.len() > 512 {
        return remote_error_result(&request, "rejected", "A valid skin source ID is required.");
    }
    let detail: HubGetSkinResponse = match crate::hub_api::post_connect_json(
        app,
        GET_SKIN_PATH,
        &serde_json::json!({"provider": hub_provider, "sourceId": request.source_id}),
    )
    .await
    {
        Ok(response) => response,
        Err(error) => {
            return remote_error_result(
                &request,
                "error",
                format!("AimMod Hub skin detail failed: {error}"),
            );
        }
    };
    if let Some(status) = detail.provider.as_ref().filter(|status| !status.available) {
        return remote_error_result(
            &request,
            "unavailable",
            if status.message.is_empty() {
                "This skin provider is unavailable."
            } else {
                &status.message
            },
        );
    }
    let Some(item) = detail.item else {
        return remote_error_result(
            &request,
            "unavailable",
            "AimMod Hub did not return this skin.",
        );
    };
    if item.provider != hub_provider || item.source_id != request.source_id {
        return remote_error_result(
            &request,
            "rejected",
            "AimMod Hub returned mismatched skin details.",
        );
    }
    let response: HubGetSkinDownloadHandoffResponse = match crate::hub_api::post_connect_json(
        app,
        GET_SKIN_DOWNLOAD_HANDOFF_PATH,
        &serde_json::json!({"provider": hub_provider, "sourceId": request.source_id}),
    )
    .await
    {
        Ok(response) => response,
        Err(error) => {
            return remote_error_result(
                &request,
                "error",
                format!("AimMod Hub skin download request failed: {error}"),
            );
        }
    };
    if let Some(status) = response
        .provider
        .as_ref()
        .filter(|status| !status.available)
    {
        return remote_error_result(
            &request,
            "unavailable",
            if status.message.is_empty() {
                "This skin provider is unavailable."
            } else {
                &status.message
            },
        );
    }
    let Some(handoff) = response.handoff else {
        return remote_error_result(
            &request,
            "unavailable",
            "AimMod Hub did not offer a skin download.",
        );
    };
    let (url, maximum_bytes) = match validate_skin_download_handoff(&handoff) {
        Ok(value) => value,
        Err(error) => {
            return remote_error_result(
                &request,
                if handoff.available && !handoff.requires_interactive_verification {
                    "rejected"
                } else {
                    "unavailable"
                },
                error,
            );
        }
    };
    let directory = match managed_skin_dir(app) {
        Ok(directory) => directory,
        Err(error) => return remote_error_result(&request, "error", error),
    };
    let sequence = SKIN_FILE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let incoming = directory.join(format!("download-{}-{sequence}.osk", std::process::id()));
    if let Err(error) = download_skin_package(
        url,
        &incoming,
        maximum_bytes,
        handoff.expected_size_bytes,
        &handoff.sha256,
    )
    .await
    {
        let _ = fs::remove_file(&incoming);
        return remote_error_result(&request, "error", error);
    }
    let (mut record, already_installed) =
        match finish_downloaded_skin(app, &incoming, &item, &handoff) {
            Ok(value) => value,
            Err(error) => {
                let _ = fs::remove_file(&incoming);
                return remote_error_result(&request, "rejected", error);
            }
        };
    let inventory = match managed_skin_dir(app) {
        Ok(directory) => directory,
        Err(error) => return result_from_record(&record, "cached", error),
    };
    match handoff_skin_to_lazer(app, &record) {
        Ok(()) => {
            record.import_status = "handoffRequested".to_string();
            let _ = write_record(&inventory, &record);
            result_from_record(
                &record,
                if already_installed {
                    "alreadyInstalled"
                } else {
                    "installed"
                },
                "Asked osu!lazer to import and select an AimMod-owned copy of the downloaded skin.",
            )
        }
        Err(error) => result_from_record(&record, "cached", error),
    }
}

pub fn import_skin_files(app: &AppHandle, paths: Vec<String>) -> Vec<OsuSkinInstallResult> {
    if paths.len() > MAX_LOCAL_IMPORT_FILES {
        return vec![error_result(
            Path::new(""),
            "rejected",
            format!("Select at most {MAX_LOCAL_IMPORT_FILES} skin packages at once."),
        )];
    }
    paths
        .into_iter()
        .map(|path| install_local_skin(app, Path::new(&path)))
        .collect()
}

pub fn list_installed_skins(app: &AppHandle) -> Result<Vec<ManagedOsuSkin>, String> {
    let directory = managed_skin_dir(app)?;
    let mut records = Vec::new();
    for entry in fs::read_dir(&directory)
        .map_err(|error| format!("Could not read AimMod's skin folder: {error}"))?
    {
        let entry = match entry {
            Ok(entry) => entry,
            Err(_) => continue,
        };
        let path = entry.path();
        if path.extension().and_then(OsStr::to_str) != Some("json") {
            continue;
        }
        let Some(id) = path.file_stem().and_then(OsStr::to_str) else {
            continue;
        };
        if !valid_managed_id(id) {
            continue;
        }
        let metadata = match fs::symlink_metadata(&path) {
            Ok(metadata)
                if metadata.is_file()
                    && !metadata.file_type().is_symlink()
                    && metadata.len() <= MAX_SKIN_METADATA_BYTES =>
            {
                metadata
            }
            _ => continue,
        };
        let _ = metadata;
        let record: ManagedOsuSkin = match fs::read(&path)
            .ok()
            .and_then(|contents| serde_json::from_slice::<ManagedOsuSkin>(&contents).ok())
        {
            Some(record) if record.id == id && record.sha256 == id => record,
            _ => continue,
        };
        let package = directory.join(format!("{id}.osk"));
        let package_is_regular = fs::symlink_metadata(package)
            .map(|metadata| metadata.is_file() && !metadata.file_type().is_symlink())
            .unwrap_or(false);
        if package_is_regular {
            records.push(record);
        }
    }
    records.sort_by(|left, right| right.installed_at.cmp(&left.installed_at));
    Ok(records)
}

pub fn remove_installed_skin(app: &AppHandle, id: String) -> OsuSkinRemoveResult {
    if !valid_managed_id(&id) {
        return OsuSkinRemoveResult {
            id,
            status: "rejected".to_string(),
            message: "AimMod does not recognize this managed skin ID.".to_string(),
        };
    }
    let directory = match managed_skin_dir(app) {
        Ok(directory) => directory,
        Err(error) => {
            return OsuSkinRemoveResult {
                id,
                status: "error".to_string(),
                message: error,
            };
        }
    };
    let package = directory.join(format!("{id}.osk"));
    let record = directory.join(format!("{id}.json"));
    if !package.exists() && !record.exists() {
        return OsuSkinRemoveResult {
            id,
            status: "notFound".to_string(),
            message: "AimMod no longer has this managed skin package.".to_string(),
        };
    }
    for path in [&package, &record] {
        let Ok(metadata) = fs::symlink_metadata(path) else {
            continue;
        };
        if !metadata.is_file() || metadata.file_type().is_symlink() {
            return OsuSkinRemoveResult {
                id,
                status: "rejected".to_string(),
                message: "AimMod refused to remove a skin path that is not a regular file."
                    .to_string(),
            };
        }
    }
    if let Err(error) = fs::remove_file(&package) {
        if error.kind() != std::io::ErrorKind::NotFound {
            return OsuSkinRemoveResult {
                id,
                status: "error".to_string(),
                message: format!("Could not remove AimMod's skin package: {error}"),
            };
        }
    }
    if let Err(error) = fs::remove_file(&record) {
        if error.kind() != std::io::ErrorKind::NotFound {
            return OsuSkinRemoveResult {
                id,
                status: "error".to_string(),
                message: format!("Could not remove AimMod's skin inventory record: {error}"),
            };
        }
    }
    OsuSkinRemoveResult {
        id,
        status: "removed".to_string(),
        message: "Removed AimMod's managed package. Skins already imported into osu!lazer were not changed."
            .to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::{
        HubSearchSkinsResponse, HubSkinDownloadHandoff, MAX_SKIN_FILES,
        MAX_SKIN_UNCOMPRESSED_BYTES, OsuSkinSearchFilters, OsuSkinSearchRequest, OsuckSkin,
        map_osuck_skin, map_skin_item, map_skin_provider_status, osuck_google_file_id,
        osuck_search_location, parse_osuck_search, parse_skin_ini, preferred_osuck_package,
        safe_archive_name, valid_managed_id, validate_skin_archive, validate_skin_download_handoff,
    };
    use std::fs::File;
    use std::io::Write;
    use std::path::Path;
    use zip::write::SimpleFileOptions;

    fn skin_archive(path: &Path, entries: &[(&str, &[u8])]) {
        let file = File::create(path).unwrap();
        let mut archive = zip::ZipWriter::new(file);
        for (name, contents) in entries {
            archive
                .start_file(*name, SimpleFileOptions::default())
                .unwrap();
            archive.write_all(contents).unwrap();
        }
        archive.finish().unwrap();
    }

    #[test]
    fn accepts_safe_archive_paths_and_rejects_traversal() {
        for safe in ["skin.ini", "cursor/cursor.png", "audio/hit.wav"] {
            assert!(safe_archive_name(safe));
        }
        for unsafe_name in [
            "../outside",
            "assets/../../outside",
            "..\\outside",
            "C:\\outside",
            "/outside",
            "",
        ] {
            assert!(!safe_archive_name(unsafe_name), "accepted {unsafe_name}");
        }
    }

    #[test]
    fn parses_skin_name_and_author_case_insensitively() {
        let (name, author) = parse_skin_ini(
            b"[General]\nName: Crunchy's skin\naUtHoR: veryCrunchy\nVersion: latest\n",
        );
        assert_eq!(name.as_deref(), Some("Crunchy's skin"));
        assert_eq!(author.as_deref(), Some("veryCrunchy"));
    }

    #[test]
    fn validates_osk_zip_and_reads_metadata() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("skin.osk");
        skin_archive(
            &path,
            &[
                (
                    "skin.ini",
                    b"[General]\nName: Tablet skin\nAuthor: Crunchy\n",
                ),
                ("cursor.png", b"fake image data"),
            ],
        );
        let metadata = validate_skin_archive(&path).unwrap();
        assert_eq!(metadata.name, "Tablet skin");
        assert_eq!(metadata.author, "Crunchy");
        assert_eq!(metadata.file_count, 2);
    }

    #[test]
    fn rejects_non_zip_empty_and_traversal_packages() {
        let directory = tempfile::tempdir().unwrap();
        let text = directory.path().join("text.osk");
        std::fs::write(&text, b"not a zip").unwrap();
        assert!(validate_skin_archive(&text).unwrap_err().contains("ZIP"));

        let empty = directory.path().join("empty.osk");
        skin_archive(&empty, &[]);
        assert!(validate_skin_archive(&empty).is_err());

        let traversal = directory.path().join("traversal.osk");
        skin_archive(&traversal, &[("../outside.txt", b"bad")]);
        assert!(
            validate_skin_archive(&traversal)
                .unwrap_err()
                .contains("unsafe")
        );
    }

    #[test]
    fn validates_only_lowercase_sha256_inventory_ids() {
        let valid = "a".repeat(64);
        assert!(valid_managed_id(&valid));
        assert!(!valid_managed_id(&"A".repeat(64)));
        assert!(!valid_managed_id("../skin"));
        assert!(!valid_managed_id(&"a".repeat(63)));
    }

    #[test]
    fn archive_limits_are_explicit_and_bounded() {
        assert_eq!(MAX_SKIN_FILES, 20_000);
        assert_eq!(MAX_SKIN_UNCOMPRESSED_BYTES, 1024 * 1024 * 1024);
    }

    #[test]
    fn maps_real_connect_skin_response_fields() {
        let response: HubSearchSkinsResponse = serde_json::from_value(serde_json::json!({
            "items": [{
                "provider": "SKIN_PROVIDER_OSU_SKINS",
                "sourceId": "42",
                "name": "Tablet skin",
                "creator": "Crunchy",
                "players": ["verycrunchy"],
                "rulesets": ["RULESET_OSU"],
                "aspectRatios": ["16:9"],
                "tags": ["minimal"],
                "thumbnailUrl": "https://cdn.example.test/skin.png",
                "viewCount": "100",
                "downloadCount": "20",
                "fileSizeBytes": "4096",
                "countsAreApproximate": true,
                "fileSizeIsApproximate": true
            }],
            "providers": [{
                "provider": "SKIN_PROVIDER_OSU_SKINS",
                "available": true,
                "supportsSearch": true,
                "supportsDetail": true,
                "supportsScreenshots": true,
                "supportsDirectDownload": false,
                "requiresInteractiveDownloadVerification": true,
                "message": "Interactive verification required"
            }]
        }))
        .unwrap();
        let item = map_skin_item(response.items.into_iter().next().unwrap());
        assert_eq!(item.provider, "osuSkins");
        assert_eq!(item.source_id, "42");
        assert_eq!(item.rulesets, ["osu"]);
        assert_eq!(item.view_count, 100);
        assert!(item.counts_are_approximate);
        assert!(item.file_size_is_approximate);
        assert!(!item.download_available);
        let provider =
            map_skin_provider_status(response.providers.into_iter().next().unwrap()).unwrap();
        assert_eq!(provider.id, "osuSkins");
        assert!(!provider.capabilities.contains(&"download".to_string()));
    }

    #[test]
    fn accepts_only_bounded_verified_https_skin_handoffs() {
        let valid = HubSkinDownloadHandoff {
            kind: "SKIN_DOWNLOAD_HANDOFF_KIND_DIRECT_URL".to_string(),
            available: true,
            uri: "https://cdn.example.test/skin.osk".to_string(),
            file_name: "skin.osk".to_string(),
            expected_size_bytes: 4096,
            sha256: "a".repeat(64),
            max_download_bytes: 8192,
            requires_interactive_verification: false,
            expires_at_iso: (chrono::Utc::now() + chrono::Duration::minutes(5)).to_rfc3339(),
            message: String::new(),
        };
        assert!(validate_skin_download_handoff(&valid).is_ok());
        for invalid in [
            HubSkinDownloadHandoff {
                uri: "http://cdn.example.test/skin.osk".to_string(),
                ..valid.clone()
            },
            HubSkinDownloadHandoff {
                uri: "https://127.0.0.1/skin.osk".to_string(),
                ..valid.clone()
            },
            HubSkinDownloadHandoff {
                requires_interactive_verification: true,
                ..valid.clone()
            },
            HubSkinDownloadHandoff {
                expected_size_bytes: 9000,
                ..valid.clone()
            },
            HubSkinDownloadHandoff {
                sha256: "not-a-digest".to_string(),
                ..valid.clone()
            },
            HubSkinDownloadHandoff {
                file_name: "../skin.osk".to_string(),
                ..valid.clone()
            },
        ] {
            assert!(validate_skin_download_handoff(&invalid).is_err());
        }
    }

    #[test]
    fn maps_live_contract_shaped_osuck_catalog_and_detail() {
        let value = serde_json::json!([[{
            "_warning_type": 0,
            "id": 2021,
            "name": "WhiteCat (CK)",
            "version": "2.1",
            "stats": {"views": 3325161, "downloads": 1196003, "size_max": 16.1},
            "creators": [{"name": "cyperdark"}, {"name": "Innith"}],
            "screenshots": [
                {"checksum": "bc6df28bb8f6823bea61ebea061e37f7", "category_id": 2, "title": "Menu"},
                {"checksum": "d4f03099d85e624e343e57b841337b4a", "category_id": 6, "title": "Gameplay"}
            ],
            "modes": [0],
            "ratios": [3, 4, 6],
            "created_at": "2021-04-25T13:44:50.000Z",
            "released_at": "2021-03-23T17:14:41.000Z",
            "updated_at": "2021-04-25T13:44:50.000Z"
        }], 1]);
        let skin = parse_osuck_search(value).unwrap().remove(0);
        let mapped = map_osuck_skin(skin, false);
        assert_eq!(mapped.source_id, "2021");
        assert_eq!(mapped.name, "WhiteCat (CK) 2.1");
        assert_eq!(mapped.creator, "cyperdark, Innith");
        assert_eq!(mapped.rulesets, ["osu"]);
        assert_eq!(mapped.aspect_ratios, ["16:10", "16:9", "4:3"]);
        assert!(
            mapped
                .thumbnail_url
                .contains("d4f03099d85e624e343e57b841337b4a_xs.webp")
        );
        assert!(!mapped.download_available);

        let detail: OsuckSkin = serde_json::from_value(serde_json::json!({
            "id": 2021,
            "name": "WhiteCat (CK)",
            "metadata_modes": [0],
            "metadata_ratios": [4],
            "files": [{
                "checksum": "a550229743714dc269976b0776697f62",
                "name": "WhiteCat 2.1.osk",
                "stats": {"google": 21188, "mega": 55, "mediafire": 14194},
                "size": {"osk": 13265532},
                "google": [true, false, false, false]
            }]
        }))
        .unwrap();
        assert_eq!(
            preferred_osuck_package(&detail).unwrap().name,
            "WhiteCat 2.1.osk"
        );
        assert!(map_osuck_skin(detail, true).download_available);
    }

    #[test]
    fn builds_bounded_osuck_search_location_from_supported_filters() {
        let request = OsuSkinSearchRequest {
            provider: "osuCK".to_string(),
            query: "white cat".to_string(),
            page_token: None,
            limit: Some(20),
            filters: OsuSkinSearchFilters {
                rulesets: vec!["osu".to_string()],
                aspect_ratio: Some("16:9".to_string()),
                tag: Some("minimal".to_string()),
                ..Default::default()
            },
            sort: Some("mostDownloaded".to_string()),
            direction: Some("descending".to_string()),
        };
        let location = osuck_search_location(&request).unwrap();
        assert!(location.starts_with("/search?"));
        assert!(location.contains("query=white+cat+minimal"));
        assert!(location.contains("mode=osu"));
        assert!(location.contains("ratio=16%3A9"));
        assert!(location.contains("sort=0"));
    }

    #[test]
    fn accepts_only_google_drive_file_redirects_from_osuck() {
        let valid = reqwest::Url::parse(
            "https://drive.google.com/file/d/1yOTP0pn2L6MZOrYLtzC5uGJOOKOoF-N5/view?usp=drivesdk",
        )
        .unwrap();
        assert_eq!(
            osuck_google_file_id(&valid),
            Some("1yOTP0pn2L6MZOrYLtzC5uGJOOKOoF-N5")
        );
        for invalid in [
            "http://drive.google.com/file/d/id/view",
            "https://evil.example/file/d/id/view",
            "https://drive.google.com/open?id=secret",
            "https://drive.google.com/file/d/../view",
        ] {
            assert!(osuck_google_file_id(&reqwest::Url::parse(invalid).unwrap()).is_none());
        }
    }

    #[test]
    fn validates_external_skin_fixture_when_requested() {
        let Some(path) = std::env::var_os("AIMMOD_OSU_TEST_SKIN") else {
            return;
        };
        let metadata = validate_skin_archive(Path::new(&path)).unwrap();
        assert!(metadata.file_count > 0);
        assert!(metadata.uncompressed_bytes > 0);
    }
}
