/// Replay store: persists per-session mouse-path and per-second metric snapshots.
///
/// Replay payloads now live in typed SQLite tables. Legacy SQLite blob rows and
/// old sidecar JSON files are imported on demand for older sessions.
/// Positions are downsampled (every Nth point) to reduce file size.
use serde::{Deserialize, Serialize};
use std::fs;
use std::io::Cursor;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use tauri::{AppHandle, Manager};

use crate::mouse_hook::{MetricPoint, RawPositionPoint};

// ─── Config ───────────────────────────────────────────────────────────────────

/// Keep every other raw position sample (~62fps, ~3700 pts/min).
const DOWNSAMPLE_FACTOR: usize = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ReplayMediaQuality {
    Standard,
    High,
    Ultra,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FfmpegStatus {
    pub available: bool,
    pub source: String,
    pub path: Option<String>,
}

const FFMPEG_DOWNLOAD_URL: &str =
    "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
const FFMPEG_DOWNLOAD_SHA256_URL: &str =
    "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";

impl ReplayMediaQuality {
    fn from_settings(value: &str) -> Self {
        match value.trim() {
            "high" => Self::High,
            "ultra" => Self::Ultra,
            _ => Self::Standard,
        }
    }

    fn ffmpeg_args(self) -> [&'static str; 4] {
        match self {
            Self::Standard => ["-preset", "veryfast", "-crf", "31"],
            Self::High => ["-preset", "medium", "-crf", "25"],
            Self::Ultra => ["-preset", "slow", "-crf", "21"],
        }
    }
}

// ─── Types ────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReplayData {
    pub positions: Vec<RawPositionPoint>,
    pub metrics: Vec<MetricPoint>,
    /// Screen frames captured from the game window using the active replay
    /// framing, framerate, and output-width settings.
    /// Absent in replays saved before this field was added.
    #[serde(default)]
    pub frames: Vec<crate::screen_recorder::ScreenFrame>,
    /// Bridge-derived run snapshot with timeline and event counts.
    /// Absent in replays saved before this field was added.
    #[serde(default)]
    pub run_snapshot: Option<crate::bridge::BridgeRunSnapshot>,
    /// FTHR video recording, stored as an MP4 next to the replay. Replays with
    /// a video usually have no JPEG `frames`.
    #[serde(default)]
    pub video: Option<crate::fthr_recorder::ReplayVideo>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReplayPayloadData {
    pub positions: Vec<RawPositionPoint>,
    pub metrics: Vec<MetricPoint>,
    #[serde(default)]
    pub frames: Vec<crate::screen_recorder::ScreenFrame>,
    #[serde(default)]
    pub video: Option<crate::fthr_recorder::ReplayVideo>,
}

impl From<&ReplayData> for ReplayPayloadData {
    fn from(value: &ReplayData) -> Self {
        Self {
            positions: value.positions.clone(),
            metrics: value.metrics.clone(),
            frames: value.frames.clone(),
            video: value.video.clone(),
        }
    }
}

impl ReplayData {
    fn has_media(&self) -> bool {
        !self.frames.is_empty() || self.video.is_some()
    }
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

fn replay_dir(app: &AppHandle) -> Option<PathBuf> {
    let data_dir = app.path().app_data_dir().ok()?;
    let dir = data_dir.join("replays");
    std::fs::create_dir_all(&dir).ok()?;
    Some(dir)
}

/// Directory holding FTHR replay videos (`<session>.mp4` + `<session>.json`).
pub fn replay_videos_dir(app: &AppHandle) -> Option<PathBuf> {
    let dir = replay_dir(app)?.join("videos");
    std::fs::create_dir_all(&dir).ok()?;
    Some(dir)
}

fn video_file_stem(session_id: &str) -> String {
    sanitize_replay_temp_component(session_id)
}

fn move_file(source: &Path, target: &Path) -> std::io::Result<()> {
    if fs::rename(source, target).is_ok() {
        return Ok(());
    }
    fs::copy(source, target)?;
    fs::remove_file(source)
}

/// Moves a freshly saved clip into the videos directory and writes its sidecar.
fn store_replay_video(
    app: &AppHandle,
    session_id: &str,
    mut video: crate::fthr_recorder::ReplayVideo,
) -> Option<crate::fthr_recorder::ReplayVideo> {
    let source = PathBuf::from(&video.path);
    let Some(dir) = replay_videos_dir(app) else {
        let _ = fs::remove_file(&source);
        return None;
    };
    let stem = video_file_stem(session_id);
    let target = dir.join(format!("{stem}.mp4"));
    if source != target {
        if let Err(error) = move_file(&source, &target) {
            log::warn!(
                "replay_store: could not store replay video for {}: {}",
                session_id,
                error
            );
            let _ = fs::remove_file(&source);
            return None;
        }
    }
    video.file_name = format!("{stem}.mp4");
    video.path = String::new();
    let sidecar = dir.join(format!("{stem}.json"));
    let written = serde_json::to_vec(&video)
        .map_err(|e| e.to_string())
        .and_then(|bytes| fs::write(&sidecar, bytes).map_err(|e| e.to_string()));
    if let Err(error) = written {
        log::warn!("replay_store: could not write video sidecar for {session_id}: {error}");
        let _ = fs::remove_file(&target);
        return None;
    }
    video.path = target.to_string_lossy().into_owned();
    Some(video)
}

/// Loads a replay's video sidecar if its MP4 still exists.
fn load_replay_video(
    app: &AppHandle,
    session_id: &str,
) -> Option<crate::fthr_recorder::ReplayVideo> {
    let dir = replay_videos_dir(app)?;
    let stem = video_file_stem(session_id);
    let bytes = fs::read(dir.join(format!("{stem}.json"))).ok()?;
    let mut video: crate::fthr_recorder::ReplayVideo = serde_json::from_slice(&bytes).ok()?;
    // Never trust a path from disk; only the file name inside our directory.
    let file = dir.join(Path::new(&video.file_name).file_name()?);
    if !file.is_file() || video.segments.is_empty() {
        return None;
    }
    video.path = file.to_string_lossy().into_owned();
    Some(video)
}

fn delete_replay_video(app: &AppHandle, session_id: &str) {
    let Some(dir) = replay_videos_dir(app) else {
        return;
    };
    let stem = video_file_stem(session_id);
    for ext in ["mp4", "json"] {
        let path = dir.join(format!("{stem}.{ext}"));
        if let Err(error) = fs::remove_file(&path) {
            if error.kind() != std::io::ErrorKind::NotFound {
                log::warn!("replay_store: could not remove {}: {}", path.display(), error);
            }
        }
    }
}

/// Removes every stored replay video (used when session history is cleared).
pub fn delete_all_replay_videos(app: &AppHandle) {
    let Some(dir) = replay_videos_dir(app) else {
        return;
    };
    let Ok(entries) = fs::read_dir(&dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let is_video_file = path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| ext.eq_ignore_ascii_case("mp4") || ext.eq_ignore_ascii_case("json"));
        if path.is_file() && is_video_file {
            let _ = fs::remove_file(&path);
        }
    }
}

fn sqlite_replay_path(session_id: &str) -> PathBuf {
    PathBuf::from(format!("sqlite://session_replay_tables/{session_id}"))
}

fn backfill_sqlite_replay(app: &AppHandle, session_id: &str, replay: &ReplayData) -> bool {
    if let Err(error) = crate::stats_db::upsert_replay_payload(app, session_id, replay) {
        log::warn!(
            "replay_store: could not persist replay payload for {}: {error}",
            session_id
        );
        return false;
    }

    if let Some(snapshot) = replay.run_snapshot.as_ref() {
        if let Err(error) = crate::stats_db::upsert_run_capture(app, session_id, snapshot) {
            log::warn!(
                "replay_store: could not persist run capture for {}: {error}",
                session_id
            );
        }
    }

    let virtual_path = sqlite_replay_path(session_id);
    if let Err(error) = crate::stats_db::upsert_replay_asset(
        app,
        &crate::stats_db::ReplayAssetRecord {
            session_id,
            file_path: &virtual_path,
            positions_count: replay.positions.len(),
            metrics_count: replay.metrics.len(),
            frames_count: replay.frames.len(),
            has_run_snapshot: replay.run_snapshot.is_some(),
        },
    ) {
        log::warn!(
            "replay_store: could not register replay metadata for {}: {error}",
            session_id
        );
    }

    true
}

fn import_legacy_replay_with_video_requirement(
    app: &AppHandle,
    session_id: &str,
    require_video_frames: bool,
) -> Option<ReplayData> {
    let dir = replay_dir(app)?;
    let path = dir.join(format!("{}.json", session_id));
    let json = std::fs::read_to_string(&path).ok()?;
    let replay = match serde_json::from_str::<ReplayData>(&json) {
        Ok(replay) => replay,
        Err(error) => {
            log::warn!("replay_store: parse error for {session_id}: {error}");
            return None;
        }
    };

    if require_video_frames && replay.frames.is_empty() {
        purge_invalid_replay_without_video_frames(app, session_id, "legacy replay", Some(&path));
        return None;
    }

    let _ = backfill_sqlite_replay(app, session_id, &replay);
    Some(replay)
}

fn import_legacy_replay(app: &AppHandle, session_id: &str) -> Option<ReplayData> {
    import_legacy_replay_with_video_requirement(app, session_id, true)
}

fn repair_run_capture_if_needed(app: &AppHandle, session_id: &str, replay: &ReplayData) {
    if replay.run_snapshot.is_none() {
        return;
    }
    let has_summary = crate::stats_db::get_run_summary(app, session_id)
        .ok()
        .flatten()
        .is_some();
    if !has_summary {
        let _ = backfill_sqlite_replay(app, session_id, replay);
    }
}

fn repair_tick_stream_if_needed(app: &AppHandle, session_id: &str, replay: &mut ReplayData) {
    let missing_tick_stream = replay
        .run_snapshot
        .as_ref()
        .and_then(|snapshot| snapshot.tick_stream_v1.as_ref())
        .is_none();
    if !missing_tick_stream {
        return;
    }

    let legacy = crate::stats_db::get_legacy_replay_blob(app, session_id)
        .ok()
        .flatten()
        .or_else(|| import_legacy_replay_with_video_requirement(app, session_id, false));
    let Some(legacy) = legacy else {
        return;
    };
    let Some(mut legacy_snapshot) = legacy.run_snapshot else {
        return;
    };
    let Some(legacy_tick_stream) = legacy_snapshot.tick_stream_v1.take() else {
        return;
    };

    if let Some(snapshot) = replay.run_snapshot.as_mut() {
        snapshot.tick_stream_v1 = Some(legacy_tick_stream);
    } else {
        replay.run_snapshot = Some(crate::bridge::BridgeRunSnapshot {
            tick_stream_v1: Some(legacy_tick_stream),
            ..legacy_snapshot
        });
    }

    let _ = backfill_sqlite_replay(app, session_id, replay);
}

fn repair_replay_timing_if_needed(app: &AppHandle, session_id: &str, replay: &mut ReplayData) {
    if replay.frames.len() < 2 || replay.positions.len() < 2 {
        return;
    }

    let frame_duration_ms = replay
        .frames
        .last()
        .map(|frame| frame.timestamp_ms)
        .unwrap_or(0);
    let position_duration_ms = replay
        .positions
        .last()
        .map(|point| point.timestamp_ms)
        .unwrap_or(0);
    let metric_duration_ms = replay
        .metrics
        .last()
        .map(|point| point.timestamp_ms)
        .unwrap_or(0);
    let run_duration_ms = replay
        .run_snapshot
        .as_ref()
        .and_then(|snapshot| snapshot.duration_secs)
        .map(|secs| (secs.max(0.0) * 1000.0).round() as u64)
        .unwrap_or(0);
    let target_duration_ms = position_duration_ms
        .max(metric_duration_ms)
        .max(run_duration_ms);

    if target_duration_ms == 0 || frame_duration_ms <= target_duration_ms.saturating_add(900) {
        return;
    }

    let lead_in_ms = frame_duration_ms.saturating_sub(target_duration_ms);
    if lead_in_ms < 900 || lead_in_ms > 5_000 {
        return;
    }

    let mut trimmed_frames = replay
        .frames
        .iter()
        .filter_map(|frame| {
            (frame.timestamp_ms >= lead_in_ms).then(|| {
                let mut next = frame.clone();
                next.timestamp_ms = next.timestamp_ms.saturating_sub(lead_in_ms);
                next
            })
        })
        .collect::<Vec<_>>();
    if trimmed_frames.len() < 2 {
        return;
    }
    trimmed_frames.sort_by_key(|frame| frame.timestamp_ms);
    let trimmed_duration_ms = trimmed_frames
        .last()
        .map(|frame| frame.timestamp_ms)
        .unwrap_or(0);
    if trimmed_duration_ms.abs_diff(target_duration_ms) > 1_500 {
        return;
    }

    log::info!(
        "replay_store: repaired replay timing for {} by trimming {}ms of leading video-only frames",
        session_id,
        lead_in_ms
    );
    replay.frames = trimmed_frames;
    let _ = backfill_sqlite_replay(app, session_id, replay);
}

fn replay_keep_count(app: &AppHandle) -> usize {
    crate::settings::load(app)
        .map(|settings| settings.replay_keep_count as usize)
        .unwrap_or(crate::settings::DEFAULT_REPLAY_KEEP_COUNT as usize)
}

fn ffmpeg_install_root(app: &AppHandle) -> Result<PathBuf, String> {
    let root = app
        .path()
        .app_local_data_dir()
        .map_err(|e| format!("Could not resolve AimMod local data directory: {e}"))?
        .join("tools")
        .join("ffmpeg");
    fs::create_dir_all(&root)
        .map_err(|e| format!("Could not create AimMod ffmpeg directory: {e}"))?;
    Ok(root)
}

fn ffmpeg_local_bin_dir(app: &AppHandle) -> Result<PathBuf, String> {
    Ok(ffmpeg_install_root(app)?.join("bin"))
}

fn ffmpeg_local_binary_path(app: &AppHandle) -> Result<PathBuf, String> {
    let bin = ffmpeg_local_bin_dir(app)?;
    #[cfg(target_os = "windows")]
    {
        Ok(bin.join("ffmpeg.exe"))
    }
    #[cfg(not(target_os = "windows"))]
    {
        Ok(bin.join("ffmpeg"))
    }
}

fn detect_ffmpeg_path(app: &AppHandle) -> Option<(PathBuf, &'static str)> {
    if let Ok(path) = std::env::var("AIMMOD_FFMPEG_PATH") {
        let path = PathBuf::from(path);
        if is_usable_ffmpeg_path(&path) {
            return Some((path, "custom"));
        }
    }

    if let Ok(path) = which::which("ffmpeg") {
        if is_usable_ffmpeg_path(&path) {
            return Some((path, "system"));
        }
    }

    if let Ok(path) = ffmpeg_local_binary_path(app) {
        if is_usable_ffmpeg_path(&path) {
            return Some((path, "aimmod"));
        }
    }

    None
}

pub fn get_ffmpeg_status(app: &AppHandle) -> FfmpegStatus {
    if let Some((path, source)) = detect_ffmpeg_path(app) {
        FfmpegStatus {
            available: true,
            source: source.to_string(),
            path: Some(path.to_string_lossy().into_owned()),
        }
    } else {
        FfmpegStatus {
            available: false,
            source: "missing".to_string(),
            path: None,
        }
    }
}

pub fn install_ffmpeg_for_app(app: &AppHandle) -> Result<FfmpegStatus, String> {
    if let Some((path, source)) = detect_ffmpeg_path(app) {
        return Ok(FfmpegStatus {
            available: true,
            source: source.to_string(),
            path: Some(path.to_string_lossy().into_owned()),
        });
    }

    let archive_bytes = reqwest::blocking::get(ffmpeg_download_url())
        .and_then(|response| response.error_for_status())
        .map_err(|e| format!("Could not download ffmpeg archive: {e}"))?
        .bytes()
        .map_err(|e| format!("Could not read ffmpeg archive: {e}"))?;

    let checksum_text = reqwest::blocking::get(ffmpeg_download_sha256_url())
        .and_then(|response| response.error_for_status())
        .map_err(|e| format!("Could not download ffmpeg checksum: {e}"))?
        .text()
        .map_err(|e| format!("Could not read ffmpeg checksum: {e}"))?;

    verify_ffmpeg_archive_checksum(&archive_bytes, &checksum_text)?;

    let install_root = ffmpeg_install_root(app)?;
    let staging_root =
        install_root.join(format!("staging-{}", chrono::Utc::now().timestamp_millis()));
    let extracted_root = staging_root.join("extracted");
    fs::create_dir_all(&extracted_root)
        .map_err(|e| format!("Could not prepare ffmpeg staging directory: {e}"))?;

    extract_ffmpeg_archive(&archive_bytes, &extracted_root)?;

    let source_bin_dir = find_ffmpeg_bin_dir(&extracted_root)
        .ok_or_else(|| "Could not find ffmpeg.exe in the downloaded archive.".to_string())?;
    let target_bin_dir = ffmpeg_local_bin_dir(app)?;
    if target_bin_dir.exists() {
        fs::remove_dir_all(&target_bin_dir)
            .map_err(|e| format!("Could not replace existing AimMod ffmpeg install: {e}"))?;
    }
    copy_directory_recursive(&source_bin_dir, &target_bin_dir)?;
    let _ = fs::remove_dir_all(&staging_root);

    let status = get_ffmpeg_status(app);
    if !status.available {
        return Err("ffmpeg downloaded, but AimMod still could not find it.".to_string());
    }
    Ok(status)
}

pub fn maybe_install_ffmpeg_for_replay_media(
    app: AppHandle,
    settings: crate::settings::AppSettings,
) {
    if !settings.hub_sync_enabled || settings.replay_media_upload_mode.trim() == "off" {
        return;
    }
    if detect_ffmpeg_path(&app).is_some() {
        return;
    }

    let _ = std::thread::Builder::new()
        .name("ffmpeg-auto-install".into())
        .spawn(move || match install_ffmpeg_for_app(&app) {
            Ok(status) => {
                log::info!(
                    "replay_store: ffmpeg ready for replay uploads (source={} path={})",
                    status.source,
                    status.path.unwrap_or_else(|| "<unknown>".to_string())
                );
            }
            Err(error) => {
                log::warn!(
                    "replay_store: could not auto-install ffmpeg for replay uploads: {error}"
                );
            }
        });
}

fn ffmpeg_download_url() -> String {
    std::env::var("AIMMOD_FFMPEG_DOWNLOAD_URL").unwrap_or_else(|_| FFMPEG_DOWNLOAD_URL.to_string())
}

fn ffmpeg_download_sha256_url() -> String {
    std::env::var("AIMMOD_FFMPEG_DOWNLOAD_SHA256_URL")
        .unwrap_or_else(|_| FFMPEG_DOWNLOAD_SHA256_URL.to_string())
}

fn verify_ffmpeg_archive_checksum(bytes: &[u8], checksum_text: &str) -> Result<(), String> {
    use sha2::{Digest, Sha256};

    let expected = checksum_text
        .split_whitespace()
        .find(|token| token.len() == 64 && token.chars().all(|c| c.is_ascii_hexdigit()))
        .ok_or_else(|| "Could not parse ffmpeg checksum response.".to_string())?
        .to_ascii_lowercase();

    let actual = {
        let mut hasher = Sha256::new();
        hasher.update(bytes);
        let digest = hasher.finalize();
        let mut out = String::with_capacity(digest.len() * 2);
        for byte in digest {
            use std::fmt::Write as _;
            let _ = write!(&mut out, "{byte:02x}");
        }
        out
    };

    if actual != expected {
        return Err("ffmpeg download checksum did not match.".to_string());
    }
    Ok(())
}

fn extract_ffmpeg_archive(bytes: &[u8], destination: &Path) -> Result<(), String> {
    let reader = Cursor::new(bytes);
    let mut archive =
        zip::ZipArchive::new(reader).map_err(|e| format!("Could not open ffmpeg archive: {e}"))?;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|e| format!("Could not read ffmpeg archive entry: {e}"))?;
        let Some(enclosed_path) = entry.enclosed_name().map(|path| path.to_path_buf()) else {
            continue;
        };
        let out_path = destination.join(enclosed_path);
        if entry.is_dir() {
            fs::create_dir_all(&out_path)
                .map_err(|e| format!("Could not create ffmpeg extract directory: {e}"))?;
            continue;
        }
        if let Some(parent) = out_path.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| format!("Could not create ffmpeg extract parent: {e}"))?;
        }
        let mut output = fs::File::create(&out_path)
            .map_err(|e| format!("Could not create extracted ffmpeg file: {e}"))?;
        std::io::copy(&mut entry, &mut output)
            .map_err(|e| format!("Could not extract ffmpeg archive file: {e}"))?;
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

