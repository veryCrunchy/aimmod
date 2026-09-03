use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use std::{
    cmp::Ordering,
    collections::VecDeque,
    env,
    fs::{self, File},
    io::{self, BufReader, Cursor, Read, Write},
    path::{Path, PathBuf},
    process::Command,
    sync::Mutex,
    time::{Duration, Instant, UNIX_EPOCH},
};

const MAX_REPLAY_FILE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_DECOMPRESSED_REPLAY_BYTES: usize = 32 * 1024 * 1024;
const MAX_LZMA_DICTIONARY_BYTES: usize = 64 * 1024 * 1024;
const MAX_REPLAY_STRING_BYTES: usize = 1024 * 1024;
const MAX_REPLAY_FILES: usize = 64;
const MAX_REPLAY_FRAMES: usize = 1_000_000;
const MAX_REPLAY_DURATION_MS: f64 = 12.0 * 60.0 * 60.0 * 1000.0;
const MAX_COORDINATE_VALUE: f64 = 131_072.0;
const WINDOWS_TICKS_AT_UNIX_EPOCH: i64 = 621_355_968_000_000_000;
const WINDOWS_TICKS_PER_SECOND: i64 = 10_000_000;
const TIMELINE_BUCKET_MS: f64 = 5_000.0;
const TRACE_SAMPLE_INTERVAL_MS: f64 = 16.0;
const MAX_TRACE_FRAMES: usize = 50_000;
const MAX_HIT_OBJECTS: usize = 20_000;
const MAX_EXACT_JUDGEMENT_OUTPUT_BYTES: usize = 32 * 1024 * 1024;
const EXACT_JUDGEMENT_CACHE_ITEMS: usize = 8;
const EXACT_JUDGEMENT_FAILURE_CACHE_TTL: Duration = Duration::from_secs(10);

#[derive(Debug, Clone, PartialEq, Eq)]
struct ExactJudgementCacheKey {
    replay_path: PathBuf,
    replay_size: u64,
    replay_modified_ns: u128,
    beatmap_path: PathBuf,
    beatmap_size: u64,
    beatmap_modified_ns: u128,
}

#[derive(Debug, Clone)]
struct ExactJudgementCacheEntry {
    key: ExactJudgementCacheKey,
    cached_at: Instant,
    result: Result<OsuExactReplayJudgements, String>,
}

