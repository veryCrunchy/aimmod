/// File watcher module: watches the KovaaK's stats directory for new CSV files.
///
/// When a new file is detected, it's parsed and a `session-complete` event is emitted.
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant, SystemTime};
use tauri::Manager;

use notify::{Config, Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter};

pub const EVENT_SESSION_COMPLETE: &str = "session-complete";

// ─── Session result ────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionResult {
    pub scenario: String,
    pub score: f64,
    pub accuracy: f64,
    pub kills: u32,
    pub deaths: u32,
    pub duration_secs: f64,
    pub avg_ttk: f64,
    pub damage_done: f64,
    pub timestamp: String,
    pub csv_path: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct SessionCompletePayload {
    #[serde(flatten)]
    pub result: SessionResult,
    pub stats_panel: Option<crate::session_store::StatsPanelSnapshot>,
    pub run_snapshot: Option<crate::bridge::BridgeRunSnapshot>,
}

#[derive(Debug, Clone, Serialize)]
pub struct CsvImportSummary {
    pub stats_dir: String,
    pub scanned: usize,
    pub parsed: usize,
    pub imported: usize,
    pub skipped_existing: usize,
    pub failed: usize,
    pub total_after: usize,
}

// ─── Watcher state ─────────────────────────────────────────────────────────────

static WATCHER: Lazy<Mutex<Option<RecommendedWatcher>>> = Lazy::new(|| Mutex::new(None));
static WATCH_TARGET: Lazy<Mutex<Option<PathBuf>>> = Lazy::new(|| Mutex::new(None));
static WATCH_RETRY_RUNNING: AtomicBool = AtomicBool::new(false);
/// Tracks the last time each stats CSV path was fully processed so duplicate
/// fs-events (Create then Modify for the same file) are silently ignored.
/// The same file must not be reprocessed within 10 seconds.
static PROCESSED: Lazy<Mutex<HashMap<PathBuf, Instant>>> = Lazy::new(|| Mutex::new(HashMap::new()));
static WATCHER_STARTED_AT: Lazy<Mutex<Option<SystemTime>>> = Lazy::new(|| Mutex::new(None));
static LAST_SESSION_COMPLETE: Lazy<Mutex<Option<SessionCompletePayload>>> =
    Lazy::new(|| Mutex::new(None));

const DEDUP_WINDOW: Duration = Duration::from_secs(10);
// ─── Public API ────────────────────────────────────────────────────────────────

pub fn start(app: AppHandle, stats_dir: &str) {
    if let Ok(mut started) = WATCHER_STARTED_AT.lock() {
        *started = Some(SystemTime::now());
    }
    let path = PathBuf::from(stats_dir);
    if let Ok(mut target) = WATCH_TARGET.lock() {
        *target = Some(path.clone());
    }
    if !path.exists() {
        log::warn!("Stats dir does not exist: {stats_dir}");
        ensure_missing_dir_retry(app.clone());
    } else {
        WATCH_RETRY_RUNNING.store(false, Ordering::Release);
    }

    let watcher = build_watcher(app, path.clone());
    match watcher {
        Ok(w) => {
            let mut guard = WATCHER.lock().unwrap();
            *guard = Some(w);
            log::info!("File watcher started on: {stats_dir}");
        }
        Err(e) => {
            log::error!("Failed to start file watcher: {e}");
        }
    }
}

pub fn restart(app: &AppHandle, stats_dir: &str) {
    // Drop old watcher
    {
        let mut guard = WATCHER.lock().unwrap();
        *guard = None;
    }
    start(app.clone(), stats_dir);
}

pub fn last_session_complete_payload() -> Option<SessionCompletePayload> {
    LAST_SESSION_COMPLETE
        .lock()
        .ok()
        .and_then(|payload| payload.clone())
}