fn find_ffmpeg_bin_dir(root: &Path) -> Option<PathBuf> {
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
                .map(|name| {
                    #[cfg(target_os = "windows")]
                    {
                        name.eq_ignore_ascii_case("ffmpeg.exe")
                    }
                    #[cfg(not(target_os = "windows"))]
                    {
                        name == "ffmpeg"
                    }
                })
                .unwrap_or(false)
            {
                return path.parent().map(Path::to_path_buf);
            }
        }
    }
    None
}

fn copy_directory_recursive(source: &Path, target: &Path) -> Result<(), String> {
    fs::create_dir_all(target)
        .map_err(|e| format!("Could not create ffmpeg target directory: {e}"))?;
    for entry in fs::read_dir(source)
        .map_err(|e| format!("Could not read ffmpeg extracted directory: {e}"))?
    {
        let entry = entry.map_err(|e| format!("Could not read ffmpeg extracted entry: {e}"))?;
        let from = entry.path();
        let to = target.join(entry.file_name());
        if from.is_dir() {
            copy_directory_recursive(&from, &to)?;
        } else {
            if let Some(parent) = to.parent() {
                fs::create_dir_all(parent)
                    .map_err(|e| format!("Could not create ffmpeg target parent directory: {e}"))?;
            }
            fs::copy(&from, &to)
                .map_err(|e| format!("Could not copy ffmpeg file into AimMod: {e}"))?;
        }
    }
    Ok(())
}

