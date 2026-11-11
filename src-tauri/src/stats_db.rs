use anyhow::{Context, Result};
use rusqlite::{Connection, OptionalExtension, params};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Manager};

pub const DB_FILE_NAME: &str = "stats.sqlite3";
const SCHEMA_VERSION: i32 = 21;

pub struct ReplayAssetRecord<'a> {
    pub session_id: &'a str,
    pub file_path: &'a Path,
    pub positions_count: usize,
    pub metrics_count: usize,
    pub frames_count: usize,
    pub has_run_snapshot: bool,
}

#[derive(Debug, Clone)]
pub struct ReplayUploadState {
    pub is_favorite: bool,
    pub hub_media_uploaded_at_unix_ms: Option<u64>,
    pub hub_media_schema_version: u32,
    pub hub_media_uploaded_quality: Option<String>,
    pub hub_mouse_path_uploaded_at_unix_ms: Option<u64>,
    pub hub_mouse_path_schema_version: u32,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SessionReplayContextWindow {
    pub window_idx: u32,
    pub context_kind: String,
    pub label: String,
    pub phase: Option<String>,
    pub start_ms: u64,
    pub end_ms: u64,
    pub shot_event_count: u32,
    pub fired_count: u32,
    pub hit_count: u32,
    pub accuracy_pct: Option<f64>,
    pub avg_bot_count: Option<f64>,
    pub primary_target_label: Option<String>,
    pub primary_target_profile: Option<String>,
    pub primary_target_entity_id: Option<String>,
    pub primary_target_share: Option<f64>,
    pub avg_nearest_distance: Option<f64>,
    pub avg_nearest_yaw_error_deg: Option<f64>,
    pub avg_nearest_pitch_error_deg: Option<f64>,
    pub avg_score_per_minute: Option<f64>,
    pub avg_kills_per_second: Option<f64>,
    pub avg_timeline_accuracy_pct: Option<f64>,
    pub avg_damage_efficiency: Option<f64>,
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoachingStateSnapshot {
    pub snapshot_kind: String,
    pub updated_at_unix_ms: u64,
    pub sample_count: u32,
    pub settled_sample_count: u32,
    pub coverage_start_unix_ms: Option<u64>,
    pub coverage_end_unix_ms: Option<u64>,
    pub summary_json: serde_json::Value,
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoachingRecommendationEvaluation {
    pub evaluation_id: String,
    pub snapshot_kind: String,
    pub recommendation_id: String,
    pub recommendation_title: String,
    pub signal_key: String,
    pub status: String,
    pub created_at_unix_ms: u64,
    pub updated_at_unix_ms: u64,
    pub anchor_sample_count: u32,
    pub latest_sample_count: u32,
    pub anchor_metric_value: Option<f64>,
    pub latest_metric_value: Option<f64>,
    pub outcome_delta: Option<f64>,
    pub context_json: serde_json::Value,
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoachingUserFeedbackRecord {
    pub snapshot_kind: String,
    pub recommendation_id: String,
    pub signal_key: Option<String>,
    pub feedback: String,
    pub notes: Option<String>,
    pub created_at_unix_ms: u64,
    pub updated_at_unix_ms: u64,
    pub context_json: serde_json::Value,
}

fn normalize_accuracy_pct(
    value: Option<f64>,
    shots_hit: Option<f64>,
    shots_fired: Option<f64>,
) -> Option<f64> {
    if let (Some(hits), Some(shots)) = (shots_hit, shots_fired) {
        if hits.is_finite() && shots.is_finite() && hits >= 0.0 && shots > 0.0 {
            if hits <= shots + 0.0001 {
                return Some(((hits / shots) * 100.0).clamp(0.0, 100.0));
            }
        }
    }

    let direct = value.filter(|value| value.is_finite() && *value >= 0.0)?;
    if direct <= 1.0 {
        Some((direct * 100.0).clamp(0.0, 100.0))
    } else if direct <= 100.0 {
        Some(direct)
    } else {
        None
    }
}

pub fn initialize(app: &AppHandle) -> Result<PathBuf> {
    let path = db_path(app)?;
    let mut conn = open_connection(&path)?;
    configure_connection(&conn)?;
    migrate_schema(&mut conn)?;
    Ok(path)
}

pub fn connect(app: &AppHandle) -> Result<Connection> {
    let path = db_path(app)?;
    let mut conn = open_connection(&path)?;
    configure_connection(&conn)?;
    migrate_schema(&mut conn)?;
    Ok(conn)
}

pub fn upsert_replay_asset(app: &AppHandle, record: &ReplayAssetRecord<'_>) -> Result<()> {
    let conn = connect(app)?;
    let updated_at_unix_ms = current_unix_ms();

    conn.execute(
        "
        INSERT INTO replay_assets (
            session_id,
            file_path,
            positions_count,
            metrics_count,
            frames_count,
                has_run_snapshot,
                is_favorite,
                hub_media_uploaded_at_unix_ms,
                hub_media_schema_version,
                hub_media_uploaded_quality,
                hub_mouse_path_uploaded_at_unix_ms,
                hub_mouse_path_schema_version,
                updated_at_unix_ms
            )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, 0, NULL, 0, NULL, NULL, 0, ?7)
        ON CONFLICT(session_id) DO UPDATE SET
            file_path = excluded.file_path,
            positions_count = excluded.positions_count,
            metrics_count = excluded.metrics_count,
            frames_count = excluded.frames_count,
            has_run_snapshot = excluded.has_run_snapshot,
            updated_at_unix_ms = excluded.updated_at_unix_ms
        ",
        params![
            record.session_id,
            record.file_path.to_string_lossy().into_owned(),
            record.positions_count as i64,
            record.metrics_count as i64,
            record.frames_count as i64,
            if record.has_run_snapshot { 1i64 } else { 0i64 },
            updated_at_unix_ms,
        ],
    )?;

    Ok(())
}

pub fn set_replay_favorite(app: &AppHandle, session_id: &str, is_favorite: bool) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        UPDATE replay_assets
        SET is_favorite = ?2,
            updated_at_unix_ms = ?3
        WHERE session_id = ?1
        ",
        params![
            session_id,
            if is_favorite { 1i64 } else { 0i64 },
            current_unix_ms(),
        ],
    )?;
    Ok(())
}

pub fn mark_replay_media_uploaded(
    app: &AppHandle,
    session_id: &str,
    schema_version: u32,
    quality: &str,
) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        UPDATE replay_assets
        SET hub_media_uploaded_at_unix_ms = ?2,
            hub_media_schema_version = ?3,
            hub_media_uploaded_quality = ?4,
            updated_at_unix_ms = ?2
        WHERE session_id = ?1
        ",
        params![
            session_id,
            current_unix_ms(),
            schema_version as i64,
            quality
        ],
    )?;
    Ok(())
}

pub fn mark_mouse_path_uploaded(
    app: &AppHandle,
    session_id: &str,
    schema_version: u32,
) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        UPDATE replay_assets
        SET hub_mouse_path_uploaded_at_unix_ms = ?2,
            hub_mouse_path_schema_version = ?3,
            updated_at_unix_ms = ?2
        WHERE session_id = ?1
        ",
        params![session_id, current_unix_ms(), schema_version as i64],
    )?;
    Ok(())
}

pub fn clear_replay_media_upload_marks(app: &AppHandle) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        UPDATE replay_assets
        SET hub_media_uploaded_at_unix_ms = NULL,
            hub_media_schema_version = 0,
            hub_media_uploaded_quality = NULL
        ",
        [],
    )?;
    Ok(())
}

pub fn clear_mouse_path_upload_marks(app: &AppHandle) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        UPDATE replay_assets
        SET hub_mouse_path_uploaded_at_unix_ms = NULL,
            hub_mouse_path_schema_version = 0
        ",
        [],
    )?;
    Ok(())
}

pub fn get_pending_mouse_path_upload_session_ids(
    app: &AppHandle,
    offset: usize,
    limit: usize,
    current_schema_version: u32,
) -> Result<Vec<String>> {
    let conn = connect(app)?;
    let mut stmt = conn.prepare(
        "
        SELECT session_id
        FROM replay_assets
        WHERE positions_count > 0
          AND (
                hub_mouse_path_uploaded_at_unix_ms IS NULL
                OR COALESCE(hub_mouse_path_schema_version, 0) < ?3
              )
        ORDER BY updated_at_unix_ms DESC, session_id DESC
        LIMIT ?1 OFFSET ?2
        ",
    )?;
    let rows = stmt.query_map(
        params![limit as i64, offset as i64, current_schema_version as i64],
        |row| row.get::<_, String>(0),
    )?;
    rows.collect::<std::result::Result<Vec<_>, _>>()
        .map_err(Into::into)
}

pub fn get_replay_upload_state(
    app: &AppHandle,
    session_id: &str,
) -> Result<Option<ReplayUploadState>> {
    let conn = connect(app)?;
    conn.query_row(
        "
        SELECT
            is_favorite,
            hub_media_uploaded_at_unix_ms,
            COALESCE(hub_media_schema_version, 0),
            hub_media_uploaded_quality,
            hub_mouse_path_uploaded_at_unix_ms,
            COALESCE(hub_mouse_path_schema_version, 0)
        FROM replay_assets
        WHERE session_id = ?1
        ",
        params![session_id],
        |row| {
            Ok(ReplayUploadState {
                is_favorite: row.get::<_, i64>(0)? != 0,
                hub_media_uploaded_at_unix_ms: row
                    .get::<_, Option<i64>>(1)?
                    .map(|value| value as u64),
                hub_media_schema_version: row.get::<_, i64>(2)? as u32,
                hub_media_uploaded_quality: row.get::<_, Option<String>>(3)?,
                hub_mouse_path_uploaded_at_unix_ms: row
                    .get::<_, Option<i64>>(4)?
                    .map(|value| value as u64),
                hub_mouse_path_schema_version: row.get::<_, i64>(5)? as u32,
            })
        },
    )
    .optional()
    .map_err(Into::into)
}

pub fn delete_replay_asset(app: &AppHandle, session_id: &str) -> Result<()> {
    let conn = connect(app)?;
    delete_replay_asset_with_conn(&conn, session_id)?;
    Ok(())
}

pub fn prune_replay_assets(
    app: &AppHandle,
    keep_count: usize,
    protected_session_id: Option<&str>,
) -> Result<Vec<String>> {
    if keep_count == 0 {
        return Ok(Vec::new());
    }

    let conn = connect(app)?;
    let mut stmt = conn.prepare(
        "
        SELECT session_id
        FROM replay_assets
        WHERE is_favorite = 0
          AND (?1 IS NULL OR session_id != ?1)
        ORDER BY updated_at_unix_ms DESC, session_id DESC
        ",
    )?;
    let session_ids = stmt
        .query_map(params![protected_session_id], |row| row.get::<_, String>(0))?
        .collect::<std::result::Result<Vec<_>, _>>()?;

    if session_ids.len() <= keep_count {
        return Ok(Vec::new());
    }

    let mut pruned = Vec::new();
    for session_id in session_ids.into_iter().skip(keep_count) {
        delete_replay_asset_with_conn(&conn, &session_id)?;
        pruned.push(session_id);
    }

    Ok(pruned)
}

pub fn upsert_replay_payload(
    app: &AppHandle,
    session_id: &str,
    replay: &crate::replay_store::ReplayData,
) -> Result<()> {
    let target_response_analysis = crate::target_response::analyze_target_responses(replay);
    let mut conn = connect(app)?;
    let tx = conn.transaction()?;

    tx.execute(
        "DELETE FROM session_replay_positions WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_replay_metrics WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_replay_frames WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_replay_tick_streams WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_target_response_episodes WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_target_response_summaries WHERE session_id = ?1",
        params![session_id],
    )?;

    {
        let mut insert = tx.prepare(
            "
            INSERT INTO session_replay_positions (
                session_id,
                seq_idx,
                x,
                y,
                timestamp_ms,
                is_click
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6)
            ",
        )?;
        for (index, point) in replay.positions.iter().enumerate() {
            insert.execute(params![
                session_id,
                index as i64,
                point.x,
                point.y,
                point.timestamp_ms as i64,
                if point.is_click { 1i64 } else { 0i64 },
            ])?;
        }
    }

    {
        let mut insert = tx.prepare(
            "
            INSERT INTO session_replay_metrics (
                session_id,
                seq_idx,
                timestamp_ms,
                smoothness,
                jitter,
                overshoot_rate,
                velocity_std,
                avg_speed,
                path_efficiency,
                click_timing_cv,
                correction_ratio,
                directional_bias,
                avg_hold_ms
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)
            ",
        )?;
        for (index, point) in replay.metrics.iter().enumerate() {
            insert.execute(params![
                session_id,
                index as i64,
                point.timestamp_ms as i64,
                point.metrics.smoothness,
                point.metrics.jitter,
                point.metrics.overshoot_rate,
                point.metrics.velocity_std,
                point.metrics.avg_speed,
                point.metrics.path_efficiency,
                point.metrics.click_timing_cv,
                point.metrics.correction_ratio,
                point.metrics.directional_bias,
                point.metrics.avg_hold_ms,
            ])?;
        }
    }

    {
        let mut insert = tx.prepare(
            "
            INSERT INTO session_replay_frames (
                session_id,
                seq_idx,
                timestamp_ms,
                jpeg_b64
            )
            VALUES (?1, ?2, ?3, ?4)
            ",
        )?;
        for (index, frame) in replay.frames.iter().enumerate() {
            insert.execute(params![
                session_id,
                index as i64,
                frame.timestamp_ms as i64,
                frame.jpeg_b64,
            ])?;
        }
    }

    tx.execute(
        "DELETE FROM session_replay_payloads WHERE session_id = ?1",
        params![session_id],
    )?;

    if let Some(tick_stream) = replay
        .run_snapshot
        .as_ref()
        .and_then(|snapshot| snapshot.tick_stream_v1.as_ref())
    {
        tx.execute(
            "
            INSERT INTO session_replay_tick_streams (
                session_id,
                tick_stream_json,
                updated_at_unix_ms
            )
            VALUES (?1, ?2, ?3)
            ON CONFLICT(session_id) DO UPDATE SET
                tick_stream_json = excluded.tick_stream_json,
                updated_at_unix_ms = excluded.updated_at_unix_ms
            ",
            params![
                session_id,
                serde_json::to_string(tick_stream)
                    .context("could not encode replay tick stream json")?,
                current_unix_ms(),
            ],
        )?;
    }

    if let Some(analysis) = target_response_analysis.as_ref() {
        upsert_target_response_analysis_with_tx(&tx, session_id, analysis)?;
    }

    tx.commit()?;

    Ok(())
}

fn delete_replay_asset_with_conn(conn: &Connection, session_id: &str) -> Result<()> {
    conn.execute(
        "DELETE FROM session_replay_positions WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "DELETE FROM session_replay_metrics WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "DELETE FROM session_replay_frames WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "DELETE FROM session_replay_tick_streams WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "DELETE FROM session_target_response_episodes WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "DELETE FROM session_target_response_summaries WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "DELETE FROM replay_assets WHERE session_id = ?1",
        params![session_id],
    )?;
    conn.execute(
        "UPDATE sessions SET has_replay = 0 WHERE id = ?1",
        params![session_id],
    )?;
    Ok(())
}