pub fn import_csv_history(app: &AppHandle, stats_dir: &str) -> Result<CsvImportSummary, String> {
    let stats_path = PathBuf::from(stats_dir);
    if !stats_path.exists() {
        return Err(format!("Stats directory does not exist: {stats_dir}"));
    }
    if !stats_path.is_dir() {
        return Err(format!("Stats path is not a directory: {stats_dir}"));
    }

    let mut csv_paths = std::fs::read_dir(&stats_path)
        .map_err(|e| format!("Failed to read stats directory: {e}"))?
        .filter_map(|entry| entry.ok().map(|item| item.path()))
        .filter(|path| path.extension().and_then(|ext| ext.to_str()) == Some("csv"))
        .collect::<Vec<_>>();
    csv_paths.sort();

    let mut scanned = 0usize;
    let mut failed = 0usize;
    let mut records = Vec::new();

    for path in csv_paths {
        scanned += 1;
        match parse_csv(&path) {
            Ok(result) => {
                let session_id = format!(
                    "{}-{}",
                    result.scenario.to_lowercase().replace(' ', "_"),
                    result.timestamp
                );
                records.push(crate::session_store::SessionRecord {
                    id: session_id,
                    scenario: result.scenario,
                    score: result.score,
                    accuracy: result.accuracy,
                    kills: result.kills,
                    deaths: result.deaths,
                    duration_secs: result.duration_secs,
                    avg_ttk: result.avg_ttk,
                    damage_done: result.damage_done,
                    timestamp: result.timestamp,
                    smoothness: None,
                    stats_panel: None,
                    shot_timing: None,
                    has_replay: false,
                    replay_is_favorite: false,
                    replay_positions_count: 0,
                    replay_metrics_count: 0,
                    replay_frames_count: 0,
                });
            }
            Err(error) => {
                failed += 1;
                log::warn!(
                    "historical csv import: failed to parse {}: {error}",
                    path.display()
                );
            }
        }
    }

    let merge = crate::session_store::merge_sessions(app, records);
    Ok(CsvImportSummary {
        stats_dir: stats_dir.to_string(),
        scanned,
        parsed: scanned.saturating_sub(failed),
        imported: merge.imported,
        skipped_existing: merge.skipped_existing,
        failed,
        total_after: merge.total_after,
    })
}

// ─── Watcher setup ─────────────────────────────────────────────────────────────

fn build_watcher(app: AppHandle, path: PathBuf) -> notify::Result<RecommendedWatcher> {
    let mut watcher = RecommendedWatcher::new(
        move |result: notify::Result<Event>| {
            if let Ok(event) = result {
                handle_fs_event(&app, &event);
            }
        },
        Config::default().with_poll_interval(Duration::from_secs(1)),
    )?;

    // The stats dir may not exist yet at startup; `start()` handles retrying
    // registration once the directory appears.
    if path.exists() {
        watcher.watch(&path, RecursiveMode::NonRecursive)?;
    }

    Ok(watcher)
}

fn ensure_missing_dir_retry(app: AppHandle) {
    if WATCH_RETRY_RUNNING
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
        .is_err()
    {
        return;
    }

    let _ = std::thread::Builder::new()
        .name("stats-dir-watcher-retry".into())
        .spawn(move || {
            while WATCH_RETRY_RUNNING.load(Ordering::Acquire) {
                std::thread::sleep(Duration::from_secs(1));
                let target = WATCH_TARGET.lock().ok().and_then(|guard| guard.clone());
                let Some(target) = target else {
                    continue;
                };
                if !target.exists() {
                    continue;
                }

                let stats_dir = target.to_string_lossy().into_owned();
                WATCH_RETRY_RUNNING.store(false, Ordering::Release);
                restart(&app, &stats_dir);
                break;
            }
        });
}