pub fn apply_replay_retention(
    app: &AppHandle,
    keep_count: Option<usize>,
    protected_session_id: Option<&str>,
) {
    let keep_count = keep_count.unwrap_or_else(|| replay_keep_count(app));
    if keep_count == 0 {
        return;
    }

    match crate::stats_db::prune_replay_assets(app, keep_count, protected_session_id) {
        Ok(pruned) if !pruned.is_empty() => {
            for session_id in &pruned {
                delete_replay_video(app, session_id);
            }
            log::info!(
                "replay_store: pruned {} non-favorited replay(s) to keep the latest {}",
                pruned.len(),
                keep_count
            );
        }
        Ok(_) => {}
        Err(error) => {
            log::warn!("replay_store: could not prune old replays: {error}");
        }
    }
}

pub fn set_replay_favorite(
    app: &AppHandle,
    session_id: &str,
    is_favorite: bool,
) -> Result<(), String> {
    crate::stats_db::set_replay_favorite(app, session_id, is_favorite)
        .map_err(|e| e.to_string())?;
    if !is_favorite {
        apply_replay_retention(app, None, None);
    }
    Ok(())
}

pub fn delete_replay(app: &AppHandle, session_id: &str) -> Result<(), String> {
    crate::stats_db::delete_replay_asset(app, session_id).map_err(|e| e.to_string())?;
    delete_replay_video(app, session_id);
    Ok(())
}

