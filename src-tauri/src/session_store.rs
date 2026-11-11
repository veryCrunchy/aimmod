/// Persistent session history store.
///
/// Each completed KovaaK's session is appended here with score, accuracy and a
/// smoothness snapshot averaged across the session. Stored in the local SQLite
/// stats database, with one-time legacy import from `sessions.json`.
use anyhow::Context;
use rusqlite::{Connection, Row, Transaction, params};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use tauri::AppHandle;

const LEGACY_STORE_PATH: &str = "sessions.json";
const LEGACY_STORE_KEY: &str = "history";
const DEFAULT_PAGE_LIMIT: usize = 500;
const REPO_SQL_AUDIT_REFRESH_INTERVAL_MS: i64 = 12 * 60 * 60 * 1000;

// ─── Types ─────────────────────────────────────────────────────────────────────

/// Session-averaged smoothness snapshot.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SmoothnessSnapshot {
    /// 0–100 composite score
    pub composite: f32,
    /// Lateral RMS jitter (lower = better)
    pub jitter: f32,
    /// Overshoot / direction-reversal rate
    pub overshoot_rate: f32,
    /// Speed consistency (coefficient of variation; lower = better)
    pub velocity_std: f32,
    /// Path straightness (0–1; higher = better)
    pub path_efficiency: f32,
    /// DPI-normalised average speed (px/s at 800 DPI baseline)
    pub avg_speed: f32,
    /// Inter-click interval coefficient of variation (lower = more rhythmic).
    #[serde(default)]
    pub click_timing_cv: f32,
    /// Fraction of time in Fitts' correction phase (lower = more decisive).
    #[serde(default)]
    pub correction_ratio: f32,
    /// Systematic overshoot direction bias (0=balanced, 1=always same direction).
    #[serde(default)]
    pub directional_bias: f32,
}

/// Per-session stats-panel snapshot (fields are Option because not all scenarios
/// populate every field — the presence pattern is used to infer scenario type).
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct StatsPanelSnapshot {
    /// Scenario type inferred from populated fields.
    pub scenario_type: String,
    /// Finer scenario classification that preserves the broad family above.
    #[serde(default)]
    pub scenario_subtype: Option<String>,
    /// Final kill count (None for pure tracking).
    #[serde(default)]
    pub kills: Option<u32>,
    /// Average kills per second over session.
    #[serde(default)]
    pub avg_kps: Option<f32>,
    /// Final accuracy % (shots hit / shots fired).
    #[serde(default)]
    pub accuracy_pct: Option<f32>,
    /// Total damage dealt (None for one-shot scenarios).
    #[serde(default)]
    pub total_damage: Option<f32>,
    /// Average time-to-kill in milliseconds.
    #[serde(default)]
    pub avg_ttk_ms: Option<f32>,
    /// Best (minimum) TTK recorded during the session.
    #[serde(default)]
    pub best_ttk_ms: Option<f32>,
    /// TTK standard deviation — consistency of kill speed.
    #[serde(default)]
    pub ttk_std_ms: Option<f32>,
    /// Accuracy trend: final accuracy minus accuracy at session midpoint.
    /// Positive = improving, negative = fatiguing.
    #[serde(default)]
    pub accuracy_trend: Option<f32>,
}

/// Per-session shot recovery snapshot derived from bridge shot_fired/shot_hit events.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ShotTimingSnapshot {
    /// Number of hits paired to a preceding shot.
    #[serde(default)]
    pub paired_shot_hits: u32,
    /// Average time from shot fired to corresponding hit event.
    #[serde(default)]
    pub avg_fire_to_hit_ms: Option<f32>,
    /// 90th percentile fired->hit latency (tail of correction delays).
    #[serde(default)]
    pub p90_fire_to_hit_ms: Option<f32>,
    /// Average number of shots needed per hit (1.0 ideal for one-shot).
    #[serde(default)]
    pub avg_shots_to_hit: Option<f32>,
    /// Fraction of hits that required >1 shot before landing.
    #[serde(default)]
    pub corrective_shot_ratio: Option<f32>,
}

/// One completed session record.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionRecord {
    /// Unique identifier: `{scenario_slug}-{timestamp}`
    pub id: String,
    pub scenario: String,
    pub score: f64,
    pub accuracy: f64,
    pub kills: u32,
    pub deaths: u32,
    pub duration_secs: f64,
    pub avg_ttk: f64,
    pub damage_done: f64,
    /// Raw timestamp string from the CSV filename (YYYY.MM.DD-HH.mm.ss)
    pub timestamp: String,
    /// Session-averaged smoothness metrics (None if mouse hook had no data)
    pub smoothness: Option<SmoothnessSnapshot>,
    /// Session stats panel snapshot (None if stats-panel OCR not configured)
    #[serde(default)]
    pub stats_panel: Option<StatsPanelSnapshot>,
    /// Shot recovery quality snapshot from bridge fired->hit timing.
    #[serde(default)]
    pub shot_timing: Option<ShotTimingSnapshot>,
    /// True if a replay file (mouse path + per-second metrics) was saved.
    #[serde(default)]
    pub has_replay: bool,
    /// Whether the saved replay is pinned and exempt from retention pruning.
    #[serde(default)]
    pub replay_is_favorite: bool,
    #[serde(default)]
    pub replay_positions_count: u32,
    #[serde(default)]
    pub replay_metrics_count: u32,
    #[serde(default)]
    pub replay_frames_count: u32,
}

#[derive(Debug, Clone, Copy, Default, Serialize)]
pub struct SessionMergeResult {
    pub imported: usize,
    pub skipped_existing: usize,
    pub total_after: usize,
}

#[derive(Debug, Clone, Copy, Default, Serialize)]
pub struct SessionStoreInitReport {
    pub imported_legacy: usize,
    pub skipped_legacy_existing: usize,
    pub total_after: usize,
}

