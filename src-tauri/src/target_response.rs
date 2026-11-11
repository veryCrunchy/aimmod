use std::collections::HashMap;

use serde_json::Value;

use crate::{
    bridge::{BridgeRunTimelinePoint, BridgeShotTelemetryEvent, BridgeTickStreamV1},
    mouse_hook::RawPositionPoint,
    replay_store::ReplayData,
};

// snake_case: the frontend (StatsWindow, coaching engine) reads these as
// episode_count, avg_reaction_time_ms, ...; camelCase left them undefined and
// crashed the session stats view.
#[derive(Debug, Clone, Default, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct SessionTargetResponseSnapshot {
    pub episode_count: u32,
    pub path_change_count: u32,
    pub target_switch_count: u32,
    pub avg_reaction_time_ms: Option<f64>,
    pub p90_reaction_time_ms: Option<f64>,
    pub avg_pre_slowdown_reaction_ms: Option<f64>,
    pub avg_recovery_time_ms: Option<f64>,
    pub p90_recovery_time_ms: Option<f64>,
    pub avg_path_change_reaction_ms: Option<f64>,
    pub avg_target_switch_reaction_ms: Option<f64>,
    pub avg_trigger_magnitude_deg: Option<f64>,
    pub avg_peak_yaw_error_deg: Option<f64>,
    pub stable_response_ratio: Option<f64>,
}

#[derive(Debug, Clone)]
pub struct TargetResponseSummaryRecord {
    pub session_id: String,
    pub response_coverage_pct: Option<f64>,
    pub summary: SessionTargetResponseSnapshot,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TargetResponseEpisodeKind {
    PathChange,
    TargetSwitch,
}

impl TargetResponseEpisodeKind {
    pub fn as_db_str(self) -> &'static str {
        match self {
            Self::PathChange => "path_change",
            Self::TargetSwitch => "target_switch",
        }
    }