pub fn export_replay_video(app: &AppHandle, session_id: &str) -> Result<PathBuf, String> {
    let export_root = app
        .path()
        .video_dir()
        .ok()
        .or_else(|| app.path().download_dir().ok())
        .or_else(|| app.path().app_data_dir().ok())
        .ok_or_else(|| "Could not resolve an export directory.".to_string())?
        .join("AimMod")
        .join("Replays");
    fs::create_dir_all(&export_root)
        .map_err(|e| format!("Could not create export directory: {e}"))?;

    let encode_quality = crate::settings::load(app)
        .map(|settings| ReplayMediaQuality::from_settings(&settings.replay_media_upload_quality))
        .unwrap_or(ReplayMediaQuality::High);
    let output_path = export_root.join(format!("{session_id}.mp4"));
    encode_replay_video_to_path(app, session_id, encode_quality, &output_path)?;
    Ok(output_path)
}

pub fn encode_replay_video_to_temp(
    app: &AppHandle,
    session_id: &str,
    quality: &str,
) -> Result<PathBuf, String> {
    let safe_session = sanitize_replay_temp_component(session_id);
    let output_path = std::env::temp_dir()
        .join("aimmod-replay-upload")
        .join(format!(
            "{}-{}-{}.mp4",
            safe_session,
            quality,
            chrono::Utc::now().timestamp_millis()
        ));
    if let Some(parent) = output_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|e| format!("Could not create replay upload temp directory: {e}"))?;
    }
    encode_replay_video_to_path(
        app,
        session_id,
        ReplayMediaQuality::from_settings(quality),
        &output_path,
    )?;
    Ok(output_path)
}