#[derive(Debug, Clone, Serialize)]
pub struct SessionHistoryPage {
    pub records: Vec<SessionRecord>,
    pub total: usize,
    pub offset: usize,
    pub limit: usize,
    pub has_more: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct RecentScenarioRecord {
    pub scenario: String,
    pub timestamp: String,
}

// ─── Public API ────────────────────────────────────────────────────────────────

pub fn initialize(app: &AppHandle) {
    match try_initialize(app) {
        Ok(report) => {
            log::info!(
                "session_store: stats db ready (legacy imported={}, skipped_existing={}, total={})",
                report.imported_legacy,
                report.skipped_legacy_existing,
                report.total_after,
            );
        }
        Err(error) => {
            log::error!("session_store: failed to initialize stats db: {error:?}");
        }
    }
}

pub fn add_session(app: &AppHandle, record: SessionRecord) {
    let _ = merge_sessions(app, vec![record]);
}

pub fn set_session_has_replay(app: &AppHandle, session_id: &str, has_replay: bool) {
    if let Err(error) = try_set_session_has_replay(app, session_id, has_replay) {
        log::warn!(
            "session_store: could not update replay flag for {}: {error}",
            session_id
        );
    }
}

pub fn merge_sessions<I>(app: &AppHandle, records: I) -> SessionMergeResult
where
    I: IntoIterator<Item = SessionRecord>,
{
    match try_merge_sessions(app, records) {
        Ok(result) => result,
        Err(error) => {
            log::warn!("session_store: merge error: {error:?}");
            SessionMergeResult::default()
        }
    }
}

pub fn get_session_page(app: &AppHandle, offset: usize, limit: usize) -> SessionHistoryPage {
    match try_get_session_page(app, offset, limit) {
        Ok(page) => page,
        Err(error) => {
            log::warn!("session_store: paged read error: {error:?}");
            SessionHistoryPage {
                records: vec![],
                total: 0,
                offset,
                limit,
                has_more: false,
            }
        }
    }
}

pub fn get_recent_scenarios(app: &AppHandle, limit: usize) -> Vec<RecentScenarioRecord> {
    match try_get_recent_scenarios(app, limit) {
        Ok(records) => records,
        Err(error) => {
            log::warn!("session_store: recent scenarios read error: {error:?}");
            vec![]
        }
    }
}

pub fn get_pending_hub_upload_page(
    app: &AppHandle,
    offset: usize,
    limit: usize,
) -> SessionHistoryPage {
    match try_get_pending_hub_upload_page(app, offset, limit) {
        Ok(page) => page,
        Err(error) => {
            log::warn!("session_store: pending hub upload read error: {error:?}");
            SessionHistoryPage {
                records: vec![],
                total: 0,
                offset,
                limit,
                has_more: false,
            }
        }
    }
}

pub fn mark_session_hub_uploaded(app: &AppHandle, session_id: &str) {
    if let Err(error) = try_mark_session_hub_uploaded(app, session_id) {
        log::warn!(
            "session_store: could not mark hub upload complete for {}: {error:?}",
            session_id
        );
    }
}

pub fn mark_session_hub_upload_failed(app: &AppHandle, session_id: &str, error_message: &str) {
    if let Err(error) = try_mark_session_hub_upload_failed(app, session_id, error_message) {
        log::warn!(
            "session_store: could not mark hub upload failure for {}: {error:?}",
            session_id
        );
    }
}

pub fn reset_all_hub_upload_marks(app: &AppHandle) {
    if let Err(error) = try_reset_all_hub_upload_marks(app) {
        log::warn!("session_store: could not reset hub upload marks: {error:?}");
    }
}

pub fn get_personal_best_for_scenario(app: &AppHandle, scenario_name: &str) -> Option<u32> {
    match try_get_personal_best_for_scenario(app, scenario_name) {
        Ok(score) => score,
        Err(error) => {
            log::warn!("session_store: personal best read error: {error:?}");
            None
        }
    }
}

pub fn clear_sessions(app: &AppHandle) {
    if let Err(error) = try_clear_sessions(app) {
        log::warn!("session_store: clear error: {error:?}");
    }
}

fn try_initialize(app: &AppHandle) -> anyhow::Result<SessionStoreInitReport> {
    let db_path = crate::stats_db::initialize(app)?;
    let conn = crate::stats_db::connect(app)?;
    let existing_sessions = count_sessions(&conn)?;
    let merge = if existing_sessions == 0 {
        load_legacy_sessions(app)
            .map(|records| try_merge_sessions(app, records))
            .transpose()?
            .unwrap_or_default()
    } else {
        SessionMergeResult {
            total_after: existing_sessions,
            ..SessionMergeResult::default()
        }
    };

    if has_legacy_snapshot_backfill_work(&conn)? {
        try_backfill_session_snapshots(app)?;
    }
    if has_legacy_scenario_name_work(&conn)? {
        try_migrate_session_names(app)?;
    }

    if should_refresh_repo_sql_audit(&conn)? {
        // Run the audit off the setup thread so startup isn't blocked.
        // The audit touches every session row (potentially thousands) and is
        // purely diagnostic — nothing in the startup path depends on it.
        let app_handle = app.clone();
        let _ = std::thread::Builder::new()
            .name("session-integrity-audit".into())
            .spawn(move || {
                match crate::stats_db::refresh_repo_sql_audit(&app_handle, Some(25)) {
                    Ok(summary) => {
                        if summary.incomplete_sessions > 0 {
                            log::warn!(
                                "session_store: integrity audit flagged {} incomplete session(s) across {} audited ids",
                                summary.incomplete_sessions,
                                summary.audited_session_ids,
                            );
                        } else {
                            log::info!(
                                "session_store: integrity audit ok across {} audited ids",
                                summary.audited_session_ids,
                            );
                        }
                    }
                    Err(error) => {
                        log::warn!("session_store: integrity audit refresh failed: {error:?}");
                    }
                }
            });
    } else {
        log::debug!("session_store: skipping repo sql audit refresh on startup (recently audited)");
    }
    match try_repair_accuracy_scale(app) {
        Ok(0) => {}
        Ok(updated) => {
            log::info!(
                "session_store: repaired legacy session accuracy scale for {} session(s)",
                updated
            );
        }
        Err(error) => {
            log::warn!("session_store: could not repair legacy accuracy scale: {error:?}");
        }
    }
    log::info!("session_store: using stats db at {}", db_path.display());
    Ok(SessionStoreInitReport {
        imported_legacy: merge.imported,
        skipped_legacy_existing: merge.skipped_existing,
        total_after: merge.total_after,
    })
}

fn try_repair_accuracy_scale(app: &AppHandle) -> anyhow::Result<usize> {
    let mut conn = crate::stats_db::connect(app)?;
    let tx = conn.transaction()?;
    let mut updated = 0usize;

    updated += tx.execute(
        "
        UPDATE sessions
        SET accuracy = accuracy * 100.0
        WHERE accuracy > 0.0
          AND accuracy <= 1.0
        ",
        [],
    )?;

    updated += tx.execute(
        "
        UPDATE session_stats_panels
        SET accuracy_pct = (
            SELECT s.accuracy
            FROM sessions s
            WHERE s.id = session_stats_panels.session_id
        )
        WHERE accuracy_pct IS NOT NULL
          AND (
            (accuracy_pct > 0.0 AND accuracy_pct <= 1.0)
            OR accuracy_pct > 100.0
          )
        ",
        [],
    )?;

    updated += tx.execute(
        "
        UPDATE session_run_summaries
        SET accuracy_pct = CASE
            WHEN shots_fired > 0.0 AND shots_hit >= 0.0 AND shots_hit <= shots_fired
                THEN (shots_hit / shots_fired) * 100.0
            WHEN accuracy_pct > 0.0 AND accuracy_pct <= 1.0
                THEN accuracy_pct * 100.0
            WHEN accuracy_pct > 100.0
                THEN NULL
            ELSE accuracy_pct
        END
        WHERE accuracy_pct IS NOT NULL
          AND (
            (accuracy_pct > 0.0 AND accuracy_pct <= 1.0)
            OR accuracy_pct > 100.0
          )
        ",
        [],
    )?;

    updated += tx.execute(
        "
        UPDATE session_run_timelines
        SET accuracy_pct = CASE
            WHEN shots_fired > 0.0 AND shots_hit >= 0.0 AND shots_hit <= shots_fired
                THEN (shots_hit / shots_fired) * 100.0
            WHEN accuracy_pct > 0.0 AND accuracy_pct <= 1.0
                THEN accuracy_pct * 100.0
            WHEN accuracy_pct > 100.0
                THEN NULL
            ELSE accuracy_pct
        END
        WHERE accuracy_pct IS NOT NULL
          AND (
            (accuracy_pct > 0.0 AND accuracy_pct <= 1.0)
            OR accuracy_pct > 100.0
          )
        ",
        [],
    )?;

    updated += tx.execute(
        "
        UPDATE session_replay_context_windows
        SET accuracy_pct = CASE
            WHEN fired_count > 0 AND hit_count >= 0 AND hit_count <= fired_count
                THEN (hit_count * 100.0) / fired_count
            ELSE NULL
        END
        WHERE accuracy_pct IS NOT NULL
          AND (
            (accuracy_pct > 0.0 AND accuracy_pct <= 1.0)
            OR accuracy_pct > 100.0
          )
        ",
        [],
    )?;

    updated += tx.execute(
        "
        UPDATE session_replay_context_windows
        SET avg_timeline_accuracy_pct = CASE
            WHEN avg_timeline_accuracy_pct > 0.0 AND avg_timeline_accuracy_pct <= 1.0
                THEN avg_timeline_accuracy_pct * 100.0
            WHEN avg_timeline_accuracy_pct > 100.0
                THEN NULL
            ELSE avg_timeline_accuracy_pct
        END
        WHERE avg_timeline_accuracy_pct IS NOT NULL
          AND (
            (avg_timeline_accuracy_pct > 0.0 AND avg_timeline_accuracy_pct <= 1.0)
            OR avg_timeline_accuracy_pct > 100.0
          )
        ",
        [],
    )?;

    tx.commit()?;
    Ok(updated)
}

fn try_merge_sessions<I>(app: &AppHandle, records: I) -> anyhow::Result<SessionMergeResult>
where
    I: IntoIterator<Item = SessionRecord>,
{
    let mut conn = crate::stats_db::connect(app)?;
    let tx = conn.transaction()?;
    let mut result = SessionMergeResult::default();

    {
        let mut insert = tx.prepare(
            "
            INSERT INTO sessions (
                id,
                scenario,
                score,
                accuracy,
                kills,
                deaths,
                duration_secs,
                avg_ttk,
                damage_done,
                timestamp,
                smoothness_json,
                stats_panel_json,
                shot_timing_json,
                has_replay
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
            ON CONFLICT(id) DO NOTHING
            ",
        )?;

        for record in records {
            let inserted = insert.execute(params![
                &record.id,
                &record.scenario,
                record.score,
                record.accuracy,
                record.kills as i64,
                record.deaths as i64,
                record.duration_secs,
                record.avg_ttk,
                record.damage_done,
                &record.timestamp,
                Option::<String>::None,
                Option::<String>::None,
                Option::<String>::None,
                if record.has_replay { 1i64 } else { 0i64 },
            ])?;
            if inserted > 0 {
                upsert_session_snapshots(&tx, &record)?;
                result.imported += 1;
            } else {
                result.skipped_existing += 1;
            }
        }
    }

    result.total_after = tx.query_row("SELECT COUNT(*) FROM sessions", [], |row| row.get(0))?;
    tx.commit()?;
    Ok(result)
}

fn try_set_session_has_replay(
    app: &AppHandle,
    session_id: &str,
    has_replay: bool,
) -> anyhow::Result<()> {
    let conn = crate::stats_db::connect(app)?;
    conn.execute(
        "UPDATE sessions SET has_replay = ?2 WHERE id = ?1",
        params![session_id, if has_replay { 1i64 } else { 0i64 }],
    )?;
    Ok(())
}

fn try_get_session_page(
    app: &AppHandle,
    offset: usize,
    limit: usize,
) -> anyhow::Result<SessionHistoryPage> {
    let conn = crate::stats_db::connect(app)?;
    let total = count_sessions(&conn)?;
    let limit = limit.max(1);
    let records = query_sessions(&conn, offset, limit)?;
    let loaded = offset.saturating_add(records.len());

    Ok(SessionHistoryPage {
        records,
        total,
        offset,
        limit,
        has_more: loaded < total,
    })
}

fn try_get_pending_hub_upload_page(
    app: &AppHandle,
    offset: usize,
    limit: usize,
) -> anyhow::Result<SessionHistoryPage> {
    let conn = crate::stats_db::connect(app)?;
    let total = count_pending_hub_uploads(&conn)?;
    let limit = limit.max(1);
    let records = query_pending_hub_uploads(&conn, offset, limit)?;
    let loaded = offset.saturating_add(records.len());

    Ok(SessionHistoryPage {
        records,
        total,
        offset,
        limit,
        has_more: loaded < total,
    })
}

fn try_get_recent_scenarios(
    app: &AppHandle,
    limit: usize,
) -> anyhow::Result<Vec<RecentScenarioRecord>> {
    let conn = crate::stats_db::connect(app)?;
    let desired = limit.max(1);
    let batch_size = desired.max(50);
    let mut recent = Vec::with_capacity(desired);
    let mut seen = HashSet::new();
    let mut offset = 0usize;

    loop {
        let mut stmt = conn.prepare(
            "
            SELECT scenario, timestamp
            FROM sessions
            ORDER BY timestamp DESC, id DESC
            LIMIT ?1 OFFSET ?2
            ",
        )?;
        let rows = stmt
            .query_map(params![batch_size as i64, offset as i64], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })?
            .collect::<Result<Vec<_>, _>>()?;

        if rows.is_empty() {
            break;
        }

        for (scenario, timestamp) in &rows {
            let normalized = normalize_scenario_name(scenario);
            if seen.insert(normalized.clone()) {
                recent.push(RecentScenarioRecord {
                    scenario: normalized,
                    timestamp: timestamp.clone(),
                });
                if recent.len() >= desired {
                    return Ok(recent);
                }
            }
        }

        if rows.len() < batch_size {
            break;
        }

        offset = offset.saturating_add(rows.len());
    }

    Ok(recent)
}

fn try_get_personal_best_for_scenario(
    app: &AppHandle,
    scenario_name: &str,
) -> anyhow::Result<Option<u32>> {
    let conn = crate::stats_db::connect(app)?;
    let target = normalize_scenario_name(scenario_name);
    let mut stmt = conn.prepare(
        "
        SELECT scenario, score
        FROM sessions
        WHERE score IS NOT NULL
        ORDER BY score DESC
        ",
    )?;
    let rows = stmt.query_map([], |row| {
        Ok((row.get::<_, String>(0)?, row.get::<_, f64>(1)?))
    })?;

    for row in rows {
        let (scenario, score) = row?;
        if normalize_scenario_name(&scenario) != target {
            continue;
        }
        if !score.is_finite() {
            continue;
        }
        return Ok(Some(score.max(0.0).round() as u32));
    }

    Ok(None)
}

fn try_clear_sessions(app: &AppHandle) -> anyhow::Result<()> {
    let conn = crate::stats_db::connect(app)?;
    conn.execute("DELETE FROM replay_assets", [])?;
    conn.execute("DELETE FROM sessions", [])?;
    // Replay videos are files, not rows, so they need clearing alongside.
    crate::replay_store::delete_all_replay_videos(app);
    Ok(())
}

fn try_migrate_session_names(app: &AppHandle) -> anyhow::Result<()> {
    let conn = crate::stats_db::connect(app)?;
    let mut stmt =
        conn.prepare("SELECT id, scenario FROM sessions ORDER BY timestamp ASC, id ASC")?;
    let rows = stmt.query_map([], |row| {
        Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
    })?;

    let mut changed = 0usize;
    for row in rows {
        let (id, scenario) = row?;
        let stripped = crate::file_watcher::strip_challenge_suffix(&scenario);
        if stripped != scenario {
            log::info!("session migration: {:?} → {:?}", scenario, stripped);
            conn.execute(
                "UPDATE sessions SET scenario = ?1 WHERE id = ?2",
                params![stripped, id],
            )?;
            changed += 1;
        }
    }

    if changed > 0 {
        log::info!("session migration: updated {} record(s)", changed);
    }
    Ok(())
}

fn load_legacy_sessions(app: &AppHandle) -> Option<Vec<SessionRecord>> {
    use tauri_plugin_store::StoreExt;

    let store = app.store(LEGACY_STORE_PATH).ok()?;
    let value = store.get(LEGACY_STORE_KEY)?;
    match serde_json::from_value::<Vec<SessionRecord>>(value.clone()) {
        Ok(records) if !records.is_empty() => Some(records),
        Ok(_) => None,
        Err(error) => {
            log::warn!("session_store: could not parse legacy sessions.json: {error}");
            None
        }
    }
}

fn count_sessions(conn: &Connection) -> anyhow::Result<usize> {
    conn.query_row("SELECT COUNT(*) FROM sessions", [], |row| row.get(0))
        .context("could not count sessions")
}

fn has_legacy_snapshot_backfill_work(conn: &Connection) -> anyhow::Result<bool> {
    conn.query_row(
        "
        SELECT EXISTS(
            SELECT 1
            FROM sessions
            WHERE smoothness_json IS NOT NULL
               OR stats_panel_json IS NOT NULL
               OR shot_timing_json IS NOT NULL
            LIMIT 1
        )
        ",
        [],
        |row| row.get::<_, bool>(0),
    )
    .context("could not check for legacy snapshot backfill work")
}

fn has_legacy_scenario_name_work(conn: &Connection) -> anyhow::Result<bool> {
    conn.query_row(
        "
        SELECT EXISTS(
            SELECT 1
            FROM sessions
            WHERE instr(scenario, ' - Challenge') > 0
            LIMIT 1
        )
        ",
        [],
        |row| row.get::<_, bool>(0),
    )
    .context("could not check for legacy scenario-name migration work")
}

fn should_refresh_repo_sql_audit(conn: &Connection) -> anyhow::Result<bool> {
    let session_count = count_sessions(conn)? as i64;
    if session_count == 0 {
        return Ok(false);
    }

    let unknown_count: i64 = conn
        .query_row(
            "
            SELECT COUNT(*)
            FROM sessions
            WHERE integrity_checked_at_unix_ms IS NULL
               OR integrity_status = 'unknown'
            ",
            [],
            |row| row.get(0),
        )
        .context("could not count unknown integrity rows")?;
    if unknown_count > 0 {
        return Ok(true);
    }

    let latest_checked_ms: Option<i64> = conn
        .query_row(
            "SELECT MAX(integrity_checked_at_unix_ms) FROM sessions",
            [],
            |row| row.get(0),
        )
        .context("could not read latest integrity audit timestamp")?;
    let now_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64;

    Ok(match latest_checked_ms {
        Some(ts) => now_ms.saturating_sub(ts) >= REPO_SQL_AUDIT_REFRESH_INTERVAL_MS,
        None => true,
    })
}

fn query_sessions(
    conn: &Connection,
    offset: usize,
    limit: usize,
) -> anyhow::Result<Vec<SessionRecord>> {
    let mut stmt = conn.prepare(
        "
        SELECT
            s.id,
            s.scenario,
            s.score,
            s.accuracy,
            s.kills,
            s.deaths,
            s.duration_secs,
            s.avg_ttk,
            s.damage_done,
            s.timestamp,
            s.smoothness_json AS legacy_smoothness_json,
            s.stats_panel_json AS legacy_stats_panel_json,
            s.shot_timing_json AS legacy_shot_timing_json,
            s.has_replay,
            COALESCE(ra.is_favorite, 0) AS replay_is_favorite,
            COALESCE(ra.positions_count, 0) AS replay_positions_count,
            COALESCE(ra.metrics_count, 0) AS replay_metrics_count,
            COALESCE(ra.frames_count, 0) AS replay_frames_count,
            sm.composite AS smoothness_composite,
            sm.jitter AS smoothness_jitter,
            sm.overshoot_rate AS smoothness_overshoot_rate,
            sm.velocity_std AS smoothness_velocity_std,
            sm.path_efficiency AS smoothness_path_efficiency,
            sm.avg_speed AS smoothness_avg_speed,
            sm.click_timing_cv AS smoothness_click_timing_cv,
            sm.correction_ratio AS smoothness_correction_ratio,
            sm.directional_bias AS smoothness_directional_bias,
            sp.scenario_type AS stats_panel_scenario_type,
            sp.scenario_subtype AS stats_panel_scenario_subtype,
            sp.kills AS stats_panel_kills,
            sp.avg_kps AS stats_panel_avg_kps,
            sp.accuracy_pct AS stats_panel_accuracy_pct,
            sp.total_damage AS stats_panel_total_damage,
            sp.avg_ttk_ms AS stats_panel_avg_ttk_ms,
            sp.best_ttk_ms AS stats_panel_best_ttk_ms,
            sp.ttk_std_ms AS stats_panel_ttk_std_ms,
            sp.accuracy_trend AS stats_panel_accuracy_trend,
            st.paired_shot_hits AS shot_timing_paired_shot_hits,
            st.avg_fire_to_hit_ms AS shot_timing_avg_fire_to_hit_ms,
            st.p90_fire_to_hit_ms AS shot_timing_p90_fire_to_hit_ms,
            st.avg_shots_to_hit AS shot_timing_avg_shots_to_hit,
            st.corrective_shot_ratio AS shot_timing_corrective_shot_ratio
        FROM sessions s
        LEFT JOIN replay_assets ra ON ra.session_id = s.id
        LEFT JOIN session_smoothness sm ON sm.session_id = s.id
        LEFT JOIN session_stats_panels sp ON sp.session_id = s.id
        LEFT JOIN session_shot_timings st ON st.session_id = s.id
        ORDER BY timestamp ASC, id ASC
        LIMIT ?1 OFFSET ?2
        ",
    )?;
    let rows = stmt.query_map(params![limit as i64, offset as i64], row_to_session_record)?;
    let mut sessions = Vec::with_capacity(limit.min(DEFAULT_PAGE_LIMIT));
    for row in rows {
        sessions.push(row?);
    }
    Ok(sessions)
}

fn query_pending_hub_uploads(
    conn: &Connection,
    offset: usize,
    limit: usize,
) -> anyhow::Result<Vec<SessionRecord>> {
    let current_schema_version = crate::hub_sync::HUB_SCHEMA_VERSION as i64;
    let mut stmt = conn.prepare(
        "
        SELECT
            s.id,
            s.scenario,
            s.score,
            s.accuracy,
            s.kills,
            s.deaths,
            s.duration_secs,
            s.avg_ttk,
            s.damage_done,
            s.timestamp,
            s.smoothness_json AS legacy_smoothness_json,
            s.stats_panel_json AS legacy_stats_panel_json,
            s.shot_timing_json AS legacy_shot_timing_json,
            s.has_replay,
            COALESCE(ra.is_favorite, 0) AS replay_is_favorite,
            COALESCE(ra.positions_count, 0) AS replay_positions_count,
            COALESCE(ra.metrics_count, 0) AS replay_metrics_count,
            COALESCE(ra.frames_count, 0) AS replay_frames_count,
            sm.composite AS smoothness_composite,
            sm.jitter AS smoothness_jitter,
            sm.overshoot_rate AS smoothness_overshoot_rate,
            sm.velocity_std AS smoothness_velocity_std,
            sm.path_efficiency AS smoothness_path_efficiency,
            sm.avg_speed AS smoothness_avg_speed,
            sm.click_timing_cv AS smoothness_click_timing_cv,
            sm.correction_ratio AS smoothness_correction_ratio,
            sm.directional_bias AS smoothness_directional_bias,
            sp.scenario_type AS stats_panel_scenario_type,
            sp.scenario_subtype AS stats_panel_scenario_subtype,
            sp.kills AS stats_panel_kills,
            sp.avg_kps AS stats_panel_avg_kps,
            sp.accuracy_pct AS stats_panel_accuracy_pct,
            sp.total_damage AS stats_panel_total_damage,
            sp.avg_ttk_ms AS stats_panel_avg_ttk_ms,
            sp.best_ttk_ms AS stats_panel_best_ttk_ms,
            sp.ttk_std_ms AS stats_panel_ttk_std_ms,
            sp.accuracy_trend AS stats_panel_accuracy_trend,
            st.paired_shot_hits AS shot_timing_paired_shot_hits,
            st.avg_fire_to_hit_ms AS shot_timing_avg_fire_to_hit_ms,
            st.p90_fire_to_hit_ms AS shot_timing_p90_fire_to_hit_ms,
            st.avg_shots_to_hit AS shot_timing_avg_shots_to_hit,
            st.corrective_shot_ratio AS shot_timing_corrective_shot_ratio
        FROM sessions s
        LEFT JOIN replay_assets ra ON ra.session_id = s.id
        LEFT JOIN session_smoothness sm ON sm.session_id = s.id
        LEFT JOIN session_stats_panels sp ON sp.session_id = s.id
        LEFT JOIN session_shot_timings st ON st.session_id = s.id
        WHERE (
                s.hub_uploaded_at_unix_ms IS NULL
                OR COALESCE(s.hub_uploaded_schema_version, 0) < ?3
              )
          AND (
                s.hub_upload_next_retry_at_unix_ms IS NULL
                OR s.hub_upload_next_retry_at_unix_ms <= CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
              )
        ORDER BY timestamp DESC, id DESC
        LIMIT ?1 OFFSET ?2
        ",
    )?;
    let rows = stmt.query_map(
        params![limit as i64, offset as i64, current_schema_version],
        row_to_session_record,
    )?;
    let mut sessions = Vec::with_capacity(limit.min(DEFAULT_PAGE_LIMIT));
    for row in rows {
        sessions.push(row?);
    }
    Ok(sessions)
}

fn count_pending_hub_uploads(conn: &Connection) -> anyhow::Result<usize> {
    let current_schema_version = crate::hub_sync::HUB_SCHEMA_VERSION as i64;
    let count = conn.query_row(
        "
        SELECT COUNT(*)
        FROM sessions
        WHERE (
                hub_uploaded_at_unix_ms IS NULL
                OR COALESCE(hub_uploaded_schema_version, 0) < ?1
              )
          AND (
                hub_upload_next_retry_at_unix_ms IS NULL
                OR hub_upload_next_retry_at_unix_ms <= CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
              )
        ",
        params![current_schema_version],
        |row| row.get::<_, i64>(0),
    )?;
    Ok(count.max(0) as usize)
}

fn try_mark_session_hub_uploaded(app: &AppHandle, session_id: &str) -> anyhow::Result<()> {
    let conn = crate::stats_db::connect(app)?;
    let uploaded_at_unix_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64;
    let uploaded_schema_version = crate::hub_sync::HUB_SCHEMA_VERSION as i64;
    conn.execute(
        "
        UPDATE sessions
        SET hub_uploaded_at_unix_ms = ?2,
            hub_uploaded_schema_version = ?3,
            hub_upload_retry_count = 0,
            hub_upload_next_retry_at_unix_ms = NULL,
            hub_upload_last_error = NULL
        WHERE id = ?1
        ",
        params![session_id, uploaded_at_unix_ms, uploaded_schema_version],
    )?;
    Ok(())
}

fn try_mark_session_hub_upload_failed(
    app: &AppHandle,
    session_id: &str,
    error_message: &str,
) -> anyhow::Result<()> {
    let conn = crate::stats_db::connect(app)?;
    let retry_count = conn
        .query_row(
            "
            SELECT COALESCE(hub_upload_retry_count, 0)
            FROM sessions
            WHERE id = ?1
            ",
            params![session_id],
            |row| row.get::<_, i64>(0),
        )
        .unwrap_or(0)
        .max(0) as u32
        + 1;
    let shift = retry_count.saturating_sub(1).min(8);
    let delay_ms = (60_000u64)
        .saturating_mul(1u64 << shift)
        .min(6 * 60 * 60 * 1000);
    let next_retry_at_unix_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .saturating_add(delay_ms as u128) as i64;
    let truncated_error: String = error_message.chars().take(512).collect();
    conn.execute(
        "
        UPDATE sessions
        SET hub_upload_retry_count = ?2,
            hub_upload_next_retry_at_unix_ms = ?3,
            hub_upload_last_error = ?4
        WHERE id = ?1
        ",
        params![
            session_id,
            retry_count as i64,
            next_retry_at_unix_ms,
            truncated_error,
        ],
    )?;
    Ok(())
}

fn try_reset_all_hub_upload_marks(app: &AppHandle) -> anyhow::Result<()> {
    let conn = crate::stats_db::connect(app)?;
    conn.execute(
        "
        UPDATE sessions
        SET hub_uploaded_at_unix_ms = NULL,
            hub_uploaded_schema_version = NULL,
            hub_upload_retry_count = 0,
            hub_upload_next_retry_at_unix_ms = NULL,
            hub_upload_last_error = NULL
        ",
        [],
    )?;
    Ok(())
}

fn try_backfill_session_snapshots(app: &AppHandle) -> anyhow::Result<()> {
    let mut conn = crate::stats_db::connect(app)?;
    let tx = conn.transaction()?;
    let rows = {
        let mut stmt = tx.prepare(
            "
            SELECT id, smoothness_json, stats_panel_json, shot_timing_json
            FROM sessions
            WHERE smoothness_json IS NOT NULL
               OR stats_panel_json IS NOT NULL
               OR shot_timing_json IS NOT NULL
            ",
        )?;
        stmt.query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, Option<String>>(1)?,
                row.get::<_, Option<String>>(2)?,
                row.get::<_, Option<String>>(3)?,
            ))
        })?
        .collect::<Result<Vec<_>, _>>()?
    };

    let mut migrated = 0usize;
    for (id, smoothness_json, stats_panel_json, shot_timing_json) in rows {
        let record = SessionRecord {
            id: id.clone(),
            scenario: String::new(),
            score: 0.0,
            accuracy: 0.0,
            kills: 0,
            deaths: 0,
            duration_secs: 0.0,
            avg_ttk: 0.0,
            damage_done: 0.0,
            timestamp: String::new(),
            smoothness: deserialize_optional_json(&id, "smoothness_json", smoothness_json),
            stats_panel: deserialize_optional_json(&id, "stats_panel_json", stats_panel_json),
            shot_timing: deserialize_optional_json(&id, "shot_timing_json", shot_timing_json),
            has_replay: false,
            replay_is_favorite: false,
            replay_positions_count: 0,
            replay_metrics_count: 0,
            replay_frames_count: 0,
        };
        upsert_session_snapshots(&tx, &record)?;
        tx.execute(
            "
            UPDATE sessions
            SET smoothness_json = NULL,
                stats_panel_json = NULL,
                shot_timing_json = NULL
            WHERE id = ?1
            ",
            params![id],
        )?;
        migrated += 1;
    }

    tx.commit()?;
    if migrated > 0 {
        log::info!(
            "session_store: migrated {} session snapshot row(s) into typed SQL tables",
            migrated
        );
    }
    Ok(())
}