fn unix_now_ms() -> u64 {
    SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// Re-anchors the run window on the stats CSV (end = when it appeared,
/// length = its duration). Bridge tracking can start well before the real
/// challenge (a restart, or a recovery start while idling), and its end is
/// derived as start + duration, so both ends drift early. The replay's mouse
/// path, frames and video are all cut to this window, so it must be right.
fn align_run_window_to_csv(
    snapshot: &mut crate::bridge::BridgeRunSnapshot,
    csv_end_unix_ms: u64,
    csv_duration_secs: f64,
    scenario: &str,
) {
    const TOLERANCE_MS: u64 = 1_500;
    if !csv_duration_secs.is_finite() || csv_duration_secs <= 0.0 || csv_end_unix_ms == 0 {
        return;
    }
    let Some(tracked_start) = snapshot.started_at_unix_ms else {
        return;
    };
    let tracked_end = snapshot.ended_at_unix_ms.unwrap_or(tracked_start);
    let duration_ms = (csv_duration_secs * 1000.0).round() as u64;
    let end = csv_end_unix_ms;
    let mut start = end.saturating_sub(duration_ms);
    // Challenge time excludes pauses, so a pause inside the run pushes the
    // wall-clock start earlier by its length.
    let paused_inside: u64 = snapshot
        .pause_windows
        .iter()
        .map(|w| w.ended_at_unix_ms.min(end).saturating_sub(w.started_at_unix_ms.max(start)))
        .sum();
    start = start.saturating_sub(paused_inside);

    let start_too_early = tracked_start + TOLERANCE_MS < start;
    let end_too_early = tracked_end + TOLERANCE_MS < end;
    if !start_too_early && !end_too_early {
        return;
    }

    let shift_ms = start as i64 - tracked_start as i64;
    log::info!(
        "file_watcher: re-anchored run window for {} to the stats CSV: start {:+}ms, end {:+}ms",
        scenario,
        shift_ms,
        end as i64 - tracked_end as i64
    );
    snapshot.started_at_unix_ms = Some(start);
    snapshot.ended_at_unix_ms = Some(end);
    if let Some(bridge_ts) = snapshot.started_at_bridge_ts_ms {
        snapshot.started_at_bridge_ts_ms = Some((bridge_ts as i64 + shift_ms).max(0) as u64);
    }
    snapshot
        .pause_windows
        .retain(|w| w.ended_at_unix_ms > start && w.started_at_unix_ms < end);
}

fn process_stats_csv(app: AppHandle, path: PathBuf) {
    // KovaaK's writes the stats CSV the moment a run ends, so detection time is
    // the most reliable run end AimMod has.
    let csv_detected_unix_ms = unix_now_ms();
    // Give KovaaK's a brief window to finish flushing the CSV before parsing.
    std::thread::sleep(Duration::from_millis(500));
    match parse_csv(&path) {
        Ok(result) => {
            log::info!(
                "Session complete: {} score={}",
                result.scenario,
                result.score
            );
            // Capture smoothness summary BEFORE draining buffers
            let smoothness = crate::mouse_hook::session_summary();
            let mut stats_panel = crate::bridge::take_stats_panel_snapshot();
            // Seal the run through the bridge lifecycle so session-active
            // state, mouse metrics, and screen capture stay in sync.
            crate::bridge::finalize_session_tracking_from_csv_completion(&app);
            let persistence_sync_ok = crate::bridge::sync_run_capture_for_persistence(4000);
            if !persistence_sync_ok {
                log::warn!(
                    "file_watcher: bridge state sync did not complete before persistence for {}",
                    result.scenario
                );
            }
            let mut run_snapshot = crate::bridge::take_run_snapshot();
            if let Some(snapshot) = run_snapshot.as_mut() {
                align_run_window_to_csv(snapshot, csv_detected_unix_ms, result.duration_secs, &result.scenario);
            }
            if let Some(snapshot) = run_snapshot.as_ref() {
                let tick_keyframes = snapshot
                    .tick_stream_v1
                    .as_ref()
                    .map(|stream| stream.keyframes.len())
                    .unwrap_or(0);
                let tick_deltas = snapshot
                    .tick_stream_v1
                    .as_ref()
                    .map(|stream| stream.deltas.len())
                    .unwrap_or(0);
                if snapshot.tick_stream_v1.is_none() {
                    log::warn!(
                        "file_watcher: persisted run snapshot without tick stream for {} shot_events={} timeline_points={}",
                        result.scenario,
                        snapshot.shot_telemetry.len(),
                        snapshot.timeline.len()
                    );
                } else {
                    log::info!(
                        "file_watcher: persisted run snapshot tick stream for {} keyframes={} deltas={} shot_events={} timeline_points={}",
                        result.scenario,
                        tick_keyframes,
                        tick_deltas,
                        snapshot.shot_telemetry.len(),
                        snapshot.timeline.len()
                    );
                }
            }
            // Drain path + metric + video buffers using the finalized
            // run timing so split capture segments from recovery/restarts
            // are matched back to the correct run before persistence.
            let mouse_capture =
                crate::mouse_hook::take_replay_capture_for_run(run_snapshot.as_ref());
            let screen_frames = crate::screen_recorder::take_frames_for_run(run_snapshot.as_ref());
            let screen_video = crate::fthr_recorder::take_video_for_run(run_snapshot.as_ref());
            let shot_timing =
                run_snapshot
                    .as_ref()
                    .map(|snap| crate::session_store::ShotTimingSnapshot {
                        paired_shot_hits: snap.paired_shot_hits,
                        avg_fire_to_hit_ms: snap.avg_fire_to_hit_ms.map(|v| v as f32),
                        p90_fire_to_hit_ms: snap.p90_fire_to_hit_ms.map(|v| v as f32),
                        avg_shots_to_hit: snap.avg_shots_to_hit.map(|v| v as f32),
                        corrective_shot_ratio: snap.corrective_shot_ratio.map(|v| v as f32),
                    });
            if let Some(base_stats_panel) = stats_panel.clone() {
                let classification = crate::bridge::classify_persisted_session(
                    Some(result.scenario.as_str()),
                    &base_stats_panel,
                    run_snapshot.as_ref(),
                    run_snapshot
                        .as_ref()
                        .map(|snap| snap.shot_telemetry.as_slice())
                        .unwrap_or(&[]),
                );
                if classification.family != "Unknown"
                    && (base_stats_panel.scenario_type == "Unknown"
                        || base_stats_panel.scenario_subtype.is_none())
                {
                    let mut next = base_stats_panel;
                    next.scenario_type = classification.family;
                    next.scenario_subtype = classification.subtype;
                    stats_panel = Some(next);
                }
            }
            // Build session id and persist the session row before
            // child replay/run tables, which are foreign-keyed to sessions.
            let session_id = format!(
                "{}-{}",
                result.scenario.to_lowercase().replace(' ', "_"),
                result.timestamp
            );
            let record = crate::session_store::SessionRecord {
                id: session_id.clone(),
                scenario: result.scenario.clone(),
                score: result.score,
                accuracy: result.accuracy,
                kills: result.kills,
                deaths: result.deaths,
                duration_secs: result.duration_secs,
                avg_ttk: result.avg_ttk,
                damage_done: result.damage_done,
                timestamp: result.timestamp.clone(),
                smoothness: smoothness.clone(),
                stats_panel: stats_panel.clone(),
                shot_timing: shot_timing.clone(),
                has_replay: false,
                replay_is_favorite: false,
                replay_positions_count: 0,
                replay_metrics_count: 0,
                replay_frames_count: 0,
            };
            crate::session_store::add_session(&app, record);

            let has_replay = crate::replay_store::save_replay(
                &app,
                &session_id,
                crate::replay_store::ReplayData {
                    positions: mouse_capture.positions,
                    metrics: mouse_capture.metrics,
                    frames: screen_frames,
                    run_snapshot: run_snapshot.clone(),
                    video: screen_video,
                },
            );
            if has_replay {
                crate::session_store::set_session_has_replay(&app, &session_id, true);
            }
            let audit_app = app.clone();
            let audit_session_id = session_id.clone();
            let _ = std::thread::Builder::new()
                .name("session-sql-audit".into())
                .spawn(move || {
                    match crate::stats_db::audit_session_sql(&audit_app, &audit_session_id) {
                        Ok(audit) if audit.has_issues() => {
                            let _ =
                                crate::stats_db::persist_session_sql_audit(&audit_app, &audit);
                            log::warn!(
                                "file_watcher: sql audit failed for {} classes={} issues={} replay=({}/{}/{}) summary_rows={} timeline_rows={} shot_events={} shot_targets={} context_windows={}",
                                audit_session_id,
                                audit.failure_classes.join(","),
                                audit.issues.join(" | "),
                                audit.replay_positions_rows,
                                audit.replay_metrics_rows,
                                audit.replay_frames_rows,
                                audit.run_summary_rows,
                                audit.run_timeline_rows,
                                audit.shot_event_rows,
                                audit.shot_target_rows,
                                audit.context_window_rows,
                            );
                        }
                        Ok(audit) => {
                            let _ =
                                crate::stats_db::persist_session_sql_audit(&audit_app, &audit);
                            log::info!(
                                "file_watcher: sql audit ok for {} replay=({}/{}/{}) summary_rows={} timeline_rows={} shot_events={} shot_targets={} context_windows={}",
                                audit_session_id,
                                audit.replay_positions_rows,
                                audit.replay_metrics_rows,
                                audit.replay_frames_rows,
                                audit.run_summary_rows,
                                audit.run_timeline_rows,
                                audit.shot_event_rows,
                                audit.shot_target_rows,
                                audit.context_window_rows,
                            );
                        }
                        Err(error) => {
                            log::warn!(
                                "file_watcher: could not audit persisted session {}: {error}",
                                audit_session_id
                            );
                        }
                    }
                });
            let payload = SessionCompletePayload {
                result: result.clone(),
                stats_panel: stats_panel.clone(),
                run_snapshot: run_snapshot.clone(),
            };
            if let Ok(mut last) = LAST_SESSION_COMPLETE.lock() {
                *last = Some(payload.clone());
            }
            crate::overlay_service::notify_state_changed();
            let _ = app.emit(EVENT_SESSION_COMPLETE, payload);
            crate::hub_sync::queue_session_upload(
                &app,
                crate::hub_sync::SessionUploadInput {
                    session_id: session_id.clone(),
                    result: result.clone(),
                    smoothness: smoothness.clone(),
                    stats_panel: stats_panel.clone(),
                    shot_timing: shot_timing.clone(),
                    run_snapshot: run_snapshot.clone(),
                },
            );
            // Bring the stats window to the foreground so the
            // user sees results immediately without manual action.
            let should_open_stats = crate::settings::load(&app)
                .map(|settings| settings.open_stats_window_on_session_complete)
                .unwrap_or(false);
            if should_open_stats {
                if let Some(win) = app.get_webview_window("stats") {
                    let _ = win.show();
                    let _ = win.set_focus();
                }
            }
        }
        Err(e) => {
            log::warn!("Failed to parse stats CSV {}: {e}", path.display());
        }
    }
}

fn handle_fs_event(app: &AppHandle, event: &Event) {
    match &event.kind {
        EventKind::Create(_) | EventKind::Modify(_) => {
            for path in &event.paths {
                if path.extension().and_then(|e| e.to_str()) == Some("csv") {
                    // Ignore stale files that predate watcher startup; only react
                    // to files written/updated during this runtime.
                    let watcher_started_at = WATCHER_STARTED_AT.lock().ok().and_then(|g| *g);
                    if let (Some(started_at), Ok(meta)) =
                        (watcher_started_at, std::fs::metadata(path))
                    {
                        if let Ok(modified_at) = meta.modified() {
                            if modified_at + Duration::from_secs(1) < started_at {
                                log::debug!(
                                    "Ignoring stale stats csv event (modified before watcher start): {}",
                                    path.display()
                                );
                                continue;
                            }
                        }
                    }
                    // ── Deduplication ─────────────────────────────────────────
                    // notify fires both Create and Modify (and sometimes two
                    // Modify events) for the same file within milliseconds.
                    // Only the first event within DEDUP_WINDOW is processed.
                    let canonical = path.canonicalize().unwrap_or_else(|_| path.clone());
                    {
                        let mut seen = PROCESSED.lock().unwrap();
                        // Evict stale entries to keep the map small.
                        seen.retain(|_, t| t.elapsed() < DEDUP_WINDOW);
                        if let Some(prev) = seen.get(&canonical) {
                            if prev.elapsed() < DEDUP_WINDOW {
                                log::debug!("Skipping duplicate fs-event for {}", path.display());
                                continue;
                            }
                        }
                        seen.insert(canonical, Instant::now());
                    }
                    log::info!("New stats file detected: {}", path.display());
                    let app = app.clone();
                    let path = path.clone();
                    let _ = std::thread::Builder::new()
                        .name("stats-csv-processor".into())
                        .spawn(move || process_stats_csv(app, path));
                }
            }
        }
        _ => {}
    }
}

// ─── CSV parsing ───────────────────────────────────────────────────────────────
//
// KovaaK's CSV format has a header section and a "Score" row.
// Example filename: "Gridshot Ultimate - Challenge Start - 2024.01.15-12.30.45.csv"
// The file contains rows like: Timestamp,Value1,Value2,...
// A summary row at the end contains key=value pairs.

fn parse_csv(path: &Path) -> anyhow::Result<SessionResult> {
    // Extract scenario name and timestamp from filename
    let filename = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("Unknown");

    let (scenario, timestamp) = parse_filename(filename);

    let content = std::fs::read_to_string(path)?;
    let mut score = 0.0f64;
    let mut kills = 0u32;
    let mut deaths = 0u32;
    let mut hit_count = 0u32;
    let mut miss_count = 0u32;
    let mut avg_ttk = 0.0f64;
    let mut damage_done = 0.0f64;
    let mut challenge_start = String::new();

    // KovaaK's stats CSVs use a "Key:,Value" summary section.
    // Real field names (from actual CSV inspection):
    //   Score:,672.0  Kills:,71  Deaths:,0  Hit Count:,71  Miss Count:,19
    //   Avg TTK:,0.844892  Damage Done:,71.0  Challenge Start:,05:55:57.528
    for line in content.lines() {
        let trimmed = line.trim();
        if let Some((key, val)) = trimmed.split_once(':') {
            let key = key.trim().to_lowercase();
            // val portion may start with "," (CSV column), strip it
            let val = val.trim().trim_start_matches(',').trim();
            match key.as_str() {
                "score" => score = val.parse().unwrap_or(0.0),
                "kills" => kills = val.parse().unwrap_or(0),
                "deaths" => deaths = val.parse().unwrap_or(0),
                "hit count" => hit_count = val.parse().unwrap_or(0),
                "miss count" => miss_count = val.parse().unwrap_or(0),
                "avg ttk" => {
                    // Value is in seconds, no "s" suffix in summary row
                    avg_ttk = val.trim_end_matches('s').parse().unwrap_or(0.0);
                }
                "damage done" => damage_done = val.parse().unwrap_or(0.0),
                "challenge start" => {
                    // split_once(':') splits on the FIRST colon (after "Challenge Start").
                    // val is then ",05:55:57.528" → after strip comma → "05:55:57.528".
                    challenge_start = val.to_string();
                }
                _ => {}
            }
        }
    }

    // Compute accuracy as a percentage (0..100) from shot counts.
    let accuracy = if hit_count + miss_count > 0 {
        (hit_count as f64 / (hit_count + miss_count) as f64) * 100.0
    } else {
        0.0
    };

    // Compute duration from filename end-timestamp minus Challenge Start.
    // Filename timestamp format: "YYYY.MM.DD-HH.mm.ss"  (e.g. "2026.02.03-05.56.57")
    // The time portion is after the last '-': "05.56.57" → seconds.
    // Challenge Start is "HH:MM:SS.mmm" → seconds.
    let duration_secs = compute_duration(&timestamp, &challenge_start);

    Ok(SessionResult {
        scenario,
        score,
        accuracy,
        kills,
        deaths,
        duration_secs,
        avg_ttk,
        damage_done,
        timestamp,
        csv_path: path.to_string_lossy().into_owned(),
    })
}

/// Compute challenge duration (seconds) from the filename end-timestamp and
/// the in-file Challenge Start time.
///
/// * `end_ts`   – filename timestamp like `"2026.02.03-05.56.57"`
/// * `start_ts` – Challenge Start field value like `"05:55:57.528"`
fn compute_duration(end_ts: &str, start_ts: &str) -> f64 {
    // Extract time portion from end timestamp: everything after the '-'
    let end_time = end_ts.splitn(2, '-').nth(1).unwrap_or("");
    // end_time is "05.56.57" — dots as separators
    let end_secs = time_str_to_secs(end_time);
    let start_secs = time_str_to_secs(start_ts);
    match (end_secs, start_secs) {
        (Some(e), Some(s)) => {
            let diff = e - s;
            // Handle midnight rollover (e.g. challenge starts at 23:59 ends at 00:01)
            if diff < 0.0 { diff + 86400.0 } else { diff }
        }
        _ => 0.0,
    }
}

/// Parse a time string to seconds since midnight.
/// Accepts separators `:` or `.` for hours, minutes, seconds.
/// Handles optional fractional seconds: "HH:MM:SS", "HH:MM:SS.mmm", "HH.MM.SS".
fn time_str_to_secs(s: &str) -> Option<f64> {
    let s = s.trim();
    // Split on the first two separators (either ':' or '.')
    // Strategy: replace '.' with ':' only if there are no ':' present already
    let normalized: String = if s.contains(':') {
        s.to_string()
    } else {
        // "05.56.57" → "05:56:57"
        let mut iter = s.splitn(3, '.');
        match (iter.next(), iter.next(), iter.next()) {
            (Some(h), Some(m), Some(sec)) => format!("{}:{}:{}", h, m, sec),
            _ => return None,
        }
    };
    // Now parse "HH:MM:SS" or "HH:MM:SS.mmm"
    let mut parts = normalized.splitn(3, ':');
    let h: f64 = parts.next()?.parse().ok()?;
    let m: f64 = parts.next()?.parse().ok()?;
    let sec: f64 = parts.next()?.parse().ok()?;
    Some(h * 3600.0 + m * 60.0 + sec)
}

/// Parse KovaaK's filename format.
///
/// KovaaK's writes several variants, all of which embed a timestamp:
///   `{Scenario} - Challenge Start - {YYYY.MM.DD-HH.mm.ss}`
///   `{Scenario} - Challenge - {YYYY.MM.DD-HH.mm.ss} Stats`
///
/// Strategy: locate the timestamp, strip the ` - <label> - ` separator that
/// precedes it, then strip any remaining challenge-mode suffix so that the
/// returned name matches the actual scenario title (not the file variant).
fn parse_filename(stem: &str) -> (String, String) {
    if let Some(ts_start) = find_timestamp(stem) {
        let timestamp = stem[ts_start..ts_start + 19].to_string();
        // Everything before the timestamp, then rfind the last " - " separator.
        let prefix = &stem[..ts_start];
        let after_sep = match prefix.rfind(" - ") {
            Some(sep) => stem[..sep].to_string(),
            None => prefix.trim_end_matches([' ', '-']).to_string(),
        };
        // Strip any challenge-mode qualifier that is part of the filename but
        // not the actual scenario name, e.g. " - Challenge Start" or " - Challenge".
        let scenario = strip_challenge_suffix(&after_sep);
        (scenario, timestamp)
    } else {
        // No timestamp found — return the whole stem unchanged.
        (stem.to_string(), String::new())
    }
}

/// Remove trailing KovaaK's file-variant suffixes from a parsed scenario name.
/// Order matters: longer/more-specific first.
pub fn strip_challenge_suffix(name: &str) -> String {
    const SUFFIXES: &[&str] = &[" - Challenge Start", " - Challenge End", " - Challenge"];
    for suffix in SUFFIXES {
        if let Some(base) = name.strip_suffix(suffix) {
            return base.to_string();
        }
    }
    name.to_string()
}

/// Return the byte offset of the first `YYYY.MM.DD-HH.mm.ss` substring, or
/// `None` if the string contains no such pattern.
fn find_timestamp(s: &str) -> Option<usize> {
    let b = s.as_bytes();
    if b.len() < 19 {
        return None;
    }
    for i in 0..=b.len() - 19 {
        if b[i + 4] == b'.'
            && b[i + 7] == b'.'
            && b[i + 10] == b'-'
            && b[i + 13] == b'.'
            && b[i + 16] == b'.'
            && b[i..i + 4].iter().all(|c| c.is_ascii_digit())
            && b[i + 5..i + 7].iter().all(|c| c.is_ascii_digit())
            && b[i + 8..i + 10].iter().all(|c| c.is_ascii_digit())
            && b[i + 11..i + 13].iter().all(|c| c.is_ascii_digit())
            && b[i + 14..i + 16].iter().all(|c| c.is_ascii_digit())
            && b[i + 17..i + 19].iter().all(|c| c.is_ascii_digit())
        {
            return Some(i);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_filename_challenge_start() {
        let (scenario, ts) =
            parse_filename("Gridshot Ultimate - Challenge Start - 2024.01.15-12.30.45");
        assert_eq!(scenario, "Gridshot Ultimate");
        assert_eq!(ts, "2024.01.15-12.30.45");
    }

    #[test]
    fn parse_filename_challenge_stats_suffix() {
        // Variant emitted by newer KovaaK's builds
        let (scenario, ts) =
            parse_filename("VT Aether Novice S5 - Challenge - 2026.02.25-12.10.59 Stats");
        assert_eq!(scenario, "VT Aether Novice S5");
        assert_eq!(ts, "2026.02.25-12.10.59");
    }

    #[test]
    fn parse_filename_no_timestamp() {
        let (scenario, ts) = parse_filename("SomeOtherFile");
        assert_eq!(scenario, "SomeOtherFile");
        assert!(ts.is_empty());
    }

    #[test]
    fn time_str_to_secs_colon_separated() {
        // "05:55:57.528" → 5*3600 + 55*60 + 57.528 = 21357.528
        let secs = time_str_to_secs("05:55:57.528").unwrap();
        assert!((secs - 21357.528).abs() < 0.001);
    }

    #[test]
    fn time_str_to_secs_dot_separated() {
        // "05.56.57" → 5*3600 + 56*60 + 57 = 21417
        let secs = time_str_to_secs("05.56.57").unwrap();
        assert!((secs - 21417.0).abs() < 0.001);
    }

    #[test]
    fn compute_duration_sixty_seconds() {
        // Filename end: "2026.02.03-05.56.57" → end time "05.56.57" → 21417s
        // Challenge Start: "05:55:57.528" → 21357.528s
        // Duration ≈ 59.472s
        let dur = compute_duration("2026.02.03-05.56.57", "05:55:57.528");
        assert!((dur - 59.472).abs() < 0.1, "got {dur}");
    }
}
