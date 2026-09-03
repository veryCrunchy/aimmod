use md5::{Digest, Md5};
use once_cell::sync::Lazy;
use serde::Serialize;
use sha2::Sha256;
use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

const MAX_CONTENT_STORE_FILES: usize = 500_000;
const MAX_BEATMAP_FILE_BYTES: u64 = 16 * 1024 * 1024;
const MAX_EXPORTED_REPLAYS: usize = 20_000;
const OSU_FILE_HEADER: &[u8] = b"osu file format v";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalBeatmap {
    pub provider: String,
    pub beatmapset_id: String,
    pub beatmap_id: String,
    pub artist: String,
    pub title: String,
    pub creator: String,
    pub difficulty_name: String,
    pub mode: String,
    pub star_rating: Option<f64>,
    pub bpm: Option<f64>,
    pub length_seconds: Option<u32>,
    pub status: String,
    pub cover_image_url: Option<String>,
    pub audio_path: Option<String>,
    pub preview_time_ms: i32,
    pub user_offset_ms: f64,
    pub skillsets: Vec<String>,
    pub local_state: String,
    pub plays: Option<u32>,
    pub favorites: Option<u32>,
    pub pp95: Option<f64>,
    pub accuracy: Option<f64>,
    pub circle_size: Option<f64>,
    pub approach_rate: Option<f64>,
    pub overall_difficulty: Option<f64>,
    pub hp_drain: Option<f64>,
    pub content_hash: String,
    pub md5_hash: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalReplayCounts {
    pub count_300: u16,
    pub count_100: u16,
    pub count_50: u16,
    pub count_miss: u16,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalReplay {
    pub path: String,
    pub file_name: String,
    pub storage_source: String,
    pub mode: String,
    pub player_name: String,
    pub score: u32,
    pub max_combo: u16,
    pub perfect: bool,
    pub mods: Vec<String>,
    pub played_at: String,
    pub counts: OsuLocalReplayCounts,
    pub beatmap_hash: String,
    pub beatmap_title: Option<String>,
    pub difficulty_name: Option<String>,
    pub cover_image_url: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalBeatmapLibrary {
    pub items: Vec<OsuLocalBeatmap>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalReplayLibrary {
    pub items: Vec<OsuLocalReplay>,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
struct ReplayCandidate {
    inspection: crate::osu::OsuReplayInspection,
    storage_source: &'static str,
}

#[derive(Debug, Clone, Default)]
struct LibrarySnapshot {
    beatmaps: Vec<OsuLocalBeatmap>,
    replays: Vec<OsuLocalReplay>,
    errors: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct StorageFingerprint {
    path: PathBuf,
    realm: Option<(u64, u128)>,
    files: Option<(u64, u128)>,
    exports: Option<(u64, u128)>,
}

#[derive(Debug, Clone)]
struct CachedLibrary {
    fingerprints: Vec<StorageFingerprint>,
    snapshot: LibrarySnapshot,
}

static LIBRARY_CACHE: Lazy<Mutex<Option<CachedLibrary>>> = Lazy::new(|| Mutex::new(None));

pub async fn list_local_beatmaps() -> OsuLocalBeatmapLibrary {
    match tokio::task::spawn_blocking(library_snapshot).await {
        Ok(Ok(snapshot)) => OsuLocalBeatmapLibrary {
            items: snapshot.beatmaps,
            error: combined_error(&snapshot.errors),
        },
        Ok(Err(error)) => OsuLocalBeatmapLibrary {
            items: Vec::new(),
            error: Some(error),
        },
        Err(error) => OsuLocalBeatmapLibrary {
            items: Vec::new(),
            error: Some(format!(
                "The local osu! library scan stopped unexpectedly: {error}"
            )),
        },
    }
}

pub async fn list_local_replays() -> OsuLocalReplayLibrary {
    match tokio::task::spawn_blocking(library_snapshot).await {
        Ok(Ok(snapshot)) => OsuLocalReplayLibrary {
            items: snapshot.replays,
            error: combined_error(&snapshot.errors),
        },
        Ok(Err(error)) => OsuLocalReplayLibrary {
            items: Vec::new(),
            error: Some(error),
        },
        Err(error) => OsuLocalReplayLibrary {
            items: Vec::new(),
            error: Some(format!(
                "The local osu! replay scan stopped unexpectedly: {error}"
            )),
        },
    }
}

#[derive(Debug, Clone)]
pub(crate) struct LocalBeatmapPlayback {
    pub beatmap_path: PathBuf,
    pub audio_path: Option<PathBuf>,
    pub storage_root: PathBuf,
    pub preview_time_ms: i32,
    pub user_offset_ms: f64,
}

fn combined_error(errors: &[String]) -> Option<String> {
    (!errors.is_empty()).then(|| errors.join(" "))
}

fn library_snapshot() -> Result<LibrarySnapshot, String> {
    let roots = detected_storage_roots();
    if roots.is_empty() {
        return Err("AimMod could not find an osu!lazer data folder.".to_string());
    }
    let fingerprints: Vec<_> = roots.iter().map(|root| fingerprint(root)).collect();
    let mut cache = LIBRARY_CACHE
        .lock()
        .map_err(|_| "AimMod's local osu! library cache is unavailable.".to_string())?;
    if let Some(cached) = cache
        .as_ref()
        .filter(|cached| cached.fingerprints == fingerprints)
    {
        return Ok(cached.snapshot.clone());
    }

    let snapshot = discover_library(&roots);
    *cache = Some(CachedLibrary {
        fingerprints,
        snapshot: snapshot.clone(),
    });
    Ok(snapshot)
}

pub(crate) fn resolve_local_beatmap_path(replay_beatmap_hash: &str) -> Option<PathBuf> {
    resolve_local_beatmap_playback(replay_beatmap_hash).map(|playback| playback.beatmap_path)
}

pub(crate) fn resolve_local_beatmap_playback(
    replay_beatmap_hash: &str,
) -> Option<LocalBeatmapPlayback> {
    let snapshot = library_snapshot().ok()?;
    let beatmap = snapshot.beatmaps.iter().find(|beatmap| {
        beatmap.md5_hash.eq_ignore_ascii_case(replay_beatmap_hash)
            || beatmap
                .content_hash
                .eq_ignore_ascii_case(replay_beatmap_hash)
    })?;
    detected_storage_roots().into_iter().find_map(|root| {
        let hash = &beatmap.content_hash;
        if hash.len() < 2 {
            return None;
        }
        let path = root
            .join("files")
            .join(&hash[..1])
            .join(&hash[..2])
            .join(hash);
        path.is_file().then(|| LocalBeatmapPlayback {
            beatmap_path: path,
            audio_path: beatmap.audio_path.as_deref().map(PathBuf::from),
            storage_root: root,
            preview_time_ms: beatmap.preview_time_ms,
            user_offset_ms: beatmap.user_offset_ms,
        })
    })
}

fn detected_storage_roots() -> Vec<PathBuf> {
    let mut seen = HashSet::new();
    crate::osu::lazer_data_candidates()
        .into_iter()
        .filter_map(|path| path.canonicalize().ok())
        .filter(|path| path.is_dir() && seen.insert(path.clone()))
        .filter(|path| path.join("client.realm").is_file() || path.join("files").is_dir())
        .collect()
}

fn metadata_stamp(path: &Path) -> Option<(u64, u128)> {
    let metadata = path.metadata().ok()?;
    let modified = metadata
        .modified()
        .ok()?
        .duration_since(UNIX_EPOCH)
        .ok()?
        .as_nanos();
    Some((metadata.len(), modified))
}

fn directory_stamp(path: &Path) -> Option<(u64, u128)> {
    let metadata = path.metadata().ok()?;
    let mut newest = metadata.modified().unwrap_or(SystemTime::UNIX_EPOCH);
    let mut entries = 0_u64;
    if let Ok(children) = fs::read_dir(path) {
        for child in children.flatten() {
            entries += 1;
            if let Ok(modified) = child.metadata().and_then(|metadata| metadata.modified()) {
                newest = newest.max(modified);
            }
        }
    }
    Some((
        entries,
        newest
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
    ))
}

fn fingerprint(root: &Path) -> StorageFingerprint {
    StorageFingerprint {
        path: root.to_path_buf(),
        realm: metadata_stamp(&root.join("client.realm")),
        files: directory_stamp(&root.join("files")),
        exports: directory_stamp(&root.join("exports")),
    }
}

fn discover_library(roots: &[PathBuf]) -> LibrarySnapshot {
    let mut snapshot = LibrarySnapshot::default();
    let mut replay_candidates = Vec::new();
    let mut seen_content_hashes = HashSet::new();
    let mut score_summaries: HashMap<String, (u32, f64, String)> = HashMap::new();

    for root in roots {
        match crate::osu_realm_reader::read_beatmaps(root) {
            Ok(items) => snapshot
                .beatmaps
                .extend(items.into_iter().map(local_beatmap_from_realm)),
            Err(error) => {
                snapshot.errors.push(format!(
                    "AimMod could not read authoritative lazer beatmap metadata and used a file scan instead: {error}"
                ));
                scan_content_store(
                    root,
                    &mut snapshot.beatmaps,
                    &mut replay_candidates,
                    &mut seen_content_hashes,
                    &mut snapshot.errors,
                );
            }
        }

        match crate::osu_realm_reader::read_scores(root) {
            Ok(scores) => {
                for score in scores {
                    let summary = score_summaries
                        .entry(score.beatmap_hash.clone())
                        .or_insert((0, score.accuracy_percent, score.played_at.clone()));
                    summary.0 = summary.0.saturating_add(1);
                    if score.played_at > summary.2 {
                        summary.1 = score.accuracy_percent;
                        summary.2 = score.played_at.clone();
                    }
                    let Some(path) = score.replay_path.map(PathBuf::from) else {
                        continue;
                    };
                    if let Some(inspection) = crate::osu::inspect_lazer_store_replay(&path) {
                        replay_candidates.push(ReplayCandidate {
                            inspection,
                            storage_source: "lazerStore",
                        });
                    }
                }
            }
            Err(error) => snapshot.errors.push(format!(
                "AimMod could not read lazer score relationships from Realm: {error}"
            )),
        }
    }

    for beatmap in &mut snapshot.beatmaps {
        if let Some((plays, latest_accuracy, _)) = score_summaries.get(&beatmap.content_hash) {
            beatmap.plays = Some(*plays);
            beatmap.accuracy = Some(*latest_accuracy);
        }
    }

    let beatmap_metadata: HashMap<_, _> = snapshot
        .beatmaps
        .iter()
        .map(|beatmap| {
            (
                beatmap.md5_hash.clone(),
                (beatmap.title.clone(), beatmap.difficulty_name.clone()),
            )
        })
        .collect();

    let mut replay_by_hash = HashMap::new();
    for replay in replay_candidates {
        if let Some(item) = local_replay(replay, &beatmap_metadata) {
            replay_by_hash.insert(replay_identity(&item), item);
        }
    }
    for root in roots {
        scan_exports(
            root,
            &beatmap_metadata,
            &mut replay_by_hash,
            &mut snapshot.errors,
        );
    }
    snapshot.replays = replay_by_hash.into_values().collect();

    snapshot.beatmaps.sort_by(|left, right| {
        left.artist
            .to_lowercase()
            .cmp(&right.artist.to_lowercase())
            .then_with(|| left.title.to_lowercase().cmp(&right.title.to_lowercase()))
            .then_with(|| {
                left.difficulty_name
                    .to_lowercase()
                    .cmp(&right.difficulty_name.to_lowercase())
            })
    });
    snapshot
        .replays
        .sort_by(|left, right| right.played_at.cmp(&left.played_at));
    snapshot
}

fn local_beatmap_from_realm(item: crate::osu_realm_reader::RealmBeatmap) -> OsuLocalBeatmap {
    OsuLocalBeatmap {
        provider: "local".to_string(),
        beatmapset_id: item.beatmapset_id,
        beatmap_id: item.beatmap_id,
        artist: item.artist,
        title: item.title,
        creator: item.creator,
        difficulty_name: item.difficulty_name,
        mode: item.mode,
        star_rating: item.star_rating,
        bpm: item.bpm,
        length_seconds: item.length_seconds,
        status: realm_status_name(&item.status),
        cover_image_url: item.background_path,
        audio_path: item.audio_path,
        preview_time_ms: item.preview_time_ms,
        user_offset_ms: item.user_offset_ms,
        skillsets: Vec::new(),
        local_state: "Installed".to_string(),
        plays: None,
        favorites: None,
        pp95: None,
        accuracy: None,
        circle_size: item.circle_size,
        approach_rate: item.approach_rate,
        overall_difficulty: item.overall_difficulty,
        hp_drain: item.hp_drain,
        content_hash: item.content_hash,
        md5_hash: item.md5_hash,
    }
}

fn realm_status_name(status: &str) -> String {
    match status {
        "-3" => "Locally modified",
        "-2" => "Graveyard",
        "-1" => "Work in progress",
        "0" => "Pending",
        "1" => "Ranked",
        "2" => "Approved",
        "3" => "Qualified",
        "4" => "Loved",
        _ => "Local",
    }
    .to_string()
}

fn scan_content_store(
    root: &Path,
    beatmaps: &mut Vec<OsuLocalBeatmap>,
    replays: &mut Vec<ReplayCandidate>,
    seen_hashes: &mut HashSet<String>,
    errors: &mut Vec<String>,
) {
    let store = root.join("files");
    let Ok(first_level) = fs::read_dir(&store) else {
        if root.join("client.realm").is_file() {
            errors.push(format!(
                "AimMod could not read the lazer file store at {}.",
                store.display()
            ));
        }
        return;
    };

    let mut visited = 0_usize;
    'outer: for first in first_level.flatten() {
        let Some(first_name) = first.file_name().to_str().map(str::to_string) else {
            continue;
        };
        if first_name.len() != 1 || !is_lower_hex(&first_name) || !is_directory(&first.path()) {
            continue;
        }
        let Ok(second_level) = fs::read_dir(first.path()) else {
            continue;
        };
        for second in second_level.flatten() {
            let Some(second_name) = second.file_name().to_str().map(str::to_string) else {
                continue;
            };
            if second_name.len() != 2
                || !second_name.starts_with(&first_name)
                || !is_lower_hex(&second_name)
                || !is_directory(&second.path())
            {
                continue;
            }
            let Ok(files) = fs::read_dir(second.path()) else {
                continue;
            };
            for entry in files.flatten() {
                visited += 1;
                if visited > MAX_CONTENT_STORE_FILES {
                    errors.push(format!(
                        "The lazer file store contains more than AimMod's {MAX_CONTENT_STORE_FILES} file scan limit."
                    ));
                    break 'outer;
                }
                let Some(hash) = entry.file_name().to_str().map(str::to_string) else {
                    continue;
                };
                if hash.len() != 64
                    || !hash.starts_with(&second_name)
                    || !is_lower_hex(&hash)
                    || !seen_hashes.insert(hash.clone())
                    || !is_regular_file(&entry.path())
                {
                    continue;
                }
                classify_store_file(&entry.path(), &hash, beatmaps, replays);
            }
        }
    }
}

fn classify_store_file(
    path: &Path,
    content_hash: &str,
    beatmaps: &mut Vec<OsuLocalBeatmap>,
    replays: &mut Vec<ReplayCandidate>,
) {
    let Ok(metadata) = path.metadata() else {
        return;
    };
    if metadata.len() == 0 {
        return;
    }
    let Ok(mut file) = File::open(path) else {
        return;
    };
    let mut prefix = [0_u8; 24];
    let Ok(read) = file.read(&mut prefix) else {
        return;
    };
    let prefix = &prefix[..read];
    let text_prefix = prefix.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(prefix);

    if text_prefix.starts_with(OSU_FILE_HEADER) && metadata.len() <= MAX_BEATMAP_FILE_BYTES {
        if file.seek(SeekFrom::Start(0)).is_err() {
            return;
        }
        let mut bytes = Vec::with_capacity(metadata.len() as usize);
        if file.read_to_end(&mut bytes).is_ok() {
            if let Some(beatmap) = parse_beatmap(&bytes, content_hash) {
                beatmaps.push(beatmap);
            }
        }
        return;
    }

    if looks_like_lazer_replay_prefix(prefix) {
        if let Some(inspection) = crate::osu::inspect_lazer_store_replay(path) {
            replays.push(ReplayCandidate {
                inspection,
                storage_source: "lazerStore",
            });
        }
    }
}

fn looks_like_lazer_replay_prefix(prefix: &[u8]) -> bool {
    if prefix.len() < 7 || prefix[0] > 3 || prefix[5] != 0x0b || prefix[6] != 32 {
        return false;
    }
    let version = u32::from_le_bytes([prefix[1], prefix[2], prefix[3], prefix[4]]);
    (30_000_000..100_000_000).contains(&version)
}

fn parse_beatmap(bytes: &[u8], content_hash: &str) -> Option<OsuLocalBeatmap> {
    if format!("{:x}", Sha256::digest(bytes)) != content_hash {
        return None;
    }
    let text = std::str::from_utf8(bytes)
        .ok()?
        .trim_start_matches('\u{feff}');
    if !text.starts_with("osu file format v") {
        return None;
    }

    let mut section = "";
    let mut values = HashMap::new();
    let mut bpm = None;
    let mut last_object_time = None::<u32>;

    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with("//") {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') {
            section = line;
            continue;
        }
        match section {
            "[General]" | "[Metadata]" | "[Difficulty]" => {
                if let Some((key, value)) = line.split_once(':') {
                    values.insert(key.trim().to_string(), value.trim().to_string());
                }
            }
            "[TimingPoints]" if bpm.is_none() => {
                let fields: Vec<_> = line.split(',').map(str::trim).collect();
                let beat_length = fields.get(1).and_then(|value| value.parse::<f64>().ok());
                let uninherited = fields.get(6).is_none_or(|value| *value == "1");
                if uninherited {
                    if let Some(beat_length) = beat_length.filter(|value| *value > 0.0) {
                        bpm = Some(60_000.0 / beat_length);
                    }
                }
            }
            "[HitObjects]" => {
                if let Some(time) = hit_object_end_time(line) {
                    last_object_time = Some(last_object_time.unwrap_or(0).max(time));
                }
            }
            _ => {}
        }
    }

    let artist = required_value(&values, "Artist")?;
    let title = required_value(&values, "Title")?;
    let creator = required_value(&values, "Creator")?;
    let difficulty_name = required_value(&values, "Version")?;
    let md5_hash = format!("{:x}", Md5::digest(bytes));
    let beatmap_id = positive_id(values.get("BeatmapID")).unwrap_or_else(|| md5_hash.clone());
    let beatmapset_id =
        positive_id(values.get("BeatmapSetID")).unwrap_or_else(|| content_hash.to_string());

    Some(OsuLocalBeatmap {
        provider: "local".to_string(),
        beatmapset_id,
        beatmap_id,
        artist,
        title,
        creator,
        difficulty_name,
        mode: mode_name(values.get("Mode").map(String::as_str)),
        star_rating: None,
        bpm: bpm.filter(|value| value.is_finite() && *value > 0.0),
        length_seconds: last_object_time.map(|milliseconds| milliseconds.div_ceil(1000)),
        status: "local".to_string(),
        cover_image_url: None,
        audio_path: None,
        preview_time_ms: values
            .get("PreviewTime")
            .and_then(|value| value.parse::<i32>().ok())
            .unwrap_or(-1),
        user_offset_ms: 0.0,
        skillsets: Vec::new(),
        local_state: "Installed".to_string(),
        plays: None,
        favorites: None,
        pp95: None,
        accuracy: None,
        circle_size: number_value(&values, "CircleSize"),
        approach_rate: number_value(&values, "ApproachRate"),
        overall_difficulty: number_value(&values, "OverallDifficulty"),
        hp_drain: number_value(&values, "HPDrainRate"),
        content_hash: content_hash.to_string(),
        md5_hash,
    })
}

fn required_value(values: &HashMap<String, String>, key: &str) -> Option<String> {
    values
        .get(key)
        .map(|value| value.trim())
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn positive_id(value: Option<&String>) -> Option<String> {
    let value = value?.trim();
    value
        .parse::<u64>()
        .ok()
        .filter(|id| *id > 0)
        .map(|_| value.to_string())
}

fn number_value(values: &HashMap<String, String>, key: &str) -> Option<f64> {
    values
        .get(key)
        .and_then(|value| value.parse::<f64>().ok())
        .filter(|value| value.is_finite())
}

fn mode_name(value: Option<&str>) -> String {
    match value.unwrap_or("0") {
        "1" => "taiko",
        "2" => "catch",
        "3" => "mania",
        _ => "osu",
    }
    .to_string()
}

fn hit_object_end_time(line: &str) -> Option<u32> {
    let fields: Vec<_> = line.split(',').map(str::trim).collect();
    let start = fields.get(2)?.parse::<u32>().ok()?;
    let object_type = fields.get(3)?.parse::<u32>().ok()?;
    if object_type & 8 != 0 {
        return fields
            .get(5)
            .and_then(|value| value.parse::<u32>().ok())
            .or(Some(start));
    }
    if object_type & 128 != 0 {
        return fields
            .get(5)
            .and_then(|value| value.split(':').next())
            .and_then(|value| value.parse::<u32>().ok())
            .or(Some(start));
    }
    Some(start)
}

fn scan_exports(
    root: &Path,
    beatmaps: &HashMap<String, (String, String)>,
    replays: &mut HashMap<String, OsuLocalReplay>,
    errors: &mut Vec<String>,
) {
    let exports = root.join("exports");
    let Ok(entries) = fs::read_dir(&exports) else {
        return;
    };
    let mut paths = Vec::new();
    for entry in entries.flatten() {
        if paths.len() >= MAX_EXPORTED_REPLAYS {
            errors.push(format!(
                "The replay exports folder contains more than AimMod's {MAX_EXPORTED_REPLAYS} file scan limit."
            ));
            break;
        }
        let path = entry.path();
        if is_regular_file(&path)
            && path
                .extension()
                .and_then(|extension| extension.to_str())
                .is_some_and(|extension| extension.eq_ignore_ascii_case("osr"))
        {
            paths.push(path.to_string_lossy().into_owned());
        }
    }

    for inspection in crate::osu::inspect_replay_files(paths) {
        if inspection.parse_error.is_some() {
            continue;
        }
        let Some(item) = local_replay(
            ReplayCandidate {
                inspection,
                storage_source: "export",
            },
            beatmaps,
        ) else {
            continue;
        };
        replays.insert(replay_identity(&item), item);
    }
}

fn local_replay(
    replay: ReplayCandidate,
    beatmaps: &HashMap<String, (String, String)>,
) -> Option<OsuLocalReplay> {
    let inspection = replay.inspection;
    let beatmap_hash = inspection.beatmap_hash?;
    let (beatmap_title, difficulty_name) = beatmaps
        .get(&beatmap_hash)
        .map(|(title, difficulty)| (Some(title.clone()), Some(difficulty.clone())))
        .unwrap_or((None, None));
    let counts = inspection.counts?;
    Some(OsuLocalReplay {
        path: inspection.path,
        file_name: inspection.file_name,
        storage_source: replay.storage_source.to_string(),
        mode: inspection.mode?,
        player_name: inspection.player_name?,
        score: inspection.score?,
        max_combo: inspection.max_combo?,
        perfect: inspection.perfect?,
        mods: inspection.mods?.names,
        played_at: inspection.played_at?,
        counts: OsuLocalReplayCounts {
            count_300: counts.count_300,
            count_100: counts.count_100,
            count_50: counts.count_50,
            count_miss: counts.count_miss,
        },
        beatmap_hash,
        beatmap_title,
        difficulty_name,
        cover_image_url: None,
    })
}

fn replay_identity(replay: &OsuLocalReplay) -> String {
    format!(
        "{}:{}:{}:{}",
        replay.beatmap_hash, replay.player_name, replay.played_at, replay.score
    )
}

fn is_lower_hex(value: &str) -> bool {
    value
        .bytes()
        .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn is_directory(path: &Path) -> bool {
    path.symlink_metadata()
        .map(|metadata| metadata.file_type().is_dir())
        .unwrap_or(false)
}

fn is_regular_file(path: &Path) -> bool {
    path.symlink_metadata()
        .map(|metadata| metadata.file_type().is_file())
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::{
        discover_library, hit_object_end_time, local_beatmap_from_realm,
        looks_like_lazer_replay_prefix, parse_beatmap,
    };
    use md5::{Digest, Md5};
    use sha2::Sha256;
    use std::fs;

    const BEATMAP: &str = "osu file format v14\n\n[General]\nMode:0\n\n[Metadata]\nTitle:Shuriken School\nArtist:Herve Lavandier\nCreator:Sotarks\nVersion:Expert\nBeatmapID:12345\nBeatmapSetID:6789\n\n[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\n\n[TimingPoints]\n0,333.333333,4,2,1,60,1,0\n\n[HitObjects]\n64,192,1000,1,0,0:0:0:0:\n256,192,119500,8,0,120500\n";

    #[test]
    fn preserves_realm_resolved_audio_path() {
        let audio_path = "/osu/files/a/b/abcdef";
        let mapped = local_beatmap_from_realm(crate::osu_realm_reader::RealmBeatmap {
            beatmapset_id: "6789".into(),
            beatmap_id: "12345".into(),
            artist: "Herve Lavandier".into(),
            title: "Shuriken School".into(),
            creator: "Sotarks".into(),
            difficulty_name: "Expert".into(),
            mode: "osu".into(),
            star_rating: Some(5.1),
            bpm: Some(180.0),
            length_seconds: Some(121),
            status: "1".into(),
            background_path: Some("/osu/files/c/d/background".into()),
            audio_path: Some(audio_path.into()),
            preview_time_ms: 45_000,
            user_offset_ms: 12.0,
            circle_size: Some(4.0),
            approach_rate: Some(9.0),
            overall_difficulty: Some(8.0),
            hp_drain: Some(5.0),
            content_hash: "content-hash".into(),
            md5_hash: "md5-hash".into(),
        });

        assert_eq!(mapped.audio_path.as_deref(), Some(audio_path));
    }

    #[test]
    fn parses_real_osu_metadata_without_inventing_remote_values() {
        let hash = format!("{:x}", Sha256::digest(BEATMAP.as_bytes()));
        let parsed = parse_beatmap(BEATMAP.as_bytes(), &hash).unwrap();
        assert_eq!(parsed.artist, "Herve Lavandier");
        assert_eq!(parsed.title, "Shuriken School");
        assert_eq!(parsed.difficulty_name, "Expert");
        assert_eq!(parsed.beatmap_id, "12345");
        assert_eq!(parsed.beatmapset_id, "6789");
        assert_eq!(parsed.bpm, Some(180.00000018));
        assert_eq!(parsed.length_seconds, Some(121));
        assert_eq!(parsed.star_rating, None);
        assert_eq!(parsed.cover_image_url, None);
    }

    #[test]
    fn reads_spinner_and_mania_hold_end_times() {
        assert_eq!(hit_object_end_time("256,192,1000,8,0,2500"), Some(2500));
        assert_eq!(
            hit_object_end_time("256,192,1000,128,0,3500:0:0:0:0:"),
            Some(3500)
        );
    }

    #[test]
    fn rejects_unrelated_binary_files_before_replay_parsing() {
        let mut replay = vec![0, 0, 0, 0, 0, 0x0b, 32];
        replay[1..5].copy_from_slice(&30_000_019_u32.to_le_bytes());
        assert!(looks_like_lazer_replay_prefix(&replay));
        assert!(!looks_like_lazer_replay_prefix(b"\0not a replay"));
        assert!(!looks_like_lazer_replay_prefix(b"ID3 audio file"));
    }

    #[test]
    fn scans_only_content_addressed_store_files() {
        let root = tempfile::tempdir().unwrap();
        let hash = format!("{:x}", Sha256::digest(BEATMAP.as_bytes()));
        let path = root
            .path()
            .join("files")
            .join(&hash[..1])
            .join(&hash[..2])
            .join(&hash);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, BEATMAP).unwrap();
        fs::write(root.path().join("client.realm"), b"read-only fixture").unwrap();

        let snapshot = discover_library(&[root.path().to_path_buf()]);
        assert_eq!(snapshot.beatmaps.len(), 1);
        assert_eq!(snapshot.beatmaps[0].content_hash, hash);
        assert_eq!(
            snapshot.beatmaps[0].md5_hash,
            format!("{:x}", Md5::digest(BEATMAP.as_bytes()))
        );
        assert!(snapshot.replays.is_empty());
    }

    #[test]
    fn scans_external_lazer_library_when_requested() {
        let Some(path) = std::env::var_os("AIMMOD_OSU_TEST_LIBRARY") else {
            return;
        };
        let snapshot = discover_library(&[path.into()]);
        assert!(snapshot.beatmaps.len() > 100);
        assert!(
            snapshot
                .replays
                .iter()
                .any(|replay| replay.beatmap_title.is_some())
        );
        eprintln!(
            "external lazer library: {} beatmaps, {} replays, errors={:?}",
            snapshot.beatmaps.len(),
            snapshot.replays.len(),
            snapshot.errors
        );
    }
}