fn encode_replay_video_to_path(
    app: &AppHandle,
    session_id: &str,
    encode_quality: ReplayMediaQuality,
    output_path: &Path,
) -> Result<(), String> {
    use base64::Engine;

    let replay =
        load_replay(app, session_id).ok_or_else(|| format!("replay not found: {session_id}"))?;
    if let Some(video) = replay.video.as_ref() {
        return encode_video_segments_to_path(app, session_id, video, encode_quality, output_path);
    }
    if replay.frames.is_empty() {
        return Err("This replay has no saved video frames.".to_string());
    }

    let safe_session = sanitize_replay_temp_component(session_id);
    let temp_root = std::env::temp_dir()
        .join("aimmod-replay-exports")
        .join(format!(
            "{}-{}",
            safe_session,
            chrono::Utc::now().timestamp_millis()
        ));
    fs::create_dir_all(&temp_root)
        .map_err(|e| format!("Could not create temporary export directory: {e}"))?;

    let decode_engine = base64::engine::general_purpose::STANDARD;
    let mut frame_paths = Vec::with_capacity(replay.frames.len());
    for (index, frame) in replay.frames.iter().enumerate() {
        let jpeg = decode_engine
            .decode(frame.jpeg_b64.as_bytes())
            .map_err(|e| format!("Could not decode replay frame {}: {}", index + 1, e))?;
        let frame_path = temp_root.join(format!("frame_{index:06}.jpg"));
        fs::write(&frame_path, jpeg).map_err(|e| {
            format!(
                "Could not write temporary replay frame {}: {}",
                index + 1,
                e
            )
        })?;
        frame_paths.push(frame_path);
    }

    let concat_timeline_path = write_ffconcat_timeline(&temp_root, &replay.frames, &frame_paths)?;
    let [preset_flag, preset_value, crf_flag, crf_value] = encode_quality.ffmpeg_args();
    let ffmpeg_path = ensure_ffmpeg_available(app)?;
    let mut command = std::process::Command::new(&ffmpeg_path);
    configure_background_command(&mut command);
    let output = command
        .arg("-y")
        .arg("-hide_banner")
        .arg("-loglevel")
        .arg("error")
        .arg("-f")
        .arg("concat")
        .arg("-safe")
        .arg("0")
        .arg("-i")
        .arg(&concat_timeline_path)
        .arg("-vsync")
        .arg("vfr")
        // libx264 with yuv420p requires even width/height.
        // Replays are encoded from stored frame data, and those dimensions may
        // differ between sessions if the user changed capture settings over time.
        // Pad to the next even size only when needed (no scaling/downsampling).
        .arg("-vf")
        .arg("pad=ceil(iw/2)*2:ceil(ih/2)*2")
        .arg("-c:v")
        .arg("libx264")
        .arg(preset_flag)
        .arg(preset_value)
        .arg(crf_flag)
        .arg(crf_value)
        .arg("-pix_fmt")
        .arg("yuv420p")
        .arg("-movflags")
        .arg("+faststart")
        .arg(&output_path)
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .output()
        .map_err(|e| format!("Could not start ffmpeg. AimMod uses an existing system install first, then installs a local copy only if needed: {e}"))?;

    let _ = fs::remove_dir_all(&temp_root);

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let detail = if stderr.is_empty() {
            "ffmpeg exited without an error message.".to_string()
        } else {
            stderr
        };
        log::warn!(
            "replay_store: ffmpeg export failed for {} using {}: {}",
            session_id,
            ffmpeg_path.display(),
            detail
        );
        return Err(format!("ffmpeg failed to export this replay: {detail}"));
    }

    Ok(())
}