pub fn get_replay_data(
    app: &AppHandle,
    session_id: &str,
) -> Result<Option<crate::replay_store::ReplayData>> {
    let conn = connect(app)?;
    let has_replay_asset = conn
        .query_row(
            "
            SELECT 1
            FROM replay_assets
            WHERE session_id = ?1
            ",
            params![session_id],
            |_| Ok(()),
        )
        .optional()
        .context("could not load replay asset marker")?
        .is_some();

    let positions = query_replay_positions(&conn, session_id)?;
    let metrics = query_replay_metrics(&conn, session_id)?;
    let frames = query_replay_frames(&conn, session_id)?;
    let mut run_snapshot = get_run_summary(app, session_id)?;
    if let Some(snapshot) = run_snapshot.as_mut() {
        snapshot.shot_telemetry = query_shot_telemetry(&conn, session_id)?;
        snapshot.tick_stream_v1 = query_replay_tick_stream(&conn, session_id)?;
    }

    if !has_replay_asset
        && positions.is_empty()
        && metrics.is_empty()
        && frames.is_empty()
        && run_snapshot.is_none()
    {
        return Ok(None);
    }

    Ok(Some(crate::replay_store::ReplayData {
        positions,
        metrics,
        frames,
        run_snapshot,
        // Videos live as files next to the replay; replay_store::load_replay
        // attaches them from their sidecar after this row is read.
        video: None,
    }))
}

pub fn get_legacy_replay_blob(
    app: &AppHandle,
    session_id: &str,
) -> Result<Option<crate::replay_store::ReplayData>> {
    let conn = connect(app)?;
    let replay_json = conn
        .query_row(
            "
            SELECT replay_json
            FROM session_replay_payloads
            WHERE session_id = ?1
            ",
            params![session_id],
            |row| row.get::<_, String>(0),
        )
        .optional()
        .context("could not load legacy replay payload")?;

    replay_json
        .map(|json| {
            serde_json::from_str::<crate::replay_store::ReplayData>(&json)
                .context("could not parse legacy replay payload")
        })
        .transpose()
}

pub fn delete_legacy_replay_blob(app: &AppHandle, session_id: &str) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "DELETE FROM session_replay_payloads WHERE session_id = ?1",
        params![session_id],
    )?;
    Ok(())
}

fn db_path(app: &AppHandle) -> Result<PathBuf> {
    let data_dir = app
        .path()
        .app_data_dir()
        .context("could not resolve app data directory for stats database")?;
    std::fs::create_dir_all(&data_dir)
        .with_context(|| format!("could not create app data directory {}", data_dir.display()))?;
    Ok(data_dir.join(DB_FILE_NAME))
}

fn open_connection(path: &Path) -> Result<Connection> {
    Connection::open(path)
        .with_context(|| format!("could not open stats database {}", path.display()))
}

fn configure_connection(conn: &Connection) -> Result<()> {
    conn.busy_timeout(Duration::from_secs(5))?;
    conn.pragma_update(None, "journal_mode", "WAL")?;
    conn.pragma_update(None, "synchronous", "NORMAL")?;
    conn.pragma_update(None, "foreign_keys", 1i64)?;
    conn.pragma_update(None, "temp_store", "MEMORY")?;
    Ok(())
}

fn current_unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64
}

fn migration_already_applied(error: &rusqlite::Error) -> bool {
    let message = error.to_string().to_ascii_lowercase();
    message.contains("duplicate column name") || message.contains("already exists")
}

