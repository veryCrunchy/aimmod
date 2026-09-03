use once_cell::sync::Lazy;
use serde::Serialize;
use serde_json::Value;
use std::env;
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{Mutex, mpsc};
use std::thread;
use std::time::{Duration, Instant};

const READY_TIMEOUT: Duration = Duration::from_secs(90);
const GRACEFUL_EXIT_TIMEOUT: Duration = Duration::from_secs(2);
const MAX_REPLAY_BYTES: u64 = 256 * 1024 * 1024;

static LAUNCH_LOCK: Lazy<tokio::sync::Mutex<()>> = Lazy::new(|| tokio::sync::Mutex::new(()));
static RUNNING_HOST: Lazy<Mutex<Option<RunningReplayHost>>> = Lazy::new(|| Mutex::new(None));

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeReplayLaunchResult {
    pub launched: bool,
    pub process_id: u32,
    pub beatmap_hash: String,
    pub staged_file_count: usize,
    pub staged_bytes: u64,
    pub renderer: &'static str,
    pub storage_mode: &'static str,
    pub active_skin_applied: bool,
}

struct RunningReplayHost {
    child: Child,
    stdin: ChildStdin,
    // The archive must outlive startup. Dropping it also removes AimMod's staged copy.
    _staged_beatmap: crate::osu_realm_reader::StagedBeatmapSet,
}

pub async fn watch_replay(replay_path: String) -> Result<NativeReplayLaunchResult, String> {
    let _launch_guard = LAUNCH_LOCK.lock().await;
    let (canonical_replay, beatmap_hash) = validate_replay(&replay_path)?;
    let staged = crate::osu_realm_reader::stage_beatmap_set_osz(&beatmap_hash).await?;

    let (master_volume, music_volume, effect_volume) =
        match crate::osu_realm_reader::get_replay_theme().await {
            Ok(theme) => (
                theme.volume_universal,
                theme.volume_music,
                theme.volume_effect,
            ),
            Err(error) => {
                log::warn!(
                    "Could not mirror osu!lazer volume settings for the replay host: {error}"
                );
                (1.0, 1.0, 1.0)
            }
        };

    let staged_file_count = staged.file_count;
    let staged_bytes = staged.total_bytes;
    let executable = replay_host_command()?;
    let result = tokio::task::spawn_blocking(move || {
        stop_running_host();
        launch_and_wait(
            executable,
            canonical_replay,
            staged,
            master_volume,
            music_volume,
            effect_volume,
        )
    })
    .await
    .map_err(|error| format!("The native replay launch task failed: {error}"))??;

    Ok(NativeReplayLaunchResult {
        launched: true,
        process_id: result,
        beatmap_hash,
        staged_file_count,
        staged_bytes,
        renderer: "official ppy.osu.Game ReplayPlayer in a native window",
        storage_mode: "isolated host storage; source lazer storage is read-only",
        active_skin_applied: false,
    })
}

pub fn stop_running_host() {
    let Ok(mut slot) = RUNNING_HOST.lock() else {
        log::error!("Native replay host lock was poisoned; could not stop the child cleanly");
        return;
    };
    let Some(mut running) = slot.take() else {
        return;
    };

    let _ = running.stdin.write_all(b"{\"type\":\"close\"}\n");
    let _ = running.stdin.flush();
    let deadline = Instant::now() + GRACEFUL_EXIT_TIMEOUT;
    loop {
        match running.child.try_wait() {
            Ok(Some(status)) => {
                log::info!("Official osu! replay host exited with {status}");
                return;
            }
            Ok(None) if Instant::now() < deadline => thread::sleep(Duration::from_millis(25)),
            Ok(None) => break,
            Err(error) => {
                log::warn!("Could not query the official osu! replay host: {error}");
                break;
            }
        }
    }

    if let Err(error) = running.child.kill() {
        log::warn!("Could not stop the official osu! replay host: {error}");
    }
    let _ = running.child.wait();
}