static EXACT_JUDGEMENT_CACHE: Lazy<Mutex<VecDeque<ExactJudgementCacheEntry>>> =
    Lazy::new(|| Mutex::new(VecDeque::new()));

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayAnalyticsResponse {
    pub items: Vec<OsuReplayAnalytics>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayAnalytics {
    pub path: String,
    pub file_name: String,
    pub provenance: OsuReplayProvenance,
    pub mode: Option<String>,
    pub game_version: Option<u32>,
    pub beatmap_hash: Option<String>,
    pub player_name: Option<String>,
    pub replay_hash: Option<String>,
    pub played_at: Option<String>,
    pub counts: Option<OsuReplayHitCounts>,
    pub score: Option<u32>,
    pub max_combo: Option<u16>,
    pub perfect: Option<bool>,
    pub mods: Option<OsuReplayMods>,
    pub accuracy_percent: Option<f64>,
    pub frame_metrics: Option<OsuReplayFrameMetrics>,
    pub timeline: Vec<OsuReplayTimelineBucket>,
    pub notable_segments: Vec<OsuReplayNotableSegment>,
    pub trace_frames: Vec<OsuReplayTraceFrame>,
    pub beatmap_trace: Option<OsuReplayBeatmapTrace>,
    pub exact_judgements: Option<OsuExactReplayJudgements>,
    pub exact_judgement_error: Option<String>,
    pub parse_error: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuExactReplayJudgements {
    pub engine_version: String,
    pub time_basis: String,
    pub pauses: Vec<i32>,
    pub judgements: Vec<OsuExactObjectJudgement>,
    pub summary: OsuExactJudgementSummary,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuExactObjectJudgement {
    pub object_index: Option<usize>,
    pub nested_path: Option<String>,
    pub object_type: String,
    pub start_time_ms: f64,
    pub end_time_ms: f64,
    pub result: String,
    pub maximum_result: String,
    pub judgement_time_ms: f64,
    pub time_offset_ms: f64,
    pub gameplay_rate: Option<f64>,
    pub object_position: Option<OsuExactPoint>,
    pub cursor_position: Option<OsuExactPoint>,
    pub combo_before: i32,
    pub combo_after: i32,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuExactPoint {
    pub x: f64,
    pub y: f64,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuExactJudgementSummary {
    pub great: u32,
    pub ok: u32,
    pub meh: u32,
    pub miss: u32,
    pub slider_breaks: u32,
    pub other: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayProvenance {
    pub storage_source: String,
    pub score_source: String,
    pub frame_source: String,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayHitCounts {
    pub count_300: u16,
    pub count_100: u16,
    pub count_50: u16,
    pub count_geki: u16,
    pub count_katu: u16,
    pub count_miss: u16,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayMods {
    pub bitmask: u32,
    pub names: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayFrameMetrics {
    pub frame_count: usize,
    pub start_time_ms: f64,
    pub end_time_ms: f64,
    pub duration_ms: f64,
    pub cursor_distance: f64,
    pub average_cursor_speed: f64,
    pub p95_cursor_speed: f64,
    pub peak_cursor_speed: f64,
    pub left_presses: u32,
    pub right_presses: u32,
    pub simultaneous_presses: u32,
    pub key_presses_per_second: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayTimelineBucket {
    pub start_ms: f64,
    pub end_ms: f64,
    pub cursor_distance: f64,
    pub average_cursor_speed: f64,
    pub p95_cursor_speed: f64,
    pub key_presses: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayNotableSegment {
    pub kind: String,
    pub label: String,
    pub start_ms: f64,
    pub end_ms: f64,
    pub detail: String,
    pub cursor_speed: f64,
    pub key_presses: u32,
    pub object_count: Option<usize>,
    pub first_object_index: Option<usize>,
    pub last_object_index: Option<usize>,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayTraceFrame {
    pub time_ms: f64,
    pub x: f64,
    pub y: f64,
    pub buttons: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayHitObject {
    pub index: usize,
    pub time_ms: f64,
    pub end_time_ms: f64,
    pub x: f64,
    pub y: f64,
    pub kind: String,
    pub new_combo: bool,
    pub combo_offset: u8,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayBeatmapTrace {
    pub circle_size: Option<f64>,
    pub combo_colours: Vec<String>,
    pub playback_rate: f64,
    pub preserves_pitch: bool,
    pub constant_rate: bool,
    pub audio_path: Option<String>,
    pub global_audio_offset_ms: f64,
    pub platform_audio_offset_ms: f64,
    pub beatmap_audio_offset_ms: f64,
    pub total_audio_offset_ms: f64,
    pub hit_objects: Vec<OsuReplayHitObject>,
}

#[derive(Debug, Clone)]
struct ReplayClock {
    playback_rate: f64,
    preserves_pitch: bool,
    constant_rate: bool,
}

#[derive(Debug)]
struct ReplayHeader {
    mode: u8,
    game_version: u32,
    beatmap_hash: String,
    player_name: String,
    replay_hash: String,
    counts: OsuReplayHitCounts,
    score: u32,
    max_combo: u16,
    perfect: bool,
    mods: OsuReplayMods,
    clock: ReplayClock,
    played_at: String,
}

#[derive(Debug, Clone, Copy)]
struct ReplayFrame {
    time: f64,
    x: f64,
    y: f64,
    buttons: u32,
}

#[derive(Default)]
struct BucketAccumulator {
    distance: f64,
    elapsed_ms: f64,
    speeds: Vec<f64>,
    key_presses: u32,
}

pub fn analyze_replay_files(paths: Vec<String>) -> OsuReplayAnalyticsResponse {
    if paths.len() > MAX_REPLAY_FILES {
        return OsuReplayAnalyticsResponse {
            items: Vec::new(),
            error: Some(format!(
                "Choose at most {MAX_REPLAY_FILES} replay files for one analysis."
            )),
        };
    }

    OsuReplayAnalyticsResponse {
        items: paths
            .into_iter()
            .map(PathBuf::from)
            .map(|path| analyze_replay_file(&path))
            .collect(),
        error: None,
    }
}

fn analyze_replay_file(path: &Path) -> OsuReplayAnalytics {
    let provenance = provenance_for(path);
    match parse_replay(path) {
        Ok((header, frames)) => {
            let accuracy_percent = standard_accuracy(&header.counts);
            let (frame_metrics, timeline) = frame_statistics(&frames);
            let trace_frames = replay_trace_frames(&frames);
            let beatmap_trace = load_beatmap_trace(&header.beatmap_hash, &header.clock);
            let notable_segments = notable_segments(&timeline, beatmap_trace.as_ref());
            let (exact_judgements, exact_judgement_error) =
                match reconstruct_exact_judgements(path, &header.beatmap_hash) {
                    Ok(value) => (Some(value), None),
                    Err(error) => (None, Some(error)),
                };
            OsuReplayAnalytics {
                path: path.to_string_lossy().into_owned(),
                file_name: display_file_name(path),
                provenance,
                mode: Some(mode_name(header.mode).to_string()),
                game_version: Some(header.game_version),
                beatmap_hash: Some(header.beatmap_hash),
                player_name: Some(header.player_name),
                replay_hash: Some(header.replay_hash),
                played_at: Some(header.played_at),
                counts: Some(header.counts),
                score: Some(header.score),
                max_combo: Some(header.max_combo),
                perfect: Some(header.perfect),
                mods: Some(header.mods),
                accuracy_percent,
                frame_metrics: Some(frame_metrics),
                timeline,
                notable_segments,
                trace_frames,
                beatmap_trace,
                exact_judgements,
                exact_judgement_error,
                parse_error: None,
            }
        }
        Err(error) => OsuReplayAnalytics {
            path: path.to_string_lossy().into_owned(),
            file_name: display_file_name(path),
            provenance,
            mode: None,
            game_version: None,
            beatmap_hash: None,
            player_name: None,
            replay_hash: None,
            played_at: None,
            counts: None,
            score: None,
            max_combo: None,
            perfect: None,
            mods: None,
            accuracy_percent: None,
            frame_metrics: None,
            timeline: Vec::new(),
            notable_segments: Vec::new(),
            trace_frames: Vec::new(),
            beatmap_trace: None,
            exact_judgements: None,
            exact_judgement_error: None,
            parse_error: Some(error),
        },
    }
}

fn parse_replay(path: &Path) -> Result<(ReplayHeader, Vec<ReplayFrame>), String> {
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
    let mut header = parse_header(&mut reader)?;
    if header.mode != 0 {
        return Err(format!(
            "Frame analytics currently support osu!standard replays, not {}.",
            mode_name(header.mode)
        ));
    }

    let compressed_length = read_i32(&mut reader)?;
    if compressed_length < 13 {
        return Err("The replay frame payload is missing or invalid.".to_string());
    }
    let compressed_length = usize::try_from(compressed_length)
        .map_err(|_| "The replay frame payload length is invalid.".to_string())?;
    if compressed_length > MAX_REPLAY_FILE_BYTES as usize {
        return Err("The replay frame payload exceeds the analysis limit.".to_string());
    }
    let mut compressed = vec![0_u8; compressed_length];
    reader
        .read_exact(&mut compressed)
        .map_err(|_| "The replay ended inside the frame payload.".to_string())?;

    if let Some((clock, names)) = read_embedded_score_clock(&mut reader, header.game_version) {
        header.clock = clock;
        header.mods.names = names;
    }

    let decoded = decompress_replay_frames(&compressed)?;
    let frames = parse_frames(&decoded)?;
    Ok((header, frames))
}

fn read_embedded_score_clock(
    reader: &mut impl Read,
    game_version: u32,
) -> Option<(ReplayClock, Vec<String>)> {
    if game_version >= 20140721 {
        read_i64(reader).ok()?;
    } else if game_version >= 20121008 {
        read_i32(reader).ok()?;
    }
    if game_version < 30000001 {
        return None;
    }
    let length = usize::try_from(read_i32(reader).ok()?).ok()?;
    if length < 13 || length > MAX_REPLAY_FILE_BYTES as usize {
        return None;
    }
    let mut compressed = vec![0; length];
    reader.read_exact(&mut compressed).ok()?;
    let decoded = decompress_replay_frames(&compressed).ok()?;
    let score: serde_json::Value = serde_json::from_slice(&decoded).ok()?;
    replay_clock_from_api_mods(score.get("mods")?)
}

fn replay_clock_from_api_mods(mods: &serde_json::Value) -> Option<(ReplayClock, Vec<String>)> {
    let mods = mods.as_array()?;
    let mut clock = ReplayClock {
        playback_rate: 1.0,
        preserves_pitch: true,
        constant_rate: true,
    };
    let mut names = Vec::with_capacity(mods.len());
    for item in mods {
        let acronym = item.get("acronym")?.as_str()?;
        names.push(api_mod_name(acronym).to_string());
        let settings = item.get("settings");
        match acronym {
            "DT" | "HT" => {
                let fallback = if acronym == "DT" { 1.5 } else { 0.75 };
                clock.playback_rate = settings
                    .and_then(|value| value.get("speed_change"))
                    .and_then(serde_json::Value::as_f64)
                    .unwrap_or(fallback);
                let adjust_pitch = settings
                    .and_then(|value| value.get("adjust_pitch"))
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(false);
                clock.preserves_pitch = !adjust_pitch;
            }
            "NC" | "DC" => {
                let fallback = if acronym == "NC" { 1.5 } else { 0.75 };
                clock.playback_rate = settings
                    .and_then(|value| value.get("speed_change"))
                    .and_then(serde_json::Value::as_f64)
                    .unwrap_or(fallback);
                clock.preserves_pitch = true;
            }
            "WU" | "WD" | "AS" => clock.constant_rate = false,
            _ => {}
        }
    }
    Some((clock, names))
}

fn replay_clock_from_legacy_mods(bitmask: u32) -> ReplayClock {
    ReplayClock {
        playback_rate: if bitmask & ((1 << 6) | (1 << 9)) != 0 {
            1.5
        } else if bitmask & (1 << 8) != 0 {
            0.75
        } else {
            1.0
        },
        preserves_pitch: true,
        constant_rate: true,
    }
}

fn api_mod_name(acronym: &str) -> &str {
    match acronym {
        "DT" => "DoubleTime",
        "NC" => "Nightcore",
        "HT" => "HalfTime",
        "DC" => "Daycore",
        "WU" => "WindUp",
        "WD" => "WindDown",
        "AS" => "AdaptiveSpeed",
        "HD" => "Hidden",
        "HR" => "HardRock",
        "EZ" => "Easy",
        "FL" => "Flashlight",
        value => value,
    }
}

fn parse_header(reader: &mut impl Read) -> Result<ReplayHeader, String> {
    let mode = read_u8(reader)?;
    if mode > 3 {
        return Err(format!("The replay uses unknown game mode {mode}."));
    }
    let game_version = read_u32(reader)?;
    let beatmap_hash = read_osu_string(reader, "beatmap hash")?;
    let player_name = read_osu_string(reader, "player name")?;
    let replay_hash = read_osu_string(reader, "replay hash")?;
    let counts = OsuReplayHitCounts {
        count_300: read_u16(reader)?,
        count_100: read_u16(reader)?,
        count_50: read_u16(reader)?,
        count_geki: read_u16(reader)?,
        count_katu: read_u16(reader)?,
        count_miss: read_u16(reader)?,
    };
    let score = read_u32(reader)?;
    let max_combo = read_u16(reader)?;
    let perfect = read_u8(reader)? != 0;
    let mod_bitmask = read_u32(reader)?;
    let _life_graph = read_osu_string(reader, "life graph")?;
    let played_at = windows_ticks_to_rfc3339(read_i64(reader)?)?;
    Ok(ReplayHeader {
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
        clock: replay_clock_from_legacy_mods(mod_bitmask),
        played_at,
    })
}

fn decompress_replay_frames(compressed: &[u8]) -> Result<Vec<u8>, String> {
    if compressed.len() < 13 {
        return Err("The LZMA frame payload is truncated.".to_string());
    }
    let unpacked_size = u64::from_le_bytes(
        compressed[5..13]
            .try_into()
            .map_err(|_| "The LZMA frame payload is truncated.".to_string())?,
    );
    if unpacked_size != u64::MAX && unpacked_size > MAX_DECOMPRESSED_REPLAY_BYTES as u64 {
        return Err(format!(
            "The decompressed replay exceeds AimMod's {} MiB analysis limit.",
            MAX_DECOMPRESSED_REPLAY_BYTES / 1024 / 1024
        ));
    }

    let mut output = LimitedWriter::new(MAX_DECOMPRESSED_REPLAY_BYTES);
    let options = lzma_rs::decompress::Options {
        memlimit: Some(MAX_LZMA_DICTIONARY_BYTES),
        ..Default::default()
    };
    lzma_rs::lzma_decompress_with_options(&mut Cursor::new(compressed), &mut output, &options)
        .map_err(|error| format!("Could not decompress the replay frames: {error}"))?;
    Ok(output.into_inner())
}

fn parse_frames(decoded: &[u8]) -> Result<Vec<ReplayFrame>, String> {
    let text = std::str::from_utf8(decoded)
        .map_err(|_| "The decompressed replay frames are not valid UTF-8.".to_string())?;
    let mut frames = Vec::new();
    let mut last_time = 0.0_f64;

    for record in text.split(',') {
        let mut fields = record.split('|');
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
        if frames.len() >= MAX_REPLAY_FRAMES {
            return Err(format!(
                "The replay contains more than {MAX_REPLAY_FRAMES} frames."
            ));
        }

        let delta = delta_text
            .parse::<i64>()
            .map(|value| value as f64)
            .or_else(|_| delta_text.parse::<f64>().map(f64::round))
            .map_err(|_| "The replay contains an invalid frame time.".to_string())?;
        let x = parse_coordinate(x_text)?;
        let y = parse_coordinate(y_text)?;
        let buttons = buttons_text
            .parse::<u32>()
            .map_err(|_| "The replay contains an invalid button state.".to_string())?;
        last_time += delta;
        if !last_time.is_finite() || last_time.abs() > MAX_REPLAY_DURATION_MS {
            return Err("The replay duration exceeds the analysis limit.".to_string());
        }
        frames.push(ReplayFrame {
            time: last_time,
            x,
            y,
            buttons,
        });
    }

    repair_legacy_start_times(&mut frames);
    frames.retain(|frame| !(frame.x == 256.0 && frame.y == -500.0));
    if frames.is_empty() {
        return Err("The replay contains no osu!standard input frames.".to_string());
    }
    if frames.windows(2).any(|pair| pair[1].time < pair[0].time) {
        return Err("The replay frame timeline moves backwards.".to_string());
    }
    Ok(frames)
}

fn repair_legacy_start_times(frames: &mut [ReplayFrame]) {
    if frames.len() >= 2 && frames[1].time < frames[0].time {
        frames[1].time = frames[0].time;
        frames[0].time = 0.0;
    }
    if frames.len() >= 3 && frames[0].time > frames[2].time {
        let time = frames[2].time;
        frames[0].time = time;
        frames[1].time = time;
    }
}

fn frame_statistics(
    frames: &[ReplayFrame],
) -> (OsuReplayFrameMetrics, Vec<OsuReplayTimelineBucket>) {
    let start_time = frames.first().map_or(0.0, |frame| frame.time);
    let end_time = frames.last().map_or(start_time, |frame| frame.time);
    let duration_ms = (end_time - start_time).max(0.0);
    let bucket_count = ((duration_ms / TIMELINE_BUCKET_MS).floor() as usize + 1).max(1);
    let mut buckets: Vec<BucketAccumulator> = (0..bucket_count)
        .map(|_| BucketAccumulator::default())
        .collect();
    let mut total_distance = 0.0;
    let mut speeds = Vec::with_capacity(frames.len().saturating_sub(1));
    let mut left_presses = 0_u32;
    let mut right_presses = 0_u32;
    let mut simultaneous_presses = 0_u32;
    let mut previous_buttons = 0_u32;

    for (index, frame) in frames.iter().enumerate() {
        let relative_time = (frame.time - start_time).max(0.0);
        let bucket_index =
            ((relative_time / TIMELINE_BUCKET_MS).floor() as usize).min(bucket_count - 1);
        let left_down = frame.buttons & (1 | 4) != 0;
        let right_down = frame.buttons & (2 | 8) != 0;
        let previous_left = previous_buttons & (1 | 4) != 0;
        let previous_right = previous_buttons & (2 | 8) != 0;
        let left_pressed = left_down && !previous_left;
        let right_pressed = right_down && !previous_right;
        if left_pressed {
            left_presses += 1;
        }
        if right_pressed {
            right_presses += 1;
        }
        if left_pressed && right_pressed {
            simultaneous_presses += 1;
        }
        buckets[bucket_index].key_presses += u32::from(left_pressed) + u32::from(right_pressed);
        previous_buttons = frame.buttons;

        if index == 0 {
            continue;
        }
        let previous = frames[index - 1];
        let elapsed_ms = frame.time - previous.time;
        if elapsed_ms <= 0.0 {
            continue;
        }
        let distance = ((frame.x - previous.x).powi(2) + (frame.y - previous.y).powi(2)).sqrt();
        let speed = distance / (elapsed_ms / 1000.0);
        total_distance += distance;
        speeds.push(speed);
        buckets[bucket_index].distance += distance;
        buckets[bucket_index].elapsed_ms += elapsed_ms;
        buckets[bucket_index].speeds.push(speed);
    }

    let total_presses = left_presses + right_presses;
    let duration_seconds = duration_ms / 1000.0;
    let metrics = OsuReplayFrameMetrics {
        frame_count: frames.len(),
        start_time_ms: start_time,
        end_time_ms: end_time,
        duration_ms,
        cursor_distance: total_distance,
        average_cursor_speed: if duration_seconds > 0.0 {
            total_distance / duration_seconds
        } else {
            0.0
        },
        p95_cursor_speed: percentile(&mut speeds.clone(), 0.95),
        peak_cursor_speed: speeds.iter().copied().fold(0.0, f64::max),
        left_presses,
        right_presses,
        simultaneous_presses,
        key_presses_per_second: if duration_seconds > 0.0 {
            f64::from(total_presses) / duration_seconds
        } else {
            0.0
        },
    };
    let timeline = buckets
        .into_iter()
        .enumerate()
        .map(|(index, mut bucket)| OsuReplayTimelineBucket {
            start_ms: start_time + index as f64 * TIMELINE_BUCKET_MS,
            end_ms: (start_time + (index + 1) as f64 * TIMELINE_BUCKET_MS).min(end_time),
            cursor_distance: bucket.distance,
            average_cursor_speed: if bucket.elapsed_ms > 0.0 {
                bucket.distance / (bucket.elapsed_ms / 1000.0)
            } else {
                0.0
            },
            p95_cursor_speed: percentile(&mut bucket.speeds, 0.95),
            key_presses: bucket.key_presses,
        })
        .collect();
    (metrics, timeline)
}

fn notable_segments(
    timeline: &[OsuReplayTimelineBucket],
    beatmap_trace: Option<&OsuReplayBeatmapTrace>,
) -> Vec<OsuReplayNotableSegment> {
    if timeline.is_empty() {
        return Vec::new();
    }
    let fastest = timeline.iter().max_by(|left, right| {
        left.p95_cursor_speed
            .partial_cmp(&right.p95_cursor_speed)
            .unwrap_or(Ordering::Equal)
    });
    let densest = timeline.iter().max_by_key(|bucket| bucket.key_presses);
    let mut segments = Vec::new();
    if let Some(bucket) = fastest.filter(|bucket| bucket.p95_cursor_speed > 0.0) {
        segments.push(OsuReplayNotableSegment {
            kind: "cursorPeak".to_string(),
            label: "Fastest cursor window".to_string(),
            start_ms: bucket.start_ms,
            end_ms: bucket.end_ms,
            detail: format!(
                "P95 cursor speed reached {:.0} playfield units/s in this replay window.",
                bucket.p95_cursor_speed
            ),
            cursor_speed: bucket.p95_cursor_speed,
            key_presses: bucket.key_presses,
            object_count: None,
            first_object_index: None,
            last_object_index: None,
        });
    }
    if let Some(bucket) = densest.filter(|bucket| bucket.key_presses > 0) {
        segments.push(OsuReplayNotableSegment {
            kind: "inputPeak".to_string(),
            label: "Densest input window".to_string(),
            start_ms: bucket.start_ms,
            end_ms: bucket.end_ms,
            detail: format!(
                "{} recorded press transitions occurred in this replay window.",
                bucket.key_presses
            ),
            cursor_speed: bucket.p95_cursor_speed,
            key_presses: bucket.key_presses,
            object_count: None,
            first_object_index: None,
            last_object_index: None,
        });
    }
    if let Some(beatmap) = beatmap_trace {
        for segment in &mut segments {
            let objects: Vec<_> = beatmap
                .hit_objects
                .iter()
                .filter(|object| {
                    object.time_ms >= segment.start_ms && object.time_ms < segment.end_ms
                })
                .collect();
            if let (Some(first), Some(last)) = (objects.first(), objects.last()) {
                segment.object_count = Some(objects.len());
                segment.first_object_index = Some(first.index);
                segment.last_object_index = Some(last.index);
                segment.detail.push_str(&format!(
                    " It overlaps {} local beatmap object start{} (objects {} to {}).",
                    objects.len(),
                    if objects.len() == 1 { "" } else { "s" },
                    first.index + 1,
                    last.index + 1
                ));
            } else {
                segment.object_count = Some(0);
                segment
                    .detail
                    .push_str(" No local beatmap objects start in this window.");
            }
        }
    }
    segments
}

fn replay_trace_frames(frames: &[ReplayFrame]) -> Vec<OsuReplayTraceFrame> {
    let Some(first) = frames.first() else {
        return Vec::new();
    };
    let mut last_sample_time = f64::NEG_INFINITY;
    let mut previous_buttons = first.buttons;
    let mut result = Vec::new();
    for (index, frame) in frames.iter().enumerate() {
        let time_ms = frame.time;
        let button_changed = frame.buttons != previous_buttons;
        let is_endpoint = index == 0 || index + 1 == frames.len();
        if is_endpoint || button_changed || time_ms - last_sample_time >= TRACE_SAMPLE_INTERVAL_MS {
            result.push(OsuReplayTraceFrame {
                time_ms,
                x: frame.x,
                y: frame.y,
                buttons: frame.buttons,
            });
            last_sample_time = time_ms;
            if result.len() >= MAX_TRACE_FRAMES {
                break;
            }
        }
        previous_buttons = frame.buttons;
    }
    result
}

pub(crate) fn reconstruct_exact_judgements(
    replay_path: &Path,
    beatmap_hash: &str,
) -> Result<OsuExactReplayJudgements, String> {
    let beatmap_path =
        crate::osu_library::resolve_local_beatmap_path(beatmap_hash).ok_or_else(|| {
            "The matching local beatmap is unavailable for exact replay playback.".to_string()
        })?;
    let replay_path = replay_path
        .canonicalize()
        .map_err(|error| format!("Could not resolve the local replay for judgement: {error}"))?;
    let beatmap_path = beatmap_path
        .canonicalize()
        .map_err(|error| format!("Could not resolve the local beatmap for judgement: {error}"))?;
    let replay_metadata = fs::metadata(&replay_path)
        .map_err(|error| format!("Could not inspect the local replay for judgement: {error}"))?;
    let beatmap_metadata = fs::metadata(&beatmap_path)
        .map_err(|error| format!("Could not inspect the local beatmap for judgement: {error}"))?;
    let modified_ns = |metadata: &fs::Metadata| {
        metadata
            .modified()
            .ok()
            .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
            .map(|duration| duration.as_nanos())
            .unwrap_or_default()
    };
    let cache_key = ExactJudgementCacheKey {
        replay_path: replay_path.clone(),
        replay_size: replay_metadata.len(),
        replay_modified_ns: modified_ns(&replay_metadata),
        beatmap_path: beatmap_path.clone(),
        beatmap_size: beatmap_metadata.len(),
        beatmap_modified_ns: modified_ns(&beatmap_metadata),
    };
    // Replay and Coaching request the same result together. Keep the lock while
    // the sidecar runs so only one official ruleset host is started for a play.
    let mut cache = EXACT_JUDGEMENT_CACHE
        .lock()
        .map_err(|_| "The exact replay judgement cache is unavailable.".to_string())?;
    let now = Instant::now();
    cache.retain(|entry| {
        entry.result.is_ok()
            || now.saturating_duration_since(entry.cached_at) < EXACT_JUDGEMENT_FAILURE_CACHE_TTL
    });
    if let Some(cached) = cache.iter().find(|entry| entry.key == cache_key) {
        return cached.result.clone();
    }

    let result = (|| {
        let executable = exact_judge_executable()?;
        let output = Command::new(&executable)
            .arg(&beatmap_path)
            .arg(&replay_path)
            .output()
            .map_err(|error| format!("Could not start the official osu! replay judge: {error}"))?;
        if output.stdout.len() > MAX_EXACT_JUDGEMENT_OUTPUT_BYTES {
            return Err(
                "The official osu! replay judgement response exceeded AimMod's safety limit."
                    .to_string(),
            );
        }
        let response: OsuExactReplayJudgements =
            serde_json::from_slice(&output.stdout).map_err(|error| {
                format!("The official osu! replay judge returned invalid data: {error}")
            })?;
        if let Some(error) = response.error.as_deref() {
            return Err(format!("Official osu! ruleset playback failed: {error}"));
        }
        if !output.status.success() {
            return Err(format!(
                "The official osu! replay judge exited with {}.",
                output.status
            ));
        }
        Ok(response)
    })();
    cache.push_front(ExactJudgementCacheEntry {
        key: cache_key,
        cached_at: Instant::now(),
        result: result.clone(),
    });
    cache.truncate(EXACT_JUDGEMENT_CACHE_ITEMS);
    result
}

fn exact_judge_executable() -> Result<PathBuf, String> {
    if let Some(path) = env::var_os("AIMMOD_OSU_REPLAY_JUDGE").map(PathBuf::from) {
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!(
            "AIMMOD_OSU_REPLAY_JUDGE points to a missing file: {}",
            path.display()
        ));
    }

    if let Ok(current_exe) = env::current_exe() {
        if let Some(directory) = current_exe.parent() {
            let sibling = directory.join(executable_name("osu-replay-judge"));
            if sibling.is_file() {
                return Ok(sibling);
            }
        }
    }

    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let bundled = manifest.join("bin").join(executable_name(&format!(
        "osu-replay-judge-{}",
        bundled_target_triple()
    )));
    if bundled.is_file() {
        return Ok(bundled);
    }

    let development = manifest
        .join("..")
        .join("tools")
        .join("osu-replay-judge")
        .join("bin")
        .join("Release")
        .join("net8.0")
        .join(development_runtime_directory())
        .join(executable_name("osu-replay-judge"));
    if development.is_file() {
        return Ok(development);
    }

    Err("This AimMod build does not include the official osu! replay judge.".to_string())
}

fn executable_name(base: &str) -> String {
    if cfg!(target_os = "windows") {
        format!("{base}.exe")
    } else {
        base.to_string()
    }
}

fn bundled_target_triple() -> &'static str {
    if cfg!(all(target_arch = "x86_64", target_os = "linux")) {
        "x86_64-unknown-linux-gnu"
    } else if cfg!(all(target_arch = "x86_64", target_os = "windows")) {
        "x86_64-pc-windows-msvc"
    } else if cfg!(all(target_arch = "aarch64", target_os = "macos")) {
        "aarch64-apple-darwin"
    } else if cfg!(all(target_arch = "x86_64", target_os = "macos")) {
        "x86_64-apple-darwin"
    } else {
        "unsupported-target"
    }
}

fn development_runtime_directory() -> &'static str {
    if cfg!(all(target_arch = "x86_64", target_os = "linux")) {
        "linux-x64"
    } else if cfg!(all(target_arch = "x86_64", target_os = "windows")) {
        "win-x64"
    } else if cfg!(all(target_arch = "aarch64", target_os = "macos")) {
        "osx-arm64"
    } else if cfg!(all(target_arch = "x86_64", target_os = "macos")) {
        "osx-x64"
    } else {
        "unsupported-runtime"
    }
}

fn load_beatmap_trace(beatmap_hash: &str, clock: &ReplayClock) -> Option<OsuReplayBeatmapTrace> {
    let playback = crate::osu_library::resolve_local_beatmap_playback(beatmap_hash)?;
    let metadata = playback.beatmap_path.metadata().ok()?;
    if metadata.len() > 16 * 1024 * 1024 {
        return None;
    }
    let text = std::fs::read_to_string(&playback.beatmap_path).ok()?;
    let mut trace = parse_beatmap_trace(&text, clock)?;
    trace.audio_path = playback
        .audio_path
        .filter(|path| path.is_file())
        .map(|path| path.to_string_lossy().into_owned());
    trace.global_audio_offset_ms =
        read_ini_number(&playback.storage_root.join("game.ini"), "AudioOffset").unwrap_or(0.0);
    trace.platform_audio_offset_ms = platform_audio_offset_ms(&playback.storage_root);
    trace.beatmap_audio_offset_ms = playback.user_offset_ms;
    trace.total_audio_offset_ms = total_audio_offset_ms(
        trace.global_audio_offset_ms,
        trace.platform_audio_offset_ms,
        trace.beatmap_audio_offset_ms,
        trace.playback_rate,
    );
    Some(trace)
}

fn parse_beatmap_trace(text: &str, clock: &ReplayClock) -> Option<OsuReplayBeatmapTrace> {
    if !text
        .trim_start_matches('\u{feff}')
        .starts_with("osu file format v")
    {
        return None;
    }
    let mut section = "";
    let mut circle_size = None;
    let combo_colours = crate::osu_realm_reader::parse_combo_colours(text);
    let mut hit_objects = Vec::new();
    for line in text.lines().map(str::trim) {
        if line.starts_with('[') && line.ends_with(']') {
            section = line;
            continue;
        }
        if section == "[Difficulty]" {
            if let Some((key, value)) = line.split_once(':') {
                if key.trim() == "CircleSize" {
                    circle_size = value.trim().parse::<f64>().ok();
                }
            }
            continue;
        }
        if section != "[HitObjects]" || line.is_empty() || line.starts_with("//") {
            continue;
        }
        let fields: Vec<_> = line.split(',').collect();
        let (Some(x), Some(y), Some(time), Some(kind_bits)) = (
            fields.first().and_then(|value| value.parse::<f64>().ok()),
            fields.get(1).and_then(|value| value.parse::<f64>().ok()),
            fields.get(2).and_then(|value| value.parse::<f64>().ok()),
            fields.get(3).and_then(|value| value.parse::<u32>().ok()),
        ) else {
            continue;
        };
        if !(0.0..=512.0).contains(&x) || !(0.0..=384.0).contains(&y) || time < 0.0 {
            continue;
        }
        let kind = if kind_bits & 1 != 0 {
            "circle"
        } else if kind_bits & 2 != 0 {
            "slider"
        } else if kind_bits & 8 != 0 {
            "spinner"
        } else {
            "other"
        };
        let end_time = if kind == "spinner" {
            fields
                .get(5)
                .and_then(|value| value.parse::<f64>().ok())
                .unwrap_or(time)
        } else {
            time
        };
        hit_objects.push(OsuReplayHitObject {
            index: hit_objects.len(),
            time_ms: time,
            end_time_ms: end_time,
            x,
            y,
            kind: kind.to_string(),
            new_combo: kind_bits & 4 != 0,
            combo_offset: ((kind_bits >> 4) & 7) as u8,
        });
        if hit_objects.len() >= MAX_HIT_OBJECTS {
            break;
        }
    }
    (!hit_objects.is_empty()).then_some(OsuReplayBeatmapTrace {
        circle_size,
        combo_colours,
        playback_rate: clock.playback_rate,
        preserves_pitch: clock.preserves_pitch,
        constant_rate: clock.constant_rate,
        audio_path: None,
        global_audio_offset_ms: 0.0,
        platform_audio_offset_ms: 0.0,
        beatmap_audio_offset_ms: 0.0,
        total_audio_offset_ms: 0.0,
        hit_objects,
    })
}

fn read_ini_number(path: &Path, key: &str) -> Option<f64> {
    let metadata = path.metadata().ok()?;
    if !metadata.is_file() || metadata.len() > 1024 * 1024 {
        return None;
    }
    std::fs::read_to_string(path)
        .ok()?
        .lines()
        .find_map(|line| {
            let (candidate, value) = line.split_once('=')?;
            candidate
                .trim()
                .eq_ignore_ascii_case(key)
                .then(|| value.trim().parse::<f64>().ok())
                .flatten()
        })
}

fn read_ini_bool(path: &Path, key: &str) -> Option<bool> {
    let metadata = path.metadata().ok()?;
    if !metadata.is_file() || metadata.len() > 1024 * 1024 {
        return None;
    }
    std::fs::read_to_string(path)
        .ok()?
        .lines()
        .find_map(|line| {
            let (candidate, value) = line.split_once('=')?;
            if !candidate.trim().eq_ignore_ascii_case(key) {
                return None;
            }
            match value.trim().to_ascii_lowercase().as_str() {
                "true" | "1" => Some(true),
                "false" | "0" => Some(false),
                _ => None,
            }
        })
}

fn platform_audio_offset_ms(storage_root: &Path) -> f64 {
    if !cfg!(target_os = "windows") {
        return 0.0;
    }
    let experimental = read_ini_bool(
        &storage_root.join("framework.ini"),
        "AudioUseExperimentalWasapi",
    )
    .unwrap_or(false);
    if experimental { -10.0 } else { 15.0 }
}

fn total_audio_offset_ms(
    global_offset_ms: f64,
    platform_offset_ms: f64,
    beatmap_offset_ms: f64,
    playback_rate: f64,
) -> f64 {
    (global_offset_ms + platform_offset_ms) * playback_rate + beatmap_offset_ms
}

fn standard_accuracy(counts: &OsuReplayHitCounts) -> Option<f64> {
    let total = u64::from(counts.count_300)
        + u64::from(counts.count_100)
        + u64::from(counts.count_50)
        + u64::from(counts.count_miss);
    (total > 0).then(|| {
        let achieved = u64::from(counts.count_300) * 300
            + u64::from(counts.count_100) * 100
            + u64::from(counts.count_50) * 50;
        achieved as f64 / (total * 300) as f64 * 100.0
    })
}

fn percentile(values: &mut [f64], quantile: f64) -> f64 {
    if values.is_empty() {
        return 0.0;
    }
    values.sort_by(|left, right| left.partial_cmp(right).unwrap_or(Ordering::Equal));
    let index = ((values.len() - 1) as f64 * quantile).ceil() as usize;
    values[index.min(values.len() - 1)]
}

fn parse_coordinate(value: &str) -> Result<f64, String> {
    let parsed = value
        .parse::<f64>()
        .map_err(|_| "The replay contains an invalid cursor coordinate.".to_string())?;
    if !parsed.is_finite() || parsed.abs() > MAX_COORDINATE_VALUE {
        return Err("The replay contains a cursor coordinate outside osu!'s limits.".to_string());
    }
    Ok(parsed)
}

fn provenance_for(path: &Path) -> OsuReplayProvenance {
    let is_lazer_store_hash = path
        .file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| {
            name.len() == 64
                && name
                    .bytes()
                    .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        });
    let is_lazer_store_path = path
        .components()
        .any(|component| component.as_os_str() == "files");
    let storage_source = if is_lazer_store_hash && is_lazer_store_path {
        "lazerStore"
    } else if path.components().any(|component| {
        component
            .as_os_str()
            .to_string_lossy()
            .eq_ignore_ascii_case("exports")
    }) {
        "export"
    } else {
        "selectedFile"
    };
    OsuReplayProvenance {
        storage_source: storage_source.to_string(),
        score_source: "osrHeader".to_string(),
        frame_source: "osrLzma".to_string(),
    }
}

fn mode_name(mode: u8) -> &'static str {
    match mode {
        0 => "osu",
        1 => "taiko",
        2 => "catch",
        3 => "mania",
        _ => "unknown",
    }
}

fn display_file_name(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| path.to_string_lossy().into_owned())
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
    use chrono::{DateTime, SecondsFormat, Utc};
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
        (1 << 23, "Target"),
        (1 << 24, "Key9"),
        (1 << 25, "KeyCoop"),
        (1 << 26, "Key1"),
        (1 << 27, "Key3"),
        (1 << 28, "Key2"),
        (1 << 29, "ScoreV2"),
        (1 << 30, "Mirror"),
    ];
    MODS.iter()
        .filter_map(|(flag, name)| (bitmask & flag != 0).then(|| (*name).to_string()))
        .collect()
}

struct LimitedWriter {
    bytes: Vec<u8>,
    limit: usize,
}

impl LimitedWriter {
    fn new(limit: usize) -> Self {
        Self {
            bytes: Vec::new(),
            limit,
        }
    }

    fn into_inner(self) -> Vec<u8> {
        self.bytes
    }
}

impl Write for LimitedWriter {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        if self.bytes.len().saturating_add(buffer.len()) > self.limit {
            return Err(io::Error::new(
                io::ErrorKind::FileTooLarge,
                "decompressed replay limit exceeded",
            ));
        }
        self.bytes.extend_from_slice(buffer);
        Ok(buffer.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

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

    fn replay_bytes(mode: u8, frame_text: &str) -> Vec<u8> {
        let mut compressed = Vec::new();
        lzma_rs::lzma_compress(&mut Cursor::new(frame_text.as_bytes()), &mut compressed).unwrap();
        let mut output = vec![mode];
        output.extend_from_slice(&20260901_u32.to_le_bytes());
        write_osu_string(&mut output, "0123456789abcdef0123456789abcdef");
        write_osu_string(&mut output, "verycrunchy");
        write_osu_string(&mut output, "fedcba9876543210fedcba9876543210");
        for count in [100_u16, 5, 1, 0, 0, 2] {
            output.extend_from_slice(&count.to_le_bytes());
        }
        output.extend_from_slice(&1_234_567_u32.to_le_bytes());
        output.extend_from_slice(&456_u16.to_le_bytes());
        output.push(0);
        output.extend_from_slice(&(1_u32 << 3).to_le_bytes());
        write_osu_string(&mut output, "");
        output.extend_from_slice(&621_355_968_000_000_000_i64.to_le_bytes());
        output.extend_from_slice(&(compressed.len() as i32).to_le_bytes());
        output.extend_from_slice(&compressed);
        output
    }

    #[test]
    fn analyzes_real_header_values_and_frame_metrics() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("sample.osr");
        fs::write(
            &path,
            replay_bytes(0, "0|0|0|0,1000|3|4|1,1000|6|8|0,1000|6|8|2,-12345|0|0|0,"),
        )
        .unwrap();

        let result = analyze_replay_file(&path);
        assert!(result.parse_error.is_none(), "{:?}", result.parse_error);
        assert_eq!(result.player_name.as_deref(), Some("verycrunchy"));
        assert_eq!(result.score, Some(1_234_567));
        assert_eq!(result.max_combo, Some(456));
        assert_eq!(result.mods.unwrap().names, vec!["Hidden"]);
        let metrics = result.frame_metrics.unwrap();
        assert_eq!(metrics.frame_count, 4);
        assert_eq!(metrics.duration_ms, 3000.0);
        assert_eq!(metrics.cursor_distance, 10.0);
        assert_eq!(metrics.left_presses, 1);
        assert_eq!(metrics.right_presses, 1);
        assert_eq!(metrics.simultaneous_presses, 0);
        assert_eq!(result.timeline.len(), 1);
        assert_eq!(result.notable_segments.len(), 2);
        assert_eq!(result.notable_segments[0].kind, "cursorPeak");
        assert_eq!(result.notable_segments[1].kind, "inputPeak");
        assert_eq!(result.trace_frames.first().unwrap().time_ms, 0.0);
        assert_eq!(result.trace_frames.last().unwrap().time_ms, 3000.0);
    }

    #[test]
    fn rejects_non_standard_replays_without_fabricated_metrics() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("mania.osr");
        fs::write(&path, replay_bytes(3, "0|0|0|0,")).unwrap();
        let result = analyze_replay_file(&path);
        assert!(result.frame_metrics.is_none());
        assert!(result.timeline.is_empty());
        assert!(result.notable_segments.is_empty());
        assert!(result.parse_error.unwrap().contains("osu!standard"));
    }

    #[test]
    fn rejects_declared_decompressed_payload_over_limit() {
        let mut compressed = vec![0_u8; 13];
        compressed[5..13]
            .copy_from_slice(&((MAX_DECOMPRESSED_REPLAY_BYTES as u64) + 1).to_le_bytes());
        let error = decompress_replay_frames(&compressed).unwrap_err();
        assert!(error.contains("decompressed replay"));
    }

    #[test]
    fn keeps_replay_and_hit_objects_on_absolute_gameplay_clock() {
        let text = "osu file format v14\n[Difficulty]\nCircleSize:4\n[HitObjects]\n64,192,1500,1,0,0:0:0:0:\n256,192,3000,8,0,4500\n";
        let clock = ReplayClock {
            playback_rate: 1.5,
            preserves_pitch: true,
            constant_rate: true,
        };
        let trace = parse_beatmap_trace(text, &clock).unwrap();
        assert_eq!(trace.circle_size, Some(4.0));
        assert_eq!(trace.playback_rate, 1.5);
        assert_eq!(trace.hit_objects.len(), 2);
        assert_eq!(trace.hit_objects[0].time_ms, 1500.0);
        assert_eq!(trace.hit_objects[0].kind, "circle");
        assert_eq!(trace.hit_objects[1].time_ms, 3000.0);
        assert_eq!(trace.hit_objects[1].end_time_ms, 4500.0);
        assert_eq!(trace.hit_objects[1].kind, "spinner");
    }

    #[test]
    fn keeps_trace_frames_on_absolute_replay_clock() {
        let frames = vec![
            ReplayFrame {
                time: 1250.0,
                x: 1.0,
                y: 2.0,
                buttons: 0,
            },
            ReplayFrame {
                time: 1270.0,
                x: 3.0,
                y: 4.0,
                buttons: 1,
            },
        ];
        let trace = replay_trace_frames(&frames);
        assert_eq!(trace.first().unwrap().time_ms, 1250.0);
        assert_eq!(trace.last().unwrap().time_ms, 1270.0);
    }

    #[test]
    fn applies_rate_adjustment_to_real_time_offsets_like_framed_beatmap_clock() {
        assert_eq!(total_audio_offset_ms(38.0, 0.0, 12.0, 1.5), 69.0);
        assert_eq!(total_audio_offset_ms(-20.0, 15.0, 7.0, 0.75), 3.25);
    }

    #[test]
    fn reads_custom_lazer_rate_and_pitch_settings() {
        let mods = serde_json::json!([
            {"acronym":"DT","settings":{"speed_change":1.26,"adjust_pitch":true}}
        ]);
        let (clock, names) = replay_clock_from_api_mods(&mods).unwrap();
        assert_eq!(names, vec!["DoubleTime"]);
        assert_eq!(clock.playback_rate, 1.26);
        assert!(!clock.preserves_pitch);
        assert!(clock.constant_rate);

        let variable = serde_json::json!([{"acronym":"WU"}]);
        let (clock, _) = replay_clock_from_api_mods(&variable).unwrap();
        assert!(!clock.constant_rate);
    }

    #[test]
    fn analyzes_external_lazer_replay_when_requested() {
        let Some(path) = std::env::var_os("AIMMOD_OSU_TEST_REPLAY") else {
            return;
        };
        let path = PathBuf::from(path);
        let result = analyze_replay_file(&path);
        assert!(result.parse_error.is_none(), "{:?}", result.parse_error);
        let metrics = result.frame_metrics.as_ref().expect("replay frame metrics");
        assert!(metrics.frame_count > 1);
        assert!(metrics.end_time_ms > metrics.start_time_ms);
        let beatmap = result.beatmap_trace.as_ref().expect("local beatmap trace");
        let audio_path = beatmap.audio_path.as_deref().expect("local beatmap audio");
        assert!(Path::new(audio_path).is_file(), "{audio_path}");
        assert!(beatmap.playback_rate > 0.0);
        assert!(beatmap.total_audio_offset_ms.is_finite());
        eprintln!(
            "external replay clock: start={:.3} end={:.3} rate={:.3} offset={:.3} audio={audio_path}",
            metrics.start_time_ms,
            metrics.end_time_ms,
            beatmap.playback_rate,
            beatmap.total_audio_offset_ms,
        );
    }

    #[test]
    fn serializes_the_tauri_contract_in_camel_case() {
        let response = analyze_replay_files(Vec::new());
        let value = serde_json::to_value(response).unwrap();
        assert!(value.get("items").is_some());
        assert!(value.get("error").is_some());
        let metrics = OsuReplayFrameMetrics {
            frame_count: 1,
            start_time_ms: 0.0,
            end_time_ms: 0.0,
            duration_ms: 0.0,
            cursor_distance: 0.0,
            average_cursor_speed: 0.0,
            p95_cursor_speed: 0.0,
            peak_cursor_speed: 0.0,
            left_presses: 0,
            right_presses: 0,
            simultaneous_presses: 0,
            key_presses_per_second: 0.0,
        };
        let value = serde_json::to_value(metrics).unwrap();
        assert!(value.get("frameCount").is_some());
        assert!(value.get("keyPressesPerSecond").is_some());
        let value = serde_json::to_value(OsuReplayNotableSegment {
            kind: "inputPeak".to_string(),
            label: "Densest input window".to_string(),
            start_ms: 0.0,
            end_ms: 5_000.0,
            detail: "4 recorded press transitions occurred in this replay window.".to_string(),
            cursor_speed: 100.0,
            key_presses: 4,
            object_count: Some(3),
            first_object_index: Some(2),
            last_object_index: Some(4),
        })
        .unwrap();
        assert!(value.get("startMs").is_some());
        assert!(value.get("keyPresses").is_some());
    }

    #[test]
    fn reads_official_judgement_sidecar_contract() {
        let response: OsuExactReplayJudgements = serde_json::from_str(
            r#"{
                "engineVersion":"ppy.osu.Game/2026.730.0",
                "timeBasis":"officialRulesetPlayback",
                "pauses":[1200],
                "judgements":[{
                    "objectIndex":4,
                    "nestedPath":"0",
                    "objectType":"SliderHeadCircle",
                    "startTimeMs":5000,
                    "endTimeMs":5000,
                    "result":"Great",
                    "maximumResult":"Great",
                    "judgementTimeMs":5012,
                    "timeOffsetMs":12,
                    "gameplayRate":1,
                    "objectPosition":{"x":128,"y":192},
                    "cursorPosition":{"x":130,"y":191},
                    "comboBefore":3,
                    "comboAfter":4
                }],
                "summary":{"great":1,"ok":0,"meh":0,"miss":0,"sliderBreaks":0,"other":0},
                "error":null
            }"#,
        )
        .unwrap();
        assert_eq!(response.pauses, vec![1200]);
        assert_eq!(response.judgements[0].object_index, Some(4));
        assert_eq!(response.judgements[0].time_offset_ms, 12.0);
        assert_eq!(response.summary.great, 1);
    }

    #[test]
    #[ignore = "requires AIMMOD_OSU_REAL_REPLAY and AIMMOD_OSU_REAL_BEATMAP_HASH"]
    fn reconstructs_real_replay_with_official_ruleset() {
        let replay = std::env::var("AIMMOD_OSU_REAL_REPLAY").unwrap();
        let beatmap_hash = std::env::var("AIMMOD_OSU_REAL_BEATMAP_HASH").unwrap();
        let exact = reconstruct_exact_judgements(Path::new(&replay), &beatmap_hash).unwrap();
        assert_eq!(exact.time_basis, "officialRulesetPlayback");
        assert!(!exact.judgements.is_empty());
        assert!(exact.error.is_none());
    }
}