fn normalize_scenario_name(name: &str) -> String {
    let stripped = crate::file_watcher::strip_challenge_suffix(name);
    if let Some(ts_start) = timestamp_marker_start(&stripped) {
        if let Some(sep) = stripped[..ts_start].rfind(" - ") {
            return stripped[..sep].to_string();
        }
    }
    stripped.to_string()
}

fn timestamp_marker_start(value: &str) -> Option<usize> {
    let bytes = value.as_bytes();
    if bytes.len() < 19 {
        return None;
    }

    (0..=bytes.len() - 19).find(|start| timestamp_matches_at(bytes, *start))
}

fn timestamp_matches_at(bytes: &[u8], start: usize) -> bool {
    const DIGIT_POSITIONS: [usize; 14] = [0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18];
    const DOT_POSITIONS: [usize; 4] = [4, 7, 13, 16];

    if bytes.get(start + 10) != Some(&b'-') {
        return false;
    }
    if DIGIT_POSITIONS
        .iter()
        .any(|offset| !bytes[start + offset].is_ascii_digit())
    {
        return false;
    }
    if DOT_POSITIONS
        .iter()
        .any(|offset| bytes[start + offset] != b'.')
    {
        return false;
    }
    true
}

fn row_to_session_record(row: &rusqlite::Row<'_>) -> rusqlite::Result<SessionRecord> {
    let id = row.get::<_, String>(0)?;
    let smoothness_json = row.get::<_, Option<String>>("legacy_smoothness_json")?;
    let stats_panel_json = row.get::<_, Option<String>>("legacy_stats_panel_json")?;
    let shot_timing_json = row.get::<_, Option<String>>("legacy_shot_timing_json")?;
    let has_replay = row.get::<_, i64>("has_replay")? != 0;

    Ok(SessionRecord {
        id: id.clone(),
        scenario: row.get(1)?,
        score: row.get(2)?,
        accuracy: row.get(3)?,
        kills: row.get::<_, i64>(4)? as u32,
        deaths: row.get::<_, i64>(5)? as u32,
        duration_secs: row.get(6)?,
        avg_ttk: row.get(7)?,
        damage_done: row.get(8)?,
        timestamp: row.get(9)?,
        smoothness: smoothness_from_row(row)?
            .or_else(|| deserialize_optional_json(&id, "smoothness_json", smoothness_json)),
        stats_panel: stats_panel_from_row(row)?
            .or_else(|| deserialize_optional_json(&id, "stats_panel_json", stats_panel_json)),
        shot_timing: shot_timing_from_row(row)?
            .or_else(|| deserialize_optional_json(&id, "shot_timing_json", shot_timing_json)),
        has_replay,
        replay_is_favorite: row.get::<_, i64>("replay_is_favorite")? != 0,
        replay_positions_count: row.get::<_, i64>("replay_positions_count")?.max(0) as u32,
        replay_metrics_count: row.get::<_, i64>("replay_metrics_count")?.max(0) as u32,
        replay_frames_count: row.get::<_, i64>("replay_frames_count")?.max(0) as u32,
    })
}

