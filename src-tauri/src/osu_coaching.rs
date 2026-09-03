use lzma_rs::{decompress::Options as LzmaOptions, lzma_decompress_with_options};
use serde::Serialize;
use serde_json::Value;
use std::cmp::Ordering;
use std::fs::File;
use std::io::{BufReader, Cursor, Read};
use std::path::Path;

use crate::osu_replay_analytics::{
    OsuExactObjectJudgement, OsuExactReplayJudgements, reconstruct_exact_judgements,
};

const MAX_REPLAY_FILE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_COMPRESSED_SECTION_BYTES: usize = 128 * 1024 * 1024;
const MAX_DECOMPRESSED_SECTION_BYTES: usize = 64 * 1024 * 1024;
const MAX_REPLAY_STRING_BYTES: usize = 1024 * 1024;
const MAX_FRAME_COUNT: usize = 2_000_000;
const SEGMENT_COUNT: usize = 4;

const MOD_RELAX: u32 = 1 << 7;
const MOD_AUTOPLAY: u32 = 1 << 11;
const MOD_AUTOPILOT: u32 = 1 << 13;
const MOD_CINEMA: u32 = 1 << 22;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum OsuCoachingConfidence {
    High,
    Medium,
    Low,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuCoachingMetric {
    pub id: String,
    pub label: String,
    pub value: f64,
    pub unit: String,
    pub confidence: OsuCoachingConfidence,
    pub evidence: String,
    pub limitation: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuUnavailableMetric {
    pub id: String,
    pub label: String,
    pub confidence: OsuCoachingConfidence,
    pub reason: String,
    pub required_data: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplaySegmentMetrics {
    pub index: usize,
    pub label: String,
    pub start_ms: f64,
    pub end_ms: f64,
    pub cursor_distance: f64,
    pub cursor_travel_rate: f64,
    pub press_count: usize,
    pub press_rate: f64,
    pub median_press_interval_ms: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuCoachingInsight {
    pub id: String,
    pub category: String,
    pub title: String,
    pub summary: String,
    pub confidence: OsuCoachingConfidence,
    pub metric_ids: Vec<String>,
    pub start_ms: Option<f64>,
    pub end_ms: Option<f64>,
    pub next_step: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayScoreEvidence {
    pub count_300: u16,
    pub count_100: u16,
    pub count_50: u16,
    pub count_miss: u16,
    pub max_combo: u16,
    pub perfect: bool,
    pub large_tick_miss_count: Option<u32>,
    pub slider_tail_hit_count: Option<u32>,
    pub maximum_slider_tail_count: Option<u32>,
    pub pause_count: Option<usize>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayEvidence {
    pub path: String,
    pub game_version: u32,
    pub beatmap_hash: String,
    pub player_name: String,
    pub replay_hash: String,
    pub mod_bitmask: u32,
    pub mods: Vec<String>,
    pub frame_count: usize,
    pub excluded_position_frame_count: usize,
    pub has_lazer_score_info: bool,
    pub official_judgement_engine: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayCoachingAnalysis {
    pub schema_version: u32,
    pub source: OsuReplayEvidence,
    pub score: OsuReplayScoreEvidence,
    pub metrics: Vec<OsuCoachingMetric>,
    pub segments: Vec<OsuReplaySegmentMetrics>,
    pub insights: Vec<OsuCoachingInsight>,
    pub unavailable_metrics: Vec<OsuUnavailableMetric>,
    pub limitations: Vec<String>,
}

#[derive(Debug, Clone, PartialEq)]
struct ReplayHeader {
    game_version: u32,
    beatmap_hash: String,
    player_name: String,
    replay_hash: String,
    count_300: u16,
    count_100: u16,
    count_50: u16,
    count_miss: u16,
    max_combo: u16,
    perfect: bool,
    mod_bitmask: u32,
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct ReplayFrameSample {
    time_ms: f64,
    x: f64,
    y: f64,
    buttons: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PressChannel {
    Left,
    Right,
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct PressEvent {
    time_ms: f64,
    channel: PressChannel,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
struct EmbeddedScoreInfo {
    large_tick_miss_count: Option<u32>,
    slider_tail_hit_count: Option<u32>,
    maximum_slider_tail_count: Option<u32>,
    pause_count: Option<usize>,
    mods: Vec<String>,
}

pub fn analyze_replay_file(path: &str) -> Result<OsuReplayCoachingAnalysis, String> {
    let path = Path::new(path);
    let metadata = path
        .metadata()
        .map_err(|error| format!("Could not inspect the replay: {error}"))?;
    if !metadata.is_file() {
        return Err("The selected replay path is not a file.".to_string());
    }
    if metadata.len() > MAX_REPLAY_FILE_BYTES {
        return Err(format!(
            "The replay is larger than AimMod's {} MiB analysis limit.",
            MAX_REPLAY_FILE_BYTES / 1024 / 1024
        ));
    }

    let file = File::open(path).map_err(|error| format!("Could not read the replay: {error}"))?;
    let mut reader = BufReader::new(file);
    let header = read_replay_header(&mut reader)?;
    let compressed_replay = read_byte_array(&mut reader, "replay frames")?;
    let _legacy_online_id = read_i64(&mut reader)?;
    let compressed_score_info = if header.game_version >= 30_000_001 {
        Some(read_byte_array(&mut reader, "lazer score metadata")?)
    } else {
        None
    };

    let replay_text = decompress_text(&compressed_replay, "replay frames")?;
    let frames = parse_replay_frames(&replay_text)?;
    if frames.len() < 2 {
        return Err(
            "The replay does not contain enough standard-mode frames to analyse.".to_string(),
        );
    }

    let mut limitations = Vec::new();
    let embedded = match compressed_score_info.as_deref() {
        Some(bytes) if !bytes.is_empty() => match parse_embedded_score_info(bytes) {
            Ok(info) => Some(info),
            Err(error) => {
                limitations.push(format!(
                    "The optional lazer score metadata could not be decoded: {error}"
                ));
                None
            }
        },
        _ => None,
    };

    let mut mods = embedded
        .as_ref()
        .map(|info| info.mods.clone())
        .filter(|mods| !mods.is_empty())
        .unwrap_or_else(|| legacy_mod_names(header.mod_bitmask));
    mods.sort();
    mods.dedup();

    let beatmap_hash = header.beatmap_hash.clone();
    let mut analysis = analyze_frames(
        path.to_string_lossy().into_owned(),
        header,
        frames,
        embedded,
        mods,
    );
    analysis.limitations.splice(0..0, limitations);
    match reconstruct_exact_judgements(path, &beatmap_hash) {
        Ok(exact) => apply_exact_judgements(&mut analysis, &exact),
        Err(error) => analysis.limitations.push(format!(
            "Exact per-object results were unavailable: {error}"
        )),
    }
    Ok(analysis)
}

fn analyze_frames(
    path: String,
    header: ReplayHeader,
    frames: Vec<ReplayFrameSample>,
    embedded: Option<EmbeddedScoreInfo>,
    mods: Vec<String>,
) -> OsuReplayCoachingAnalysis {
    let valid_frames: Vec<ReplayFrameSample> = frames
        .iter()
        .copied()
        .filter(|frame| is_analysis_position(frame.x, frame.y))
        .collect();
    let excluded_position_frame_count = frames.len().saturating_sub(valid_frames.len());
    let first_time = frames.first().map(|frame| frame.time_ms).unwrap_or(0.0);
    let last_time = frames
        .last()
        .map(|frame| frame.time_ms)
        .unwrap_or(first_time);
    let duration_ms = (last_time - first_time).max(0.0);
    let duration_seconds = duration_ms / 1000.0;

    let (cursor_distance, moving_speeds) = movement_samples(&valid_frames);
    let cursor_travel_rate = divide(cursor_distance, duration_seconds).unwrap_or(0.0);
    let presses = collect_press_events(&frames);
    let press_rate = divide(presses.len() as f64, duration_seconds).unwrap_or(0.0);
    let press_intervals = positive_press_intervals(&presses);
    let alternating_share = alternating_channel_share(&presses);
    let segments = build_segments(&valid_frames, &presses, first_time, last_time);

    let embedded_ref = embedded.as_ref();
    let score = OsuReplayScoreEvidence {
        count_300: header.count_300,
        count_100: header.count_100,
        count_50: header.count_50,
        count_miss: header.count_miss,
        max_combo: header.max_combo,
        perfect: header.perfect,
        large_tick_miss_count: embedded_ref.and_then(|info| info.large_tick_miss_count),
        slider_tail_hit_count: embedded_ref.and_then(|info| info.slider_tail_hit_count),
        maximum_slider_tail_count: embedded_ref.and_then(|info| info.maximum_slider_tail_count),
        pause_count: embedded_ref.and_then(|info| info.pause_count),
    };

    let mut metrics = vec![
        metric(
            "duration",
            "Replay duration",
            duration_seconds,
            "seconds",
            OsuCoachingConfidence::High,
            "Difference between the first and last accepted replay-frame timestamps.",
            None,
        ),
        metric(
            "cursorPathDistance",
            "Recorded cursor path",
            cursor_distance,
            "playfield units",
            OsuCoachingConfidence::Medium,
            "Sum of distances between recorded cursor samples with forward-moving timestamps.",
            Some(
                "Sampling frequency and cursor motion outside recorded samples affect this value; it is not aim accuracy.",
            ),
        ),
        metric(
            "cursorTravelRate",
            "Cursor travel rate",
            cursor_travel_rate,
            "playfield units/s",
            OsuCoachingConfidence::Medium,
            "Recorded cursor path divided by replay duration.",
            Some("Map geometry, breaks, spinners, and replay sampling all affect this rate."),
        ),
        metric(
            "pressCount",
            "Recorded press transitions",
            presses.len() as f64,
            "presses",
            OsuCoachingConfidence::High,
            "Rising edges of the effective left and right osu! input channels.",
            Some("A press transition is an input event, not proof that a hit object was judged."),
        ),
        metric(
            "pressRate",
            "Recorded press rate",
            press_rate,
            "presses/s",
            OsuCoachingConfidence::High,
            "Recorded press transitions divided by replay duration.",
            Some("Breaks, sliders, spinners, and map rhythm affect this value."),
        ),
        metric(
            "missCount",
            "Object misses",
            f64::from(header.count_miss),
            "misses",
            OsuCoachingConfidence::High,
            "Aggregate miss count stored in the replay score header.",
            Some("The score header does not include miss timestamps or object identities."),
        ),
    ];

    if let Some(value) = percentile(&moving_speeds, 0.95) {
        metrics.push(metric(
            "movingSpeedP95",
            "95th-percentile recorded movement speed",
            value,
            "playfield units/s",
            OsuCoachingConfidence::Medium,
            "95th percentile of non-zero cursor-sample speeds.",
            Some("This is a replay-sample measurement, not target acquisition speed."),
        ));
    }
    if let Some(value) = percentile(&press_intervals, 0.5) {
        metrics.push(metric(
            "medianPressInterval",
            "Median press interval",
            value,
            "ms",
            OsuCoachingConfidence::High,
            "Median positive interval between recorded press transitions.",
            Some("Without beatmap object times, this cannot measure hit error or unstable rate."),
        ));
    }
    if press_intervals.len() >= 4 {
        if let (Some(q1), Some(q3)) = (
            percentile(&press_intervals, 0.25),
            percentile(&press_intervals, 0.75),
        ) {
            metrics.push(metric(
                "pressIntervalIqr",
                "Press interval IQR",
                q3 - q1,
                "ms",
                OsuCoachingConfidence::High,
                "Interquartile range of positive intervals between recorded press transitions.",
                Some("Rhythm changes in the beatmap can cause interval variation; this is not a timing-consistency grade."),
            ));
        }
    }
    if let Some(value) = alternating_share {
        metrics.push(metric(
            "alternatingChannelShare",
            "Alternating-channel share",
            value * 100.0,
            "%",
            OsuCoachingConfidence::High,
            "Share of consecutive, non-simultaneous press transitions that changed effective input channel.",
            Some("This describes input style only; it does not grade tapping technique."),
        ));
    }
    if let Some(value) = score.large_tick_miss_count {
        metrics.push(metric(
            "largeTickMissCount",
            "Large slider tick misses",
            f64::from(value),
            "judgements",
            OsuCoachingConfidence::High,
            "LargeTickMiss count from lazer's embedded score statistics.",
            Some("This is not a canonical slider-break count and has no timestamps in the replay metadata."),
        ));
    }
    if let (Some(hit), Some(maximum)) =
        (score.slider_tail_hit_count, score.maximum_slider_tail_count)
    {
        metrics.push(metric(
            "sliderTailHitCount",
            "Slider tails hit",
            f64::from(hit),
            format!("of {maximum}"),
            OsuCoachingConfidence::High,
            "SliderTailHit and maximum SliderTailHit counts from lazer's embedded score statistics.",
            Some("A missing tail does not by itself identify the cause or timestamp of a combo break."),
        ));
    }

    let mut insights = build_insights(&score, &segments, header.mod_bitmask);
    insights.truncate(4);

    let mut limitations = vec![
        "Confidence describes how directly a value is derived from the replay, not confidence that it represents player skill.".to_string(),
        "Replay frames contain cursor positions and button states, but no hit-object association or judgement timestamp.".to_string(),
        "Quarter segments are equal replay-time windows. They are not beatmap strain sections.".to_string(),
    ];
    if excluded_position_frame_count > 0 {
        limitations.push(format!(
            "{excluded_position_frame_count} cursor frames outside AimMod's bounded analysis area were excluded from movement metrics."
        ));
    }
    if header.mod_bitmask & (MOD_RELAX | MOD_AUTOPLAY | MOD_AUTOPILOT | MOD_CINEMA) != 0 {
        limitations.push(
            "Relax, Autoplay, Autopilot, or Cinema changes which replay inputs are player-controlled; affected metrics must not be used as player coaching evidence."
                .to_string(),
        );
    }
    if header.mod_bitmask & ((1 << 6) | (1 << 8) | (1 << 9)) != 0 {
        limitations.push(
            "Rate-changing mods alter replay timing. Cross-replay timing comparisons require gameplay-rate normalization."
                .to_string(),
        );
    }

    OsuReplayCoachingAnalysis {
        schema_version: 1,
        source: OsuReplayEvidence {
            path,
            game_version: header.game_version,
            beatmap_hash: header.beatmap_hash,
            player_name: header.player_name,
            replay_hash: header.replay_hash,
            mod_bitmask: header.mod_bitmask,
            mods,
            frame_count: frames.len(),
            excluded_position_frame_count,
            has_lazer_score_info: embedded.is_some(),
            official_judgement_engine: None,
        },
        score,
        metrics,
        segments,
        insights,
        unavailable_metrics: unavailable_metrics(),
        limitations,
    }
}

fn apply_exact_judgements(
    analysis: &mut OsuReplayCoachingAnalysis,
    exact: &OsuExactReplayJudgements,
) {
    analysis.source.official_judgement_engine = Some(exact.engine_version.clone());
    analysis
        .insights
        .retain(|insight| insight.id != "aggregateMisses" && insight.id != "largeTickMisses");
    analysis.unavailable_metrics.retain(|metric| {
        !matches!(
            metric.id.as_str(),
            "averageHitError" | "missTimeline" | "sliderBreakCount" | "objectRelativeAim"
        )
    });
    analysis.limitations.retain(|limitation| {
        !limitation.contains("no hit-object association or judgement timestamp")
    });

    let primary_hits: Vec<&OsuExactObjectJudgement> = exact
        .judgements
        .iter()
        .filter(|judgement| {
            judgement.cursor_position.is_some()
                && matches!(judgement.result.as_str(), "Great" | "Ok" | "Meh")
        })
        .collect();
    let offsets: Vec<f64> = primary_hits
        .iter()
        .map(|judgement| judgement.time_offset_ms)
        .filter(|offset| offset.is_finite())
        .collect();
    if let (Some(median_offset), Some(q1), Some(q3)) = (
        percentile(&offsets, 0.5),
        percentile(&offsets, 0.25),
        percentile(&offsets, 0.75),
    ) {
        analysis.metrics.push(metric(
            "medianHitOffset",
            "Median judged hit offset",
            median_offset,
            "ms",
            OsuCoachingConfidence::High,
            "Median official osu! ruleset time offset for judged positional hit objects.",
            Some("Negative is early and positive is late. This describes the selected play, not a diagnosis."),
        ));
        analysis.metrics.push(metric(
            "hitOffsetIqr",
            "Judged hit offset IQR",
            q3 - q1,
            "ms",
            OsuCoachingConfidence::High,
            "Middle-half spread of official osu! ruleset hit offsets for judged positional objects.",
            Some("Compare repeated plays of the same map and mod setup before treating a change as improvement."),
        ));

        let early = offsets.iter().filter(|offset| **offset < -5.0).count();
        let late = offsets.iter().filter(|offset| **offset > 5.0).count();
        let centred = offsets.len().saturating_sub(early + late);
        let tendency = if median_offset < -5.0 {
            "early"
        } else if median_offset > 5.0 {
            "late"
        } else {
            "centred"
        };
        analysis.insights.push(OsuCoachingInsight {
            id: "timingBalance".to_string(),
            category: "timing".to_string(),
            title: format!("Hit timing was {tendency} overall"),
            summary: format!(
                "Median offset was {median_offset:+.1} ms; {early} hits were early, {centred} within 5 ms, and {late} late."
            ),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec!["medianHitOffset".to_string(), "hitOffsetIqr".to_string()],
            start_ms: None,
            end_ms: None,
            next_step: if tendency == "centred" {
                "Keep the same timing approach and compare this spread on another play of the same setup."
            } else {
                "Replay the hardest rhythm section and listen for whether the same offset direction repeats before changing your tapping cue."
            }
            .to_string(),
        });
    }

    let aim_errors: Vec<f64> = primary_hits
        .iter()
        .filter_map(|judgement| {
            let object = judgement.object_position?;
            let cursor = judgement.cursor_position?;
            Some(((cursor.x - object.x).powi(2) + (cursor.y - object.y).powi(2)).sqrt())
        })
        .filter(|distance| distance.is_finite())
        .collect();
    if let Some(median_error) = percentile(&aim_errors, 0.5) {
        analysis.metrics.push(metric(
            "medianCursorErrorAtHit",
            "Median cursor error at hit",
            median_error,
            "playfield units",
            OsuCoachingConfidence::High,
            "Median target-to-cursor distance reported by official positional judgement results.",
            Some("Only judgement types that expose cursor-at-hit position are included."),
        ));
    }

    let first_miss = exact
        .judgements
        .iter()
        .find(|judgement| judgement.result == "Miss" && judgement.object_index.is_some());
    if let Some(miss) = first_miss {
        let object_number = miss.object_index.unwrap_or_default() + 1;
        analysis.insights.insert(0, OsuCoachingInsight {
            id: "exactMiss".to_string(),
            category: "accuracy".to_string(),
            title: format!("Review miss on object {object_number}"),
            summary: format!(
                "{} was missed at {:.2} seconds.",
                readable_object_type(&miss.object_type),
                miss.judgement_time_ms / 1000.0
            ),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec!["missCount".to_string()],
            start_ms: Some((miss.judgement_time_ms - 750.0).max(0.0)),
            end_ms: Some(miss.judgement_time_ms + 750.0),
            next_step: "Replay this window and compare the cursor path with the object position before choosing an aim or reading correction.".to_string(),
        });
    }

    if let Some(slider_break) = exact.judgements.iter().find(|judgement| {
        matches!(
            judgement.result.as_str(),
            "LargeTickMiss" | "SmallTickMiss" | "SliderTailMiss"
        )
    }) {
        analysis.insights.insert(
            usize::from(first_miss.is_some()),
            OsuCoachingInsight {
                id: "exactSliderBreak".to_string(),
                category: "sliders".to_string(),
                title: "Review the first slider break".to_string(),
                summary: format!(
                    "{} occurred at {:.2} seconds{}.",
                    readable_result(&slider_break.result),
                    slider_break.judgement_time_ms / 1000.0,
                    slider_break
                        .object_index
                        .map(|index| format!(" on object {}", index + 1))
                        .unwrap_or_default()
                ),
                confidence: OsuCoachingConfidence::High,
                metric_ids: vec!["largeTickMissCount".to_string()],
                start_ms: Some((slider_break.judgement_time_ms - 750.0).max(0.0)),
                end_ms: Some(slider_break.judgement_time_ms + 750.0),
                next_step: "Inspect the cursor and held input through this slider, including its nested tick or tail, before retrying it.".to_string(),
            },
        );
    }
    analysis.insights.truncate(4);
}

fn readable_object_type(value: &str) -> &'static str {
    match value {
        "HitCircle" => "A hit circle",
        "SliderHeadCircle" => "A slider head",
        "SliderTailCircle" => "A slider tail",
        "SliderTick" => "A slider tick",
        "Spinner" => "A spinner",
        _ => "The object",
    }
}

fn readable_result(value: &str) -> &'static str {
    match value {
        "LargeTickMiss" => "A missed large slider tick",
        "SmallTickMiss" => "A missed small slider tick",
        "SliderTailMiss" => "A missed slider tail",
        _ => "A slider break",
    }
}

fn build_insights(
    score: &OsuReplayScoreEvidence,
    segments: &[OsuReplaySegmentMetrics],
    mod_bitmask: u32,
) -> Vec<OsuCoachingInsight> {
    let mut insights = Vec::new();

    if mod_bitmask & (MOD_AUTOPLAY | MOD_CINEMA) != 0 {
        insights.push(OsuCoachingInsight {
            id: "nonPlayerReplay".to_string(),
            category: "context".to_string(),
            title: "Player coaching is disabled for this replay".to_string(),
            summary: "Autoplay or Cinema is active, so cursor and tapping traces are not evidence of player execution.".to_string(),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec![],
            start_ms: None,
            end_ms: None,
            next_step: "Choose a user-played replay for input coaching.".to_string(),
        });
        return insights;
    }

    if score.count_miss > 0 {
        insights.push(OsuCoachingInsight {
            id: "aggregateMisses".to_string(),
            category: "accuracy".to_string(),
            title: "Misses are present in the score".to_string(),
            summary: format!(
                "The replay header records {} object miss{}.",
                score.count_miss,
                if score.count_miss == 1 { "" } else { "es" }
            ),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec!["missCount".to_string()],
            start_ms: None,
            end_ms: None,
            next_step: "AimMod needs the exact beatmap and ruleset judgement pass before it can identify which patterns caused them.".to_string(),
        });
    }

    if let Some(count) = score.large_tick_miss_count.filter(|count| *count > 0) {
        insights.push(OsuCoachingInsight {
            id: "largeTickMisses".to_string(),
            category: "sliders".to_string(),
            title: "Large slider tick misses are present".to_string(),
            summary: format!(
                "Lazer's embedded score statistics record {count} LargeTickMiss judgement{}.",
                if count == 1 { "" } else { "s" }
            ),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec!["largeTickMissCount".to_string()],
            start_ms: None,
            end_ms: None,
            next_step: "Review slider-follow sections after beatmap alignment is available; the aggregate count has no event timestamps.".to_string(),
        });
    }

    if let (Some(early), Some(late)) = (segments.first(), segments.last()) {
        let press_change = relative_change(early.press_rate, late.press_rate);
        let travel_change = relative_change(early.cursor_travel_rate, late.cursor_travel_rate);
        let enough_input = early.press_count >= 10 && late.press_count >= 10;
        let changed = press_change
            .map(|change| change.abs() >= 0.20)
            .unwrap_or(false)
            || travel_change
                .map(|change| change.abs() >= 0.20)
                .unwrap_or(false);
        if enough_input && changed {
            let press_copy = press_change
                .map(|change| format!("press rate {:+.0}%", change * 100.0))
                .unwrap_or_else(|| "press rate unavailable".to_string());
            let travel_copy = travel_change
                .map(|change| format!("cursor travel rate {:+.0}%", change * 100.0))
                .unwrap_or_else(|| "cursor travel rate unavailable".to_string());
            insights.push(OsuCoachingInsight {
                id: "lateInputChange".to_string(),
                category: "consistency".to_string(),
                title: "Late replay input changed".to_string(),
                summary: format!(
                    "Compared with the opening quarter, the final quarter changed in {press_copy} and {travel_copy}."
                ),
                confidence: OsuCoachingConfidence::Low,
                metric_ids: vec!["pressRate".to_string(), "cursorTravelRate".to_string()],
                start_ms: Some(late.start_ms),
                end_ms: Some(late.end_ms),
                next_step: "Check the beatmap pattern in this window before attributing the change to strain, consistency, or fatigue.".to_string(),
            });
        }
    }

    if mod_bitmask & MOD_RELAX != 0 {
        insights.push(OsuCoachingInsight {
            id: "relaxContext".to_string(),
            category: "context".to_string(),
            title: "Tapping coaching is unavailable with Relax".to_string(),
            summary: "Relax removes timed player tapping, so recorded press metrics cannot represent tapping execution.".to_string(),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec!["pressCount".to_string(), "pressRate".to_string()],
            start_ms: None,
            end_ms: None,
            next_step: "Use a replay without Relax for tapping analysis.".to_string(),
        });
    }
    if mod_bitmask & MOD_AUTOPILOT != 0 {
        insights.push(OsuCoachingInsight {
            id: "autopilotContext".to_string(),
            category: "context".to_string(),
            title: "Aim coaching is unavailable with Autopilot".to_string(),
            summary: "Autopilot controls cursor movement, so the cursor trace cannot represent player aim execution.".to_string(),
            confidence: OsuCoachingConfidence::High,
            metric_ids: vec!["cursorPathDistance".to_string(), "cursorTravelRate".to_string()],
            start_ms: None,
            end_ms: None,
            next_step: "Use a replay without Autopilot for cursor-path analysis.".to_string(),
        });
    }

    insights
}

fn unavailable_metrics() -> Vec<OsuUnavailableMetric> {
    vec![
        unavailable(
            "unstableRate",
            "Unstable rate",
            "Replay frames do not store judged hit offsets. Keypress intervals are not hit errors.",
            "The exact beatmap plus lazer's ruleset/mod judgement processing, or persisted per-object hit events.",
        ),
        unavailable(
            "averageHitError",
            "Average hit error",
            "Replay frames do not associate presses with hit objects or their judgement times.",
            "Per-object judged hit events produced against the exact beatmap and gameplay rate.",
        ),
        unavailable(
            "missTimeline",
            "Miss timeline",
            "The replay score header stores only an aggregate miss count.",
            "A replay judgement pass against the exact beatmap and mods.",
        ),
        unavailable(
            "sliderBreakCount",
            "Slider-break count and timeline",
            "The header has no canonical slider-break field. Embedded tick/tail statistics do not identify every combo-break cause or timestamp.",
            "Per-object slider judgements from the exact beatmap and ruleset processing.",
        ),
        unavailable(
            "objectRelativeAim",
            "Object-relative aim error",
            "Cursor samples do not contain target positions or judgement associations.",
            "Beatmap hit-object geometry aligned with replay time and ruleset results.",
        ),
        unavailable(
            "strainAndFatigue",
            "Strain and fatigue attribution",
            "Late-run input changes can be caused by map patterns, breaks, spinners, mods, or player state.",
            "Beatmap difficulty/strain sections plus repeated-play baselines; fatigue still remains an inference.",
        ),
    ]
}

fn unavailable(id: &str, label: &str, reason: &str, required_data: &str) -> OsuUnavailableMetric {
    OsuUnavailableMetric {
        id: id.to_string(),
        label: label.to_string(),
        confidence: OsuCoachingConfidence::Unavailable,
        reason: reason.to_string(),
        required_data: required_data.to_string(),
    }
}

fn metric(
    id: &str,
    label: &str,
    value: f64,
    unit: impl Into<String>,
    confidence: OsuCoachingConfidence,
    evidence: &str,
    limitation: Option<&str>,
) -> OsuCoachingMetric {
    OsuCoachingMetric {
        id: id.to_string(),
        label: label.to_string(),
        value,
        unit: unit.into(),
        confidence,
        evidence: evidence.to_string(),
        limitation: limitation.map(str::to_string),
    }
}

fn movement_samples(frames: &[ReplayFrameSample]) -> (f64, Vec<f64>) {
    let mut distance = 0.0;
    let mut speeds = Vec::new();
    for pair in frames.windows(2) {
        let dt_ms = pair[1].time_ms - pair[0].time_ms;
        if dt_ms <= 0.0 {
            continue;
        }
        let dx = pair[1].x - pair[0].x;
        let dy = pair[1].y - pair[0].y;
        let sample_distance = dx.hypot(dy);
        if !sample_distance.is_finite() {
            continue;
        }
        distance += sample_distance;
        if sample_distance > 0.0 {
            speeds.push(sample_distance / (dt_ms / 1000.0));
        }
    }
    (distance, speeds)
}

fn collect_press_events(frames: &[ReplayFrameSample]) -> Vec<PressEvent> {
    let mut events = Vec::new();
    let mut previous_left = false;
    let mut previous_right = false;
    for frame in frames {
        let left = frame.buttons & (1 | 4) != 0;
        let right = frame.buttons & (2 | 8) != 0;
        if left && !previous_left {
            events.push(PressEvent {
                time_ms: frame.time_ms,
                channel: PressChannel::Left,
            });
        }
        if right && !previous_right {
            events.push(PressEvent {
                time_ms: frame.time_ms,
                channel: PressChannel::Right,
            });
        }
        previous_left = left;
        previous_right = right;
    }
    events.sort_by(|a, b| a.time_ms.partial_cmp(&b.time_ms).unwrap_or(Ordering::Equal));
    events
}

fn positive_press_intervals(presses: &[PressEvent]) -> Vec<f64> {
    presses
        .windows(2)
        .filter_map(|pair| {
            let delta = pair[1].time_ms - pair[0].time_ms;
            (delta > 0.0 && delta.is_finite()).then_some(delta)
        })
        .collect()
}

fn alternating_channel_share(presses: &[PressEvent]) -> Option<f64> {
    let mut comparable = 0_u32;
    let mut alternating = 0_u32;
    for pair in presses.windows(2) {
        if pair[1].time_ms <= pair[0].time_ms {
            continue;
        }
        comparable += 1;
        if pair[0].channel != pair[1].channel {
            alternating += 1;
        }
    }
    (comparable > 0).then_some(f64::from(alternating) / f64::from(comparable))
}

fn build_segments(
    frames: &[ReplayFrameSample],
    presses: &[PressEvent],
    first_time: f64,
    last_time: f64,
) -> Vec<OsuReplaySegmentMetrics> {
    let duration = (last_time - first_time).max(0.0);
    if duration <= 0.0 {
        return Vec::new();
    }
    let segment_duration = duration / SEGMENT_COUNT as f64;
    (0..SEGMENT_COUNT)
        .map(|index| {
            let start = first_time + segment_duration * index as f64;
            let end = if index + 1 == SEGMENT_COUNT {
                last_time
            } else {
                start + segment_duration
            };
            let cursor_distance = frames
                .windows(2)
                .filter(|pair| pair[1].time_ms > start && pair[1].time_ms <= end)
                .filter_map(|pair| {
                    let dx = pair[1].x - pair[0].x;
                    let dy = pair[1].y - pair[0].y;
                    let distance = dx.hypot(dy);
                    distance.is_finite().then_some(distance)
                })
                .sum::<f64>();
            let segment_presses: Vec<PressEvent> = presses
                .iter()
                .copied()
                .filter(|press| press.time_ms > start && press.time_ms <= end)
                .collect();
            let seconds = (end - start) / 1000.0;
            OsuReplaySegmentMetrics {
                index,
                label: format!("Quarter {}", index + 1),
                start_ms: start,
                end_ms: end,
                cursor_distance,
                cursor_travel_rate: divide(cursor_distance, seconds).unwrap_or(0.0),
                press_count: segment_presses.len(),
                press_rate: divide(segment_presses.len() as f64, seconds).unwrap_or(0.0),
                median_press_interval_ms: percentile(
                    &positive_press_intervals(&segment_presses),
                    0.5,
                ),
            }
        })
        .collect()
}

fn percentile(values: &[f64], percentile: f64) -> Option<f64> {
    if values.is_empty() {
        return None;
    }
    let mut sorted: Vec<f64> = values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .collect();
    if sorted.is_empty() {
        return None;
    }
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(Ordering::Equal));
    let rank = percentile.clamp(0.0, 1.0) * (sorted.len() - 1) as f64;
    let lower = rank.floor() as usize;
    let upper = rank.ceil() as usize;
    if lower == upper {
        return Some(sorted[lower]);
    }
    let weight = rank - lower as f64;
    Some(sorted[lower] * (1.0 - weight) + sorted[upper] * weight)
}

fn divide(numerator: f64, denominator: f64) -> Option<f64> {
    (denominator > 0.0).then_some(numerator / denominator)
}

fn relative_change(early: f64, late: f64) -> Option<f64> {
    (early > 0.0).then_some((late - early) / early)
}

fn is_analysis_position(x: f64, y: f64) -> bool {
    x.is_finite() && y.is_finite() && x.abs() <= 4096.0 && y.abs() <= 4096.0
}

fn parse_embedded_score_info(bytes: &[u8]) -> Result<EmbeddedScoreInfo, String> {
    let json = decompress_text(bytes, "lazer score metadata")?;
    let value: Value = serde_json::from_str(&json)
        .map_err(|error| format!("score metadata is not valid JSON: {error}"))?;
    let statistics = value.get("statistics").and_then(Value::as_object);
    let maximum_statistics = value.get("maximum_statistics").and_then(Value::as_object);
    let mods = value
        .get("mods")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|item| item.get("acronym").and_then(Value::as_str))
        .map(str::to_string)
        .collect();
    Ok(EmbeddedScoreInfo {
        large_tick_miss_count: statistics.and_then(|map| statistic_value(map, "LargeTickMiss")),
        slider_tail_hit_count: statistics.and_then(|map| statistic_value(map, "SliderTailHit")),
        maximum_slider_tail_count: maximum_statistics
            .and_then(|map| statistic_value(map, "SliderTailHit")),
        pause_count: value.get("pauses").and_then(Value::as_array).map(Vec::len),
        mods,
    })
}

fn statistic_value(map: &serde_json::Map<String, Value>, name: &str) -> Option<u32> {
    map.iter().find_map(|(key, value)| {
        let normalized: String = key.chars().filter(|char| char.is_alphanumeric()).collect();
        normalized
            .eq_ignore_ascii_case(name)
            .then(|| value.as_u64().and_then(|number| u32::try_from(number).ok()))
            .flatten()
    })
}

fn decompress_text(bytes: &[u8], section: &str) -> Result<String, String> {
    if bytes.len() < 13 {
        return Err(format!("The compressed {section} section is truncated."));
    }
    let declared_size = u64::from_le_bytes(
        bytes[5..13]
            .try_into()
            .map_err(|_| format!("The compressed {section} has no output size."))?,
    );
    if declared_size == u64::MAX || declared_size > MAX_DECOMPRESSED_SECTION_BYTES as u64 {
        return Err(format!(
            "The decompressed {section} exceeds AimMod's {} MiB limit.",
            MAX_DECOMPRESSED_SECTION_BYTES / 1024 / 1024
        ));
    }
    let mut output = Vec::with_capacity(declared_size as usize);
    let mut input = Cursor::new(bytes);
    let options = LzmaOptions {
        memlimit: Some(MAX_DECOMPRESSED_SECTION_BYTES),
        ..LzmaOptions::default()
    };
    lzma_decompress_with_options(&mut input, &mut output, &options)
        .map_err(|error| format!("Could not decompress {section}: {error}"))?;
    if output.len() != declared_size as usize {
        return Err(format!(
            "The decompressed {section} length does not match its header."
        ));
    }
    String::from_utf8(output).map_err(|_| format!("The decompressed {section} is not UTF-8."))
}

fn parse_replay_frames(text: &str) -> Result<Vec<ReplayFrameSample>, String> {
    let mut frames = Vec::new();
    let mut time_ms = 0.0_f64;
    for encoded in text.split(',') {
        let mut fields = encoded.split('|');
        let Some(delta_text) = fields.next() else {
            continue;
        };
        let (Some(x_text), Some(y_text), Some(buttons_text)) =
            (fields.next(), fields.next(), fields.next())
        else {
            continue;
        };
        if delta_text == "-12345" {
            continue;
        }
        let delta = delta_text
            .parse::<i64>()
            .map(|value| value as f64)
            .or_else(|_| {
                delta_text
                    .parse::<f64>()
                    .map(|value| value.round())
                    .map_err(|_| ())
            })
            .map_err(|_| "A replay frame has an invalid time delta.".to_string())?;
        let x = x_text
            .parse::<f64>()
            .map_err(|_| "A replay frame has an invalid X coordinate.".to_string())?;
        let y = y_text
            .parse::<f64>()
            .map_err(|_| "A replay frame has an invalid Y coordinate.".to_string())?;
        let buttons = buttons_text
            .parse::<u32>()
            .map_err(|_| "A replay frame has an invalid button state.".to_string())?;
        if !delta.is_finite() || !x.is_finite() || !y.is_finite() {
            return Err("A replay frame contains a non-finite number.".to_string());
        }
        time_ms += delta;
        frames.push(ReplayFrameSample {
            time_ms,
            x,
            y,
            buttons,
        });
        if frames.len() > MAX_FRAME_COUNT {
            return Err(format!(
                "The replay contains more than AimMod's {MAX_FRAME_COUNT} frame limit."
            ));
        }
    }

    if frames.len() >= 2 && is_stable_intro_frame(frames[1]) {
        frames.remove(1);
    }
    if frames.first().copied().is_some_and(is_stable_intro_frame) {
        frames.remove(0);
    }

    let mut monotonic = Vec::with_capacity(frames.len());
    for frame in frames {
        if monotonic
            .last()
            .is_some_and(|previous: &ReplayFrameSample| frame.time_ms < previous.time_ms)
        {
            continue;
        }
        monotonic.push(frame);
    }
    Ok(monotonic)
}

fn is_stable_intro_frame(frame: ReplayFrameSample) -> bool {
    frame.x == 256.0 && frame.y == -500.0
}

fn read_replay_header(reader: &mut impl Read) -> Result<ReplayHeader, String> {
    let mode = read_u8(reader)?;
    if mode != 0 {
        return Err(format!(
            "Coaching currently supports osu! standard replays; this replay uses mode {mode}."
        ));
    }
    let game_version = read_u32(reader)?;
    let beatmap_hash = read_osu_string(reader, "beatmap hash")?;
    let player_name = read_osu_string(reader, "player name")?;
    let replay_hash = read_osu_string(reader, "replay hash")?;
    let count_300 = read_u16(reader)?;
    let count_100 = read_u16(reader)?;
    let count_50 = read_u16(reader)?;
    let _count_geki = read_u16(reader)?;
    let _count_katu = read_u16(reader)?;
    let count_miss = read_u16(reader)?;
    let _score = read_u32(reader)?;
    let max_combo = read_u16(reader)?;
    let perfect = read_u8(reader)? != 0;
    let mod_bitmask = read_u32(reader)?;
    let _life_graph = read_osu_string(reader, "life graph")?;
    let _played_at_ticks = read_i64(reader)?;
    Ok(ReplayHeader {
        game_version,
        beatmap_hash,
        player_name,
        replay_hash,
        count_300,
        count_100,
        count_50,
        count_miss,
        max_combo,
        perfect,
        mod_bitmask,
    })
}

fn read_byte_array(reader: &mut impl Read, field: &str) -> Result<Vec<u8>, String> {
    let length = read_i32(reader)?;
    if length < 0 {
        return Err(format!("The replay has a negative {field} length."));
    }
    let length = length as usize;
    if length > MAX_COMPRESSED_SECTION_BYTES {
        return Err(format!(
            "The compressed {field} exceeds AimMod's {} MiB limit.",
            MAX_COMPRESSED_SECTION_BYTES / 1024 / 1024
        ));
    }
    let mut bytes = vec![0; length];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| format!("The replay ended inside {field}."))?;
    Ok(bytes)
}

fn read_osu_string(reader: &mut impl Read, field: &str) -> Result<String, String> {
    match read_u8(reader)? {
        0x00 => Ok(String::new()),
        0x0b => {
            let length = read_uleb128(reader)?;
            if length > MAX_REPLAY_STRING_BYTES as u64 {
                return Err(format!("The replay {field} is too large."));
            }
            let mut bytes = vec![0; length as usize];
            reader
                .read_exact(&mut bytes)
                .map_err(|_| format!("The replay ended inside the {field}."))?;
            String::from_utf8(bytes).map_err(|_| format!("The replay {field} is not UTF-8."))
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
    let mut bytes = [0; 1];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(bytes[0])
}

fn read_u16(reader: &mut impl Read) -> Result<u16, String> {
    let mut bytes = [0; 2];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(u16::from_le_bytes(bytes))
}

fn read_u32(reader: &mut impl Read) -> Result<u32, String> {
    let mut bytes = [0; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(u32::from_le_bytes(bytes))
}

fn read_i32(reader: &mut impl Read) -> Result<i32, String> {
    let mut bytes = [0; 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(i32::from_le_bytes(bytes))
}

fn read_i64(reader: &mut impl Read) -> Result<i64, String> {
    let mut bytes = [0; 8];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "The replay header is truncated.".to_string())?;
    Ok(i64::from_le_bytes(bytes))
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
        (1 << 22, "Cinema"),
        (1 << 23, "TargetPractice"),
    ];
    MODS.iter()
        .filter(|(flag, _)| bitmask & flag != 0)
        .map(|(_, name)| (*name).to_string())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn header() -> ReplayHeader {
        ReplayHeader {
            game_version: 30_000_019,
            beatmap_hash: "26e47e7ed9ca09553cf2b51fd064786d".to_string(),
            player_name: "verycrunchy".to_string(),
            replay_hash: "a716eb5190e59becb712587d5500b672".to_string(),
            count_300: 100,
            count_100: 4,
            count_50: 1,
            count_miss: 2,
            max_combo: 116,
            perfect: false,
            mod_bitmask: 0,
        }
    }

    fn frame(time_ms: f64, x: f64, y: f64, buttons: u32) -> ReplayFrameSample {
        ReplayFrameSample {
            time_ms,
            x,
            y,
            buttons,
        }
    }

    #[test]
    fn derives_only_observable_path_and_press_metrics() {
        let frames = vec![
            frame(0.0, 0.0, 0.0, 0),
            frame(100.0, 3.0, 4.0, 1),
            frame(200.0, 6.0, 8.0, 0),
            frame(300.0, 9.0, 12.0, 2),
        ];
        let analysis = analyze_frames("fixture.osr".to_string(), header(), frames, None, vec![]);
        let path = analysis
            .metrics
            .iter()
            .find(|metric| metric.id == "cursorPathDistance")
            .unwrap();
        let presses = analysis
            .metrics
            .iter()
            .find(|metric| metric.id == "pressCount")
            .unwrap();
        let median = analysis
            .metrics
            .iter()
            .find(|metric| metric.id == "medianPressInterval")
            .unwrap();
        assert_eq!(path.value, 15.0);
        assert_eq!(presses.value, 2.0);
        assert_eq!(median.value, 200.0);
    }

    #[test]
    fn does_not_substitute_keypresses_for_ur_or_slider_breaks() {
        let frames = vec![frame(0.0, 0.0, 0.0, 0), frame(100.0, 1.0, 1.0, 1)];
        let analysis = analyze_frames("fixture.osr".to_string(), header(), frames, None, vec![]);
        assert!(
            analysis
                .metrics
                .iter()
                .all(|metric| metric.id != "unstableRate")
        );
        assert!(
            analysis
                .metrics
                .iter()
                .all(|metric| metric.id != "sliderBreakCount")
        );
        assert!(
            analysis
                .unavailable_metrics
                .iter()
                .any(|metric| metric.id == "unstableRate")
        );
        assert!(
            analysis
                .unavailable_metrics
                .iter()
                .any(|metric| metric.id == "sliderBreakCount")
        );
    }

    #[test]
    fn labels_late_change_as_low_confidence_not_fatigue() {
        let mut frames = Vec::new();
        for index in 0..120 {
            let time = index as f64 * 100.0;
            let x = if index < 90 {
                index as f64 * 2.0
            } else {
                180.0 + (index - 90) as f64 * 0.2
            };
            frames.push(frame(time, x, 100.0, if index % 2 == 0 { 1 } else { 0 }));
        }
        let analysis = analyze_frames("fixture.osr".to_string(), header(), frames, None, vec![]);
        let insight = analysis
            .insights
            .iter()
            .find(|insight| insight.id == "lateInputChange")
            .unwrap();
        assert_eq!(insight.confidence, OsuCoachingConfidence::Low);
        assert!(!insight.summary.to_ascii_lowercase().contains("fatigue"));
        assert!(insight.next_step.contains("before attributing"));
    }

    #[test]
    fn parses_official_legacy_frame_rules() {
        let frames = parse_replay_frames(
            "0|256|-500|0,100|256|-500|0,20.4|1|2|1,-5|3|4|0,10|5|6|2,-12345|0|0|0",
        )
        .unwrap();
        assert_eq!(frames.len(), 2);
        assert_eq!(frames[0], frame(120.0, 1.0, 2.0, 1));
        assert_eq!(frames[1], frame(125.0, 5.0, 6.0, 2));
    }

    #[test]
    fn parses_lazer_embedded_statistics_without_calling_them_slider_breaks() {
        let compressed = compress_lzma(
            br#"{"mods":[{"acronym":"HD"}],"statistics":{"LargeTickMiss":2,"SliderTailHit":8},"maximum_statistics":{"SliderTailHit":10},"pauses":[1200]}"#,
        );
        let info = parse_embedded_score_info(&compressed).unwrap();
        assert_eq!(info.large_tick_miss_count, Some(2));
        assert_eq!(info.slider_tail_hit_count, Some(8));
        assert_eq!(info.maximum_slider_tail_count, Some(10));
        assert_eq!(info.pause_count, Some(1));
        assert_eq!(info.mods, vec!["HD"]);
    }

    #[test]
    fn replaces_aggregate_gaps_with_exact_object_findings() {
        let frames = vec![frame(0.0, 0.0, 0.0, 0), frame(100.0, 1.0, 1.0, 1)];
        let mut analysis =
            analyze_frames("fixture.osr".to_string(), header(), frames, None, vec![]);
        let exact = OsuExactReplayJudgements {
            engine_version: "ppy.osu.Game/2026.730.0".to_string(),
            time_basis: "officialRulesetPlayback".to_string(),
            pauses: vec![],
            judgements: vec![
                exact_judgement(Some(3), "Great", 1_012.0, 12.0, true),
                exact_judgement(Some(4), "Miss", 2_140.0, 140.0, false),
            ],
            summary: crate::osu_replay_analytics::OsuExactJudgementSummary {
                great: 1,
                ok: 0,
                meh: 0,
                miss: 1,
                slider_breaks: 0,
                other: 0,
            },
            error: None,
        };

        apply_exact_judgements(&mut analysis, &exact);

        assert_eq!(
            analysis.source.official_judgement_engine.as_deref(),
            Some("ppy.osu.Game/2026.730.0")
        );
        let miss = analysis
            .insights
            .iter()
            .find(|insight| insight.id == "exactMiss")
            .unwrap();
        assert_eq!(miss.start_ms, Some(1_390.0));
        assert!(miss.title.contains("object 5"));
        assert!(
            analysis
                .insights
                .iter()
                .all(|insight| insight.id != "aggregateMisses")
        );
        assert!(
            analysis
                .metrics
                .iter()
                .any(|metric| metric.id == "medianHitOffset" && metric.value == 12.0)
        );
        assert!(
            analysis
                .unavailable_metrics
                .iter()
                .all(|metric| metric.id != "missTimeline")
        );
    }

    fn exact_judgement(
        object_index: Option<usize>,
        result: &str,
        judgement_time_ms: f64,
        time_offset_ms: f64,
        hit_position: bool,
    ) -> OsuExactObjectJudgement {
        OsuExactObjectJudgement {
            object_index,
            nested_path: None,
            object_type: "HitCircle".to_string(),
            start_time_ms: judgement_time_ms - time_offset_ms,
            end_time_ms: judgement_time_ms - time_offset_ms,
            result: result.to_string(),
            maximum_result: "Great".to_string(),
            judgement_time_ms,
            time_offset_ms,
            gameplay_rate: Some(1.0),
            object_position: hit_position
                .then_some(crate::osu_replay_analytics::OsuExactPoint { x: 128.0, y: 192.0 }),
            cursor_position: hit_position
                .then_some(crate::osu_replay_analytics::OsuExactPoint { x: 131.0, y: 196.0 }),
            combo_before: 2,
            combo_after: if result == "Miss" { 0 } else { 3 },
        }
    }

    #[test]
    fn analyses_a_complete_lazer_osr_container() {
        let replay_text = b"0|0|0|0,100|3|4|1,100|6|8|0,100|9|12|2,-12345|0|0|0";
        let score_info =
            br#"{"mods":[],"statistics":{"LargeTickMiss":2},"maximum_statistics":{},"pauses":[]}"#;
        let mut bytes = Vec::new();
        bytes.push(0);
        bytes.extend_from_slice(&30_000_019_u32.to_le_bytes());
        write_osu_string(&mut bytes, "26e47e7ed9ca09553cf2b51fd064786d");
        write_osu_string(&mut bytes, "verycrunchy");
        write_osu_string(&mut bytes, "a716eb5190e59becb712587d5500b672");
        for count in [100_u16, 4, 1, 0, 0, 2] {
            bytes.extend_from_slice(&count.to_le_bytes());
        }
        bytes.extend_from_slice(&556_291_u32.to_le_bytes());
        bytes.extend_from_slice(&116_u16.to_le_bytes());
        bytes.push(0);
        bytes.extend_from_slice(&0_u32.to_le_bytes());
        bytes.push(0);
        bytes.extend_from_slice(&0_i64.to_le_bytes());
        write_byte_array(&mut bytes, &compress_lzma(replay_text));
        bytes.extend_from_slice(&(-1_i64).to_le_bytes());
        write_byte_array(&mut bytes, &compress_lzma(score_info));

        let mut file = tempfile::NamedTempFile::new().unwrap();
        file.write_all(&bytes).unwrap();
        let analysis = analyze_replay_file(file.path().to_str().unwrap()).unwrap();
        assert_eq!(analysis.source.frame_count, 4);
        assert!(analysis.source.has_lazer_score_info);
        assert_eq!(analysis.score.large_tick_miss_count, Some(2));
        assert!(
            analysis
                .metrics
                .iter()
                .any(|metric| metric.id == "largeTickMissCount" && metric.value == 2.0)
        );
    }

    fn compress_lzma(bytes: &[u8]) -> Vec<u8> {
        let mut input = Cursor::new(bytes);
        let mut output = Vec::new();
        lzma_rs::lzma_compress(&mut input, &mut output).unwrap();
        output[5..13].copy_from_slice(&(bytes.len() as u64).to_le_bytes());
        output
    }

    fn write_osu_string(output: &mut Vec<u8>, value: &str) {
        output.push(0x0b);
        let mut length = value.len() as u64;
        loop {
            let mut byte = (length & 0x7f) as u8;
            length >>= 7;
            if length != 0 {
                byte |= 0x80;
            }
            output.push(byte);
            if length == 0 {
                break;
            }
        }
        output.extend_from_slice(value.as_bytes());
    }

    fn write_byte_array(output: &mut Vec<u8>, bytes: &[u8]) {
        output.extend_from_slice(&(bytes.len() as i32).to_le_bytes());
        output.extend_from_slice(bytes);
    }
}