/// Re-encodes the run's stretches of an FTHR clip to H.264 so exports and
/// uploads play everywhere (HEVC playback depends on the viewer's decoder).
fn encode_video_segments_to_path(
    app: &AppHandle,
    session_id: &str,
    video: &crate::fthr_recorder::ReplayVideo,
    encode_quality: ReplayMediaQuality,
    output_path: &Path,
) -> Result<(), String> {
    if video.segments.is_empty() {
        return Err("This replay video has no playable segments.".to_string());
    }
    let mut filter = String::new();
    for (index, segment) in video.segments.iter().enumerate() {
        filter.push_str(&format!(
            "[0:v]trim=start={:.3}:duration={:.3},setpts=PTS-STARTPTS[v{index}];",
            segment.video_start_ms as f64 / 1000.0,
            segment.duration_ms as f64 / 1000.0,
        ));
    }
    for index in 0..video.segments.len() {
        filter.push_str(&format!("[v{index}]"));
    }
    filter.push_str(&format!(
        "concat=n={}:v=1:a=0,pad=ceil(iw/2)*2:ceil(ih/2)*2[out]",
        video.segments.len()
    ));

    let [preset_flag, preset_value, crf_flag, crf_value] = encode_quality.ffmpeg_args();
    let ffmpeg_path = ensure_ffmpeg_available(app)?;
    let mut command = std::process::Command::new(&ffmpeg_path);
    configure_background_command(&mut command);
    let output = command
        .args(["-y", "-hide_banner", "-loglevel", "error", "-i"])
        .arg(&video.path)
        .args(["-filter_complex", filter.as_str(), "-map", "[out]", "-c:v", "libx264"])
        .args([preset_flag, preset_value, crf_flag, crf_value])
        .args(["-pix_fmt", "yuv420p", "-movflags", "+faststart"])
        .arg(output_path)
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .output()
        .map_err(|e| format!("Could not start ffmpeg: {e}"))?;
    if !output.status.success() {
        let detail = String::from_utf8_lossy(&output.stderr).trim().to_string();
        log::warn!(
            "replay_store: ffmpeg video export failed for {} using {}: {}",
            session_id,
            ffmpeg_path.display(),
            detail
        );
        return Err(format!("ffmpeg failed to export this replay: {detail}"));
    }
    Ok(())
}

fn sanitize_replay_temp_component(value: &str) -> String {
    let mut sanitized = String::with_capacity(value.len());
    for ch in value.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            sanitized.push(ch);
        } else {
            sanitized.push('_');
        }
    }

    let trimmed = sanitized.trim_matches('_');
    if trimmed.is_empty() {
        "replay".to_string()
    } else {
        trimmed.to_string()
    }
}

