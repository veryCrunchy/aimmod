// ─── Mouse movement metrics ────────────────────────────────────────────────────

export interface MouseMetrics {
  /** 0–100 composite smoothness score */
  smoothness: number;
  /**
   * Lateral RMS ÷ mean speed — deviation perpendicular to the primary axis of
   * motion. Dimensionless and DPI-independent. 0 = perfectly on-axis.
   * Tracking scenarios (left-right) use the horizontal axis, so only vertical
   * wobble registers as jitter.
   */
  jitter: number;
  /**
   * Fraction of qualified axial segments where the motion reverses sharply
   * (without decelerating through zero). Lower is better.
   */
  overshoot_rate: number;
  /**
   * Coefficient of variation of speed (std ÷ mean). Dimensionless and
   * DPI-independent. Lower = more consistent speed.
   */
  velocity_std: number;
  /** Average speed normalised to an 800-DPI baseline (px/s ÷ dpi/800). */
  avg_speed: number;
  /**
   * Path straightness: straight-line displacement ÷ total path length,
   * averaged over sliding position windows. 1.0 = perfectly straight;
   * lower values mean the cursor curved/weaved toward the target.
   */
  path_efficiency: number;
  /** Inter-click interval coefficient of variation. 0 = perfect metronome. */
  click_timing_cv: number;
  /** Fraction of movement in Fitts' correction phase. Lower = more decisive. */
  correction_ratio: number;
  /** Systematic overshoot direction bias. 0 = balanced, 1 = always same side. */
  directional_bias: number;
  /** Average LMB hold duration (ms) in the last tick window. Near-zero = tapping; high = holding. */
  avg_hold_ms: number;
}

export interface MetricPoint {
  timestamp_ms: number;
  metrics: MouseMetrics;
}

/** A single downsampled cursor position sample recorded during a session. */
export interface RawPositionPoint {
  /** Integrated X in mouse-delta space (session starts at 0). */
  x: number;
  /** Integrated Y in mouse-delta space (session starts at 0). */
  y: number;
  /** Milliseconds since session start. */
  timestamp_ms: number;
  /** True when this sample coincides with a left-button click. */
  is_click: boolean;
}

/**
 * A replay JPEG frame captured from the game window during a session.
 * Resolution and framing depend on the user's replay settings. Used as an
 * underlay in the mouse-path replay viewer so the user can see where targets were
 * relative to their cursor movement.
 */
export interface ScreenFrame {
  /** Milliseconds since session start. */
  timestamp_ms: number;
  /** Standard base64-encoded JPEG (RFC 4648). Use as `data:image/jpeg;base64,${jpeg_b64}`. */
  jpeg_b64: string;
}

export interface BridgeRunEventCounts {
  shot_fired_events: number;
  shot_hit_events: number;
  kill_events: number;
  challenge_queued_events: number;
  challenge_start_events: number;
  challenge_end_events: number;
  challenge_complete_events: number;
  challenge_canceled_events: number;
}

export interface BridgeRunTimelinePoint {
  t_sec: number;
  score_per_minute: number | null;
  kills_per_second: number | null;
  accuracy_pct: number | null;
  damage_efficiency: number | null;
  score_total: number | null;
  score_total_derived: number | null;
  kills: number | null;
  shots_fired: number | null;
  shots_hit: number | null;
}

export interface BridgeShotTelemetryEntity {
  entity_id: string;
  profile: string;
  is_player: boolean;
  is_bot: boolean;
  x: number;
  y: number;
  z: number;
  pitch: number;
  yaw: number;
  roll: number;
  vx: number;
  vy: number;
  vz: number;
}

export interface BridgeShotTelemetryTarget extends BridgeShotTelemetryEntity {
  distance_2d: number | null;
  distance_3d: number | null;
  yaw_error_deg: number | null;
  pitch_error_deg: number | null;
  is_nearest: boolean;
}

export interface BridgeShotTelemetryEvent {
  event: string;
  ts_ms: number;
  count: number | null;
  total: number | null;
  run_id: number | null;
  sample_seq: number | null;
  sample_count: number | null;
  source: string | null;
  method: string | null;
  origin_flag: string | null;
  player: BridgeShotTelemetryEntity | null;
  targets: BridgeShotTelemetryTarget[];
}

export interface BridgeReplayContextWindow {
  window_idx: number;
  context_kind: string;
  label: string;
  phase: string | null;
  start_ms: number;
  end_ms: number;
  shot_event_count: number;
  fired_count: number;
  hit_count: number;
  accuracy_pct: number | null;
  avg_bot_count: number | null;
  primary_target_label: string | null;
  primary_target_profile: string | null;
  primary_target_entity_id: string | null;
  primary_target_share: number | null;
  avg_nearest_distance: number | null;
  avg_nearest_yaw_error_deg: number | null;
  avg_nearest_pitch_error_deg: number | null;
  avg_score_per_minute: number | null;
  avg_kills_per_second: number | null;
  avg_timeline_accuracy_pct: number | null;
  avg_damage_efficiency: number | null;
}

export interface BridgeRunSnapshot {
  duration_secs: number | null;
  score_total: number | null;
  score_total_derived: number | null;
  score_per_minute: number | null;
  shots_fired: number | null;
  shots_hit: number | null;
  kills: number | null;
  kills_per_second: number | null;
  damage_done: number | null;
  damage_possible: number | null;
  damage_efficiency: number | null;
  accuracy_pct: number | null;
  peak_score_per_minute: number | null;
  peak_kills_per_second: number | null;
  paired_shot_hits: number;
  avg_fire_to_hit_ms: number | null;
  p90_fire_to_hit_ms: number | null;
  avg_shots_to_hit: number | null;
  corrective_shot_ratio: number | null;
  started_at_bridge_ts_ms: number | null;
  started_at_unix_ms: number | null;
  ended_at_unix_ms: number | null;
  event_counts: BridgeRunEventCounts;
  timeline: BridgeRunTimelinePoint[];
  shot_telemetry?: BridgeShotTelemetryEvent[];
  tick_stream_v1?: BridgeTickStreamV1 | null;
}

export interface BridgeTickStreamV1 {
  sample_hz: number | null;
  keyframe_interval_ms: number | null;
  context: unknown | null;
  keyframes: unknown[];
  deltas: unknown[];
}

/** One continuous stretch of the replay timeline and where it sits in the video. */
export interface ReplayVideoSegment {
  /** Replay-timeline start, same ms domain as positions and frames. */
  replay_start_ms: number;
  /** Offset into the video file. */
  video_start_ms: number;
  duration_ms: number;
}

/** FTHR-recorded replay video (HEVC MP4 stored next to the replay). */
export interface ReplayVideo {
  file_name: string;
  /** Absolute file path; load it with `convertFileSrc`. */
  path: string;
  codec: string;
  width: number;
  height: number;
  segments: ReplayVideoSegment[];
}

export interface ReplayData {
  positions: RawPositionPoint[];
  metrics: MetricPoint[];
  frames?: ScreenFrame[];
  run_snapshot?: BridgeRunSnapshot | null;
  video?: ReplayVideo | null;
}

export interface ReplayPayloadData {
  positions: RawPositionPoint[];
  metrics: MetricPoint[];
  frames?: ScreenFrame[];
  video?: ReplayVideo | null;
}
