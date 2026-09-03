use md5::{Digest as _, Md5};
use serde::{Deserialize, Serialize};
use sha2::Sha256;
use std::collections::{BTreeMap, HashSet};
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipWriter};

const MAX_REALM_READER_OUTPUT_BYTES: usize = 128 * 1024 * 1024;
const MAX_OSU_CONFIG_BYTES: u64 = 2 * 1024 * 1024;
const MAX_SKIN_INI_BYTES: u64 = 1024 * 1024;
const MAX_BEATMAP_SET_FILES: usize = 4096;
const MAX_BEATMAP_SET_FILE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_BEATMAP_SET_BYTES: u64 = 2 * 1024 * 1024 * 1024;
const MAX_BEATMAP_FILENAME_BYTES: usize = 1024;
static BEATMAP_EXPORT_SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RealmBeatmap {
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
    pub background_path: Option<String>,
    pub audio_path: Option<String>,
    pub preview_time_ms: i32,
    pub user_offset_ms: f64,
    pub circle_size: Option<f64>,
    pub approach_rate: Option<f64>,
    pub overall_difficulty: Option<f64>,
    pub hp_drain: Option<f64>,
    pub content_hash: String,
    pub md5_hash: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RealmNamedFile {
    pub filename: String,
    pub path: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuInstalledLazerSkin {
    pub id: String,
    pub name: String,
    pub creator: String,
    pub hash: String,
    pub file_count: usize,
    pub files: Vec<RealmNamedFile>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RealmReplayThemeSkin {
    id: String,
    name: String,
    creator: String,
    files: Vec<RealmNamedFileHash>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RealmNamedFileHash {
    pub filename: String,
    pub hash: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RealmBeatmapSetFiles {
    pub beatmap_set_id: String,
    pub online_beatmap_set_id: i32,
    pub beatmap_set_hash: String,
    pub selected_beatmap_id: String,
    pub online_beatmap_id: i32,
    pub selected_content_hash: String,
    pub selected_md5_hash: String,
    pub files: Vec<RealmNamedFileHash>,
}

#[derive(Debug)]
pub struct StagedBeatmapSet {
    pub osz_path: PathBuf,
    pub file_count: usize,
    pub total_bytes: u64,
}

impl Drop for StagedBeatmapSet {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.osz_path);
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayThemeSkin {
    pub id: String,
    pub name: String,
    pub creator: String,
    pub combo_colours: Vec<String>,
    pub normalised_combo_colours: Vec<String>,
    pub cursor_image_hash: Option<String>,
    pub cursor_2x_image_hash: Option<String>,
    pub cursor_trail_image_hash: Option<String>,
    pub cursor_trail_2x_image_hash: Option<String>,
    pub sample_hashes: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuReplayTheme {
    pub active_skin: OsuReplayThemeSkin,
    pub beatmap_colours_enabled: bool,
    pub beatmap_hitsounds_enabled: bool,
    pub beatmap_skins_enabled: bool,
    pub ignore_beatmap_skins: bool,
    pub ignore_beatmap_samples: bool,
    pub use_skin_hitsounds: bool,
    pub combo_colour_normalisation_amount: f64,
    pub preferred_combo_colour_source: String,
    pub preferred_sample_source: String,
    pub volume_universal: f64,
    pub volume_music: f64,
    pub volume_effect: f64,
    pub effective_music_volume: f64,
    pub effective_sample_volume: f64,
    pub positional_hitsounds_level: f64,
    pub audio_offset_ms: f64,
    pub use_experimental_wasapi: bool,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalScore {
    pub id: String,
    pub beatmap_hash: String,
    pub beatmap_id: Option<i32>,
    pub mode: String,
    pub player_name: String,
    pub player_id: i32,
    pub total_score: i64,
    pub total_score_without_mods: i64,
    pub max_combo: i32,
    pub accuracy_percent: f64,
    pub pp: Option<f64>,
    pub played_at: String,
    pub online_id: i64,
    pub legacy_online_id: i64,
    pub client_version: String,
    pub score_hash: String,
    pub mods_json: String,
    pub statistics_json: String,
    pub maximum_statistics_json: String,
    pub pauses: Vec<i32>,
    pub replay_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalScoreLibrary {
    pub items: Vec<OsuLocalScore>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLocalPlayer {
    pub player_id: i32,
    pub player_name: String,
    pub last_played_at: String,
}

enum ReaderCommand {
    Binary(PathBuf),
    Dotnet(PathBuf),
}

fn reader_command() -> Result<ReaderCommand, String> {
    if let Some(path) = env::var_os("AIMMOD_OSU_REALM_READER").map(PathBuf::from) {
        if path.is_file() {
            return Ok(ReaderCommand::Binary(path));
        }
        return Err(format!(
            "AIMMOD_OSU_REALM_READER points to a missing file: {}",
            path.display()
        ));
    }

    if let Ok(current_exe) = env::current_exe() {
        if let Some(directory) = current_exe.parent() {
            let sibling = directory.join(executable_name("osu-realm-reader"));
            if sibling.is_file() {
                return Ok(ReaderCommand::Binary(sibling));
            }
        }
    }

    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let target_binary = manifest.join("bin").join(executable_name(&format!(
        "osu-realm-reader-{}",
        bundled_target_triple()
    )));
    if target_binary.is_file() {
        return Ok(ReaderCommand::Binary(target_binary));
    }

    let development_dll = manifest
        .join("..")
        .join("tools")
        .join("osu-realm-reader")
        .join("bin")
        .join("Release")
        .join("net8.0")
        .join("linux-x64")
        .join("osu-realm-reader.dll");
    if development_dll.is_file() {
        return Ok(ReaderCommand::Dotnet(development_dll));
    }

    Err("The bundled read-only osu!lazer library reader is unavailable.".to_string())
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

fn read_payload_with_argument<T: for<'de> Deserialize<'de>>(
    kind: &str,
    root: &Path,
    argument: Option<&str>,
) -> Result<T, String> {
    let root = root
        .canonicalize()
        .map_err(|error| format!("Could not resolve the osu!lazer data folder: {error}"))?;
    let mut command = match reader_command()? {
        ReaderCommand::Binary(binary) => Command::new(binary),
        ReaderCommand::Dotnet(dll) => {
            let mut command = Command::new("dotnet");
            command.arg(dll);
            command
        }
    };
    command.arg(kind).arg(&root);
    if let Some(argument) = argument {
        command.arg(argument);
    }
    let output = command.output().map_err(|error| {
        format!("Could not start the read-only osu!lazer library reader: {error}")
    })?;

    if !output.status.success() {
        let message = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if message.is_empty() {
            format!(
                "The read-only osu!lazer library reader exited with {}.",
                output.status
            )
        } else {
            message
        });
    }
    if output.stdout.len() > MAX_REALM_READER_OUTPUT_BYTES {
        return Err("The osu!lazer library response exceeded AimMod's safety limit.".to_string());
    }
    serde_json::from_slice(&output.stdout)
        .map_err(|error| format!("The osu!lazer library reader returned invalid data: {error}"))
}

fn read_payload<T: for<'de> Deserialize<'de>>(kind: &str, root: &Path) -> Result<T, String> {
    read_payload_with_argument(kind, root, None)
}

pub fn read_beatmaps(root: &Path) -> Result<Vec<RealmBeatmap>, String> {
    read_payload("beatmaps", root)
}

pub fn read_skins(root: &Path) -> Result<Vec<OsuInstalledLazerSkin>, String> {
    read_payload("skins", root)
}

pub fn read_scores(root: &Path) -> Result<Vec<OsuLocalScore>, String> {
    read_payload("scores", root)
}

pub fn read_beatmap_set_files(
    root: &Path,
    requested_beatmap_hash: &str,
) -> Result<RealmBeatmapSetFiles, String> {
    read_payload_with_argument("beatmap-set-files", root, Some(requested_beatmap_hash))
}

fn normalise_archive_filename(filename: &str) -> Result<String, String> {
    if filename.is_empty()
        || filename.len() > MAX_BEATMAP_FILENAME_BYTES
        || filename.chars().any(char::is_control)
    {
        return Err("The beatmap set contains an invalid filename.".to_string());
    }
    let normalised = filename.replace('\\', "/");
    if normalised.starts_with('/')
        || normalised.ends_with('/')
        || normalised
            .as_bytes()
            .get(1)
            .is_some_and(|byte| *byte == b':')
    {
        return Err(format!(
            "The beatmap set filename is not relative: {filename}"
        ));
    }
    if normalised
        .split('/')
        .any(|component| component.is_empty() || component == "." || component == "..")
    {
        return Err(format!("The beatmap set filename is unsafe: {filename}"));
    }
    Ok(normalised)
}

fn store_file(root: &Path, store: &Path, hash: &str) -> Result<(PathBuf, u64), String> {
    if !valid_store_hash(hash) {
        return Err(format!(
            "The beatmap set contains an invalid store hash: {hash}"
        ));
    }
    let first = root.join("files").join(&hash[..1]);
    let second = first.join(&hash[..2]);
    let source = second.join(hash);
    for component in [&first, &second, &source] {
        let metadata = component
            .symlink_metadata()
            .map_err(|_| format!("The beatmap set is missing content-store object {hash}."))?;
        if metadata.file_type().is_symlink() {
            return Err(format!(
                "The beatmap set store path contains a symlink for object {hash}."
            ));
        }
    }
    let resolved = source
        .canonicalize()
        .map_err(|error| format!("Could not resolve beatmap set store object {hash}: {error}"))?;
    if !resolved.starts_with(store) {
        return Err(format!(
            "Beatmap set store object {hash} resolves outside the lazer file store."
        ));
    }
    let metadata = resolved
        .metadata()
        .map_err(|error| format!("Could not inspect beatmap set store object {hash}: {error}"))?;
    if !metadata.is_file() {
        return Err(format!(
            "Beatmap set store object {hash} is not a regular file."
        ));
    }
    if metadata.len() > MAX_BEATMAP_SET_FILE_BYTES {
        return Err(format!(
            "Beatmap set store object {hash} exceeds the per-file limit."
        ));
    }
    Ok((resolved, metadata.len()))
}

fn stage_beatmap_set_manifest(
    root: &Path,
    manifest: RealmBeatmapSetFiles,
    cache_root: &Path,
) -> Result<StagedBeatmapSet, String> {
    if manifest.files.is_empty() || manifest.files.len() > MAX_BEATMAP_SET_FILES {
        return Err(format!(
            "The beatmap set must contain between 1 and {MAX_BEATMAP_SET_FILES} files."
        ));
    }
    if !valid_store_hash(&manifest.selected_content_hash)
        || manifest.selected_md5_hash.len() != 32
        || !manifest
            .selected_md5_hash
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err("The selected local beatmap has invalid stored hashes.".to_string());
    }

    let root = root
        .canonicalize()
        .map_err(|error| format!("Could not resolve the osu!lazer data folder: {error}"))?;
    let store_path = root.join("files");
    if store_path
        .symlink_metadata()
        .is_ok_and(|metadata| metadata.file_type().is_symlink())
    {
        return Err("The osu!lazer file store cannot be a symlink.".to_string());
    }
    let store = store_path
        .canonicalize()
        .map_err(|error| format!("Could not resolve the osu!lazer file store: {error}"))?;

    let mut seen_names = HashSet::new();
    let mut selected_usage_found = false;
    let mut total_bytes = 0_u64;
    let mut files = Vec::with_capacity(manifest.files.len());
    for usage in &manifest.files {
        let filename = normalise_archive_filename(&usage.filename)?;
        if !seen_names.insert(filename.to_lowercase()) {
            return Err(format!(
                "The beatmap set contains a duplicate filename after normalisation: {}",
                usage.filename
            ));
        }
        let (source, length) = store_file(&root, &store, &usage.hash)?;
        total_bytes = total_bytes
            .checked_add(length)
            .filter(|total| *total <= MAX_BEATMAP_SET_BYTES)
            .ok_or_else(|| "The beatmap set exceeds AimMod's 2 GiB export limit.".to_string())?;
        selected_usage_found |= usage.hash == manifest.selected_content_hash;
        files.push((filename, usage.hash.clone(), source, length));
    }
    if !selected_usage_found {
        return Err("The selected beatmap file is absent from its Realm beatmap set.".to_string());
    }
    if !files.iter().any(|(name, _, _, _)| {
        name.rsplit_once('.')
            .is_some_and(|(_, extension)| extension.eq_ignore_ascii_case("osu"))
    }) {
        return Err("The local beatmap set contains no .osu difficulties.".to_string());
    }

    let export_directory = cache_root.join("osu-replay-host").join("beatmapsets");
    fs::create_dir_all(&export_directory)
        .map_err(|error| format!("Could not create AimMod's beatmap export cache: {error}"))?;
    let export_directory = export_directory
        .canonicalize()
        .map_err(|error| format!("Could not resolve AimMod's beatmap export cache: {error}"))?;
    let sequence = BEATMAP_EXPORT_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let name = format!(
        "set-{}-{}-{sequence}",
        &manifest.selected_content_hash[..16],
        std::process::id()
    );
    let temporary = export_directory.join(format!(".{name}.tmp"));
    let osz_path = export_directory.join(format!("{name}.osz"));
    let result = (|| -> Result<(), String> {
        let output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
            .map_err(|error| format!("Could not create the staged beatmap set: {error}"))?;
        let mut archive = ZipWriter::new(output);
        let options = SimpleFileOptions::default()
            .compression_method(CompressionMethod::Deflated)
            .unix_permissions(0o644);
        let mut selected_md5 = None;
        for (filename, expected_hash, source, expected_length) in &files {
            archive.start_file(filename, options).map_err(|error| {
                format!("Could not add {filename} to the beatmap archive: {error}")
            })?;
            let mut input = File::open(source)
                .map_err(|error| format!("Could not open beatmap set file {filename}: {error}"))?;
            let metadata = input.metadata().map_err(|error| {
                format!("Could not inspect beatmap set file {filename}: {error}")
            })?;
            if !metadata.is_file() || metadata.len() != *expected_length {
                return Err(format!(
                    "Beatmap set file {filename} changed during export."
                ));
            }
            let mut sha256 = Sha256::new();
            let mut md5 = (expected_hash == &manifest.selected_content_hash).then(Md5::new);
            let mut buffer = [0_u8; 64 * 1024];
            loop {
                let count = input.read(&mut buffer).map_err(|error| {
                    format!("Could not read beatmap set file {filename}: {error}")
                })?;
                if count == 0 {
                    break;
                }
                sha256.update(&buffer[..count]);
                if let Some(hasher) = &mut md5 {
                    hasher.update(&buffer[..count]);
                }
                archive.write_all(&buffer[..count]).map_err(|error| {
                    format!("Could not write beatmap set file {filename}: {error}")
                })?;
            }
            let actual_hash = format!("{:x}", sha256.finalize());
            if actual_hash != *expected_hash {
                return Err(format!(
                    "Beatmap set file {filename} does not match its lazer content hash."
                ));
            }
            if let Some(md5) = md5 {
                selected_md5 = Some(format!("{:x}", md5.finalize()));
            }
        }
        if selected_md5.as_deref() != Some(manifest.selected_md5_hash.as_str()) {
            return Err("The selected .osu file does not match its stored MD5 hash.".to_string());
        }
        let output = archive
            .finish()
            .map_err(|error| format!("Could not finish the staged beatmap set: {error}"))?;
        output
            .sync_all()
            .map_err(|error| format!("Could not flush the staged beatmap set: {error}"))?;
        fs::rename(&temporary, &osz_path)
            .map_err(|error| format!("Could not publish the staged beatmap set: {error}"))?;
        Ok(())
    })();
    if let Err(error) = result {
        let _ = fs::remove_file(&temporary);
        return Err(error);
    }

    Ok(StagedBeatmapSet {
        osz_path,
        file_count: files.len(),
        total_bytes,
    })
}

fn stage_local_beatmap_set(
    root: &Path,
    requested_beatmap_hash: &str,
    cache_root: &Path,
) -> Result<StagedBeatmapSet, String> {
    let manifest = read_beatmap_set_files(root, requested_beatmap_hash)?;
    stage_beatmap_set_manifest(root, manifest, cache_root)
}

fn aimmod_cache_root() -> Result<PathBuf, String> {
    #[cfg(target_os = "windows")]
    let base = env::var_os("LOCALAPPDATA").map(PathBuf::from);
    #[cfg(target_os = "macos")]
    let base = env::var_os("HOME")
        .map(PathBuf::from)
        .map(|home| home.join("Library").join("Caches"));
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    let base = env::var_os("XDG_CACHE_HOME")
        .map(PathBuf::from)
        .or_else(|| {
            env::var_os("HOME")
                .map(PathBuf::from)
                .map(|home| home.join(".cache"))
        });

    base.map(|base| base.join("com.verycrunchy.kovaaks"))
        .ok_or_else(|| "Could not locate AimMod's cache folder.".to_string())
}

pub async fn stage_beatmap_set_osz(beatmap_hash: &str) -> Result<StagedBeatmapSet, String> {
    let beatmap_hash = beatmap_hash.trim().to_ascii_lowercase();
    let cache_root = aimmod_cache_root()?;
    tokio::task::spawn_blocking(move || {
        let mut errors = Vec::new();
        for root in crate::osu::lazer_data_candidates()
            .into_iter()
            .filter(|path| path.join("client.realm").is_file())
        {
            match stage_local_beatmap_set(&root, &beatmap_hash, &cache_root) {
                Ok(staged) => return Ok(staged),
                Err(error) => errors.push(error),
            }
        }
        Err(if errors.is_empty() {
            "AimMod could not find an osu!lazer library containing this replay's beatmap."
                .to_string()
        } else {
            errors.join(" ")
        })
    })
    .await
    .map_err(|error| format!("The local beatmap set exporter stopped unexpectedly: {error}"))?
}

fn read_replay_theme_skin(root: &Path) -> Result<RealmReplayThemeSkin, String> {
    read_payload("replay-theme-skin", root)
}

fn read_allowed_ini(path: &Path, allowed: &[&str]) -> BTreeMap<String, String> {
    let allowed: HashSet<String> = allowed.iter().map(|key| key.to_ascii_lowercase()).collect();
    let Ok(metadata) = path.metadata() else {
        return BTreeMap::new();
    };
    if !metadata.is_file() || metadata.len() > MAX_OSU_CONFIG_BYTES {
        return BTreeMap::new();
    }
    let Ok(contents) = fs::read_to_string(path) else {
        return BTreeMap::new();
    };
    contents
        .lines()
        .filter_map(|line| {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') || line.starts_with(';') {
                return None;
            }
            let (key, value) = line.split_once('=')?;
            let key = key.trim().to_ascii_lowercase();
            allowed
                .contains(&key)
                .then(|| (key, value.trim().to_string()))
        })
        .collect()
}

fn setting_bool(settings: &BTreeMap<String, String>, key: &str, default: bool) -> bool {
    settings
        .get(&key.to_ascii_lowercase())
        .and_then(|value| match value.to_ascii_lowercase().as_str() {
            "true" => Some(true),
            "false" => Some(false),
            _ => None,
        })
        .unwrap_or(default)
}

fn setting_number(
    settings: &BTreeMap<String, String>,
    key: &str,
    default: f64,
    minimum: f64,
    maximum: f64,
) -> f64 {
    settings
        .get(&key.to_ascii_lowercase())
        .and_then(|value| value.parse::<f64>().ok())
        .filter(|value| value.is_finite())
        .map(|value| value.clamp(minimum, maximum))
        .unwrap_or(default)
}

fn valid_store_hash(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn stored_file_path(root: &Path, hash: &str) -> Option<PathBuf> {
    if !valid_store_hash(hash) {
        return None;
    }
    let store = root.join("files").canonicalize().ok()?;
    let path = store
        .join(&hash[..1])
        .join(&hash[..2])
        .join(hash)
        .canonicalize()
        .ok()?;
    path.starts_with(&store).then_some(path).filter(|path| {
        path.metadata()
            .is_ok_and(|metadata| metadata.is_file() && metadata.len() <= MAX_SKIN_INI_BYTES)
    })
}

fn basename(value: &str) -> String {
    value
        .replace('\\', "/")
        .rsplit('/')
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase()
}

pub(crate) fn parse_combo_colours(contents: &str) -> Vec<String> {
    let mut in_colours = false;
    let mut colours = BTreeMap::new();
    for line in contents.lines() {
        let line = line.trim().trim_start_matches('\u{feff}');
        if line.starts_with('[') && line.ends_with(']') {
            in_colours = line[1..line.len() - 1].eq_ignore_ascii_case("Colours");
            continue;
        }
        if !in_colours {
            continue;
        }
        let Some((key, value)) = line.split_once(':').or_else(|| line.split_once('=')) else {
            continue;
        };
        let key = key.trim();
        let Some(index) = key
            .strip_prefix("Combo")
            .or_else(|| key.strip_prefix("combo"))
            .and_then(|value| value.trim().parse::<u8>().ok())
            .filter(|index| (1..=8).contains(index))
        else {
            continue;
        };
        let channels: Vec<_> = value
            .split(',')
            .take(3)
            .filter_map(|channel| channel.trim().parse::<u8>().ok())
            .collect();
        if channels.len() == 3 {
            colours.insert(
                index,
                format!("#{:02X}{:02X}{:02X}", channels[0], channels[1], channels[2]),
            );
        }
    }
    colours.into_values().collect()
}

fn built_in_combo_colours(id: &str) -> Vec<String> {
    match id.to_ascii_lowercase().as_str() {
        "cffa69de-b3e3-4dee-8563-3c4f425c05d0" | "9fc9cf5d-0f16-4c71-8256-98868321ac43" => [
            "#F17400", "#00F135", "#0052F1", "#F10000", "#E8EB00", "#5C00F1",
        ]
        .into_iter()
        .map(str::to_string)
        .collect(),
        "0555c76a-cc6b-4bb4-9548-df76ba72ef25" => ["#FF9600", "#05F005", "#0505F0", "#F00505"]
            .into_iter()
            .map(str::to_string)
            .collect(),
        _ => ["#FFC000", "#00CA00", "#127CFF", "#F21839"]
            .into_iter()
            .map(str::to_string)
            .collect(),
    }
}

fn normalise_combo_colour(value: &str, amount: f64) -> Option<String> {
    let value = value.strip_prefix('#')?;
    if value.len() != 6 {
        return None;
    }
    let channel = |range| {
        u8::from_str_radix(&value[range], 16)
            .ok()
            .map(|v| v as f64 / 255.0)
    };
    let original = [channel(0..2)?, channel(2..4)?, channel(4..6)?];
    let [r, g, b] = original;
    let (h, s) = if r == g && r == b {
        (0.0, 0.0)
    } else if r >= g && r >= b {
        if b >= g {
            (1.0 - (b - g) / (r - g) / 6.0, 1.0 - g / r)
        } else {
            ((g - b) / (r - b) / 6.0, 1.0 - b / r)
        }
    } else if g >= r && g >= b {
        if r >= b {
            (2.0 / 6.0 - (r - b) / (g - b) / 6.0, 1.0 - b / g)
        } else {
            (2.0 / 6.0 + (b - r) / (g - r) / 6.0, 1.0 - r / g)
        }
    } else if g >= r {
        (4.0 / 6.0 - (g - r) / (b - r) / 6.0, 1.0 - r / b)
    } else {
        (4.0 / 6.0 + (r - g) / (b - g) / 6.0, 1.0 - g / b)
    };
    let target = hspa_to_rgb(h, s, 0.6);
    let mixed = [
        r + (target[0] - r) * amount,
        g + (target[1] - g) * amount,
        b + (target[2] - b) * amount,
    ];
    Some(format!(
        "#{:02X}{:02X}{:02X}",
        (mixed[0].clamp(0.0, 1.0) * 255.0).round() as u8,
        (mixed[1].clamp(0.0, 1.0) * 255.0).round() as u8,
        (mixed[2].clamp(0.0, 1.0) * 255.0).round() as u8,
    ))
}

fn hspa_to_rgb(mut h: f64, s: f64, p: f64) -> [f64; 3] {
    const PR: f64 = 0.299;
    const PG: f64 = 0.587;
    const PB: f64 = 0.114;
    let min_over_max = 1.0 - s;
    let mut result = [0.0; 3];
    if min_over_max > 0.0 {
        if h < 1.0 / 6.0 {
            h *= 6.0;
            let part = 1.0 + h * (1.0 / min_over_max - 1.0);
            result[2] = p / (PR / min_over_max.powi(2) + PG * part.powi(2) + PB).sqrt();
            result[0] = result[2] / min_over_max;
            result[1] = result[2] + h * (result[0] - result[2]);
        } else if h < 2.0 / 6.0 {
            h = 6.0 * (-h + 2.0 / 6.0);
            let part = 1.0 + h * (1.0 / min_over_max - 1.0);
            result[2] = p / (PG / min_over_max.powi(2) + PR * part.powi(2) + PB).sqrt();
            result[1] = result[2] / min_over_max;
            result[0] = result[2] + h * (result[1] - result[2]);
        } else if h < 3.0 / 6.0 {
            h = 6.0 * (h - 2.0 / 6.0);
            let part = 1.0 + h * (1.0 / min_over_max - 1.0);
            result[0] = p / (PG / min_over_max.powi(2) + PB * part.powi(2) + PR).sqrt();
            result[1] = result[0] / min_over_max;
            result[2] = result[0] + h * (result[1] - result[0]);
        } else if h < 4.0 / 6.0 {
            h = 6.0 * (-h + 4.0 / 6.0);
            let part = 1.0 + h * (1.0 / min_over_max - 1.0);
            result[0] = p / (PB / min_over_max.powi(2) + PG * part.powi(2) + PR).sqrt();
            result[2] = result[0] / min_over_max;
            result[1] = result[0] + h * (result[2] - result[0]);
        } else if h < 5.0 / 6.0 {
            h = 6.0 * (h - 4.0 / 6.0);
            let part = 1.0 + h * (1.0 / min_over_max - 1.0);
            result[1] = p / (PB / min_over_max.powi(2) + PR * part.powi(2) + PG).sqrt();
            result[2] = result[1] / min_over_max;
            result[0] = result[1] + h * (result[2] - result[1]);
        } else {
            h = 6.0 * (-h + 1.0);
            let part = 1.0 + h * (1.0 / min_over_max - 1.0);
            result[1] = p / (PR / min_over_max.powi(2) + PB * part.powi(2) + PG).sqrt();
            result[0] = result[1] / min_over_max;
            result[2] = result[1] + h * (result[0] - result[1]);
        }
    } else if h < 1.0 / 6.0 {
        h *= 6.0;
        result[0] = (p * p / (PR + PG * h * h)).sqrt();
        result[1] = result[0] * h;
    } else if h < 2.0 / 6.0 {
        h = 6.0 * (-h + 2.0 / 6.0);
        result[1] = (p * p / (PG + PR * h * h)).sqrt();
        result[0] = result[1] * h;
    } else if h < 3.0 / 6.0 {
        h = 6.0 * (h - 2.0 / 6.0);
        result[1] = (p * p / (PG + PB * h * h)).sqrt();
        result[2] = result[1] * h;
    } else if h < 4.0 / 6.0 {
        h = 6.0 * (-h + 4.0 / 6.0);
        result[2] = (p * p / (PB + PG * h * h)).sqrt();
        result[1] = result[2] * h;
    } else if h < 5.0 / 6.0 {
        h = 6.0 * (h - 4.0 / 6.0);
        result[2] = (p * p / (PB + PR * h * h)).sqrt();
        result[0] = result[2] * h;
    } else {
        h = 6.0 * (-h + 1.0);
        result[0] = (p * p / (PR + PB * h * h)).sqrt();
        result[2] = result[0] * h;
    }
    result
}

fn is_gameplay_sample(filename: &str) -> bool {
    let Some((stem, extension)) = filename.rsplit_once('.') else {
        return false;
    };
    if !matches!(extension, "wav" | "mp3" | "ogg") {
        return false;
    }
    stem == "combobreak"
        || stem == "sectionpass"
        || stem == "sectionfail"
        || stem.starts_with("spinnerbonus")
        || stem.starts_with("spinnerspin")
        || ["normal-", "soft-", "drum-"].iter().any(|prefix| {
            stem.starts_with(prefix)
                && [
                    "hitnormal",
                    "hitwhistle",
                    "hitfinish",
                    "hitclap",
                    "slidertick",
                    "sliderslide",
                    "sliderwhistle",
                ]
                .iter()
                .any(|part| stem[prefix.len()..].starts_with(part))
        })
}

fn build_replay_theme(root: &Path) -> Result<OsuReplayTheme, String> {
    let skin = read_replay_theme_skin(root)?;
    let game = read_allowed_ini(
        &root.join("game.ini"),
        &[
            "AudioOffset",
            "BeatmapSkins",
            "BeatmapColours",
            "BeatmapHitsounds",
            "ComboColourNormalisationAmount",
            "PositionalHitsoundsLevel",
        ],
    );
    let framework = read_allowed_ini(
        &root.join("framework.ini"),
        &[
            "VolumeUniversal",
            "VolumeMusic",
            "VolumeEffect",
            "AudioUseExperimentalWasapi",
        ],
    );
    let beatmap_skins_enabled = setting_bool(&game, "BeatmapSkins", true);
    let beatmap_colours_enabled = setting_bool(&game, "BeatmapColours", true);
    let beatmap_hitsounds_enabled = setting_bool(&game, "BeatmapHitsounds", true);
    let normalisation = setting_number(&game, "ComboColourNormalisationAmount", 0.2, 0.0, 1.0);
    let volume_universal = setting_number(&framework, "VolumeUniversal", 0.6, 0.0, 1.0);
    let volume_music = setting_number(&framework, "VolumeMusic", 0.6, 0.0, 1.0);
    let volume_effect = setting_number(&framework, "VolumeEffect", 0.6, 0.0, 1.0);

    let files: BTreeMap<_, _> = skin
        .files
        .iter()
        .filter(|file| valid_store_hash(&file.hash))
        .map(|file| (basename(&file.filename), file.hash.clone()))
        .collect();
    let mut combo_colours = files
        .get("skin.ini")
        .and_then(|hash| stored_file_path(root, hash))
        .and_then(|path| fs::read_to_string(path).ok())
        .map(|contents| parse_combo_colours(&contents))
        .unwrap_or_default();
    if combo_colours.is_empty() {
        combo_colours = built_in_combo_colours(&skin.id);
    }
    let normalised_combo_colours = combo_colours
        .iter()
        .filter_map(|colour| normalise_combo_colour(colour, normalisation))
        .collect();
    let sample_hashes = files
        .iter()
        .filter(|(filename, _)| is_gameplay_sample(filename))
        .take(256)
        .map(|(filename, hash)| (filename.clone(), hash.clone()))
        .collect();
    let active_skin = OsuReplayThemeSkin {
        id: skin.id,
        name: skin.name,
        creator: skin.creator,
        combo_colours,
        normalised_combo_colours,
        cursor_image_hash: files.get("cursor.png").cloned(),
        cursor_2x_image_hash: files.get("cursor@2x.png").cloned(),
        cursor_trail_image_hash: files.get("cursortrail.png").cloned(),
        cursor_trail_2x_image_hash: files.get("cursortrail@2x.png").cloned(),
        sample_hashes,
    };

    Ok(OsuReplayTheme {
        active_skin,
        beatmap_colours_enabled,
        beatmap_hitsounds_enabled,
        beatmap_skins_enabled,
        ignore_beatmap_skins: !beatmap_skins_enabled,
        ignore_beatmap_samples: !beatmap_hitsounds_enabled,
        use_skin_hitsounds: !beatmap_hitsounds_enabled,
        combo_colour_normalisation_amount: normalisation,
        preferred_combo_colour_source: if beatmap_colours_enabled {
            "beatmapWithSkinFallback"
        } else {
            "skin"
        }
        .to_string(),
        preferred_sample_source: if beatmap_hitsounds_enabled {
            "beatmapWithSkinFallback"
        } else {
            "skin"
        }
        .to_string(),
        volume_universal,
        volume_music,
        volume_effect,
        effective_music_volume: volume_universal * volume_music,
        effective_sample_volume: volume_universal * volume_effect,
        positional_hitsounds_level: setting_number(
            &game,
            "PositionalHitsoundsLevel",
            0.2,
            0.0,
            1.0,
        ),
        audio_offset_ms: setting_number(&game, "AudioOffset", 0.0, -500.0, 500.0),
        use_experimental_wasapi: setting_bool(&framework, "AudioUseExperimentalWasapi", false),
    })
}

pub async fn get_replay_theme() -> Result<OsuReplayTheme, String> {
    tokio::task::spawn_blocking(|| {
        let mut errors = Vec::new();
        for root in crate::osu::lazer_data_candidates()
            .into_iter()
            .filter(|path| path.join("client.realm").is_file())
        {
            match build_replay_theme(&root) {
                Ok(theme) => return Ok(theme),
                Err(error) => errors.push(error),
            }
        }
        Err(if errors.is_empty() {
            "AimMod could not find an osu!lazer library for replay settings.".to_string()
        } else {
            errors.join(" ")
        })
    })
    .await
    .map_err(|error| {
        format!("The local lazer replay settings reader stopped unexpectedly: {error}")
    })?
}

pub async fn list_local_scores() -> OsuLocalScoreLibrary {
    match tokio::task::spawn_blocking(|| {
        let mut items = Vec::new();
        let mut errors = Vec::new();
        for root in crate::osu::lazer_data_candidates()
            .into_iter()
            .filter(|path| path.join("client.realm").is_file())
        {
            match read_scores(&root) {
                Ok(mut scores) => items.append(&mut scores),
                Err(error) => errors.push(error),
            }
        }
        items.sort_by(|left, right| right.played_at.cmp(&left.played_at));
        items.dedup_by(|left, right| left.id == right.id);
        (items, errors)
    })
    .await
    {
        Ok((items, errors)) => OsuLocalScoreLibrary {
            items,
            error: (!errors.is_empty()).then(|| errors.join(" ")),
        },
        Err(error) => OsuLocalScoreLibrary {
            items: Vec::new(),
            error: Some(format!(
                "The local lazer score reader stopped unexpectedly: {error}"
            )),
        },
    }
}

fn most_recent_local_player(scores: &[OsuLocalScore]) -> Option<OsuLocalPlayer> {
    scores
        .iter()
        .filter(|score| score.player_id > 0 && !score.player_name.trim().is_empty())
        .max_by(|left, right| left.played_at.cmp(&right.played_at))
        .map(|score| OsuLocalPlayer {
            player_id: score.player_id,
            player_name: score.player_name.trim().to_string(),
            last_played_at: score.played_at.clone(),
        })
}

pub async fn get_local_player() -> Result<Option<OsuLocalPlayer>, String> {
    let library = list_local_scores().await;
    if library.items.is_empty() {
        if let Some(error) = library.error {
            return Err(error);
        }
    }
    Ok(most_recent_local_player(&library.items))
}

pub async fn list_installed_lazer_skins() -> Result<Vec<OsuInstalledLazerSkin>, String> {
    tokio::task::spawn_blocking(|| {
        let mut items = Vec::new();
        let mut errors = Vec::new();
        for root in crate::osu::lazer_data_candidates()
            .into_iter()
            .filter(|path| path.join("client.realm").is_file())
        {
            match read_skins(&root) {
                Ok(mut skins) => items.append(&mut skins),
                Err(error) => errors.push(error),
            }
        }
        items.sort_by(|left, right| left.name.to_lowercase().cmp(&right.name.to_lowercase()));
        items.dedup_by(|left, right| left.id == right.id);
        if items.is_empty() && !errors.is_empty() {
            Err(errors.join(" "))
        } else {
            Ok(items)
        }
    })
    .await
    .map_err(|error| format!("The local lazer skin reader stopped unexpectedly: {error}"))?
}

#[cfg(test)]
mod tests {
    use super::{
        RealmBeatmapSetFiles, RealmNamedFileHash, build_replay_theme, is_gameplay_sample,
        normalise_archive_filename, normalise_combo_colour, parse_combo_colours, read_allowed_ini,
        stage_beatmap_set_manifest,
    };
    use md5::{Digest as _, Md5};
    use sha2::Sha256;
    use std::fs;
    use std::io::Read;

    fn write_store_file(root: &std::path::Path, contents: &[u8]) -> String {
        let hash = format!("{:x}", Sha256::digest(contents));
        let path = root
            .join("files")
            .join(&hash[..1])
            .join(&hash[..2])
            .join(&hash);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, contents).unwrap();
        hash
    }

    fn manifest(selected_contents: &[u8], audio_hash: String) -> RealmBeatmapSetFiles {
        let selected_hash = format!("{:x}", Sha256::digest(selected_contents));
        RealmBeatmapSetFiles {
            beatmap_set_id: "set-id".into(),
            online_beatmap_set_id: 12,
            beatmap_set_hash: "set-hash".into(),
            selected_beatmap_id: "beatmap-id".into(),
            online_beatmap_id: 34,
            selected_content_hash: selected_hash.clone(),
            selected_md5_hash: format!("{:x}", Md5::digest(selected_contents)),
            files: vec![
                RealmNamedFileHash {
                    filename: "Artist - Title [Hard].osu".into(),
                    hash: selected_hash,
                },
                RealmNamedFileHash {
                    filename: "audio/song.mp3".into(),
                    hash: audio_hash,
                },
            ],
        }
    }

    #[test]
    fn reads_only_allowlisted_non_secret_settings() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("game.ini");
        fs::write(
            &path,
            "Token = secret\nUsername = player\nBeatmapSkins = False\nAudioOffset = 38\n",
        )
        .unwrap();
        let settings = read_allowed_ini(&path, &["BeatmapSkins", "AudioOffset"]);
        assert_eq!(
            settings.get("beatmapskins").map(String::as_str),
            Some("False")
        );
        assert_eq!(settings.get("audiooffset").map(String::as_str), Some("38"));
        assert!(!settings.contains_key("token"));
        assert!(!settings.contains_key("username"));
    }

    #[test]
    fn parses_ordered_legacy_skin_combo_colours() {
        let colours = parse_combo_colours(
            "[General]\nName: test\n[Colours]\nCombo3: 1,2,3\nCombo1: 255, 0, 16\nCombo9: 9,9,9\n",
        );
        assert_eq!(colours, vec!["#FF0010", "#010203"]);
    }

    #[test]
    fn ports_official_hspa_brightness_normalisation() {
        assert_eq!(
            normalise_combo_colour("#127CFF", 0.0).as_deref(),
            Some("#127CFF")
        );
        let adjusted = normalise_combo_colour("#127CFF", 1.0).unwrap();
        assert_ne!(adjusted, "#127CFF");
    }

    #[test]
    fn exposes_only_known_gameplay_sample_names() {
        assert!(is_gameplay_sample("normal-hitnormal.wav"));
        assert!(is_gameplay_sample("soft-hitclap2.ogg"));
        assert!(is_gameplay_sample("combobreak.mp3"));
        assert!(!is_gameplay_sample("applause.mp3"));
        assert!(!is_gameplay_sample("cursor.png"));
    }

    #[test]
    fn accepts_only_relative_normalised_beatmap_filenames() {
        assert_eq!(
            normalise_archive_filename("audio\\song.mp3").unwrap(),
            "audio/song.mp3"
        );
        for unsafe_name in [
            "",
            "/absolute.osu",
            "C:\\absolute.osu",
            "../outside.osu",
            "assets/../../outside.osu",
            "assets//song.mp3",
            "assets/./song.mp3",
        ] {
            assert!(
                normalise_archive_filename(unsafe_name).is_err(),
                "accepted {unsafe_name}"
            );
        }
    }

    #[test]
    fn stages_complete_verified_osz_and_removes_it_on_drop() {
        let root = tempfile::tempdir().unwrap();
        let cache = tempfile::tempdir().unwrap();
        let beatmap = b"osu file format v14\n[General]\nAudioFilename: song.mp3\n";
        let audio = b"not-real-audio-but-byte-exact";
        let selected_hash = write_store_file(root.path(), beatmap);
        let audio_hash = write_store_file(root.path(), audio);
        let staged =
            stage_beatmap_set_manifest(root.path(), manifest(beatmap, audio_hash), cache.path())
                .unwrap();
        assert_eq!(staged.file_count, 2);
        assert_eq!(staged.total_bytes, (beatmap.len() + audio.len()) as u64);
        assert!(staged.osz_path.is_file());
        assert_eq!(
            fs::read(
                root.path()
                    .join("files")
                    .join(&selected_hash[..1])
                    .join(&selected_hash[..2])
                    .join(selected_hash)
            )
            .unwrap(),
            beatmap
        );

        let archive_file = fs::File::open(&staged.osz_path).unwrap();
        let mut archive = zip::ZipArchive::new(archive_file).unwrap();
        let mut extracted = Vec::new();
        archive
            .by_name("audio/song.mp3")
            .unwrap()
            .read_to_end(&mut extracted)
            .unwrap();
        assert_eq!(extracted, audio);

        let staged_path = staged.osz_path.clone();
        drop(staged);
        assert!(!staged_path.exists());
    }

    #[test]
    fn rejects_duplicate_normalised_names_and_missing_store_objects() {
        let root = tempfile::tempdir().unwrap();
        let cache = tempfile::tempdir().unwrap();
        let beatmap = b"osu file format v14\n";
        let audio_hash = write_store_file(root.path(), b"audio");
        write_store_file(root.path(), beatmap);
        let mut duplicate = manifest(beatmap, audio_hash);
        duplicate.files[1].filename = "ARTIST - TITLE [HARD].OSU".into();
        assert!(
            stage_beatmap_set_manifest(root.path(), duplicate, cache.path())
                .unwrap_err()
                .contains("duplicate filename")
        );

        let missing_hash = "f".repeat(64);
        let missing = manifest(beatmap, missing_hash);
        assert!(
            stage_beatmap_set_manifest(root.path(), missing, cache.path())
                .unwrap_err()
                .contains("missing content-store object")
        );
    }

    #[cfg(unix)]
    #[test]
    fn rejects_symlinked_store_objects() {
        use std::os::unix::fs::symlink;

        let root = tempfile::tempdir().unwrap();
        let cache = tempfile::tempdir().unwrap();
        let beatmap = b"osu file format v14\n";
        write_store_file(root.path(), beatmap);
        let external = tempfile::NamedTempFile::new().unwrap();
        fs::write(external.path(), b"external").unwrap();
        let audio_hash = format!("{:x}", Sha256::digest(b"external"));
        let audio_path = root
            .path()
            .join("files")
            .join(&audio_hash[..1])
            .join(&audio_hash[..2])
            .join(&audio_hash);
        fs::create_dir_all(audio_path.parent().unwrap()).unwrap();
        symlink(external.path(), audio_path).unwrap();
        assert!(
            stage_beatmap_set_manifest(root.path(), manifest(beatmap, audio_hash), cache.path())
                .unwrap_err()
                .contains("symlink")
        );
    }

    #[test]
    fn reads_external_replay_theme_when_requested() {
        let Some(path) = std::env::var_os("AIMMOD_OSU_TEST_LIBRARY") else {
            return;
        };
        let theme = build_replay_theme(path.as_ref()).unwrap();
        assert!(!theme.active_skin.id.is_empty());
        assert!(!theme.active_skin.name.is_empty());
        assert!(!theme.active_skin.combo_colours.is_empty());
        let json = serde_json::to_string(&theme).unwrap();
        assert!(!json.to_ascii_lowercase().contains("token"));
        assert!(!json.contains("/home/"));
    }

    #[test]
    fn stages_external_beatmap_set_when_requested() {
        let (Some(root), Some(hash)) = (
            std::env::var_os("AIMMOD_OSU_TEST_LIBRARY"),
            std::env::var_os("AIMMOD_OSU_TEST_BEATMAP_HASH"),
        ) else {
            return;
        };
        let cache = tempfile::tempdir().unwrap();
        let staged =
            super::stage_local_beatmap_set(root.as_ref(), hash.to_str().unwrap(), cache.path())
                .unwrap();
        assert!(staged.osz_path.is_file());
        assert!(staged.file_count > 1);
        assert!(staged.total_bytes > 0);
        assert_eq!(
            zip::ZipArchive::new(fs::File::open(&staged.osz_path).unwrap())
                .unwrap()
                .len(),
            staged.file_count
        );
    }
}