fn configure_background_command(command: &mut std::process::Command) {
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
}

fn ensure_ffmpeg_available(app: &AppHandle) -> Result<PathBuf, String> {
    if let Some((path, _source)) = detect_ffmpeg_path(app) {
        return Ok(path);
    }
    install_ffmpeg_for_app(app)?;
    detect_ffmpeg_path(app)
        .map(|(path, _)| path)
        .ok_or_else(|| "AimMod could not find or install ffmpeg.".to_string())
}

fn is_usable_ffmpeg_path(path: &Path) -> bool {
    path.exists() && path.is_file()
}

fn infer_export_fps(frames: &[crate::screen_recorder::ScreenFrame]) -> u32 {
    if frames.len() < 2 {
        return crate::screen_recorder::replay_capture_fps();
    }

    let mut deltas = Vec::with_capacity(frames.len().saturating_sub(1));
    for window in frames.windows(2) {
        let delta = window[1]
            .timestamp_ms
            .saturating_sub(window[0].timestamp_ms);
        if delta > 0 {
            deltas.push(delta);
        }
    }

    if deltas.is_empty() {
        return crate::screen_recorder::replay_capture_fps();
    }

    let avg_delta_ms = deltas.iter().sum::<u64>() as f64 / deltas.len() as f64;
    if avg_delta_ms <= 0.0 {
        return crate::screen_recorder::replay_capture_fps();
    }

    (1000.0 / avg_delta_ms).round().clamp(6.0, 60.0) as u32
}

fn infer_export_frame_duration_ms(frames: &[crate::screen_recorder::ScreenFrame]) -> u64 {
    if frames.len() < 2 {
        return (1000.0 / infer_export_fps(frames).max(1) as f64)
            .round()
            .max(1.0) as u64;
    }

    let mut deltas = Vec::with_capacity(frames.len().saturating_sub(1));
    for window in frames.windows(2) {
        let delta = window[1]
            .timestamp_ms
            .saturating_sub(window[0].timestamp_ms);
        if delta > 0 {
            deltas.push(delta);
        }
    }

    if deltas.is_empty() {
        return (1000.0 / infer_export_fps(frames).max(1) as f64)
            .round()
            .max(1.0) as u64;
    }

    deltas.sort_unstable();
    deltas[deltas.len() / 2].max(1)
}

fn write_ffconcat_timeline(
    temp_root: &Path,
    frames: &[crate::screen_recorder::ScreenFrame],
    frame_paths: &[PathBuf],
) -> Result<PathBuf, String> {
    if frame_paths.is_empty() || frames.is_empty() || frame_paths.len() != frames.len() {
        return Err("Could not build replay timeline for ffmpeg export.".to_string());
    }

    fn ffconcat_file_line(path: &Path) -> String {
        let escaped = path
            .to_string_lossy()
            .replace('\\', "/")
            .replace('\'', "'\\''");
        format!("file '{escaped}'\n")
    }

    let fallback_duration_ms = infer_export_frame_duration_ms(frames);
    let mut timeline = String::from("ffconcat version 1.0\n");
    for (index, path) in frame_paths.iter().enumerate() {
        timeline.push_str(&ffconcat_file_line(path));
        let duration_ms = if let Some(next) = frames.get(index + 1) {
            next.timestamp_ms
                .saturating_sub(frames[index].timestamp_ms)
                .max(1)
        } else {
            fallback_duration_ms
        };
        let duration_secs = duration_ms.min(2_000) as f64 / 1000.0;
        timeline.push_str(&format!("duration {:.6}\n", duration_secs));
    }

    if let Some(last) = frame_paths.last() {
        timeline.push_str(&ffconcat_file_line(last));
    }

    let timeline_path = temp_root.join("frames.ffconcat");
    fs::write(&timeline_path, timeline)
        .map_err(|e| format!("Could not write replay ffmpeg timeline: {e}"))?;
    Ok(timeline_path)
}

// ─── Public API ───────────────────────────────────────────────────────────────

/// Persist `data` for `session_id`.  Positions are downsampled before writing.
/// Returns `true` if the replay payload was written successfully.
pub fn save_replay(app: &AppHandle, session_id: &str, mut data: ReplayData) -> bool {
    if let Some(video) = data.video.take() {
        data.video = store_replay_video(app, session_id, video);
    }
    if !data.has_media() {
        purge_invalid_replay_without_video_frames(app, session_id, "replay save", None);
        return false;
    }

    // Downsample positions: keep every DOWNSAMPLE_FACTOR-th point.
    // Click events are never dropped so they remain visible in the canvas.
    let downsampled_positions: Vec<RawPositionPoint> = data
        .positions
        .iter()
        .enumerate()
        .filter(|(i, p)| *i % DOWNSAMPLE_FACTOR == 0 || p.is_click)
        .map(|(_, p)| p.clone())
        .collect();

    let stored = ReplayData {
        positions: downsampled_positions,
        metrics: data.metrics,
        frames: data.frames,
        run_snapshot: data.run_snapshot,
        video: data.video,
    };

    if !backfill_sqlite_replay(app, session_id, &stored) {
        delete_replay_video(app, session_id);
        return false;
    }
    log::info!(
        "replay_store: saved {} ({} positions, {} metrics, {} frames, video={}, run_snapshot={})",
        session_id,
        stored.positions.len(),
        stored.metrics.len(),
        stored.frames.len(),
        stored.video.as_ref().map(|v| v.codec.as_str()).unwrap_or("none"),
        stored.run_snapshot.is_some(),
    );
    apply_replay_retention(app, None, Some(session_id));
    true
}