fn run_schema_migration<F>(conn: &mut Connection, version: i32, apply: F) -> Result<()>
where
    F: FnOnce(&rusqlite::Transaction<'_>) -> Result<()>,
{
    let tx = conn.transaction()?;
    apply(&tx)?;
    tx.pragma_update(None, "user_version", version)?;
    tx.commit()?;
    Ok(())
}

fn migrate_schema(conn: &mut Connection) -> Result<()> {
    let user_version = conn.pragma_query_value(None, "user_version", |row| row.get::<_, i32>(0))?;
    if user_version >= SCHEMA_VERSION {
        return Ok(());
    }

    if user_version < 1 {
        run_schema_migration(conn, 1, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                scenario TEXT NOT NULL,
                score REAL NOT NULL,
                accuracy REAL NOT NULL,
                kills INTEGER NOT NULL,
                deaths INTEGER NOT NULL,
                duration_secs REAL NOT NULL,
                avg_ttk REAL NOT NULL,
                damage_done REAL NOT NULL,
                timestamp TEXT NOT NULL,
                smoothness_json TEXT,
                stats_panel_json TEXT,
                shot_timing_json TEXT,
                has_replay INTEGER NOT NULL DEFAULT 0 CHECK (has_replay IN (0, 1))
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_timestamp ON sessions(timestamp);
            CREATE INDEX IF NOT EXISTS idx_sessions_scenario_timestamp ON sessions(scenario, timestamp);

            CREATE TABLE IF NOT EXISTS replay_assets (
                session_id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                positions_count INTEGER NOT NULL DEFAULT 0,
                metrics_count INTEGER NOT NULL DEFAULT 0,
                frames_count INTEGER NOT NULL DEFAULT 0,
                has_run_snapshot INTEGER NOT NULL DEFAULT 0 CHECK (has_run_snapshot IN (0, 1)),
                is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
                hub_media_uploaded_at_unix_ms INTEGER,
                hub_media_schema_version INTEGER NOT NULL DEFAULT 0,
                hub_media_uploaded_quality TEXT,
                hub_mouse_path_uploaded_at_unix_ms INTEGER,
                hub_mouse_path_schema_version INTEGER NOT NULL DEFAULT 0,
                updated_at_unix_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_replay_assets_updated_at ON replay_assets(updated_at_unix_ms);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 2 {
        run_schema_migration(conn, 2, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_run_summaries (
                session_id TEXT PRIMARY KEY,
                duration_secs REAL,
                score_total REAL,
                score_total_derived REAL,
                score_per_minute REAL,
                shots_fired REAL,
                shots_hit REAL,
                kills REAL,
                kills_per_second REAL,
                damage_done REAL,
                damage_possible REAL,
                damage_efficiency REAL,
                accuracy_pct REAL,
                peak_score_per_minute REAL,
                peak_kills_per_second REAL,
                paired_shot_hits INTEGER NOT NULL DEFAULT 0,
                avg_fire_to_hit_ms REAL,
                p90_fire_to_hit_ms REAL,
                avg_shots_to_hit REAL,
                corrective_shot_ratio REAL,
                started_at_bridge_ts_ms INTEGER,
                started_at_unix_ms INTEGER,
                ended_at_unix_ms INTEGER,
                shot_fired_events INTEGER NOT NULL DEFAULT 0,
                shot_hit_events INTEGER NOT NULL DEFAULT 0,
                kill_events INTEGER NOT NULL DEFAULT 0,
                challenge_queued_events INTEGER NOT NULL DEFAULT 0,
                challenge_start_events INTEGER NOT NULL DEFAULT 0,
                challenge_end_events INTEGER NOT NULL DEFAULT 0,
                challenge_complete_events INTEGER NOT NULL DEFAULT 0,
                challenge_canceled_events INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_run_summaries_peak_spm ON session_run_summaries(peak_score_per_minute DESC);
            CREATE INDEX IF NOT EXISTS idx_run_summaries_peak_kps ON session_run_summaries(peak_kills_per_second DESC);

            CREATE TABLE IF NOT EXISTS session_run_timelines (
                session_id TEXT NOT NULL,
                t_sec INTEGER NOT NULL,
                score_per_minute REAL,
                kills_per_second REAL,
                accuracy_pct REAL,
                damage_efficiency REAL,
                score_total REAL,
                score_total_derived REAL,
                kills REAL,
                shots_fired REAL,
                shots_hit REAL,
                PRIMARY KEY(session_id, t_sec),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_run_timelines_session ON session_run_timelines(session_id, t_sec);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 3 {
        run_schema_migration(conn, 3, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_replay_payloads (
                session_id TEXT PRIMARY KEY,
                replay_json TEXT NOT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_replay_payloads_updated_at ON session_replay_payloads(updated_at_unix_ms);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 4 {
        run_schema_migration(conn, 4, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_smoothness (
                session_id TEXT PRIMARY KEY,
                composite REAL NOT NULL,
                jitter REAL NOT NULL,
                overshoot_rate REAL NOT NULL,
                velocity_std REAL NOT NULL,
                path_efficiency REAL NOT NULL,
                avg_speed REAL NOT NULL,
                click_timing_cv REAL NOT NULL,
                correction_ratio REAL NOT NULL,
                directional_bias REAL NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS session_stats_panels (
                session_id TEXT PRIMARY KEY,
                scenario_type TEXT NOT NULL,
                kills INTEGER,
                avg_kps REAL,
                accuracy_pct REAL,
                total_damage REAL,
                avg_ttk_ms REAL,
                best_ttk_ms REAL,
                ttk_std_ms REAL,
                accuracy_trend REAL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS session_shot_timings (
                session_id TEXT PRIMARY KEY,
                paired_shot_hits INTEGER NOT NULL DEFAULT 0,
                avg_fire_to_hit_ms REAL,
                p90_fire_to_hit_ms REAL,
                avg_shots_to_hit REAL,
                corrective_shot_ratio REAL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS session_replay_positions (
                session_id TEXT NOT NULL,
                seq_idx INTEGER NOT NULL,
                x REAL NOT NULL,
                y REAL NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                is_click INTEGER NOT NULL DEFAULT 0 CHECK (is_click IN (0, 1)),
                PRIMARY KEY(session_id, seq_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_replay_positions_session ON session_replay_positions(session_id, seq_idx);

            CREATE TABLE IF NOT EXISTS session_replay_metrics (
                session_id TEXT NOT NULL,
                seq_idx INTEGER NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                smoothness REAL NOT NULL,
                jitter REAL NOT NULL,
                overshoot_rate REAL NOT NULL,
                velocity_std REAL NOT NULL,
                avg_speed REAL NOT NULL,
                path_efficiency REAL NOT NULL,
                click_timing_cv REAL NOT NULL,
                correction_ratio REAL NOT NULL,
                directional_bias REAL NOT NULL,
                avg_hold_ms REAL NOT NULL,
                PRIMARY KEY(session_id, seq_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_replay_metrics_session ON session_replay_metrics(session_id, seq_idx);

            CREATE TABLE IF NOT EXISTS session_replay_frames (
                session_id TEXT NOT NULL,
                seq_idx INTEGER NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                jpeg_b64 TEXT NOT NULL,
                PRIMARY KEY(session_id, seq_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_replay_frames_session ON session_replay_frames(session_id, seq_idx);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 5 {
        run_schema_migration(conn, 5, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_shot_events (
                session_id TEXT NOT NULL,
                shot_seq_idx INTEGER NOT NULL,
                event_kind TEXT NOT NULL,
                ts_ms INTEGER NOT NULL,
                count INTEGER,
                total INTEGER,
                run_id INTEGER,
                sample_seq INTEGER,
                sample_count INTEGER,
                source TEXT,
                method TEXT,
                origin_flag TEXT,
                player_entity_id TEXT,
                player_profile TEXT,
                player_is_player INTEGER,
                player_is_bot INTEGER,
                player_x REAL,
                player_y REAL,
                player_z REAL,
                player_pitch REAL,
                player_yaw REAL,
                player_roll REAL,
                player_vx REAL,
                player_vy REAL,
                player_vz REAL,
                PRIMARY KEY(session_id, shot_seq_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_shot_events_session_ts
                ON session_shot_events(session_id, ts_ms, shot_seq_idx);

            CREATE TABLE IF NOT EXISTS session_shot_targets (
                session_id TEXT NOT NULL,
                shot_seq_idx INTEGER NOT NULL,
                target_idx INTEGER NOT NULL,
                entity_id TEXT NOT NULL,
                profile TEXT NOT NULL,
                is_player INTEGER NOT NULL DEFAULT 0 CHECK (is_player IN (0, 1)),
                is_bot INTEGER NOT NULL DEFAULT 0 CHECK (is_bot IN (0, 1)),
                x REAL NOT NULL,
                y REAL NOT NULL,
                z REAL NOT NULL,
                pitch REAL NOT NULL,
                yaw REAL NOT NULL,
                roll REAL NOT NULL,
                vx REAL NOT NULL,
                vy REAL NOT NULL,
                vz REAL NOT NULL,
                distance_2d REAL,
                distance_3d REAL,
                yaw_error_deg REAL,
                pitch_error_deg REAL,
                is_nearest INTEGER NOT NULL DEFAULT 0 CHECK (is_nearest IN (0, 1)),
                PRIMARY KEY(session_id, shot_seq_idx, target_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_shot_targets_session_seq
                ON session_shot_targets(session_id, shot_seq_idx, target_idx);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 6 {
        run_schema_migration(conn, 6, |tx| {
            if let Err(error) = tx
                .execute_batch("ALTER TABLE session_stats_panels ADD COLUMN scenario_subtype TEXT;")
            {
                if !migration_already_applied(&error) {
                    return Err(error.into());
                }
            }
            Ok(())
        })?;
    }

    if user_version < 7 {
        run_schema_migration(conn, 7, |tx| {
            if let Err(error) =
                tx.execute_batch("ALTER TABLE session_shot_events ADD COLUMN count INTEGER;")
            {
                if !migration_already_applied(&error) {
                    return Err(error.into());
                }
            }
            Ok(())
        })?;
    }

    if user_version < 8 {
        run_schema_migration(conn, 8, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_replay_context_windows (
                session_id TEXT NOT NULL,
                window_idx INTEGER NOT NULL,
                context_kind TEXT NOT NULL,
                label TEXT NOT NULL,
                phase TEXT,
                start_ms INTEGER NOT NULL,
                end_ms INTEGER NOT NULL,
                shot_event_count INTEGER NOT NULL DEFAULT 0,
                fired_count INTEGER NOT NULL DEFAULT 0,
                hit_count INTEGER NOT NULL DEFAULT 0,
                accuracy_pct REAL,
                avg_bot_count REAL,
                primary_target_label TEXT,
                primary_target_profile TEXT,
                primary_target_entity_id TEXT,
                primary_target_share REAL,
                avg_nearest_distance REAL,
                avg_nearest_yaw_error_deg REAL,
                avg_nearest_pitch_error_deg REAL,
                avg_score_per_minute REAL,
                avg_kills_per_second REAL,
                avg_timeline_accuracy_pct REAL,
                avg_damage_efficiency REAL,
                PRIMARY KEY(session_id, window_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_replay_context_windows_session
                ON session_replay_context_windows(session_id, start_ms);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 9 {
        run_schema_migration(conn, 9, |tx| {
            for statement in [
                "ALTER TABLE sessions ADD COLUMN integrity_status TEXT NOT NULL DEFAULT 'unknown';",
                "ALTER TABLE sessions ADD COLUMN integrity_failure_codes TEXT;",
                "ALTER TABLE sessions ADD COLUMN integrity_checked_at_unix_ms INTEGER;",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 10 {
        run_schema_migration(conn, 10, |tx| {
            for statement in [
                "ALTER TABLE sessions ADD COLUMN hub_uploaded_at_unix_ms INTEGER;",
                "CREATE INDEX IF NOT EXISTS idx_sessions_hub_uploaded_at ON sessions(hub_uploaded_at_unix_ms, timestamp);",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 11 {
        run_schema_migration(conn, 11, |tx| {
            for statement in [
                "ALTER TABLE sessions ADD COLUMN hub_upload_retry_count INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE sessions ADD COLUMN hub_upload_next_retry_at_unix_ms INTEGER;",
                "ALTER TABLE sessions ADD COLUMN hub_upload_last_error TEXT;",
                "CREATE INDEX IF NOT EXISTS idx_sessions_hub_upload_retry ON sessions(hub_uploaded_at_unix_ms, hub_upload_next_retry_at_unix_ms, timestamp);",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 12 {
        run_schema_migration(conn, 12, |tx| {
            for statement in [
                "ALTER TABLE sessions ADD COLUMN hub_uploaded_schema_version INTEGER;",
                "CREATE INDEX IF NOT EXISTS idx_sessions_hub_upload_schema ON sessions(hub_uploaded_at_unix_ms, hub_uploaded_schema_version, hub_upload_next_retry_at_unix_ms, timestamp);",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 13 {
        run_schema_migration(conn, 13, |tx| {
            for statement in [
                "ALTER TABLE replay_assets ADD COLUMN is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1));",
                "CREATE INDEX IF NOT EXISTS idx_replay_assets_favorite_updated ON replay_assets(is_favorite, updated_at_unix_ms);",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 14 {
        run_schema_migration(conn, 14, |tx| {
            for statement in [
                "ALTER TABLE replay_assets ADD COLUMN hub_media_uploaded_at_unix_ms INTEGER;",
                "ALTER TABLE replay_assets ADD COLUMN hub_media_schema_version INTEGER NOT NULL DEFAULT 0;",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 15 {
        run_schema_migration(conn, 15, |tx| {
            for statement in
                ["ALTER TABLE replay_assets ADD COLUMN hub_media_uploaded_quality TEXT;"]
            {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 16 {
        run_schema_migration(conn, 16, |tx| {
            for statement in
                ["ALTER TABLE session_run_summaries ADD COLUMN started_at_bridge_ts_ms INTEGER;"]
            {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 17 {
        run_schema_migration(conn, 17, |tx| {
            for statement in [
                "ALTER TABLE replay_assets ADD COLUMN hub_mouse_path_uploaded_at_unix_ms INTEGER;",
                "ALTER TABLE replay_assets ADD COLUMN hub_mouse_path_schema_version INTEGER NOT NULL DEFAULT 0;",
            ] {
                if let Err(error) = tx.execute_batch(statement) {
                    if !migration_already_applied(&error) {
                        return Err(error.into());
                    }
                }
            }
            Ok(())
        })?;
    }

    if user_version < 18 {
        run_schema_migration(conn, 18, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS coaching_state_snapshots (
                snapshot_kind TEXT PRIMARY KEY,
                updated_at_unix_ms INTEGER NOT NULL,
                sample_count INTEGER NOT NULL,
                settled_sample_count INTEGER NOT NULL,
                coverage_start_unix_ms INTEGER,
                coverage_end_unix_ms INTEGER,
                summary_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_coaching_state_updated_at
                ON coaching_state_snapshots(updated_at_unix_ms DESC);

            CREATE TABLE IF NOT EXISTS coaching_recommendation_evaluations (
                evaluation_id TEXT PRIMARY KEY,
                snapshot_kind TEXT NOT NULL,
                recommendation_id TEXT NOT NULL,
                recommendation_title TEXT NOT NULL,
                signal_key TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at_unix_ms INTEGER NOT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                anchor_sample_count INTEGER NOT NULL,
                latest_sample_count INTEGER NOT NULL,
                anchor_metric_value REAL,
                latest_metric_value REAL,
                outcome_delta REAL,
                context_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_coaching_eval_snapshot_status
                ON coaching_recommendation_evaluations(snapshot_kind, status, updated_at_unix_ms DESC);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 19 {
        run_schema_migration(conn, 19, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS coaching_user_feedback (
                snapshot_kind TEXT NOT NULL,
                recommendation_id TEXT NOT NULL,
                signal_key TEXT,
                feedback TEXT NOT NULL,
                notes TEXT,
                created_at_unix_ms INTEGER NOT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                context_json TEXT NOT NULL,
                PRIMARY KEY(snapshot_kind, recommendation_id)
            );
            CREATE INDEX IF NOT EXISTS idx_coaching_feedback_updated_at
                ON coaching_user_feedback(updated_at_unix_ms DESC);
            CREATE INDEX IF NOT EXISTS idx_coaching_feedback_signal
                ON coaching_user_feedback(signal_key, updated_at_unix_ms DESC);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 20 {
        run_schema_migration(conn, 20, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_target_response_summaries (
                session_id TEXT PRIMARY KEY,
                episode_count INTEGER NOT NULL DEFAULT 0,
                path_change_count INTEGER NOT NULL DEFAULT 0,
                target_switch_count INTEGER NOT NULL DEFAULT 0,
                response_coverage_pct REAL,
                avg_reaction_time_ms REAL,
                p90_reaction_time_ms REAL,
                avg_pre_slowdown_reaction_ms REAL,
                avg_recovery_time_ms REAL,
                p90_recovery_time_ms REAL,
                avg_path_change_reaction_ms REAL,
                avg_target_switch_reaction_ms REAL,
                avg_trigger_magnitude_deg REAL,
                avg_peak_yaw_error_deg REAL,
                stable_response_ratio REAL,
                updated_at_unix_ms INTEGER NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_target_response_summary_updated
                ON session_target_response_summaries(updated_at_unix_ms DESC);

            CREATE TABLE IF NOT EXISTS session_target_response_episodes (
                session_id TEXT NOT NULL,
                episode_idx INTEGER NOT NULL,
                episode_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                start_ms INTEGER NOT NULL,
                end_ms INTEGER NOT NULL,
                target_id TEXT NOT NULL,
                target_label TEXT NOT NULL,
                trigger_magnitude_deg REAL,
                peak_yaw_error_deg REAL,
                reaction_time_ms REAL,
                pre_slowdown_reaction_ms REAL,
                recovery_time_ms REAL,
                stable_response INTEGER NOT NULL DEFAULT 0 CHECK (stable_response IN (0, 1)),
                PRIMARY KEY(session_id, episode_idx),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_target_response_episode_time
                ON session_target_response_episodes(session_id, start_ms, end_ms);
            CREATE INDEX IF NOT EXISTS idx_target_response_episode_kind
                ON session_target_response_episodes(session_id, kind, start_ms);
            ",
            )?;
            Ok(())
        })?;
    }

    if user_version < 21 {
        run_schema_migration(conn, 21, |tx| {
            tx.execute_batch(
                "
            CREATE TABLE IF NOT EXISTS session_replay_tick_streams (
                session_id TEXT PRIMARY KEY,
                tick_stream_json TEXT NOT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_replay_tick_streams_updated
                ON session_replay_tick_streams(updated_at_unix_ms DESC);
            ",
            )?;
            Ok(())
        })?;
    }

    Ok(())
}

fn query_replay_positions(
    conn: &Connection,
    session_id: &str,
) -> Result<Vec<crate::mouse_hook::RawPositionPoint>> {
    let mut stmt = conn.prepare(
        "
        SELECT x, y, timestamp_ms, is_click
        FROM session_replay_positions
        WHERE session_id = ?1
        ORDER BY seq_idx ASC
        ",
    )?;
    let rows = stmt.query_map(params![session_id], |row| {
        Ok(crate::mouse_hook::RawPositionPoint {
            x: row.get(0)?,
            y: row.get(1)?,
            timestamp_ms: row.get::<_, i64>(2)? as u64,
            is_click: row.get::<_, i64>(3)? != 0,
        })
    })?;

    let mut positions = Vec::new();
    for row in rows {
        positions.push(row?);
    }
    Ok(positions)
}

fn query_replay_metrics(
    conn: &Connection,
    session_id: &str,
) -> Result<Vec<crate::mouse_hook::MetricPoint>> {
    let mut stmt = conn.prepare(
        "
        SELECT
            timestamp_ms,
            smoothness,
            jitter,
            overshoot_rate,
            velocity_std,
            avg_speed,
            path_efficiency,
            click_timing_cv,
            correction_ratio,
            directional_bias,
            avg_hold_ms
        FROM session_replay_metrics
        WHERE session_id = ?1
        ORDER BY seq_idx ASC
        ",
    )?;
    let rows = stmt.query_map(params![session_id], |row| {
        Ok(crate::mouse_hook::MetricPoint {
            timestamp_ms: row.get::<_, i64>(0)? as u64,
            metrics: crate::mouse_hook::MouseMetrics {
                smoothness: row.get(1)?,
                jitter: row.get(2)?,
                overshoot_rate: row.get(3)?,
                velocity_std: row.get(4)?,
                avg_speed: row.get(5)?,
                path_efficiency: row.get(6)?,
                click_timing_cv: row.get(7)?,
                correction_ratio: row.get(8)?,
                directional_bias: row.get(9)?,
                avg_hold_ms: row.get(10)?,
            },
        })
    })?;

    let mut metrics = Vec::new();
    for row in rows {
        metrics.push(row?);
    }
    Ok(metrics)
}

fn query_replay_frames(
    conn: &Connection,
    session_id: &str,
) -> Result<Vec<crate::screen_recorder::ScreenFrame>> {
    let mut stmt = conn.prepare(
        "
        SELECT timestamp_ms, jpeg_b64
        FROM session_replay_frames
        WHERE session_id = ?1
        ORDER BY seq_idx ASC
        ",
    )?;
    let rows = stmt.query_map(params![session_id], |row| {
        Ok(crate::screen_recorder::ScreenFrame {
            timestamp_ms: row.get::<_, i64>(0)? as u64,
            jpeg_b64: row.get(1)?,
        })
    })?;

    let mut frames = Vec::new();
    for row in rows {
        frames.push(row?);
    }
    Ok(frames)
}

fn query_replay_tick_stream(
    conn: &Connection,
    session_id: &str,
) -> Result<Option<crate::bridge::BridgeTickStreamV1>> {
    let tick_stream_json = conn
        .query_row(
            "
            SELECT tick_stream_json
            FROM session_replay_tick_streams
            WHERE session_id = ?1
            ",
            params![session_id],
            |row| row.get::<_, String>(0),
        )
        .optional()
        .context("could not load replay tick stream")?;

    tick_stream_json
        .map(|json| {
            serde_json::from_str::<crate::bridge::BridgeTickStreamV1>(&json)
                .context("could not parse replay tick stream")
        })
        .transpose()
}

fn query_shot_telemetry(
    conn: &Connection,
    session_id: &str,
) -> Result<Vec<crate::bridge::BridgeShotTelemetryEvent>> {
    let mut stmt = conn.prepare(
        "
        SELECT
            e.shot_seq_idx AS shot_seq_idx,
            e.event_kind AS event_kind,
            e.ts_ms AS ts_ms,
            e.count AS count,
            e.total AS total,
            e.run_id AS run_id,
            e.sample_seq AS sample_seq,
            e.sample_count AS sample_count,
            e.source AS source,
            e.method AS method,
            e.origin_flag AS origin_flag,
            e.player_entity_id AS player_entity_id,
            e.player_profile AS player_profile,
            e.player_is_player AS player_is_player,
            e.player_is_bot AS player_is_bot,
            e.player_x AS player_x,
            e.player_y AS player_y,
            e.player_z AS player_z,
            e.player_pitch AS player_pitch,
            e.player_yaw AS player_yaw,
            e.player_roll AS player_roll,
            e.player_vx AS player_vx,
            e.player_vy AS player_vy,
            e.player_vz AS player_vz,
            t.target_idx AS target_idx,
            t.entity_id AS target_entity_id,
            t.profile AS target_profile,
            t.is_player AS target_is_player,
            t.is_bot AS target_is_bot,
            t.x AS target_x,
            t.y AS target_y,
            t.z AS target_z,
            t.pitch AS target_pitch,
            t.yaw AS target_yaw,
            t.roll AS target_roll,
            t.vx AS target_vx,
            t.vy AS target_vy,
            t.vz AS target_vz,
            t.distance_2d AS target_distance_2d,
            t.distance_3d AS target_distance_3d,
            t.yaw_error_deg AS target_yaw_error_deg,
            t.pitch_error_deg AS target_pitch_error_deg,
            t.is_nearest AS target_is_nearest
        FROM session_shot_events e
        LEFT JOIN session_shot_targets t
            ON t.session_id = e.session_id
           AND t.shot_seq_idx = e.shot_seq_idx
        WHERE e.session_id = ?1
        ORDER BY e.shot_seq_idx ASC, t.target_idx ASC
        ",
    )?;

    let mut rows = stmt.query(params![session_id])?;
    let mut events = Vec::new();
    let mut current_seq_idx: Option<i64> = None;
    let mut current_event: Option<crate::bridge::BridgeShotTelemetryEvent> = None;

    while let Some(row) = rows.next()? {
        let shot_seq_idx = row.get::<_, i64>("shot_seq_idx")?;
        if current_seq_idx != Some(shot_seq_idx) {
            if let Some(event) = current_event.take() {
                events.push(event);
            }

            let player_entity_id = row.get::<_, Option<String>>("player_entity_id")?;
            let player = if let Some(entity_id) = player_entity_id {
                Some(crate::bridge::BridgeShotTelemetryEntity {
                    entity_id,
                    profile: row
                        .get::<_, Option<String>>("player_profile")?
                        .unwrap_or_default(),
                    is_player: row
                        .get::<_, Option<i64>>("player_is_player")?
                        .unwrap_or_default()
                        != 0,
                    is_bot: row
                        .get::<_, Option<i64>>("player_is_bot")?
                        .unwrap_or_default()
                        != 0,
                    x: row.get::<_, Option<f64>>("player_x")?.unwrap_or_default(),
                    y: row.get::<_, Option<f64>>("player_y")?.unwrap_or_default(),
                    z: row.get::<_, Option<f64>>("player_z")?.unwrap_or_default(),
                    pitch: row
                        .get::<_, Option<f64>>("player_pitch")?
                        .unwrap_or_default(),
                    yaw: row.get::<_, Option<f64>>("player_yaw")?.unwrap_or_default(),
                    roll: row
                        .get::<_, Option<f64>>("player_roll")?
                        .unwrap_or_default(),
                    vx: row.get::<_, Option<f64>>("player_vx")?.unwrap_or_default(),
                    vy: row.get::<_, Option<f64>>("player_vy")?.unwrap_or_default(),
                    vz: row.get::<_, Option<f64>>("player_vz")?.unwrap_or_default(),
                })
            } else {
                None
            };
            current_event = Some(crate::bridge::BridgeShotTelemetryEvent {
                event: row.get::<_, String>("event_kind")?,
                ts_ms: row.get::<_, i64>("ts_ms")? as u64,
                count: row
                    .get::<_, Option<i64>>("count")?
                    .map(|value| value as u32),
                total: row
                    .get::<_, Option<i64>>("total")?
                    .map(|value| value as u32),
                run_id: row
                    .get::<_, Option<i64>>("run_id")?
                    .map(|value| value as u64),
                sample_seq: row
                    .get::<_, Option<i64>>("sample_seq")?
                    .map(|value| value as u64),
                sample_count: row
                    .get::<_, Option<i64>>("sample_count")?
                    .map(|value| value as u64),
                source: row.get("source")?,
                method: row.get("method")?,
                origin_flag: row.get("origin_flag")?,
                player,
                targets: Vec::new(),
            });
            current_seq_idx = Some(shot_seq_idx);
        }

        let target_entity_id = row.get::<_, Option<String>>("target_entity_id")?;
        if let (Some(event), Some(entity_id)) = (current_event.as_mut(), target_entity_id) {
            event
                .targets
                .push(crate::bridge::BridgeShotTelemetryTarget {
                    entity_id,
                    profile: row.get::<_, String>("target_profile")?,
                    is_player: row.get::<_, i64>("target_is_player")? != 0,
                    is_bot: row.get::<_, i64>("target_is_bot")? != 0,
                    x: row.get("target_x")?,
                    y: row.get("target_y")?,
                    z: row.get("target_z")?,
                    pitch: row.get("target_pitch")?,
                    yaw: row.get("target_yaw")?,
                    roll: row.get("target_roll")?,
                    vx: row.get("target_vx")?,
                    vy: row.get("target_vy")?,
                    vz: row.get("target_vz")?,
                    distance_2d: row.get("target_distance_2d")?,
                    distance_3d: row.get("target_distance_3d")?,
                    yaw_error_deg: row.get("target_yaw_error_deg")?,
                    pitch_error_deg: row.get("target_pitch_error_deg")?,
                    is_nearest: row.get::<_, i64>("target_is_nearest")? != 0,
                });
        }
    }

    if let Some(event) = current_event {
        events.push(event);
    }

    Ok(events)
}

fn average_optional(values: impl Iterator<Item = Option<f64>>) -> Option<f64> {
    let mut sum = 0.0;
    let mut count = 0u32;
    for value in values.flatten() {
        if value.is_finite() {
            sum += value;
            count += 1;
        }
    }
    if count == 0 {
        None
    } else {
        Some(sum / count as f64)
    }
}

fn weighted_average(sum: f64, count: u32) -> Option<f64> {
    if count == 0 {
        None
    } else {
        Some(sum / count as f64)
    }
}

fn format_entity_suffix(entity_id: &str) -> String {
    let compact: String = entity_id
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric())
        .collect();
    if compact.is_empty() {
        return entity_id.to_string();
    }
    let start = compact.len().saturating_sub(6);
    compact[start..].to_ascii_uppercase()
}

fn stable_target_label(profile: Option<&str>, entity_id: &str, duplicate_profile: bool) -> String {
    let trimmed_profile = profile.unwrap_or_default().trim();
    let base = if trimmed_profile.is_empty() {
        entity_id
    } else {
        trimmed_profile
    };
    if !duplicate_profile {
        base.to_string()
    } else {
        format!("{base} · {}", format_entity_suffix(entity_id))
    }
}

fn select_nearest_target(
    event: &crate::bridge::BridgeShotTelemetryEvent,
) -> Option<&crate::bridge::BridgeShotTelemetryTarget> {
    event
        .targets
        .iter()
        .find(|target| target.is_bot && target.is_nearest)
        .or_else(|| event.targets.iter().find(|target| target.is_bot))
        .or_else(|| event.targets.iter().find(|target| target.is_nearest))
        .or_else(|| event.targets.first())
}

fn telemetry_window_thresholds(duration_secs: Option<f64>, sample_count: usize) -> (u64, u64) {
    if let Some(duration_secs) = duration_secs.filter(|value| value.is_finite() && *value > 0.0) {
        if duration_secs <= 45.0 {
            return (1_200, 5_000);
        }
        if duration_secs <= 120.0 {
            return (1_800, 10_000);
        }
        if duration_secs <= 300.0 {
            return (2_500, 15_000);
        }
        return (4_000, 30_000);
    }

    if sample_count <= 24 {
        (1_200, 5_000)
    } else if sample_count <= 72 {
        (1_800, 10_000)
    } else {
        (2_500, 15_000)
    }
}

fn phase_for_offset(offset_ms: u64, duration_secs: Option<f64>) -> Option<String> {
    let total_ms = duration_secs
        .filter(|value| value.is_finite() && *value > 0.0)
        .map(|value| (value * 1000.0).round() as u64)?;
    if total_ms == 0 {
        return None;
    }
    if offset_ms < total_ms / 3 {
        Some("opening".to_string())
    } else if offset_ms < (total_ms * 2) / 3 {
        Some("mid".to_string())
    } else {
        Some("closing".to_string())
    }
}

fn metric_state(
    value: Option<f64>,
    average: Option<f64>,
    relative_threshold: f64,
    absolute_threshold: f64,
    high_label: &'static str,
    low_label: &'static str,
) -> &'static str {
    let Some(value) = value.filter(|value| value.is_finite()) else {
        return "steady";
    };
    let Some(average) = average.filter(|value| value.is_finite() && *value > 0.0) else {
        return "steady";
    };

    let threshold = (average * relative_threshold).max(absolute_threshold);
    if value >= average + threshold {
        high_label
    } else if value <= average - threshold {
        low_label
    } else {
        "steady"
    }
}

fn dominant_state(counts: &HashMap<&'static str, u32>) -> &'static str {
    counts
        .iter()
        .max_by_key(|(_, count)| *count)
        .map(|(state, _)| *state)
        .unwrap_or("steady")
}

#[derive(Clone)]
struct AnnotatedShotEvent {
    offset_ms: u64,
    weight: u32,
    event_kind: String,
    phase: Option<String>,
    nearest_label: Option<String>,
    nearest_profile: Option<String>,
    nearest_entity_id: Option<String>,
    bot_count: u32,
    nearest_distance: Option<f64>,
    nearest_yaw_error_deg: Option<f64>,
    nearest_pitch_error_deg: Option<f64>,
    score_per_minute: Option<f64>,
    kills_per_second: Option<f64>,
    timeline_accuracy_pct: Option<f64>,
    damage_efficiency: Option<f64>,
    pace_state: &'static str,
    accuracy_state: &'static str,
}

#[derive(Default)]
struct ContextWindowAccumulator {
    start_ms: u64,
    end_ms: u64,
    phase: Option<String>,
    shot_event_count: u32,
    fired_count: u32,
    hit_count: u32,
    weighted_event_count: u32,
    weighted_bot_count_sum: f64,
    nearest_counts: HashMap<String, u32>,
    label_meta: HashMap<String, (String, String)>,
    pace_counts: HashMap<&'static str, u32>,
    accuracy_counts: HashMap<&'static str, u32>,
    weighted_distance_sum: f64,
    weighted_distance_count: u32,
    weighted_yaw_sum: f64,
    weighted_yaw_count: u32,
    weighted_pitch_sum: f64,
    weighted_pitch_count: u32,
    weighted_spm_sum: f64,
    weighted_spm_count: u32,
    weighted_kps_sum: f64,
    weighted_kps_count: u32,
    weighted_timeline_accuracy_sum: f64,
    weighted_timeline_accuracy_count: u32,
    weighted_damage_efficiency_sum: f64,
    weighted_damage_efficiency_count: u32,
}

fn finalize_context_window(
    window_idx: usize,
    accumulator: ContextWindowAccumulator,
) -> SessionReplayContextWindow {
    let dominant_pace = dominant_state(&accumulator.pace_counts);
    let dominant_accuracy = dominant_state(&accumulator.accuracy_counts);

    let (primary_target_label, primary_target_count) = accumulator
        .nearest_counts
        .iter()
        .max_by_key(|(_, count)| *count)
        .map(|(label, count)| (Some(label.clone()), *count))
        .unwrap_or((None, 0));
    let (primary_target_profile, primary_target_entity_id) = primary_target_label
        .as_ref()
        .and_then(|label| accumulator.label_meta.get(label))
        .map(|(profile, entity_id)| (Some(profile.clone()), Some(entity_id.clone())))
        .unwrap_or((None, None));
    let primary_target_share = if primary_target_count > 0 && accumulator.weighted_event_count > 0 {
        Some(primary_target_count as f64 / accumulator.weighted_event_count as f64)
    } else {
        None
    };

    let context_kind = if primary_target_share.unwrap_or(0.0) < 0.55 {
        "mixed_cluster"
    } else if dominant_pace != "steady" || dominant_accuracy != "steady" {
        "metric_shift"
    } else {
        "target_focus"
    };

    let mut label_parts = Vec::new();
    if let Some(phase) = accumulator.phase.as_deref() {
        label_parts.push(match phase {
            "opening" => "Opening".to_string(),
            "mid" => "Mid".to_string(),
            "closing" => "Closing".to_string(),
            _ => phase.to_string(),
        });
    }
    if primary_target_share.unwrap_or(0.0) >= 0.35 {
        if let Some(label) = primary_target_label.as_ref() {
            label_parts.push(label.clone());
        }
    } else if !accumulator.nearest_counts.is_empty() {
        label_parts.push("Mixed cluster".to_string());
    }
    if dominant_pace != "steady" {
        label_parts.push(format!("pace {dominant_pace}"));
    }
    if dominant_accuracy != "steady" {
        label_parts.push(format!("accuracy {dominant_accuracy}"));
    }
    let label = if label_parts.is_empty() {
        "Engagement window".to_string()
    } else {
        label_parts.join(" · ")
    };

    SessionReplayContextWindow {
        window_idx: window_idx as u32,
        context_kind: context_kind.to_string(),
        label,
        phase: accumulator.phase,
        start_ms: accumulator.start_ms,
        end_ms: accumulator.end_ms,
        shot_event_count: accumulator.shot_event_count,
        fired_count: accumulator.fired_count,
        hit_count: accumulator.hit_count,
        accuracy_pct: normalize_accuracy_pct(
            None,
            Some(accumulator.hit_count as f64),
            Some(accumulator.fired_count as f64),
        ),
        avg_bot_count: weighted_average(
            accumulator.weighted_bot_count_sum,
            accumulator.weighted_event_count,
        ),
        primary_target_label,
        primary_target_profile,
        primary_target_entity_id,
        primary_target_share,
        avg_nearest_distance: weighted_average(
            accumulator.weighted_distance_sum,
            accumulator.weighted_distance_count,
        ),
        avg_nearest_yaw_error_deg: weighted_average(
            accumulator.weighted_yaw_sum,
            accumulator.weighted_yaw_count,
        ),
        avg_nearest_pitch_error_deg: weighted_average(
            accumulator.weighted_pitch_sum,
            accumulator.weighted_pitch_count,
        ),
        avg_score_per_minute: weighted_average(
            accumulator.weighted_spm_sum,
            accumulator.weighted_spm_count,
        ),
        avg_kills_per_second: weighted_average(
            accumulator.weighted_kps_sum,
            accumulator.weighted_kps_count,
        ),
        avg_timeline_accuracy_pct: normalize_accuracy_pct(
            weighted_average(
                accumulator.weighted_timeline_accuracy_sum,
                accumulator.weighted_timeline_accuracy_count,
            ),
            None,
            None,
        ),
        avg_damage_efficiency: weighted_average(
            accumulator.weighted_damage_efficiency_sum,
            accumulator.weighted_damage_efficiency_count,
        ),
    }
}

fn build_replay_context_windows(
    snapshot: &crate::bridge::BridgeRunSnapshot,
) -> Vec<SessionReplayContextWindow> {
    if snapshot.shot_telemetry.is_empty() {
        return Vec::new();
    }

    let mut nearest_profile_entities: HashMap<String, HashSet<String>> = HashMap::new();
    for event in &snapshot.shot_telemetry {
        if let Some(nearest) = select_nearest_target(event) {
            let profile_key = {
                let trimmed = nearest.profile.trim();
                if trimmed.is_empty() {
                    nearest.entity_id.clone()
                } else {
                    trimmed.to_string()
                }
            };
            nearest_profile_entities
                .entry(profile_key)
                .or_default()
                .insert(nearest.entity_id.clone());
        }
    }

    let normalized_timeline = normalize_run_timeline(&snapshot.timeline);
    let timeline_by_sec: HashMap<u32, &crate::bridge::BridgeRunTimelinePoint> = normalized_timeline
        .iter()
        .map(|point| (point.t_sec, point))
        .collect();
    let avg_spm = average_optional(
        normalized_timeline
            .iter()
            .map(|point| point.score_per_minute),
    );
    let avg_timeline_accuracy =
        average_optional(normalized_timeline.iter().map(|point| point.accuracy_pct));

    let first_ts_ms = snapshot
        .shot_telemetry
        .first()
        .map(|event| event.ts_ms)
        .unwrap_or(0);
    let mut annotated = Vec::with_capacity(snapshot.shot_telemetry.len());
    for event in &snapshot.shot_telemetry {
        let offset_ms = event.ts_ms.saturating_sub(first_ts_ms);
        let sec = ((offset_ms / 1000) as u32).saturating_add(1);
        let timeline_point = timeline_by_sec
            .get(&sec)
            .or_else(|| timeline_by_sec.get(&sec.saturating_sub(1)));

        let bot_count = event.targets.iter().filter(|target| target.is_bot).count() as u32;
        let nearest = select_nearest_target(event);
        let nearest_label = nearest.map(|target| {
            let profile_key = {
                let trimmed = target.profile.trim();
                if trimmed.is_empty() {
                    target.entity_id.clone()
                } else {
                    trimmed.to_string()
                }
            };
            let duplicate_profile = nearest_profile_entities
                .get(&profile_key)
                .map(|entities| entities.len() > 1)
                .unwrap_or(false);
            stable_target_label(
                Some(target.profile.as_str()),
                &target.entity_id,
                duplicate_profile,
            )
        });

        annotated.push(AnnotatedShotEvent {
            offset_ms,
            weight: std::cmp::max(1, event.count.unwrap_or(1)),
            event_kind: event.event.clone(),
            phase: phase_for_offset(offset_ms, snapshot.duration_secs),
            nearest_label,
            nearest_profile: nearest.map(|target| target.profile.clone()),
            nearest_entity_id: nearest.map(|target| target.entity_id.clone()),
            bot_count,
            nearest_distance: nearest.and_then(|target| target.distance_3d.or(target.distance_2d)),
            nearest_yaw_error_deg: nearest.and_then(|target| target.yaw_error_deg.map(f64::abs)),
            nearest_pitch_error_deg: nearest
                .and_then(|target| target.pitch_error_deg.map(f64::abs)),
            score_per_minute: timeline_point.and_then(|point| point.score_per_minute),
            kills_per_second: timeline_point.and_then(|point| point.kills_per_second),
            timeline_accuracy_pct: timeline_point.and_then(|point| point.accuracy_pct),
            damage_efficiency: timeline_point.and_then(|point| point.damage_efficiency),
            pace_state: metric_state(
                timeline_point.and_then(|point| point.score_per_minute),
                avg_spm,
                0.12,
                60.0,
                "surge",
                "dip",
            ),
            accuracy_state: metric_state(
                timeline_point.and_then(|point| point.accuracy_pct),
                avg_timeline_accuracy,
                0.08,
                3.0,
                "hot",
                "cold",
            ),
        });
    }

    let (max_gap_ms, max_span_ms) =
        telemetry_window_thresholds(snapshot.duration_secs, annotated.len());
    let mut windows = Vec::new();
    let mut current = ContextWindowAccumulator::default();
    let mut last_event: Option<&AnnotatedShotEvent> = None;

    for event in &annotated {
        let should_split = if let Some(previous) = last_event {
            let gap_ms = event.offset_ms.saturating_sub(previous.offset_ms);
            let span_ms = event.offset_ms.saturating_sub(current.start_ms);
            let phase_changed = event.phase != previous.phase && current.shot_event_count > 0;
            let target_changed =
                event.nearest_label != previous.nearest_label && current.shot_event_count >= 2;
            let pace_changed = event.pace_state != previous.pace_state
                && current.shot_event_count >= 2
                && (event.pace_state != "steady" || previous.pace_state != "steady");
            let accuracy_changed = event.accuracy_state != previous.accuracy_state
                && current.shot_event_count >= 2
                && (event.accuracy_state != "steady" || previous.accuracy_state != "steady");

            gap_ms > max_gap_ms
                || span_ms > max_span_ms
                || phase_changed
                || target_changed
                || pace_changed
                || accuracy_changed
        } else {
            false
        };

        if should_split && current.shot_event_count > 0 {
            windows.push(finalize_context_window(windows.len(), current));
            current = ContextWindowAccumulator::default();
        }

        if current.shot_event_count == 0 {
            current.start_ms = event.offset_ms;
            current.phase = event.phase.clone();
        }
        current.end_ms = event.offset_ms;
        current.shot_event_count += 1;
        current.weighted_event_count += event.weight;
        current.weighted_bot_count_sum += event.bot_count as f64 * event.weight as f64;

        if event.event_kind == "shot_fired" {
            current.fired_count += event.weight;
        }
        if event.event_kind == "shot_hit" {
            current.hit_count += event.weight;
        }

        if let Some(label) = event.nearest_label.as_ref() {
            *current.nearest_counts.entry(label.clone()).or_insert(0) += event.weight;
            current.label_meta.entry(label.clone()).or_insert_with(|| {
                (
                    event.nearest_profile.clone().unwrap_or_default(),
                    event.nearest_entity_id.clone().unwrap_or_default(),
                )
            });
        }
        *current.pace_counts.entry(event.pace_state).or_insert(0) += event.weight;
        *current
            .accuracy_counts
            .entry(event.accuracy_state)
            .or_insert(0) += event.weight;

        if let Some(distance) = event.nearest_distance.filter(|value| value.is_finite()) {
            current.weighted_distance_sum += distance * event.weight as f64;
            current.weighted_distance_count += event.weight;
        }
        if let Some(yaw_error) = event
            .nearest_yaw_error_deg
            .filter(|value| value.is_finite())
        {
            current.weighted_yaw_sum += yaw_error * event.weight as f64;
            current.weighted_yaw_count += event.weight;
        }
        if let Some(pitch_error) = event
            .nearest_pitch_error_deg
            .filter(|value| value.is_finite())
        {
            current.weighted_pitch_sum += pitch_error * event.weight as f64;
            current.weighted_pitch_count += event.weight;
        }
        if let Some(score_per_minute) = event.score_per_minute.filter(|value| value.is_finite()) {
            current.weighted_spm_sum += score_per_minute * event.weight as f64;
            current.weighted_spm_count += event.weight;
        }
        if let Some(kills_per_second) = event.kills_per_second.filter(|value| value.is_finite()) {
            current.weighted_kps_sum += kills_per_second * event.weight as f64;
            current.weighted_kps_count += event.weight;
        }
        if let Some(accuracy_pct) = event
            .timeline_accuracy_pct
            .filter(|value| value.is_finite())
        {
            current.weighted_timeline_accuracy_sum += accuracy_pct * event.weight as f64;
            current.weighted_timeline_accuracy_count += event.weight;
        }
        if let Some(damage_efficiency) = event.damage_efficiency.filter(|value| value.is_finite()) {
            current.weighted_damage_efficiency_sum += damage_efficiency * event.weight as f64;
            current.weighted_damage_efficiency_count += event.weight;
        }

        last_event = Some(event);
    }

    if current.shot_event_count > 0 {
        windows.push(finalize_context_window(windows.len(), current));
    }

    windows
}

fn query_replay_context_windows(
    conn: &Connection,
    session_id: &str,
) -> Result<Vec<SessionReplayContextWindow>> {
    let mut stmt = conn.prepare(
        "
        SELECT
            window_idx,
            context_kind,
            label,
            phase,
            start_ms,
            end_ms,
            shot_event_count,
            fired_count,
            hit_count,
            accuracy_pct,
            avg_bot_count,
            primary_target_label,
            primary_target_profile,
            primary_target_entity_id,
            primary_target_share,
            avg_nearest_distance,
            avg_nearest_yaw_error_deg,
            avg_nearest_pitch_error_deg,
            avg_score_per_minute,
            avg_kills_per_second,
            avg_timeline_accuracy_pct,
            avg_damage_efficiency
        FROM session_replay_context_windows
        WHERE session_id = ?1
        ORDER BY start_ms DESC, window_idx DESC
        ",
    )?;
    let rows = stmt.query_map(params![session_id], |row| {
        Ok(SessionReplayContextWindow {
            window_idx: row.get::<_, i64>(0)? as u32,
            context_kind: row.get(1)?,
            label: row.get(2)?,
            phase: row.get(3)?,
            start_ms: row.get::<_, i64>(4)? as u64,
            end_ms: row.get::<_, i64>(5)? as u64,
            shot_event_count: row.get::<_, i64>(6)? as u32,
            fired_count: row.get::<_, i64>(7)? as u32,
            hit_count: row.get::<_, i64>(8)? as u32,
            accuracy_pct: normalize_accuracy_pct(
                row.get(9)?,
                Some(row.get::<_, i64>(8)? as f64),
                Some(row.get::<_, i64>(7)? as f64),
            ),
            avg_bot_count: row.get(10)?,
            primary_target_label: row.get(11)?,
            primary_target_profile: row.get(12)?,
            primary_target_entity_id: row.get(13)?,
            primary_target_share: row.get(14)?,
            avg_nearest_distance: row.get(15)?,
            avg_nearest_yaw_error_deg: row.get(16)?,
            avg_nearest_pitch_error_deg: row.get(17)?,
            avg_score_per_minute: row.get(18)?,
            avg_kills_per_second: row.get(19)?,
            avg_timeline_accuracy_pct: normalize_accuracy_pct(row.get(20)?, None, None),
            avg_damage_efficiency: row.get(21)?,
        })
    })?;

    let mut windows = Vec::new();
    for row in rows {
        windows.push(row?);
    }
    Ok(windows)
}

pub fn upsert_run_capture(
    app: &AppHandle,
    session_id: &str,
    snapshot: &crate::bridge::BridgeRunSnapshot,
) -> Result<()> {
    let replay_context_windows = build_replay_context_windows(snapshot);
    let normalized_summary_accuracy_pct = normalize_accuracy_pct(
        snapshot.accuracy_pct,
        snapshot.shots_hit,
        snapshot.shots_fired,
    );
    let normalized_timeline = normalize_run_timeline(&snapshot.timeline);
    let mut conn = connect(app)?;
    let tx = conn.transaction()?;

    tx.execute(
        "
        INSERT INTO session_run_summaries (
            session_id,
            duration_secs,
            score_total,
            score_total_derived,
            score_per_minute,
            shots_fired,
            shots_hit,
            kills,
            kills_per_second,
            damage_done,
            damage_possible,
            damage_efficiency,
            accuracy_pct,
            peak_score_per_minute,
            peak_kills_per_second,
            paired_shot_hits,
            avg_fire_to_hit_ms,
            p90_fire_to_hit_ms,
            avg_shots_to_hit,
            corrective_shot_ratio,
            started_at_bridge_ts_ms,
            started_at_unix_ms,
            ended_at_unix_ms,
            shot_fired_events,
            shot_hit_events,
            kill_events,
            challenge_queued_events,
            challenge_start_events,
            challenge_end_events,
            challenge_complete_events,
            challenge_canceled_events
        )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?22, ?23, ?24, ?25, ?26, ?27, ?28, ?29, ?30, ?31)
        ON CONFLICT(session_id) DO UPDATE SET
            duration_secs = excluded.duration_secs,
            score_total = excluded.score_total,
            score_total_derived = excluded.score_total_derived,
            score_per_minute = excluded.score_per_minute,
            shots_fired = excluded.shots_fired,
            shots_hit = excluded.shots_hit,
            kills = excluded.kills,
            kills_per_second = excluded.kills_per_second,
            damage_done = excluded.damage_done,
            damage_possible = excluded.damage_possible,
            damage_efficiency = excluded.damage_efficiency,
            accuracy_pct = excluded.accuracy_pct,
            peak_score_per_minute = excluded.peak_score_per_minute,
            peak_kills_per_second = excluded.peak_kills_per_second,
            paired_shot_hits = excluded.paired_shot_hits,
            avg_fire_to_hit_ms = excluded.avg_fire_to_hit_ms,
            p90_fire_to_hit_ms = excluded.p90_fire_to_hit_ms,
            avg_shots_to_hit = excluded.avg_shots_to_hit,
            corrective_shot_ratio = excluded.corrective_shot_ratio,
            started_at_bridge_ts_ms = excluded.started_at_bridge_ts_ms,
            started_at_unix_ms = excluded.started_at_unix_ms,
            ended_at_unix_ms = excluded.ended_at_unix_ms,
            shot_fired_events = excluded.shot_fired_events,
            shot_hit_events = excluded.shot_hit_events,
            kill_events = excluded.kill_events,
            challenge_queued_events = excluded.challenge_queued_events,
            challenge_start_events = excluded.challenge_start_events,
            challenge_end_events = excluded.challenge_end_events,
            challenge_complete_events = excluded.challenge_complete_events,
            challenge_canceled_events = excluded.challenge_canceled_events
        ",
        params![
            session_id,
            snapshot.duration_secs,
            snapshot.score_total,
            snapshot.score_total_derived,
            snapshot.score_per_minute,
            snapshot.shots_fired,
            snapshot.shots_hit,
            snapshot.kills,
            snapshot.kills_per_second,
            snapshot.damage_done,
            snapshot.damage_possible,
            snapshot.damage_efficiency,
            normalized_summary_accuracy_pct,
            snapshot.peak_score_per_minute,
            snapshot.peak_kills_per_second,
            snapshot.paired_shot_hits as i64,
            snapshot.avg_fire_to_hit_ms,
            snapshot.p90_fire_to_hit_ms,
            snapshot.avg_shots_to_hit,
            snapshot.corrective_shot_ratio,
            snapshot.started_at_bridge_ts_ms.map(|value| value as i64),
            snapshot.started_at_unix_ms.map(|value| value as i64),
            snapshot.ended_at_unix_ms.map(|value| value as i64),
            snapshot.event_counts.shot_fired_events as i64,
            snapshot.event_counts.shot_hit_events as i64,
            snapshot.event_counts.kill_events as i64,
            snapshot.event_counts.challenge_queued_events as i64,
            snapshot.event_counts.challenge_start_events as i64,
            snapshot.event_counts.challenge_end_events as i64,
            snapshot.event_counts.challenge_complete_events as i64,
            snapshot.event_counts.challenge_canceled_events as i64,
        ],
    )?;

    tx.execute(
        "DELETE FROM session_run_timelines WHERE session_id = ?1",
        params![session_id],
    )?;
    {
        let mut insert = tx.prepare(
            "
            INSERT INTO session_run_timelines (
                session_id,
                t_sec,
                score_per_minute,
                kills_per_second,
                accuracy_pct,
                damage_efficiency,
                score_total,
                score_total_derived,
                kills,
                shots_fired,
                shots_hit
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
            ",
        )?;

        for point in &normalized_timeline {
            insert.execute(params![
                session_id,
                point.t_sec as i64,
                point.score_per_minute,
                point.kills_per_second,
                point.accuracy_pct,
                point.damage_efficiency,
                point.score_total,
                point.score_total_derived,
                point.kills,
                point.shots_fired,
                point.shots_hit,
            ])?;
        }
    }

    tx.commit()?;

    let mut conn = connect(app)?;
    let tx = conn.transaction()?;
    tx.execute(
        "DELETE FROM session_shot_targets WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_shot_events WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_replay_context_windows WHERE session_id = ?1",
        params![session_id],
    )?;

    {
        let mut insert_event = tx.prepare(
            "
            INSERT INTO session_shot_events (
                session_id,
                shot_seq_idx,
                event_kind,
                ts_ms,
                count,
                total,
                run_id,
                sample_seq,
                sample_count,
                source,
                method,
                origin_flag,
                player_entity_id,
                player_profile,
                player_is_player,
                player_is_bot,
                player_x,
                player_y,
                player_z,
                player_pitch,
                player_yaw,
                player_roll,
                player_vx,
                player_vy,
                player_vz
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?22, ?23, ?24, ?25)
            ",
        )?;
        let mut insert_target = tx.prepare(
            "
            INSERT INTO session_shot_targets (
                session_id,
                shot_seq_idx,
                target_idx,
                entity_id,
                profile,
                is_player,
                is_bot,
                x,
                y,
                z,
                pitch,
                yaw,
                roll,
                vx,
                vy,
                vz,
                distance_2d,
                distance_3d,
                yaw_error_deg,
                pitch_error_deg,
                is_nearest
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21)
            ",
        )?;

        for (event_index, event) in snapshot.shot_telemetry.iter().enumerate() {
            let player = event.player.as_ref();
            insert_event.execute(params![
                session_id,
                event_index as i64,
                &event.event,
                event.ts_ms as i64,
                event.count.map(|value| value as i64),
                event.total.map(|value| value as i64),
                event.run_id.map(|value| value as i64),
                event.sample_seq.map(|value| value as i64),
                event.sample_count.map(|value| value as i64),
                event.source.as_deref(),
                event.method.as_deref(),
                event.origin_flag.as_deref(),
                player.map(|value| value.entity_id.as_str()),
                player.map(|value| value.profile.as_str()),
                player.map(|value| if value.is_player { 1i64 } else { 0i64 }),
                player.map(|value| if value.is_bot { 1i64 } else { 0i64 }),
                player.map(|value| value.x),
                player.map(|value| value.y),
                player.map(|value| value.z),
                player.map(|value| value.pitch),
                player.map(|value| value.yaw),
                player.map(|value| value.roll),
                player.map(|value| value.vx),
                player.map(|value| value.vy),
                player.map(|value| value.vz),
            ])?;

            for (target_index, target) in event.targets.iter().enumerate() {
                insert_target.execute(params![
                    session_id,
                    event_index as i64,
                    target_index as i64,
                    &target.entity_id,
                    &target.profile,
                    if target.is_player { 1i64 } else { 0i64 },
                    if target.is_bot { 1i64 } else { 0i64 },
                    target.x,
                    target.y,
                    target.z,
                    target.pitch,
                    target.yaw,
                    target.roll,
                    target.vx,
                    target.vy,
                    target.vz,
                    target.distance_2d,
                    target.distance_3d,
                    target.yaw_error_deg,
                    target.pitch_error_deg,
                    if target.is_nearest { 1i64 } else { 0i64 },
                ])?;
            }
        }

        let mut insert_window = tx.prepare(
            "
            INSERT INTO session_replay_context_windows (
                session_id,
                window_idx,
                context_kind,
                label,
                phase,
                start_ms,
                end_ms,
                shot_event_count,
                fired_count,
                hit_count,
                accuracy_pct,
                avg_bot_count,
                primary_target_label,
                primary_target_profile,
                primary_target_entity_id,
                primary_target_share,
                avg_nearest_distance,
                avg_nearest_yaw_error_deg,
                avg_nearest_pitch_error_deg,
                avg_score_per_minute,
                avg_kills_per_second,
                avg_timeline_accuracy_pct,
                avg_damage_efficiency
            )
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?22, ?23)
            ",
        )?;

        for window in &replay_context_windows {
            insert_window.execute(params![
                session_id,
                window.window_idx as i64,
                window.context_kind,
                window.label,
                window.phase,
                window.start_ms as i64,
                window.end_ms as i64,
                window.shot_event_count as i64,
                window.fired_count as i64,
                window.hit_count as i64,
                window.accuracy_pct,
                window.avg_bot_count,
                window.primary_target_label,
                window.primary_target_profile,
                window.primary_target_entity_id,
                window.primary_target_share,
                window.avg_nearest_distance,
                window.avg_nearest_yaw_error_deg,
                window.avg_nearest_pitch_error_deg,
                window.avg_score_per_minute,
                window.avg_kills_per_second,
                window.avg_timeline_accuracy_pct,
                window.avg_damage_efficiency,
            ])?;
        }
    }

    tx.commit()?;
    Ok(())
}

fn normalize_run_timeline(
    timeline: &[crate::bridge::BridgeRunTimelinePoint],
) -> Vec<crate::bridge::BridgeRunTimelinePoint> {
    let mut sorted = timeline.to_vec();
    sorted.sort_by_key(|point| point.t_sec);

    let mut merged: Vec<crate::bridge::BridgeRunTimelinePoint> = Vec::with_capacity(sorted.len());
    for mut point in sorted {
        point.accuracy_pct =
            normalize_accuracy_pct(point.accuracy_pct, point.shots_hit, point.shots_fired);
        if let Some(current) = merged.last_mut() {
            if current.t_sec == point.t_sec {
                merge_timeline_point(current, &point);
                continue;
            }
        }
        merged.push(point);
    }
    merged
}

fn merge_timeline_point(
    current: &mut crate::bridge::BridgeRunTimelinePoint,
    next: &crate::bridge::BridgeRunTimelinePoint,
) {
    current.score_per_minute = next.score_per_minute.or(current.score_per_minute);
    current.kills_per_second = next.kills_per_second.or(current.kills_per_second);
    current.accuracy_pct = next.accuracy_pct.or(current.accuracy_pct);
    current.damage_efficiency = next.damage_efficiency.or(current.damage_efficiency);
    current.score_total = next.score_total.or(current.score_total);
    current.score_total_derived = next.score_total_derived.or(current.score_total_derived);
    current.kills = next.kills.or(current.kills);
    current.shots_fired = next.shots_fired.or(current.shots_fired);
    current.shots_hit = next.shots_hit.or(current.shots_hit);
}

pub fn get_run_summary(
    app: &AppHandle,
    session_id: &str,
) -> Result<Option<crate::bridge::BridgeRunSnapshot>> {
    let conn = connect(app)?;
    conn.query_row(
        "
        SELECT
            duration_secs,
            score_total,
            score_total_derived,
            score_per_minute,
            shots_fired,
            shots_hit,
            kills,
            kills_per_second,
            damage_done,
            damage_possible,
            damage_efficiency,
            accuracy_pct,
            peak_score_per_minute,
            peak_kills_per_second,
            paired_shot_hits,
            avg_fire_to_hit_ms,
            p90_fire_to_hit_ms,
            avg_shots_to_hit,
            corrective_shot_ratio,
            started_at_bridge_ts_ms,
            started_at_unix_ms,
            ended_at_unix_ms,
            shot_fired_events,
            shot_hit_events,
            kill_events,
            challenge_queued_events,
            challenge_start_events,
            challenge_end_events,
            challenge_complete_events,
            challenge_canceled_events
        FROM session_run_summaries
        WHERE session_id = ?1
        ",
        params![session_id],
        |row| {
            let shots_fired = row.get(4)?;
            let shots_hit = row.get(5)?;
            let accuracy_pct = normalize_accuracy_pct(row.get(11)?, shots_hit, shots_fired);
            Ok(crate::bridge::BridgeRunSnapshot {
                duration_secs: row.get(0)?,
                score_total: row.get(1)?,
                score_total_derived: row.get(2)?,
                score_per_minute: row.get(3)?,
                shots_fired,
                shots_hit,
                kills: row.get(6)?,
                kills_per_second: row.get(7)?,
                damage_done: row.get(8)?,
                damage_possible: row.get(9)?,
                damage_efficiency: row.get(10)?,
                accuracy_pct,
                peak_score_per_minute: row.get(12)?,
                peak_kills_per_second: row.get(13)?,
                paired_shot_hits: row.get::<_, i64>(14)? as u32,
                avg_fire_to_hit_ms: row.get(15)?,
                p90_fire_to_hit_ms: row.get(16)?,
                avg_shots_to_hit: row.get(17)?,
                corrective_shot_ratio: row.get(18)?,
                started_at_bridge_ts_ms: row.get::<_, Option<i64>>(19)?.map(|value| value as u64),
                started_at_unix_ms: row.get::<_, Option<i64>>(20)?.map(|value| value as u64),
                ended_at_unix_ms: row.get::<_, Option<i64>>(21)?.map(|value| value as u64),
                event_counts: crate::bridge::BridgeRunEventCounts {
                    shot_fired_events: row.get::<_, i64>(22)? as u32,
                    shot_hit_events: row.get::<_, i64>(23)? as u32,
                    kill_events: row.get::<_, i64>(24)? as u32,
                    challenge_queued_events: row.get::<_, i64>(25)? as u32,
                    challenge_start_events: row.get::<_, i64>(26)? as u32,
                    challenge_end_events: row.get::<_, i64>(27)? as u32,
                    challenge_complete_events: row.get::<_, i64>(28)? as u32,
                    challenge_canceled_events: row.get::<_, i64>(29)? as u32,
                },
                timeline: vec![],
                pause_windows: vec![],
                shot_telemetry: vec![],
                tick_stream_v1: None,
            })
        },
    )
    .optional()
    .context("could not load run summary")
}

pub fn get_run_timeline(
    app: &AppHandle,
    session_id: &str,
) -> Result<Vec<crate::bridge::BridgeRunTimelinePoint>> {
    let conn = connect(app)?;
    let mut stmt = conn.prepare(
        "
        SELECT
            t_sec,
            score_per_minute,
            kills_per_second,
            accuracy_pct,
            damage_efficiency,
            score_total,
            score_total_derived,
            kills,
            shots_fired,
            shots_hit
        FROM session_run_timelines
        WHERE session_id = ?1
        ORDER BY t_sec ASC
        ",
    )?;
    let rows = stmt.query_map(params![session_id], |row| {
        let shots_fired = row.get(8)?;
        let shots_hit = row.get(9)?;
        Ok(crate::bridge::BridgeRunTimelinePoint {
            t_sec: row.get::<_, i64>(0)? as u32,
            score_per_minute: row.get(1)?,
            kills_per_second: row.get(2)?,
            accuracy_pct: normalize_accuracy_pct(row.get(3)?, shots_hit, shots_fired),
            damage_efficiency: row.get(4)?,
            score_total: row.get(5)?,
            score_total_derived: row.get(6)?,
            kills: row.get(7)?,
            shots_fired,
            shots_hit,
        })
    })?;

    let mut timeline = Vec::new();
    for row in rows {
        timeline.push(row?);
    }
    Ok(timeline)
}

pub fn get_shot_telemetry(
    app: &AppHandle,
    session_id: &str,
) -> Result<Vec<crate::bridge::BridgeShotTelemetryEvent>> {
    let conn = connect(app)?;
    query_shot_telemetry(&conn, session_id)
}

pub fn get_replay_context_windows(
    app: &AppHandle,
    session_id: &str,
) -> Result<Vec<SessionReplayContextWindow>> {
    let conn = connect(app)?;
    query_replay_context_windows(&conn, session_id)
}

pub fn get_target_response_analysis(
    app: &AppHandle,
    session_id: &str,
) -> Result<Option<crate::target_response::TargetResponseAnalysis>> {
    let conn = connect(app)?;
    query_target_response_analysis(&conn, session_id)
}

pub fn upsert_target_response_analysis(
    app: &AppHandle,
    session_id: &str,
    analysis: &crate::target_response::TargetResponseAnalysis,
) -> Result<()> {
    let mut conn = connect(app)?;
    let tx = conn.transaction()?;
    tx.execute(
        "DELETE FROM session_target_response_episodes WHERE session_id = ?1",
        params![session_id],
    )?;
    tx.execute(
        "DELETE FROM session_target_response_summaries WHERE session_id = ?1",
        params![session_id],
    )?;
    upsert_target_response_analysis_with_tx(&tx, session_id, analysis)?;
    tx.commit()?;
    Ok(())
}

pub fn get_all_target_response_summaries(
    app: &AppHandle,
) -> Result<HashMap<String, crate::target_response::TargetResponseSummaryRecord>> {
    let conn = connect(app)?;
    query_all_target_response_summaries(&conn)
}

pub fn upsert_coaching_state_snapshot(
    app: &AppHandle,
    snapshot: &CoachingStateSnapshot,
) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        INSERT INTO coaching_state_snapshots (
            snapshot_kind,
            updated_at_unix_ms,
            sample_count,
            settled_sample_count,
            coverage_start_unix_ms,
            coverage_end_unix_ms,
            summary_json
        )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
        ON CONFLICT(snapshot_kind) DO UPDATE SET
            updated_at_unix_ms = excluded.updated_at_unix_ms,
            sample_count = excluded.sample_count,
            settled_sample_count = excluded.settled_sample_count,
            coverage_start_unix_ms = excluded.coverage_start_unix_ms,
            coverage_end_unix_ms = excluded.coverage_end_unix_ms,
            summary_json = excluded.summary_json
        ",
        params![
            &snapshot.snapshot_kind,
            snapshot.updated_at_unix_ms as i64,
            snapshot.sample_count as i64,
            snapshot.settled_sample_count as i64,
            snapshot.coverage_start_unix_ms.map(|value| value as i64),
            snapshot.coverage_end_unix_ms.map(|value| value as i64),
            serde_json::to_string(&snapshot.summary_json)
                .context("could not encode coaching snapshot json")?,
        ],
    )?;
    Ok(())
}

pub fn get_coaching_state_snapshot(
    app: &AppHandle,
    snapshot_kind: &str,
) -> Result<Option<CoachingStateSnapshot>> {
    let conn = connect(app)?;
    conn.query_row(
        "
        SELECT
            snapshot_kind,
            updated_at_unix_ms,
            sample_count,
            settled_sample_count,
            coverage_start_unix_ms,
            coverage_end_unix_ms,
            summary_json
        FROM coaching_state_snapshots
        WHERE snapshot_kind = ?1
        ",
        params![snapshot_kind],
        |row| {
            let summary_json = row.get::<_, String>(6)?;
            Ok(CoachingStateSnapshot {
                snapshot_kind: row.get(0)?,
                updated_at_unix_ms: row.get::<_, i64>(1)? as u64,
                sample_count: row.get::<_, i64>(2)? as u32,
                settled_sample_count: row.get::<_, i64>(3)? as u32,
                coverage_start_unix_ms: row.get::<_, Option<i64>>(4)?.map(|value| value as u64),
                coverage_end_unix_ms: row.get::<_, Option<i64>>(5)?.map(|value| value as u64),
                summary_json: serde_json::from_str(&summary_json)
                    .unwrap_or(serde_json::Value::Null),
            })
        },
    )
    .optional()
    .context("could not load coaching state snapshot")
}

fn upsert_target_response_analysis_with_tx(
    tx: &rusqlite::Transaction<'_>,
    session_id: &str,
    analysis: &crate::target_response::TargetResponseAnalysis,
) -> Result<()> {
    tx.execute(
        "
        INSERT INTO session_target_response_summaries (
            session_id,
            episode_count,
            path_change_count,
            target_switch_count,
            response_coverage_pct,
            avg_reaction_time_ms,
            p90_reaction_time_ms,
            avg_pre_slowdown_reaction_ms,
            avg_recovery_time_ms,
            p90_recovery_time_ms,
            avg_path_change_reaction_ms,
            avg_target_switch_reaction_ms,
            avg_trigger_magnitude_deg,
            avg_peak_yaw_error_deg,
            stable_response_ratio,
            updated_at_unix_ms
        )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)
        ",
        params![
            session_id,
            analysis.summary.episode_count as i64,
            analysis.summary.path_change_count as i64,
            analysis.summary.target_switch_count as i64,
            analysis.response_coverage_pct,
            analysis.summary.avg_reaction_time_ms,
            analysis.summary.p90_reaction_time_ms,
            analysis.summary.avg_pre_slowdown_reaction_ms,
            analysis.summary.avg_recovery_time_ms,
            analysis.summary.p90_recovery_time_ms,
            analysis.summary.avg_path_change_reaction_ms,
            analysis.summary.avg_target_switch_reaction_ms,
            analysis.summary.avg_trigger_magnitude_deg,
            analysis.summary.avg_peak_yaw_error_deg,
            analysis.summary.stable_response_ratio,
            current_unix_ms(),
        ],
    )?;

    let mut stmt = tx.prepare(
        "
        INSERT INTO session_target_response_episodes (
            session_id,
            episode_idx,
            episode_id,
            kind,
            start_ms,
            end_ms,
            target_id,
            target_label,
            trigger_magnitude_deg,
            peak_yaw_error_deg,
            reaction_time_ms,
            pre_slowdown_reaction_ms,
            recovery_time_ms,
            stable_response
        )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
        ",
    )?;

    for (index, episode) in analysis.episodes.iter().enumerate() {
        stmt.execute(params![
            session_id,
            index as i64,
            &episode.id,
            episode.kind.as_db_str(),
            episode.start_ms as i64,
            episode.end_ms as i64,
            &episode.target_id,
            &episode.target_label,
            episode.trigger_magnitude_deg,
            episode.peak_yaw_error_deg,
            episode.reaction_time_ms,
            episode.pre_slowdown_reaction_ms,
            episode.recovery_time_ms,
            if episode.stable_response { 1i64 } else { 0i64 },
        ])?;
    }
    drop(stmt);

    Ok(())
}

fn query_target_response_analysis(
    conn: &Connection,
    session_id: &str,
) -> Result<Option<crate::target_response::TargetResponseAnalysis>> {
    let summary = conn
        .query_row(
            "
            SELECT
                episode_count,
                path_change_count,
                target_switch_count,
                response_coverage_pct,
                avg_reaction_time_ms,
                p90_reaction_time_ms,
                avg_pre_slowdown_reaction_ms,
                avg_recovery_time_ms,
                p90_recovery_time_ms,
                avg_path_change_reaction_ms,
                avg_target_switch_reaction_ms,
                avg_trigger_magnitude_deg,
                avg_peak_yaw_error_deg,
                stable_response_ratio
            FROM session_target_response_summaries
            WHERE session_id = ?1
            ",
            params![session_id],
            |row| {
                Ok((
                    crate::target_response::SessionTargetResponseSnapshot {
                        episode_count: row.get::<_, i64>(0)? as u32,
                        path_change_count: row.get::<_, i64>(1)? as u32,
                        target_switch_count: row.get::<_, i64>(2)? as u32,
                        avg_reaction_time_ms: row.get(4)?,
                        p90_reaction_time_ms: row.get(5)?,
                        avg_pre_slowdown_reaction_ms: row.get(6)?,
                        avg_recovery_time_ms: row.get(7)?,
                        p90_recovery_time_ms: row.get(8)?,
                        avg_path_change_reaction_ms: row.get(9)?,
                        avg_target_switch_reaction_ms: row.get(10)?,
                        avg_trigger_magnitude_deg: row.get(11)?,
                        avg_peak_yaw_error_deg: row.get(12)?,
                        stable_response_ratio: row.get(13)?,
                    },
                    row.get::<_, Option<f64>>(3)?,
                ))
            },
        )
        .optional()?;

    let Some((summary, response_coverage_pct)) = summary else {
        return Ok(None);
    };

    let mut stmt = conn.prepare(
        "
        SELECT
            episode_id,
            kind,
            start_ms,
            end_ms,
            target_id,
            target_label,
            trigger_magnitude_deg,
            peak_yaw_error_deg,
            reaction_time_ms,
            pre_slowdown_reaction_ms,
            recovery_time_ms,
            stable_response
        FROM session_target_response_episodes
        WHERE session_id = ?1
        ORDER BY episode_idx ASC
        ",
    )?;
    let rows = stmt.query_map(params![session_id], |row| {
        let kind_text = row.get::<_, String>(1)?;
        Ok(crate::target_response::TargetResponseEpisode {
            id: row.get(0)?,
            kind: crate::target_response::TargetResponseEpisodeKind::from_db_str(&kind_text)
                .unwrap_or(crate::target_response::TargetResponseEpisodeKind::PathChange),
            start_ms: row.get::<_, i64>(2)? as u64,
            end_ms: row.get::<_, i64>(3)? as u64,
            target_id: row.get(4)?,
            target_label: row.get(5)?,
            trigger_magnitude_deg: row.get(6)?,
            peak_yaw_error_deg: row.get(7)?,
            reaction_time_ms: row.get(8)?,
            pre_slowdown_reaction_ms: row.get(9)?,
            recovery_time_ms: row.get(10)?,
            stable_response: row.get::<_, i64>(11)? != 0,
        })
    })?;

    let mut episodes = Vec::new();
    for row in rows {
        episodes.push(row?);
    }

    Ok(Some(crate::target_response::TargetResponseAnalysis {
        episode_count: summary.episode_count,
        response_coverage_pct,
        summary,
        episodes,
    }))
}

fn query_all_target_response_summaries(
    conn: &Connection,
) -> Result<HashMap<String, crate::target_response::TargetResponseSummaryRecord>> {
    let mut stmt = conn.prepare(
        "
        SELECT
            session_id,
            episode_count,
            path_change_count,
            target_switch_count,
            response_coverage_pct,
            avg_reaction_time_ms,
            p90_reaction_time_ms,
            avg_pre_slowdown_reaction_ms,
            avg_recovery_time_ms,
            p90_recovery_time_ms,
            avg_path_change_reaction_ms,
            avg_target_switch_reaction_ms,
            avg_trigger_magnitude_deg,
            avg_peak_yaw_error_deg,
            stable_response_ratio
        FROM session_target_response_summaries
        ",
    )?;
    let rows = stmt.query_map([], |row| {
        let session_id = row.get::<_, String>(0)?;
        Ok(crate::target_response::TargetResponseSummaryRecord {
            session_id: session_id.clone(),
            response_coverage_pct: row.get(4)?,
            summary: crate::target_response::SessionTargetResponseSnapshot {
                episode_count: row.get::<_, i64>(1)? as u32,
                path_change_count: row.get::<_, i64>(2)? as u32,
                target_switch_count: row.get::<_, i64>(3)? as u32,
                avg_reaction_time_ms: row.get(5)?,
                p90_reaction_time_ms: row.get(6)?,
                avg_pre_slowdown_reaction_ms: row.get(7)?,
                avg_recovery_time_ms: row.get(8)?,
                p90_recovery_time_ms: row.get(9)?,
                avg_path_change_reaction_ms: row.get(10)?,
                avg_target_switch_reaction_ms: row.get(11)?,
                avg_trigger_magnitude_deg: row.get(12)?,
                avg_peak_yaw_error_deg: row.get(13)?,
                stable_response_ratio: row.get(14)?,
            },
        })
    })?;

    let mut summaries = HashMap::new();
    for row in rows {
        let summary = row?;
        summaries.insert(summary.session_id.clone(), summary);
    }
    Ok(summaries)
}

pub fn upsert_coaching_recommendation_evaluations(
    app: &AppHandle,
    evaluations: &[CoachingRecommendationEvaluation],
) -> Result<usize> {
    if evaluations.is_empty() {
        return Ok(0);
    }
    let mut conn = connect(app)?;
    let tx = conn.transaction()?;
    let mut stmt = tx.prepare(
        "
        INSERT INTO coaching_recommendation_evaluations (
            evaluation_id,
            snapshot_kind,
            recommendation_id,
            recommendation_title,
            signal_key,
            status,
            created_at_unix_ms,
            updated_at_unix_ms,
            anchor_sample_count,
            latest_sample_count,
            anchor_metric_value,
            latest_metric_value,
            outcome_delta,
            context_json
        )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
        ON CONFLICT(evaluation_id) DO UPDATE SET
            snapshot_kind = excluded.snapshot_kind,
            recommendation_id = excluded.recommendation_id,
            recommendation_title = excluded.recommendation_title,
            signal_key = excluded.signal_key,
            status = excluded.status,
            created_at_unix_ms = excluded.created_at_unix_ms,
            updated_at_unix_ms = excluded.updated_at_unix_ms,
            anchor_sample_count = excluded.anchor_sample_count,
            latest_sample_count = excluded.latest_sample_count,
            anchor_metric_value = excluded.anchor_metric_value,
            latest_metric_value = excluded.latest_metric_value,
            outcome_delta = excluded.outcome_delta,
            context_json = excluded.context_json
        ",
    )?;

    for evaluation in evaluations {
        stmt.execute(params![
            &evaluation.evaluation_id,
            &evaluation.snapshot_kind,
            &evaluation.recommendation_id,
            &evaluation.recommendation_title,
            &evaluation.signal_key,
            &evaluation.status,
            evaluation.created_at_unix_ms as i64,
            evaluation.updated_at_unix_ms as i64,
            evaluation.anchor_sample_count as i64,
            evaluation.latest_sample_count as i64,
            evaluation.anchor_metric_value,
            evaluation.latest_metric_value,
            evaluation.outcome_delta,
            serde_json::to_string(&evaluation.context_json)
                .context("could not encode coaching evaluation json")?,
        ])?;
    }
    drop(stmt);
    tx.commit()?;
    Ok(evaluations.len())
}

pub fn get_coaching_recommendation_evaluations(
    app: &AppHandle,
    snapshot_kind: Option<&str>,
    status: Option<&str>,
) -> Result<Vec<CoachingRecommendationEvaluation>> {
    let conn = connect(app)?;
    let mut stmt = conn.prepare(
        "
        SELECT
            evaluation_id,
            snapshot_kind,
            recommendation_id,
            recommendation_title,
            signal_key,
            status,
            created_at_unix_ms,
            updated_at_unix_ms,
            anchor_sample_count,
            latest_sample_count,
            anchor_metric_value,
            latest_metric_value,
            outcome_delta,
            context_json
        FROM coaching_recommendation_evaluations
        WHERE (?1 IS NULL OR snapshot_kind = ?1)
          AND (?2 IS NULL OR status = ?2)
        ORDER BY updated_at_unix_ms DESC, created_at_unix_ms DESC
        ",
    )?;
    let rows = stmt.query_map(params![snapshot_kind, status], |row| {
        let context_json = row.get::<_, String>(13)?;
        Ok(CoachingRecommendationEvaluation {
            evaluation_id: row.get(0)?,
            snapshot_kind: row.get(1)?,
            recommendation_id: row.get(2)?,
            recommendation_title: row.get(3)?,
            signal_key: row.get(4)?,
            status: row.get(5)?,
            created_at_unix_ms: row.get::<_, i64>(6)? as u64,
            updated_at_unix_ms: row.get::<_, i64>(7)? as u64,
            anchor_sample_count: row.get::<_, i64>(8)? as u32,
            latest_sample_count: row.get::<_, i64>(9)? as u32,
            anchor_metric_value: row.get(10)?,
            latest_metric_value: row.get(11)?,
            outcome_delta: row.get(12)?,
            context_json: serde_json::from_str(&context_json).unwrap_or(serde_json::Value::Null),
        })
    })?;

    let mut evaluations = Vec::new();
    for row in rows {
        evaluations.push(row?);
    }
    Ok(evaluations)
}

pub fn upsert_coaching_user_feedback(
    app: &AppHandle,
    feedback: &CoachingUserFeedbackRecord,
) -> Result<()> {
    let conn = connect(app)?;
    conn.execute(
        "
        INSERT INTO coaching_user_feedback (
            snapshot_kind,
            recommendation_id,
            signal_key,
            feedback,
            notes,
            created_at_unix_ms,
            updated_at_unix_ms,
            context_json
        )
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
        ON CONFLICT(snapshot_kind, recommendation_id) DO UPDATE SET
            signal_key = excluded.signal_key,
            feedback = excluded.feedback,
            notes = excluded.notes,
            created_at_unix_ms = excluded.created_at_unix_ms,
            updated_at_unix_ms = excluded.updated_at_unix_ms,
            context_json = excluded.context_json
        ",
        params![
            &feedback.snapshot_kind,
            &feedback.recommendation_id,
            &feedback.signal_key,
            &feedback.feedback,
            &feedback.notes,
            feedback.created_at_unix_ms as i64,
            feedback.updated_at_unix_ms as i64,
            serde_json::to_string(&feedback.context_json)
                .context("could not encode coaching user feedback json")?,
        ],
    )?;
    Ok(())
}

pub fn get_coaching_user_feedback(
    app: &AppHandle,
    snapshot_kind: Option<&str>,
) -> Result<Vec<CoachingUserFeedbackRecord>> {
    let conn = connect(app)?;
    let mut stmt = conn.prepare(
        "
        SELECT
            snapshot_kind,
            recommendation_id,
            signal_key,
            feedback,
            notes,
            created_at_unix_ms,
            updated_at_unix_ms,
            context_json
        FROM coaching_user_feedback
        WHERE (?1 IS NULL OR snapshot_kind = ?1)
        ORDER BY updated_at_unix_ms DESC
        ",
    )?;
    let rows = stmt.query_map(params![snapshot_kind], |row| {
        let context_json = row.get::<_, String>(7)?;
        Ok(CoachingUserFeedbackRecord {
            snapshot_kind: row.get(0)?,
            recommendation_id: row.get(1)?,
            signal_key: row.get(2)?,
            feedback: row.get(3)?,
            notes: row.get(4)?,
            created_at_unix_ms: row.get::<_, i64>(5)? as u64,
            updated_at_unix_ms: row.get::<_, i64>(6)? as u64,
            context_json: serde_json::from_str(&context_json).unwrap_or(serde_json::Value::Null),
        })
    })?;

    let mut feedback_rows = Vec::new();
    for row in rows {
        feedback_rows.push(row?);
    }
    Ok(feedback_rows)
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SessionSqlAuditFinding {
    pub code: String,
    pub message: String,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SessionSqlAudit {
    pub session_id: String,
    pub session_exists: bool,
    pub replay_asset_exists: bool,
    pub replay_asset_has_run_snapshot: bool,
    pub replay_positions_rows: usize,
    pub replay_metrics_rows: usize,
    pub replay_frames_rows: usize,
    pub run_summary_rows: usize,
    pub run_timeline_rows: usize,
    pub shot_event_rows: usize,
    pub shot_target_rows: usize,
    pub context_window_rows: usize,
    pub summary_shots_fired: Option<f64>,
    pub summary_shots_hit: Option<f64>,
    pub summary_accuracy_pct: Option<f64>,
    pub integrity_status: String,
    pub integrity_checked_at_unix_ms: Option<i64>,
    pub failure_classes: Vec<String>,
    pub findings: Vec<SessionSqlAuditFinding>,
    pub issues: Vec<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SqlAuditFailureCount {
    pub code: String,
    pub count: usize,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct RepoSqlAuditSummary {
    pub audited_session_ids: usize,
    pub incomplete_sessions: usize,
    pub failure_counts: Vec<SqlAuditFailureCount>,
    pub sessions: Vec<SessionSqlAudit>,
}

impl SessionSqlAudit {
    pub fn has_issues(&self) -> bool {
        !self.issues.is_empty()
    }
}

fn push_audit_failure(audit: &mut SessionSqlAudit, code: &str, message: impl Into<String>) {
    let message = message.into();
    audit.failure_classes.push(code.to_string());
    audit.findings.push(SessionSqlAuditFinding {
        code: code.to_string(),
        message: message.clone(),
    });
    audit.issues.push(message);
}

fn audit_session_sql_with_conn(conn: &Connection, session_id: &str) -> Result<SessionSqlAudit> {
    let mut audit = conn.query_row(
        "
        SELECT
            EXISTS(SELECT 1 FROM sessions WHERE id = ?1) AS session_exists,
            EXISTS(SELECT 1 FROM replay_assets WHERE session_id = ?1) AS replay_asset_exists,
            COALESCE((SELECT has_run_snapshot FROM replay_assets WHERE session_id = ?1), 0) AS replay_asset_has_run_snapshot,
            (SELECT COUNT(*) FROM session_replay_positions WHERE session_id = ?1) AS replay_positions_rows,
            (SELECT COUNT(*) FROM session_replay_metrics WHERE session_id = ?1) AS replay_metrics_rows,
            (SELECT COUNT(*) FROM session_replay_frames WHERE session_id = ?1) AS replay_frames_rows,
            (SELECT COUNT(*) FROM session_run_summaries WHERE session_id = ?1) AS run_summary_rows,
            (SELECT COUNT(*) FROM session_run_timelines WHERE session_id = ?1) AS run_timeline_rows,
            (SELECT COUNT(*) FROM session_shot_events WHERE session_id = ?1) AS shot_event_rows,
            (SELECT COUNT(*) FROM session_shot_targets WHERE session_id = ?1) AS shot_target_rows,
            (SELECT COUNT(*) FROM session_replay_context_windows WHERE session_id = ?1) AS context_window_rows,
            (SELECT shots_fired FROM session_run_summaries WHERE session_id = ?1) AS summary_shots_fired,
            (SELECT shots_hit FROM session_run_summaries WHERE session_id = ?1) AS summary_shots_hit,
            (SELECT accuracy_pct FROM session_run_summaries WHERE session_id = ?1) AS summary_accuracy_pct,
            COALESCE((SELECT integrity_status FROM sessions WHERE id = ?1), 'missing') AS integrity_status,
            (SELECT integrity_checked_at_unix_ms FROM sessions WHERE id = ?1) AS integrity_checked_at_unix_ms
        ",
        params![session_id],
        |row| {
            Ok(SessionSqlAudit {
                session_id: session_id.to_string(),
                session_exists: row.get::<_, i64>(0)? != 0,
                replay_asset_exists: row.get::<_, i64>(1)? != 0,
                replay_asset_has_run_snapshot: row.get::<_, i64>(2)? != 0,
                replay_positions_rows: row.get::<_, i64>(3)? as usize,
                replay_metrics_rows: row.get::<_, i64>(4)? as usize,
                replay_frames_rows: row.get::<_, i64>(5)? as usize,
                run_summary_rows: row.get::<_, i64>(6)? as usize,
                run_timeline_rows: row.get::<_, i64>(7)? as usize,
                shot_event_rows: row.get::<_, i64>(8)? as usize,
                shot_target_rows: row.get::<_, i64>(9)? as usize,
                context_window_rows: row.get::<_, i64>(10)? as usize,
                summary_shots_fired: row.get(11)?,
                summary_shots_hit: row.get(12)?,
                summary_accuracy_pct: row.get(13)?,
                integrity_status: row.get(14)?,
                integrity_checked_at_unix_ms: row.get(15)?,
                failure_classes: Vec::new(),
                findings: Vec::new(),
                issues: Vec::new(),
            })
        },
    )?;

    if !audit.session_exists {
        push_audit_failure(&mut audit, "missing_sessions_row", "missing sessions row");
    }
    if audit.replay_asset_exists
        && audit.replay_positions_rows == 0
        && audit.replay_metrics_rows == 0
        && audit.replay_frames_rows == 0
    {
        push_audit_failure(
            &mut audit,
            "missing_replay_payload_rows",
            "replay asset exists but replay payload tables are empty",
        );
    }
    if audit.replay_asset_has_run_snapshot && audit.run_summary_rows == 0 {
        push_audit_failure(
            &mut audit,
            "missing_run_summary_rows",
            "replay asset expects run snapshot but session_run_summaries is empty",
        );
    }
    if audit.run_summary_rows > 0 && audit.run_timeline_rows == 0 {
        push_audit_failure(
            &mut audit,
            "missing_run_timeline_rows",
            "run summary exists but session_run_timelines is empty",
        );
    }
    let expects_shot_rows = audit.summary_shots_fired.unwrap_or(0.0) > 0.0
        || audit.summary_shots_hit.unwrap_or(0.0) > 0.0;
    if expects_shot_rows && audit.shot_event_rows == 0 {
        push_audit_failure(
            &mut audit,
            "missing_shot_event_rows",
            "run summary has shots but session_shot_events is empty",
        );
    }
    if audit.shot_event_rows > 0 && audit.shot_target_rows == 0 {
        push_audit_failure(
            &mut audit,
            "missing_shot_target_rows",
            "session_shot_events exists but session_shot_targets is empty",
        );
    }
    if audit.shot_event_rows > 0 && audit.context_window_rows == 0 {
        push_audit_failure(
            &mut audit,
            "missing_context_window_rows",
            "session_shot_events exists but session_replay_context_windows is empty",
        );
    }
    if let (Some(shots_fired), Some(shots_hit)) =
        (audit.summary_shots_fired, audit.summary_shots_hit)
    {
        if shots_hit > shots_fired + 0.0001 {
            push_audit_failure(
                &mut audit,
                "impossible_shot_counts",
                format!("shots_hit ({shots_hit:.3}) exceeds shots_fired ({shots_fired:.3})"),
            );
        }
    }
    if let Some(accuracy_pct) = audit.summary_accuracy_pct {
        if !(0.0..=100.0).contains(&accuracy_pct) {
            push_audit_failure(
                &mut audit,
                "impossible_accuracy_pct",
                format!("accuracy_pct ({accuracy_pct:.3}) is outside 0..=100"),
            );
        }
        if audit.summary_shots_fired.unwrap_or(0.0) <= 0.0 && accuracy_pct > 0.0 {
            push_audit_failure(
                &mut audit,
                "accuracy_without_shots",
                "accuracy_pct is non-zero while shots_fired is zero or missing",
            );
        }
    }

    Ok(audit)
}

pub fn audit_session_sql(app: &AppHandle, session_id: &str) -> Result<SessionSqlAudit> {
    let conn = connect(app)?;
    audit_session_sql_with_conn(&conn, session_id)
}

pub fn persist_session_sql_audit(app: &AppHandle, audit: &SessionSqlAudit) -> Result<()> {
    if !audit.session_exists {
        return Ok(());
    }
    let conn = connect(app)?;
    let status = if audit.has_issues() {
        "incomplete"
    } else {
        "ok"
    };
    let failure_codes = if audit.failure_classes.is_empty() {
        None
    } else {
        Some(audit.failure_classes.join(","))
    };
    conn.execute(
        "
        UPDATE sessions
        SET integrity_status = ?2,
            integrity_failure_codes = ?3,
            integrity_checked_at_unix_ms = ?4
        WHERE id = ?1
        ",
        params![audit.session_id, status, failure_codes, current_unix_ms()],
    )?;
    Ok(())
}

pub fn refresh_repo_sql_audit(
    app: &AppHandle,
    failing_session_limit: Option<usize>,
) -> Result<RepoSqlAuditSummary> {
    let mut conn = connect(app)?;
    let session_ids = {
        let mut stmt = conn.prepare(
            "
            SELECT session_id FROM (
                SELECT id AS session_id FROM sessions
                UNION
                SELECT session_id FROM replay_assets
            )
            ORDER BY session_id ASC
            ",
        )?;
        stmt.query_map([], |row| row.get::<_, String>(0))?
            .collect::<std::result::Result<Vec<_>, _>>()?
    };

    let limit = failing_session_limit.unwrap_or(100);
    let mut failure_counts = std::collections::HashMap::<String, usize>::new();
    let mut incomplete_sessions = 0usize;
    let mut sessions = Vec::new();
    let mut audits_to_persist: Vec<SessionSqlAudit> = Vec::new();

    for session_id in &session_ids {
        let audit = audit_session_sql_with_conn(&conn, session_id)?;
        if audit.has_issues() {
            incomplete_sessions += 1;
            for code in &audit.failure_classes {
                *failure_counts.entry(code.clone()).or_insert(0) += 1;
            }
            if sessions.len() < limit {
                sessions.push(audit.clone());
            }
        }
        if audit.session_exists {
            audits_to_persist.push(audit);
        }
    }

    // Write all audit results in a single transaction instead of one
    // connection-open per session (which was O(n) connection overhead).
    let now_ms = current_unix_ms();
    let tx = conn.transaction()?;
    {
        let mut upd = tx.prepare(
            "
            UPDATE sessions
            SET integrity_status = ?2,
                integrity_failure_codes = ?3,
                integrity_checked_at_unix_ms = ?4
            WHERE id = ?1
            ",
        )?;
        for audit in &audits_to_persist {
            let status = if audit.has_issues() {
                "incomplete"
            } else {
                "ok"
            };
            let failure_codes = if audit.failure_classes.is_empty() {
                None
            } else {
                Some(audit.failure_classes.join(","))
            };
            upd.execute(params![audit.session_id, status, failure_codes, now_ms])?;
        }
    }
    tx.commit()?;

    let mut failure_counts = failure_counts
        .into_iter()
        .map(|(code, count)| SqlAuditFailureCount { code, count })
        .collect::<Vec<_>>();
    failure_counts.sort_by(|a, b| b.count.cmp(&a.count).then_with(|| a.code.cmp(&b.code)));

    Ok(RepoSqlAuditSummary {
        audited_session_ids: session_ids.len(),
        incomplete_sessions,
        failure_counts,
        sessions,
    })
}

pub fn backfill_session_classifications(app: &AppHandle) -> Result<usize> {
    #[derive(Clone)]
    struct ClassificationRow {
        session_id: String,
        has_stats_panel: bool,
        scenario_name: String,
        stats_panel: crate::session_store::StatsPanelSnapshot,
        run_summary: Option<crate::bridge::BridgeRunSnapshot>,
    }

    let mut conn = connect(app)?;
    let rows = {
        let mut stmt = conn.prepare(
            "
            SELECT
                s.id,
                CASE WHEN sp.session_id IS NULL THEN 0 ELSE 1 END AS has_stats_panel,
                s.scenario,
                sp.scenario_type,
                sp.scenario_subtype,
                COALESCE(sp.kills, s.kills),
                COALESCE(sp.avg_kps, CASE WHEN s.duration_secs > 0 THEN s.kills / s.duration_secs ELSE NULL END),
                COALESCE(sp.accuracy_pct, s.accuracy),
                COALESCE(sp.total_damage, s.damage_done),
                COALESCE(sp.avg_ttk_ms, CASE WHEN s.avg_ttk > 0 THEN s.avg_ttk * 1000.0 ELSE NULL END),
                sp.best_ttk_ms,
                sp.ttk_std_ms,
                sp.accuracy_trend,
                rs.shots_fired,
                rs.shots_hit,
                rs.kills,
                rs.kills_per_second,
                rs.damage_done,
                rs.damage_possible
            FROM sessions s
            LEFT JOIN session_stats_panels sp ON sp.session_id = s.id
            LEFT JOIN session_run_summaries rs ON rs.session_id = s.id
            ",
        )?;
        stmt.query_map([], |row| {
            let shots_fired = row.get::<_, Option<f64>>(13)?;
            let shots_hit = row.get::<_, Option<f64>>(14)?;
            let kills = row.get::<_, Option<f64>>(15)?;
            let kills_per_second = row.get::<_, Option<f64>>(16)?;
            let damage_done = row.get::<_, Option<f64>>(17)?;
            let damage_possible = row.get::<_, Option<f64>>(18)?;
            let has_run_summary = shots_fired.is_some()
                || shots_hit.is_some()
                || kills.is_some()
                || kills_per_second.is_some()
                || damage_done.is_some()
                || damage_possible.is_some();

            Ok(ClassificationRow {
                session_id: row.get(0)?,
                has_stats_panel: row.get::<_, i64>(1)? != 0,
                scenario_name: row.get(2)?,
                stats_panel: crate::session_store::StatsPanelSnapshot {
                    scenario_type: row
                        .get::<_, Option<String>>(3)?
                        .unwrap_or_else(|| "Unknown".to_string()),
                    scenario_subtype: row.get(4)?,
                    kills: row.get::<_, Option<i64>>(5)?.map(|value| value as u32),
                    avg_kps: row.get(6)?,
                    accuracy_pct: normalize_accuracy_pct(row.get(7)?, None, None)
                        .map(|value| value as f32),
                    total_damage: row.get(8)?,
                    avg_ttk_ms: row.get(9)?,
                    best_ttk_ms: row.get(10)?,
                    ttk_std_ms: row.get(11)?,
                    accuracy_trend: row.get(12)?,
                },
                run_summary: has_run_summary.then(|| crate::bridge::BridgeRunSnapshot {
                    duration_secs: None,
                    score_total: None,
                    score_total_derived: None,
                    score_per_minute: None,
                    shots_fired,
                    shots_hit,
                    kills,
                    kills_per_second,
                    damage_done,
                    damage_possible,
                    damage_efficiency: None,
                    accuracy_pct: None,
                    peak_score_per_minute: None,
                    peak_kills_per_second: None,
                    paired_shot_hits: 0,
                    avg_fire_to_hit_ms: None,
                    p90_fire_to_hit_ms: None,
                    avg_shots_to_hit: None,
                    corrective_shot_ratio: None,
                    started_at_bridge_ts_ms: None,
                    started_at_unix_ms: None,
                    ended_at_unix_ms: None,
                    event_counts: crate::bridge::BridgeRunEventCounts::default(),
                    timeline: vec![],
                    pause_windows: vec![],
                    shot_telemetry: vec![],
                    tick_stream_v1: None,
                }),
            })
        })?
        .collect::<Result<Vec<_>, _>>()?
    };

    let mut updates = Vec::new();
    for row in rows {
        let shot_telemetry = query_shot_telemetry(&conn, &row.session_id)?;
        let mut classification = crate::bridge::classify_persisted_session(
            Some(row.scenario_name.as_str()),
            &row.stats_panel,
            row.run_summary.as_ref(),
            &shot_telemetry,
        );
        if classification.family == "Unknown" {
            classification = classify_csv_only_session(&row.scenario_name, &row.stats_panel);
        }
        if classification.family == "Unknown" {
            continue;
        }
        if row.has_stats_panel
            && classification.family == row.stats_panel.scenario_type
            && classification.subtype == row.stats_panel.scenario_subtype
        {
            continue;
        }
        updates.push((
            row.session_id,
            row.stats_panel,
            row.has_stats_panel,
            classification,
        ));
    }

    if updates.is_empty() {
        return Ok(0);
    }

    let tx = conn.transaction()?;
    for (session_id, stats_panel, has_stats_panel, classification) in &updates {
        if *has_stats_panel {
            tx.execute(
                "
                UPDATE session_stats_panels
                SET scenario_type = ?2,
                    scenario_subtype = ?3
                WHERE session_id = ?1
                ",
                params![session_id, &classification.family, &classification.subtype],
            )?;
        } else {
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
                    session_id,
                    &classification.family,
                    &classification.subtype,
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
    }
    tx.commit()?;

    Ok(updates.len())
}

fn classify_csv_only_session(
    scenario_name: &str,
    stats_panel: &crate::session_store::StatsPanelSnapshot,
) -> crate::bridge::PersistedScenarioClassification {
    if let Some(classification) = crate::bridge::classify_scenario_name_family(scenario_name) {
        return classification;
    }

    let lower = scenario_name.to_ascii_lowercase();
    let kills = stats_panel.kills.unwrap_or(0);
    let avg_ttk_secs = stats_panel
        .avg_ttk_ms
        .map(|value| value as f64 / 1000.0)
        .unwrap_or(0.0);
    let damage_done = stats_panel.total_damage.map(f64::from).unwrap_or(0.0);
    let accuracy = stats_panel.accuracy_pct.map(f64::from).unwrap_or(0.0);
    let avg_kps = stats_panel.avg_kps.map(f64::from).unwrap_or(0.0);
    let damage_per_kill = if kills > 0 {
        damage_done / kills as f64
    } else {
        0.0
    };

    if lower.contains("track")
        || lower.contains("smooth")
        || lower.contains("air ")
        || lower.contains("centering")
        || lower.contains("sphere")
        || lower.contains("ground")
        || lower.contains("close long strafes")
        || lower.contains("controlsphere")
    {
        return crate::bridge::PersistedScenarioClassification {
            family: "Tracking".to_string(),
            subtype: None,
        };
    }

    if kills == 0 && damage_done > 0.0 {
        return crate::bridge::PersistedScenarioClassification {
            family: "Tracking".to_string(),
            subtype: None,
        };
    }

    let target_switching_hint = lower.contains("switch")
        || lower.contains(" patts")
        || lower.contains(" voxts")
        || lower.contains(" driftts")
        || lower.contains(" flyts")
        || lower.contains(" bouncets")
        || lower.ends_with(" ts")
        || lower.contains(" target switching");
    let dynamic_clicking_hint = lower.contains("pasu")
        || lower.contains("popcorn")
        || lower.contains("bounce")
        || lower.contains("air angelic")
        || lower.contains("dynamic");
    let static_clicking_hint = lower.contains("static")
        || lower.contains("1w")
        || lower.contains("ww")
        || lower.contains("sixshot")
        || lower.contains("clicking");

    if kills > 0 && (avg_ttk_secs >= 5.0 || damage_per_kill < 0.5) {
        return crate::bridge::PersistedScenarioClassification {
            family: "Tracking".to_string(),
            subtype: None,
        };
    }

    if target_switching_hint || (kills > 0 && damage_per_kill > 1.25) {
        return crate::bridge::PersistedScenarioClassification {
            family: "TargetSwitching".to_string(),
            subtype: None,
        };
    }

    if dynamic_clicking_hint
        || (kills > 0 && (avg_ttk_secs >= 0.45 || (avg_kps > 0.0 && avg_kps <= 2.25)))
    {
        return crate::bridge::PersistedScenarioClassification {
            family: "DynamicClicking".to_string(),
            subtype: None,
        };
    }

    if static_clicking_hint || kills > 0 {
        return crate::bridge::PersistedScenarioClassification {
            family: "StaticClicking".to_string(),
            subtype: None,
        };
    }

    if accuracy > 0.0 {
        return crate::bridge::PersistedScenarioClassification {
            family: "AccuracyDrill".to_string(),
            subtype: None,
        };
    }

    crate::bridge::PersistedScenarioClassification {
        family: "Unknown".to_string(),
        subtype: None,
    }
}