fn validate_replay(raw_path: &str) -> Result<(PathBuf, String), String> {
    let path = PathBuf::from(raw_path);
    if !path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("osr"))
    {
        return Err("Select an .osr replay file.".to_string());
    }

    let canonical = path
        .canonicalize()
        .map_err(|error| format!("Could not resolve the selected replay: {error}"))?;
    let metadata = fs::metadata(&canonical)
        .map_err(|error| format!("Could not inspect the selected replay: {error}"))?;
    if !metadata.is_file() || metadata.len() > MAX_REPLAY_BYTES {
        return Err("The selected replay is not a readable file or exceeds 256 MiB.".to_string());
    }

    let inspection =
        crate::osu::inspect_replay_files(vec![canonical.to_string_lossy().into_owned()])
            .into_iter()
            .next()
            .ok_or_else(|| "The selected replay could not be inspected.".to_string())?;
    if let Some(error) = inspection.parse_error {
        return Err(format!("The selected replay is invalid: {error}"));
    }
    let hash = inspection
        .beatmap_hash
        .filter(|hash| hash.len() == 32 && hash.bytes().all(|byte| byte.is_ascii_hexdigit()))
        .ok_or_else(|| {
            "The selected replay does not contain a valid beatmap MD5 hash.".to_string()
        })?;

    Ok((canonical, hash.to_ascii_lowercase()))
}

fn launch_and_wait(
    executable: PathBuf,
    replay: PathBuf,
    staged: crate::osu_realm_reader::StagedBeatmapSet,
    master_volume: f64,
    music_volume: f64,
    effect_volume: f64,
) -> Result<u32, String> {
    let mut child = Command::new(&executable)
        .arg("--beatmap")
        .arg(&staged.osz_path)
        .arg("--replay")
        .arg(&replay)
        .arg("--master")
        .arg(format_volume(master_volume))
        .arg("--music")
        .arg(format_volume(music_volume))
        .arg("--effects")
        .arg(format_volume(effect_volume))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| {
            format!(
                "Could not start the bundled official osu! replay host at {}: {error}",
                executable.display()
            )
        })?;

    let process_id = child.id();
    let Some(stdin) = child.stdin.take() else {
        terminate_spawned_child(&mut child);
        return Err("The official replay host did not expose its command channel.".to_string());
    };
    let Some(stdout) = child.stdout.take() else {
        terminate_spawned_child(&mut child);
        return Err("The official replay host did not expose its status channel.".to_string());
    };
    let stderr = child.stderr.take();
    let (ready_sender, ready_receiver) = mpsc::channel::<Result<(), String>>();

    if let Err(error) = thread::Builder::new()
        .name("osu-replay-host-status".to_string())
        .spawn(move || read_status(stdout, ready_sender))
    {
        terminate_spawned_child(&mut child);
        return Err(format!(
            "Could not monitor the official replay host: {error}"
        ));
    }
    if let Some(stderr) = stderr {
        let _ = thread::Builder::new()
            .name("osu-replay-host-errors".to_string())
            .spawn(move || {
                for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                    let line = line.trim();
                    if !line.is_empty() {
                        log::warn!("official osu! replay host: {}", truncate_line(line));
                    }
                }
            });
    }

    let Ok(mut running_host) = RUNNING_HOST.lock() else {
        terminate_spawned_child(&mut child);
        return Err("The native replay host lock was poisoned.".to_string());
    };
    running_host.replace(RunningReplayHost {
        child,
        stdin,
        _staged_beatmap: staged,
    });
    drop(running_host);

    match ready_receiver.recv_timeout(READY_TIMEOUT) {
        Ok(Ok(())) => Ok(process_id),
        Ok(Err(error)) => {
            stop_running_host();
            Err(error)
        }
        Err(mpsc::RecvTimeoutError::Timeout) => {
            stop_running_host();
            Err("The official osu! replay host did not become ready within 90 seconds.".to_string())
        }
        Err(mpsc::RecvTimeoutError::Disconnected) => {
            stop_running_host();
            Err("The official osu! replay host exited before the replay was ready.".to_string())
        }
    }
}

fn terminate_spawned_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

fn read_status(stdout: impl std::io::Read, sender: mpsc::Sender<Result<(), String>>) {
    let mut sender = Some(sender);
    for line in BufReader::new(stdout).lines() {
        let Ok(line) = line else {
            break;
        };
        let Ok(message) = serde_json::from_str::<Value>(&line) else {
            log::warn!(
                "Official osu! replay host returned invalid status JSON: {}",
                truncate_line(&line)
            );
            continue;
        };
        match message.get("type").and_then(Value::as_str) {
            Some("ready") => {
                if let Some(sender) = sender.take() {
                    let _ = sender.send(Ok(()));
                }
            }
            Some("fatal") => {
                let detail = message
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("unknown native host failure");
                if let Some(sender) = sender.take() {
                    let _ = sender.send(Err(format!(
                        "The official osu! replay host could not load this replay: {detail}"
                    )));
                } else {
                    log::error!("Official osu! replay host failed: {detail}");
                }
            }
            Some("hello" | "state" | "ack" | "ended") => {}
            Some(kind) => log::debug!("Official osu! replay host event: {kind}"),
            None => log::warn!("Official osu! replay host returned an event without a type"),
        }
    }

    if let Some(sender) = sender {
        let _ = sender.send(Err(
            "The official osu! replay host exited before the replay was ready.".to_string(),
        ));
    }
}