fn upsert_session_snapshots(tx: &Transaction<'_>, record: &SessionRecord) -> anyhow::Result<()> {
    tx.execute(
        "DELETE FROM session_smoothness WHERE session_id = ?1",
        params![&record.id],
    )?;
    tx.execute(
        "DELETE FROM session_stats_panels WHERE session_id = ?1",
        params![&record.id],
    )?;
    tx.execute(
        "DELETE FROM session_shot_timings WHERE session_id = ?1",
        params![&record.id],
    )?;

    if let Some(smoothness) = record.smoothness.as_ref() {
        tx.execute(
            "
            INSERT INTO session_smoothness (
                session_id,
                composite,
                jitter,
                overshoot_rate,
                velocity_std,
                path_efficiency,
                avg_speed,
                click_timing_cv,
                correction_ratio,
                directional_bias
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
            ",
            params![
                &record.id,
                smoothness.composite,
                smoothness.jitter,
                smoothness.overshoot_rate,
                smoothness.velocity_std,
                smoothness.path_efficiency,
                smoothness.avg_speed,
                smoothness.click_timing_cv,
                smoothness.correction_ratio,
                smoothness.directional_bias,
            ],
        )?;
    }

    if let Some(stats_panel) = record.stats_panel.as_ref() {
        tx.execute(
            "
            INSERT INTO session_stats_panels (
                session_id,
                scenario_type,
                scenario_subtype,
                kills,
                avg_kps,
                accuracy_pct,
                total_damage,
                avg_ttk_ms,
                best_ttk_ms,
                ttk_std_ms,
                accuracy_trend
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
            ",
            params![
                &record.id,
                &stats_panel.scenario_type,
                &stats_panel.scenario_subtype,
                stats_panel.kills.map(|value| value as i64),
                stats_panel.avg_kps,
                stats_panel.accuracy_pct,
                stats_panel.total_damage,
                stats_panel.avg_ttk_ms,
                stats_panel.best_ttk_ms,
                stats_panel.ttk_std_ms,
                stats_panel.accuracy_trend,
            ],
        )?;
    }

    if let Some(shot_timing) = record.shot_timing.as_ref() {
        tx.execute(
            "
            INSERT INTO session_shot_timings (
                session_id,
                paired_shot_hits,
                avg_fire_to_hit_ms,
                p90_fire_to_hit_ms,
                avg_shots_to_hit,
                corrective_shot_ratio
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6)
            ",
            params![
                &record.id,
                shot_timing.paired_shot_hits as i64,
                shot_timing.avg_fire_to_hit_ms,
                shot_timing.p90_fire_to_hit_ms,
                shot_timing.avg_shots_to_hit,
                shot_timing.corrective_shot_ratio,
            ],
        )?;
    }

    Ok(())
}

fn smoothness_from_row(row: &Row<'_>) -> rusqlite::Result<Option<SmoothnessSnapshot>> {
    let composite = row.get::<_, Option<f32>>("smoothness_composite")?;
    let Some(composite) = composite else {
        return Ok(None);
    };

    Ok(Some(SmoothnessSnapshot {
        composite,
        jitter: row
            .get::<_, Option<f32>>("smoothness_jitter")?
            .unwrap_or_default(),
        overshoot_rate: row
            .get::<_, Option<f32>>("smoothness_overshoot_rate")?
            .unwrap_or_default(),
        velocity_std: row
            .get::<_, Option<f32>>("smoothness_velocity_std")?
            .unwrap_or_default(),
        path_efficiency: row
            .get::<_, Option<f32>>("smoothness_path_efficiency")?
            .unwrap_or_default(),
        avg_speed: row
            .get::<_, Option<f32>>("smoothness_avg_speed")?
            .unwrap_or_default(),
        click_timing_cv: row
            .get::<_, Option<f32>>("smoothness_click_timing_cv")?
            .unwrap_or_default(),
        correction_ratio: row
            .get::<_, Option<f32>>("smoothness_correction_ratio")?
            .unwrap_or_default(),
        directional_bias: row
            .get::<_, Option<f32>>("smoothness_directional_bias")?
            .unwrap_or_default(),
    }))
}

fn stats_panel_from_row(row: &Row<'_>) -> rusqlite::Result<Option<StatsPanelSnapshot>> {
    let scenario_type = row.get::<_, Option<String>>("stats_panel_scenario_type")?;
    let Some(scenario_type) = scenario_type else {
        return Ok(None);
    };
    let accuracy_pct = row.get::<_, Option<f32>>("stats_panel_accuracy_pct")?;
    let session_accuracy = row
        .get::<_, Option<f64>>("accuracy")?
        .filter(|value| value.is_finite() && *value >= 0.0 && *value <= 100.0)
        .map(|value| value as f32);

    Ok(Some(StatsPanelSnapshot {
        scenario_type,
        scenario_subtype: row.get("stats_panel_scenario_subtype")?,
        kills: row
            .get::<_, Option<i64>>("stats_panel_kills")?
            .map(|value| value as u32),
        avg_kps: row.get("stats_panel_avg_kps")?,
        accuracy_pct: normalize_stats_panel_accuracy_pct(accuracy_pct, session_accuracy),
        total_damage: row.get("stats_panel_total_damage")?,
        avg_ttk_ms: row.get("stats_panel_avg_ttk_ms")?,
        best_ttk_ms: row.get("stats_panel_best_ttk_ms")?,
        ttk_std_ms: row.get("stats_panel_ttk_std_ms")?,
        accuracy_trend: row.get("stats_panel_accuracy_trend")?,
    }))
}

fn shot_timing_from_row(row: &Row<'_>) -> rusqlite::Result<Option<ShotTimingSnapshot>> {
    let paired_shot_hits = row.get::<_, Option<i64>>("shot_timing_paired_shot_hits")?;
    let avg_fire_to_hit_ms = row.get::<_, Option<f32>>("shot_timing_avg_fire_to_hit_ms")?;
    let p90_fire_to_hit_ms = row.get::<_, Option<f32>>("shot_timing_p90_fire_to_hit_ms")?;
    let avg_shots_to_hit = row.get::<_, Option<f32>>("shot_timing_avg_shots_to_hit")?;
    let corrective_shot_ratio = row.get::<_, Option<f32>>("shot_timing_corrective_shot_ratio")?;

    if paired_shot_hits.is_none()
        && avg_fire_to_hit_ms.is_none()
        && p90_fire_to_hit_ms.is_none()
        && avg_shots_to_hit.is_none()
        && corrective_shot_ratio.is_none()
    {
        return Ok(None);
    }

    Ok(Some(ShotTimingSnapshot {
        paired_shot_hits: paired_shot_hits.unwrap_or_default() as u32,
        avg_fire_to_hit_ms,
        p90_fire_to_hit_ms,
        avg_shots_to_hit,
        corrective_shot_ratio,
    }))
}

fn normalize_stats_panel_accuracy_pct(
    accuracy_pct: Option<f32>,
    fallback_accuracy_pct: Option<f32>,
) -> Option<f32> {
    if let Some(value) = accuracy_pct.filter(|value| value.is_finite() && *value >= 0.0) {
        if value <= 1.0 {
            return Some((value * 100.0).clamp(0.0, 100.0));
        }
        if value <= 100.0 {
            return Some(value);
        }
    }

    fallback_accuracy_pct
}

fn deserialize_optional_json<T>(
    session_id: &str,
    field_name: &str,
    value: Option<String>,
) -> Option<T>
where
    T: for<'de> Deserialize<'de>,
{
    let json = value?;
    match serde_json::from_str::<T>(&json) {
        Ok(parsed) => Some(parsed),
        Err(error) => {
            log::warn!(
                "session_store: could not decode {} for {}: {}",
                field_name,
                session_id,
                error
            );
            None
        }
    }
}