/// Load a previously saved replay. Returns `None` if the file does not exist or
/// cannot be parsed.
pub fn load_replay(app: &AppHandle, session_id: &str) -> Option<ReplayData> {
    match crate::stats_db::get_replay_data(app, session_id) {
        Ok(Some(mut replay)) => {
            replay.video = load_replay_video(app, session_id);
            if !replay.has_media() {
                purge_invalid_replay_without_video_frames(app, session_id, "sqlite replay", None);
                return None;
            }
            repair_replay_timing_if_needed(app, session_id, &mut replay);
            repair_tick_stream_if_needed(app, session_id, &mut replay);
            repair_run_capture_if_needed(app, session_id, &replay);
            Some(replay)
        }
        Ok(None) => match crate::stats_db::get_legacy_replay_blob(app, session_id) {
            Ok(Some(replay)) => {
                if replay.frames.is_empty() {
                    purge_invalid_replay_without_video_frames(
                        app,
                        session_id,
                        "legacy sqlite replay blob",
                        None,
                    );
                    let _ = crate::stats_db::delete_legacy_replay_blob(app, session_id);
                    return None;
                }
                let mut replay = replay;
                repair_replay_timing_if_needed(app, session_id, &mut replay);
                repair_tick_stream_if_needed(app, session_id, &mut replay);
                let _ = backfill_sqlite_replay(app, session_id, &replay);
                let _ = crate::stats_db::delete_legacy_replay_blob(app, session_id);
                repair_run_capture_if_needed(app, session_id, &replay);
                Some(replay)
            }
            Ok(None) => import_legacy_replay(app, session_id),
            Err(error) => {
                log::warn!(
                    "replay_store: could not load legacy sqlite replay blob for {}: {error}",
                    session_id
                );
                import_legacy_replay(app, session_id)
            }
        },
        Err(error) => {
            log::warn!(
                "replay_store: could not load sqlite replay payload for {}: {error}",
                session_id
            );
            import_legacy_replay(app, session_id)
        }
    }
}

fn load_replay_allowing_missing_video(app: &AppHandle, session_id: &str) -> Option<ReplayData> {
    match crate::stats_db::get_replay_data(app, session_id) {
        Ok(Some(mut replay)) => {
            replay.video = load_replay_video(app, session_id);
            repair_replay_timing_if_needed(app, session_id, &mut replay);
            repair_tick_stream_if_needed(app, session_id, &mut replay);
            repair_run_capture_if_needed(app, session_id, &replay);
            Some(replay)
        }
        Ok(None) => match crate::stats_db::get_legacy_replay_blob(app, session_id) {
            Ok(Some(mut replay)) => {
                repair_replay_timing_if_needed(app, session_id, &mut replay);
                repair_tick_stream_if_needed(app, session_id, &mut replay);
                let _ = backfill_sqlite_replay(app, session_id, &replay);
                let _ = crate::stats_db::delete_legacy_replay_blob(app, session_id);
                repair_run_capture_if_needed(app, session_id, &replay);
                Some(replay)
            }
            Ok(None) => import_legacy_replay_with_video_requirement(app, session_id, false),
            Err(error) => {
                log::warn!(
                    "replay_store: could not load legacy sqlite replay blob for {}: {error}",
                    session_id
                );
                import_legacy_replay_with_video_requirement(app, session_id, false)
            }
        },
        Err(error) => {
            log::warn!(
                "replay_store: could not load sqlite replay payload for {}: {error}",
                session_id
            );
            import_legacy_replay_with_video_requirement(app, session_id, false)
        }
    }
}

fn purge_invalid_replay_without_video_frames(
    app: &AppHandle,
    session_id: &str,
    source: &str,
    legacy_path: Option<&Path>,
) {
    log::warn!(
        "replay_store: deleting {} for {} because it has no saved video frames",
        source,
        session_id
    );
    if let Err(error) = crate::stats_db::delete_replay_asset(app, session_id) {
        log::warn!(
            "replay_store: could not delete replay asset for {}: {}",
            session_id,
            error
        );
    }
    delete_replay_video(app, session_id);
    let _ = crate::stats_db::delete_legacy_replay_blob(app, session_id);
    if let Some(path) = legacy_path {
        if let Err(error) = std::fs::remove_file(path) {
            if error.kind() != std::io::ErrorKind::NotFound {
                log::warn!(
                    "replay_store: could not remove legacy replay file for {} at {}: {}",
                    session_id,
                    path.display(),
                    error
                );
            }
        }
    }
}

pub fn load_replay_payload(app: &AppHandle, session_id: &str) -> Option<ReplayPayloadData> {
    load_replay(app, session_id).map(|replay| ReplayPayloadData::from(&replay))
}

pub fn load_mouse_path_payload(app: &AppHandle, session_id: &str) -> Option<ReplayPayloadData> {
    load_replay_allowing_missing_video(app, session_id)
        .map(|replay| ReplayPayloadData::from(&replay))
}