fn format_volume(value: f64) -> String {
    format!("{:.6}", value.clamp(0.0, 1.0))
}

fn truncate_line(line: &str) -> &str {
    const MAX_LOG_LINE: usize = 2048;
    if line.len() <= MAX_LOG_LINE {
        return line;
    }
    let mut boundary = MAX_LOG_LINE;
    while !line.is_char_boundary(boundary) {
        boundary -= 1;
    }
    &line[..boundary]
}

fn replay_host_command() -> Result<PathBuf, String> {
    if let Some(path) = env::var_os("AIMMOD_OSU_REPLAY_HOST").map(PathBuf::from) {
        return require_file(path, "AIMMOD_OSU_REPLAY_HOST");
    }

    if let Ok(current_exe) = env::current_exe() {
        if let Some(directory) = current_exe.parent() {
            let sibling = directory.join(executable_name("osu-replay-host"));
            if sibling.is_file() {
                return Ok(sibling);
            }
        }
    }

    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let bundled = manifest.join("bin").join(executable_name(&format!(
        "osu-replay-host-{}",
        bundled_target_triple()
    )));
    if bundled.is_file() {
        return Ok(bundled);
    }

    let published = manifest
        .join("..")
        .join("tools")
        .join("osu-replay-host")
        .join("publish-single")
        .join(executable_name("osu-replay-host"));
    if published.is_file() {
        return Ok(published);
    }

    Err("This AimMod build does not include the official osu! replay host.".to_string())
}

fn require_file(path: PathBuf, variable: &str) -> Result<PathBuf, String> {
    if path.is_file() {
        Ok(path)
    } else {
        Err(format!(
            "{variable} points to a missing file: {}",
            path.display()
        ))
    }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn volume_arguments_are_bounded() {
        assert_eq!(format_volume(-1.0), "0.000000");
        assert_eq!(format_volume(0.25), "0.250000");
        assert_eq!(format_volume(2.0), "1.000000");
    }

    #[test]
    fn log_lines_are_bounded_without_breaking_short_text() {
        assert_eq!(truncate_line("short"), "short");
        assert_eq!(truncate_line(&"x".repeat(4096)).len(), 2048);
    }

    #[test]
    fn bundled_linux_name_matches_tauri_sidecar_convention() {
        if cfg!(all(target_arch = "x86_64", target_os = "linux")) {
            assert_eq!(
                executable_name(&format!("osu-replay-host-{}", bundled_target_triple())),
                "osu-replay-host-x86_64-unknown-linux-gnu"
            );
        }
    }

    #[cfg(unix)]
    #[test]
    fn supervises_ready_protocol_and_removes_staged_archive_on_stop() {
        use std::os::unix::fs::PermissionsExt;

        stop_running_host();
        let directory = tempfile::tempdir().unwrap();
        let fake_host = directory.path().join("fake-replay-host");
        fs::write(
            &fake_host,
            "#!/bin/sh\nprintf '%s\\n' '{\"type\":\"hello\"}' '{\"type\":\"ready\"}'\nwhile IFS= read -r command; do\n  case \"$command\" in *\"close\"*) exit 0;; esac\ndone\n",
        )
        .unwrap();
        let mut permissions = fs::metadata(&fake_host).unwrap().permissions();
        permissions.set_mode(0o755);
        fs::set_permissions(&fake_host, permissions).unwrap();

        let osz_path = directory.path().join("set.osz");
        fs::write(&osz_path, b"test archive").unwrap();
        let staged = crate::osu_realm_reader::StagedBeatmapSet {
            osz_path: osz_path.clone(),
            file_count: 1,
            total_bytes: 12,
        };
        let process_id = launch_and_wait(
            fake_host,
            directory.path().join("replay.osr"),
            staged,
            1.0,
            1.0,
            1.0,
        )
        .unwrap();
        assert!(process_id > 0);
        assert!(osz_path.is_file());

        stop_running_host();
        assert!(!osz_path.exists());
    }
}