    pub fn from_db_str(value: &str) -> Option<Self> {
        match value.trim() {
            "path_change" => Some(Self::PathChange),
            "target_switch" => Some(Self::TargetSwitch),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TargetResponseEpisode {
    pub id: String,
    pub kind: TargetResponseEpisodeKind,
    pub start_ms: u64,
    pub end_ms: u64,
    pub target_id: String,
    pub target_label: String,
    pub trigger_magnitude_deg: Option<f64>,
    pub peak_yaw_error_deg: Option<f64>,
    pub reaction_time_ms: Option<f64>,
    pub pre_slowdown_reaction_ms: Option<f64>,
    pub recovery_time_ms: Option<f64>,
    pub stable_response: bool,
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TargetResponseAnalysis {
    pub episode_count: u32,
    pub response_coverage_pct: Option<f64>,
    pub summary: SessionTargetResponseSnapshot,
    pub episodes: Vec<TargetResponseEpisode>,
}

#[derive(Debug, Clone)]
struct ReplayTickEntityState {
    id: String,
    profile: String,
    is_player: bool,
    is_bot: bool,
    x: f64,
    y: f64,
    yaw: f64,
}

#[derive(Debug, Clone)]
struct ReplayTickFrameCommand {
    ts_ms: u64,
    seq: u64,
    upserts: Vec<ReplayTickEntityState>,
    removes: Vec<String>,
}

#[derive(Debug, Clone)]
struct TargetResponseFrame {
    ts_ms: u64,
    player: ReplayTickEntityState,
    target: ReplayTickEntityState,
    aim_error_deg: Option<f64>,
}

#[derive(Debug, Clone)]
struct CursorMotionSample {
    t_ms: u64,
    speed: f64,
    heading_deg: Option<f64>,
}

fn json_number(value: &Value) -> Option<f64> {
    match value {
        Value::Number(number) => number.as_f64().filter(|value| value.is_finite()),
        Value::String(text) => text.parse::<f64>().ok().filter(|value| value.is_finite()),
        _ => None,
    }
}

fn json_boolish(value: &Value) -> Option<bool> {
    match value {
        Value::Bool(flag) => Some(*flag),
        Value::Number(number) => number.as_f64().map(|value| value >= 0.5),
        Value::String(text) => {
            let trimmed = text.trim().to_ascii_lowercase();
            match trimmed.as_str() {
                "1" | "true" => Some(true),
                "0" | "false" => Some(false),
                _ => None,
            }
        }
        _ => None,
    }
}

fn normalize_angle_deg(value: f64) -> f64 {
    let mut normalized = value % 360.0;
    if normalized <= -180.0 {
        normalized += 360.0;
    }
    if normalized > 180.0 {
        normalized -= 360.0;
    }
    normalized
}

fn angle_diff_deg(a: f64, b: f64) -> f64 {
    normalize_angle_deg(a - b).abs()
}

fn vector_heading_deg(dx: f64, dy: f64) -> Option<f64> {
    if !dx.is_finite() || !dy.is_finite() {
        return None;
    }
    if dx.abs() < 0.0001 && dy.abs() < 0.0001 {
        return None;
    }
    Some(dy.atan2(dx).to_degrees())
}

fn distance2d(x0: f64, y0: f64, x1: f64, y1: f64) -> f64 {
    (x1 - x0).hypot(y1 - y0)
}

fn clamp_number(value: f64, min: f64, max: f64) -> f64 {
    value.clamp(min, max)
}

fn safe_mean(values: &[Option<f64>]) -> Option<f64> {
    let mut sum = 0.0;
    let mut count = 0usize;
    for value in values.iter().flatten() {
        if value.is_finite() {
            sum += value;
            count += 1;
        }
    }
    (count > 0).then_some(sum / count as f64)
}

fn percentile(values: &[f64], p: f64) -> Option<f64> {
    if values.is_empty() {
        return None;
    }
    let mut sorted = values.to_vec();
    sorted.sort_by(|left, right| left.total_cmp(right));
    let index = (((sorted.len() as f64) * p / 100.0).ceil() as isize - 1)
        .clamp(0, sorted.len() as isize - 1) as usize;
    sorted.get(index).copied()
}

fn mean_heading_deg(samples: &[&CursorMotionSample]) -> Option<f64> {
    let mut sum_x = 0.0;
    let mut sum_y = 0.0;
    let mut any = false;
    for sample in samples {
        let Some(heading) = sample.heading_deg else {
            continue;
        };
        if !sample.speed.is_finite() {
            continue;
        }
        let radians = heading.to_radians();
        let weight = sample.speed.max(1.0);
        sum_x += radians.cos() * weight;
        sum_y += radians.sin() * weight;
        any = true;
    }
    if !any || (sum_x.abs() < 0.0001 && sum_y.abs() < 0.0001) {
        None
    } else {
        Some(sum_y.atan2(sum_x).to_degrees())
    }
}

fn parse_replay_tick_entity(value: &Value) -> Option<ReplayTickEntityState> {
    let obj = value.as_object()?;
    let id = obj.get("id")?.as_str()?.trim().to_string();
    if id.is_empty() {
        return None;
    }

    let location = obj.get("location")?.as_object()?;
    let rotation = obj.get("rotation")?.as_object()?;
    Some(ReplayTickEntityState {
        id,
        profile: obj
            .get("profile")
            .and_then(Value::as_str)
            .map(|value| value.trim().to_string())
            .unwrap_or_default(),
        is_player: obj.get("is_player").and_then(json_boolish).unwrap_or(false),
        is_bot: obj.get("is_bot").and_then(json_boolish).unwrap_or(false),
        x: location.get("x").and_then(json_number).unwrap_or(0.0),
        y: location.get("y").and_then(json_number).unwrap_or(0.0),
        yaw: rotation.get("yaw").and_then(json_number).unwrap_or(0.0),
    })
}

fn parse_replay_tick_frame_command(
    value: &Value,
    entity_field: &str,
    replay_base_ts_ms: u64,
) -> Option<ReplayTickFrameCommand> {
    let obj = value.as_object()?;
    let raw_ts_ms = obj.get("ts_ms").and_then(json_number)?;
    let raw_ts_ms = raw_ts_ms.max(0.0).round() as u64;
    let raw_seq = obj
        .get("seq")
        .and_then(json_number)
        .map(|value| value.max(0.0).round() as u64);

    let mut upserts = Vec::new();
    if let Some(entries) = obj.get(entity_field).and_then(Value::as_array) {
        upserts.reserve(entries.len());
        for entry in entries {
            if let Some(entity) = parse_replay_tick_entity(entry) {
                upserts.push(entity);
            }
        }
    }

    let mut removes = Vec::new();
    if let Some(entries) = obj.get("remove").and_then(Value::as_array) {
        removes.reserve(entries.len());
        for entry in entries {
            if let Some(id) = entry.as_str() {
                let trimmed = id.trim();
                if !trimmed.is_empty() {
                    removes.push(trimmed.to_string());
                }
            }
        }
    }

    let ts_ms = raw_ts_ms.saturating_sub(replay_base_ts_ms);
    Some(ReplayTickFrameCommand {
        ts_ms,
        seq: raw_seq.unwrap_or(ts_ms),
        upserts,
        removes,
    })
}

fn build_replay_tick_frames(
    stream: &BridgeTickStreamV1,
    replay_base_ts_ms: u64,
) -> Vec<TargetResponseFrame> {
    let mut commands = Vec::with_capacity(stream.keyframes.len() + stream.deltas.len());
    for value in &stream.keyframes {
        if let Some(command) = parse_replay_tick_frame_command(value, "entities", replay_base_ts_ms)
        {
            commands.push(command);
        }
    }
    for value in &stream.deltas {
        if let Some(command) = parse_replay_tick_frame_command(value, "upserts", replay_base_ts_ms)
        {
            commands.push(command);
        }
    }
    if commands.is_empty() {
        return Vec::new();
    }

    commands.sort_by(|left, right| {
        left.ts_ms
            .cmp(&right.ts_ms)
            .then_with(|| left.seq.cmp(&right.seq))
    });

    let mut active_entities = HashMap::<String, ReplayTickEntityState>::new();
    let mut frames = Vec::new();

    for command in commands {
        for entity in command.upserts {
            active_entities.insert(entity.id.clone(), entity);
        }
        for id in command.removes {
            active_entities.remove(&id);
        }

        let entities = active_entities.values().cloned().collect::<Vec<_>>();
        let Some(player) = entities.iter().find(|entity| entity.is_player).cloned() else {
            continue;
        };

        let nearest_target = entities
            .iter()
            .filter(|entity| entity.is_bot)
            .min_by(|left, right| {
                let left_distance = distance2d(player.x, player.y, left.x, left.y);
                let right_distance = distance2d(player.x, player.y, right.x, right.y);
                left_distance.total_cmp(&right_distance)
            })
            .cloned();
        let Some(target) = nearest_target else {
            continue;
        };

        let target_yaw = vector_heading_deg(target.x - player.x, target.y - player.y);
        let aim_error_deg = target_yaw.map(|yaw| angle_diff_deg(player.yaw, yaw));
        frames.push(TargetResponseFrame {
            ts_ms: command.ts_ms,
            player,
            target,
            aim_error_deg,
        });
    }

    frames
}

fn build_shot_telemetry_frames(
    shot_telemetry: &[BridgeShotTelemetryEvent],
    replay_base_ts_ms: u64,
) -> Vec<TargetResponseFrame> {
    let mut events = shot_telemetry.to_vec();
    events.sort_by_key(|event| event.ts_ms);
    let mut frames = Vec::new();

    for event in events {
        let Some(player_entity) = event.player.as_ref() else {
            continue;
        };
        let bot_targets = event
            .targets
            .iter()
            .filter(|target| target.is_bot)
            .collect::<Vec<_>>();
        let nearest = bot_targets
            .iter()
            .find(|target| target.is_nearest)
            .copied()
            .or_else(|| bot_targets.first().copied())
            .or_else(|| event.targets.iter().find(|target| target.is_nearest))
            .or_else(|| event.targets.first());
        let Some(target) = nearest else {
            continue;
        };

        frames.push(TargetResponseFrame {
            ts_ms: event.ts_ms.saturating_sub(replay_base_ts_ms),
            player: ReplayTickEntityState {
                id: player_entity.entity_id.clone(),
                profile: player_entity.profile.clone(),
                is_player: player_entity.is_player,
                is_bot: player_entity.is_bot,
                x: player_entity.x,
                y: player_entity.y,
                yaw: player_entity.yaw,
            },
            target: ReplayTickEntityState {
                id: target.entity_id.clone(),
                profile: target.profile.clone(),
                is_player: target.is_player,
                is_bot: target.is_bot,
                x: target.x,
                y: target.y,
                yaw: target.yaw,
            },
            aim_error_deg: target.yaw_error_deg.map(f64::abs),
        });
    }

    frames
}

fn build_cursor_motion_samples(points: &[RawPositionPoint]) -> Vec<CursorMotionSample> {
    if points.len() < 2 {
        return Vec::new();
    }

    let mut ordered = points.to_vec();
    ordered.sort_by_key(|point| point.timestamp_ms);

    let mut samples = Vec::with_capacity(ordered.len().saturating_sub(1));
    for index in 1..ordered.len() {
        let previous = &ordered[index - 1];
        let current = &ordered[index];
        let dt_ms = current
            .timestamp_ms
            .saturating_sub(previous.timestamp_ms)
            .max(1);
        let dx = current.x - previous.x;
        let dy = current.y - previous.y;
        samples.push(CursorMotionSample {
            t_ms: current.timestamp_ms,
            speed: dx.hypot(dy) / dt_ms as f64 * 1000.0,
            heading_deg: vector_heading_deg(dx, dy),
        });
    }
    samples
}

fn motion_samples_in_range<'a>(
    samples: &'a [CursorMotionSample],
    start_ms: u64,
    end_ms: u64,
) -> Vec<&'a CursorMotionSample> {
    samples
        .iter()
        .filter(|sample| sample.t_ms >= start_ms && sample.t_ms <= end_ms)
        .collect()
}

fn find_reaction_time_ms(
    samples: &[CursorMotionSample],
    event_ms: u64,
    baseline_heading_deg: Option<f64>,
    baseline_speed: Option<f64>,
    trigger_magnitude_deg: Option<f64>,
) -> Option<f64> {
    let after = motion_samples_in_range(samples, event_ms + 8, event_ms + 650);
    if after.is_empty() {
        return None;
    }
    let heading_threshold =
        clamp_number((trigger_magnitude_deg.unwrap_or(32.0)) * 0.35, 12.0, 34.0);
    let speed_floor = baseline_speed
        .map(|speed| speed * 0.4)
        .unwrap_or(20.0)
        .max(20.0);

    for sample in after {
        let heading_delta = match (baseline_heading_deg, sample.heading_deg) {
            (Some(baseline), Some(current)) => Some(angle_diff_deg(current, baseline)),
            _ => None,
        };
        let speed_delta_ratio = match baseline_speed {
            Some(speed) if speed > 0.0 => Some((sample.speed - speed).abs() / speed),
            _ => None,
        };
        if (heading_delta.is_some_and(|delta| delta >= heading_threshold)
            && sample.speed >= speed_floor)
            || (heading_delta.is_some_and(|delta| delta >= heading_threshold * 0.65)
                && speed_delta_ratio.unwrap_or(0.0) >= 0.32)
        {
            return Some(sample.t_ms.saturating_sub(event_ms) as f64);
        }
    }

    None
}

fn find_pre_slowdown_reaction_ms(
    samples: &[CursorMotionSample],
    event_ms: u64,
    baseline_speed: Option<f64>,
) -> Option<f64> {
    let baseline_speed = baseline_speed?;
    if baseline_speed <= 10.0 {
        return None;
    }
    let after = motion_samples_in_range(samples, event_ms, event_ms + 500);
    for sample in after {
        if sample.speed <= baseline_speed * 0.82 {
            return Some(sample.t_ms.saturating_sub(event_ms) as f64);
        }
    }
    None
}

fn find_recovery_time_ms(
    frames: &[TargetResponseFrame],
    start_index: usize,
    target_id: &str,
    initial_aim_error_deg: Option<f64>,
) -> Option<f64> {
    let start_frame = frames.get(start_index)?;
    let threshold_deg = clamp_number(initial_aim_error_deg.unwrap_or(6.0) * 0.55, 2.2, 10.0);
    let mut stable_start_ms = None::<u64>;
    let mut stable_count = 0usize;

    for frame in frames.iter().skip(start_index) {
        if frame.ts_ms.saturating_sub(start_frame.ts_ms) > 1400 {
            break;
        }
        if frame.target.id != target_id {
            stable_start_ms = None;
            stable_count = 0;
            continue;
        }
        if frame
            .aim_error_deg
            .is_some_and(|aim_error| aim_error <= threshold_deg)
        {
            if stable_start_ms.is_none() {
                stable_start_ms = Some(frame.ts_ms);
            }
            stable_count += 1;
            if stable_count >= 2 {
                return stable_start_ms
                    .map(|stable| stable.saturating_sub(start_frame.ts_ms) as f64);
            }
        } else {
            stable_start_ms = None;
            stable_count = 0;
        }
    }

    None
}

fn format_target_response_label(target: &ReplayTickEntityState) -> String {
    if target.profile.trim().is_empty() {
        target.id.clone()
    } else {
        target.profile.trim().to_string()
    }
}

fn summarize_target_response_episodes(
    episodes: &[TargetResponseEpisode],
) -> SessionTargetResponseSnapshot {
    let reaction_values = episodes
        .iter()
        .filter_map(|episode| episode.reaction_time_ms)
        .collect::<Vec<_>>();
    let recovery_values = episodes
        .iter()
        .filter_map(|episode| episode.recovery_time_ms)
        .collect::<Vec<_>>();
    let path_change_values = episodes
        .iter()
        .filter(|episode| episode.kind == TargetResponseEpisodeKind::PathChange)
        .filter_map(|episode| episode.reaction_time_ms)
        .collect::<Vec<_>>();
    let target_switch_values = episodes
        .iter()
        .filter(|episode| episode.kind == TargetResponseEpisodeKind::TargetSwitch)
        .filter_map(|episode| episode.reaction_time_ms)
        .collect::<Vec<_>>();
    let stable_responses = episodes
        .iter()
        .filter(|episode| episode.stable_response)
        .count();

    SessionTargetResponseSnapshot {
        episode_count: episodes.len() as u32,
        path_change_count: episodes
            .iter()
            .filter(|episode| episode.kind == TargetResponseEpisodeKind::PathChange)
            .count() as u32,
        target_switch_count: episodes
            .iter()
            .filter(|episode| episode.kind == TargetResponseEpisodeKind::TargetSwitch)
            .count() as u32,
        avg_reaction_time_ms: safe_mean(
            &episodes
                .iter()
                .map(|episode| episode.reaction_time_ms)
                .collect::<Vec<_>>(),
        ),
        p90_reaction_time_ms: percentile(&reaction_values, 90.0),
        avg_pre_slowdown_reaction_ms: safe_mean(
            &episodes
                .iter()
                .map(|episode| episode.pre_slowdown_reaction_ms)
                .collect::<Vec<_>>(),
        ),
        avg_recovery_time_ms: safe_mean(
            &episodes
                .iter()
                .map(|episode| episode.recovery_time_ms)
                .collect::<Vec<_>>(),
        ),
        p90_recovery_time_ms: percentile(&recovery_values, 90.0),
        avg_path_change_reaction_ms: safe_mean(
            &path_change_values
                .iter()
                .map(|value| Some(*value))
                .collect::<Vec<_>>(),
        ),
        avg_target_switch_reaction_ms: safe_mean(
            &target_switch_values
                .iter()
                .map(|value| Some(*value))
                .collect::<Vec<_>>(),
        ),
        avg_trigger_magnitude_deg: safe_mean(
            &episodes
                .iter()
                .map(|episode| episode.trigger_magnitude_deg)
                .collect::<Vec<_>>(),
        ),
        avg_peak_yaw_error_deg: safe_mean(
            &episodes
                .iter()
                .map(|episode| episode.peak_yaw_error_deg)
                .collect::<Vec<_>>(),
        ),
        stable_response_ratio: (!episodes.is_empty())
            .then_some(stable_responses as f64 / episodes.len() as f64),
    }
}

fn estimate_replay_bridge_base_ts_ms(
    shot_telemetry: &[BridgeShotTelemetryEvent],
    timeline: &[BridgeRunTimelinePoint],
) -> Option<u64> {
    let mut sorted_events = shot_telemetry.to_vec();
    sorted_events.sort_by_key(|event| event.ts_ms);
    if sorted_events.is_empty() {
        return None;
    }

    let estimate_offset_ms = |field: &str, total: u32| -> Option<u64> {
        let point = timeline.iter().find(|entry| {
            let value = match field {
                "shots_fired" => entry.shots_fired,
                "shots_hit" => entry.shots_hit,
                _ => None,
            };
            value.is_some_and(|value| value.is_finite() && value + 0.0001 >= total as f64)
        })?;
        Some(point.t_sec as u64 * 1000)
    };

    let mut cumulative_fired = 0u32;
    let mut cumulative_hit = 0u32;
    let mut bases = Vec::new();

    for event in sorted_events.iter().take(64) {
        let weight = event.count.unwrap_or(1).max(1);
        let offset_ms = if event.event == "shot_fired" {
            cumulative_fired = cumulative_fired.saturating_add(weight);
            estimate_offset_ms("shots_fired", event.total.unwrap_or(cumulative_fired))
        } else if event.event == "shot_hit" {
            cumulative_hit = cumulative_hit.saturating_add(weight);
            estimate_offset_ms("shots_hit", event.total.unwrap_or(cumulative_hit))
        } else {
            None
        };

        if let Some(offset_ms) = offset_ms {
            bases.push(event.ts_ms.saturating_sub(offset_ms));
            if bases.len() >= 12 {
                break;
            }
        }
    }

    if bases.is_empty() {
        return sorted_events.first().map(|event| event.ts_ms);
    }

    bases.sort_unstable();
    bases.get(bases.len() / 2).copied()
}

pub fn analyze_target_responses(replay: &ReplayData) -> Option<TargetResponseAnalysis> {
    let run_snapshot = replay.run_snapshot.as_ref()?;
    let replay_base_ts_ms = run_snapshot.started_at_bridge_ts_ms.or_else(|| {
        estimate_replay_bridge_base_ts_ms(&run_snapshot.shot_telemetry, &run_snapshot.timeline)
    })?;

    let motion_samples = build_cursor_motion_samples(&replay.positions);
    let mut frames = run_snapshot
        .tick_stream_v1
        .as_ref()
        .map(|tick_stream| build_replay_tick_frames(tick_stream, replay_base_ts_ms))
        .unwrap_or_default();
    if frames.len() < 6 && !run_snapshot.shot_telemetry.is_empty() {
        frames = build_shot_telemetry_frames(&run_snapshot.shot_telemetry, replay_base_ts_ms);
    }
    if motion_samples.len() < 12 || frames.len() < 6 {
        return None;
    }

    let mut episodes = Vec::<TargetResponseEpisode>::new();
    let mut last_trigger_ms = 0u64;
    let mut has_last_trigger = false;

    for index in 2..frames.len() {
        let previous2 = &frames[index - 2];
        let previous = &frames[index - 1];
        let current = &frames[index];

        if has_last_trigger && current.ts_ms.saturating_sub(last_trigger_ms) < 180 {
            continue;
        }

        let mut kind = None::<TargetResponseEpisodeKind>;
        let mut trigger_magnitude_deg = None::<f64>;
        let target_id = current.target.id.clone();
        let target_label = format_target_response_label(&current.target);

        if previous.target.id != current.target.id {
            kind = Some(TargetResponseEpisodeKind::TargetSwitch);
            let previous_heading = vector_heading_deg(
                previous.target.x - previous.player.x,
                previous.target.y - previous.player.y,
            );
            let current_heading = vector_heading_deg(
                current.target.x - current.player.x,
                current.target.y - current.player.y,
            );
            trigger_magnitude_deg = match (previous_heading, current_heading) {
                (Some(left), Some(right)) => Some(angle_diff_deg(left, right)),
                _ => Some(90.0),
            };
        } else if previous2.target.id == previous.target.id
            && previous.target.id == current.target.id
        {
            let prev_vector_x = previous.target.x - previous2.target.x;
            let prev_vector_y = previous.target.y - previous2.target.y;
            let next_vector_x = current.target.x - previous.target.x;
            let next_vector_y = current.target.y - previous.target.y;
            let prev_heading = vector_heading_deg(prev_vector_x, prev_vector_y);
            let next_heading = vector_heading_deg(next_vector_x, next_vector_y);
            let prev_distance = prev_vector_x.hypot(prev_vector_y);
            let next_distance = next_vector_x.hypot(next_vector_y);
            let prev_dt_ms = previous.ts_ms.saturating_sub(previous2.ts_ms).max(1);
            let next_dt_ms = current.ts_ms.saturating_sub(previous.ts_ms).max(1);
            let prev_speed = prev_distance / prev_dt_ms as f64 * 1000.0;
            let next_speed = next_distance / next_dt_ms as f64 * 1000.0;
            let speed_change_pct =
                (next_speed - prev_speed).abs() / prev_speed.max(next_speed).max(1.0);
            let heading_change_deg = match (prev_heading, next_heading) {
                (Some(left), Some(right)) => angle_diff_deg(left, right),
                _ => 0.0,
            };

            if (heading_change_deg >= 34.0 && (prev_distance >= 2.0 || next_distance >= 2.0))
                || (heading_change_deg >= 22.0 && speed_change_pct >= 0.4)
            {
                kind = Some(TargetResponseEpisodeKind::PathChange);
                trigger_magnitude_deg = Some(heading_change_deg);
            }
        }

        let Some(kind) = kind else {
            continue;
        };

        let baseline_window = motion_samples_in_range(
            &motion_samples,
            current.ts_ms.saturating_sub(140),
            current.ts_ms.saturating_sub(25),
        );
        let baseline_heading_deg = mean_heading_deg(&baseline_window);
        let baseline_speed = safe_mean(
            &baseline_window
                .iter()
                .map(|sample| Some(sample.speed))
                .collect::<Vec<_>>(),
        );
        let reaction_time_ms = find_reaction_time_ms(
            &motion_samples,
            current.ts_ms,
            baseline_heading_deg,
            baseline_speed,
            trigger_magnitude_deg,
        );
        let pre_slowdown_reaction_ms =
            find_pre_slowdown_reaction_ms(&motion_samples, current.ts_ms, baseline_speed);
        let recovery_time_ms = find_recovery_time_ms(
            &frames,
            index,
            &target_id,
            previous.aim_error_deg.or(current.aim_error_deg),
        );

        let episode_window_end =
            current.ts_ms + (recovery_time_ms.unwrap_or(0.0) as u64 + 220).max(600);
        let mut peak_yaw_error_deg = previous
            .aim_error_deg
            .or(current.aim_error_deg)
            .map(f64::abs);
        for frame in frames.iter().skip(index) {
            if frame.ts_ms < current.ts_ms {
                continue;
            }
            if frame.ts_ms > episode_window_end {
                break;
            }
            if frame.target.id != target_id {
                continue;
            }
            if let Some(aim_error) = frame.aim_error_deg.map(f64::abs) {
                peak_yaw_error_deg = Some(
                    peak_yaw_error_deg
                        .map(|current_peak| current_peak.max(aim_error))
                        .unwrap_or(aim_error),
                );
            }
        }

        episodes.push(TargetResponseEpisode {
            id: format!(
                "target-response-{}-{}-{}",
                kind.as_db_str(),
                current.ts_ms,
                target_id
            ),
            kind,
            start_ms: current.ts_ms,
            end_ms: current.ts_ms + (recovery_time_ms.unwrap_or(360.0) as u64).max(180),
            target_id,
            target_label,
            trigger_magnitude_deg,
            peak_yaw_error_deg,
            reaction_time_ms,
            pre_slowdown_reaction_ms,
            recovery_time_ms,
            stable_response: reaction_time_ms.is_some_and(|value| value <= 260.0)
                && recovery_time_ms.is_some_and(|value| value <= 460.0),
        });
        last_trigger_ms = current.ts_ms;
        has_last_trigger = true;
    }

    if episodes.is_empty() {
        return None;
    }

    let summary = summarize_target_response_episodes(&episodes);
    let response_coverage_pct = if !run_snapshot.shot_telemetry.is_empty() {
        Some(clamp_number(
            episodes.len() as f64 / run_snapshot.shot_telemetry.len().max(1) as f64 * 100.0,
            0.0,
            100.0,
        ))
    } else {
        Some(clamp_number(
            episodes.len() as f64 / frames.len().max(1) as f64 * 100.0,
            0.0,
            100.0,
        ))
    };

    Some(TargetResponseAnalysis {
        episode_count: episodes.len() as u32,
        response_coverage_pct,
        summary,
        episodes,
    })
}
