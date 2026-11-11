//! Replay video capture through an embedded FTHR engine.
//!
//! FTHR (NVENC replay buffer, recording H.264) runs as a child process under a private
//! instance name, so it never collides with a standalone FTHR install. A
//! background watcher starts it once the game window exists and restarts it
//! when the capture geometry changes between sessions. At session end AimMod
//! asks it to save the session span as an MP4; the reply carries the clip's
//! QPC interval, which lines the video up with the replay timeline.
//!
//! When the engine is missing, still starting, or unhealthy, `session_started`
//! returns false and `screen_recorder` keeps using its JPEG capture path.

use serde::{Deserialize, Serialize};

/// One continuous stretch of the replay timeline and where it sits in the clip.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ReplayVideoSegment {
    /// Replay-timeline start, in the same ms domain as replay positions/frames.
    pub replay_start_ms: u64,
    /// Offset into the video file.
    pub video_start_ms: u64,
    pub duration_ms: u64,
}

/// A replay's saved video. `path` is filled in when the replay is loaded and is
/// never trusted from disk.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ReplayVideo {
    pub file_name: String,
    #[serde(default)]
    pub path: String,
    pub codec: String,
    pub width: u32,
    pub height: u32,
    pub segments: Vec<ReplayVideoSegment>,
}

/// What FTHR produced for a run. `recorded` is true when FTHR recorded the run
/// (so no JPEG frames exist), even if its clip was then lost.
#[derive(Debug, Default)]
pub struct RunVideo {
    pub video: Option<ReplayVideo>,
    pub recorded: bool,
}

#[cfg(target_os = "windows")]
pub use imp::*;

#[cfg(not(target_os = "windows"))]
mod fallback {
    use super::RunVideo;
    pub fn init(_app: &tauri::AppHandle, _videos_dir: std::path::PathBuf) {}
    pub fn session_started(_session_start: std::time::Instant) -> bool {
        false
    }
    pub fn session_stopped() {}
    pub fn take_video_for_run(
        _run_snapshot: Option<&crate::bridge::BridgeRunSnapshot>,
    ) -> RunVideo {
        RunVideo::default()
    }
}
#[cfg(not(target_os = "windows"))]
pub use fallback::*;

#[cfg(target_os = "windows")]
mod imp {
    use super::{ReplayVideo, ReplayVideoSegment, RunVideo};
    use once_cell::sync::{Lazy, OnceCell};
    use std::collections::VecDeque;
    use std::os::windows::io::AsRawHandle;
    use std::os::windows::process::CommandExt;
    use std::path::{Path, PathBuf};
    use std::process::{Child, Command, Stdio};
    use std::sync::{Condvar, Mutex};
    use std::time::{Duration, Instant};
    use tauri::Manager;
    use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, POINT, RECT};
    use windows::Win32::Graphics::Gdi::{
        ClientToScreen, DISPLAY_DEVICEW, EnumDisplayDevicesW, GetMonitorInfoW,
        MONITOR_DEFAULTTONEAREST, MONITORINFO, MONITORINFOEXW, MonitorFromWindow,
    };
    use windows::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
        SetInformationJobObject,
    };
    use windows::Win32::System::Memory::{
        FILE_MAP_ALL_ACCESS, MEMORY_MAPPED_VIEW_ADDRESS, MapViewOfFile, OpenFileMappingW,
        UnmapViewOfFile,
    };
    use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
    use windows::Win32::UI::WindowsAndMessaging::GetClientRect;
    use windows::core::{PCWSTR, w};

    const INSTANCE_NAME: &str = "aimmod";
    const SHARED_MEMORY_NAME: PCWSTR = w!("FTHR_SharedMemory_v4_aimmod");
    /// Replay buffer length. Runs longer than this lose their opening seconds.
    const BUFFER_SECONDS: u32 = 185;
    /// H.264: AimMod's WebView2 has no HEVC decoder (canPlayType("hvc1") is
    /// empty even with Microsoft's HEVC extension), so HEVC clips can't play.
    const CODEC_H264: u32 = 1;
    const ENGINE_INIT_TIMEOUT: Duration = Duration::from_secs(8);
    const SAVE_TIMEOUT: Duration = Duration::from_secs(30);
    /// After a save times out, how long the next save waits for the engine's
    /// late reply before sending its own command.
    const STALE_SAVE_GRACE: Duration = Duration::from_secs(15);
    /// Longest a save can take: draining a late reply, then its own timeout.
    /// The claim returns as soon as the run's clip arrives, so this is only
    /// reached when a save is failing.
    const WAIT_FOR_SAVE_TIMEOUT: Duration =
        Duration::from_secs(STALE_SAVE_GRACE.as_secs() + SAVE_TIMEOUT.as_secs() + 5);
    /// Shut the engine down after the game window has been gone this long.
    const IDLE_SHUTDOWN: Duration = Duration::from_secs(60);
    const WATCH_INTERVAL: Duration = Duration::from_secs(2);
    /// Geometry must hold this long before the engine (re)starts, so window
    /// resizes while KovaaK's launches don't churn the engine.
    const GEOMETRY_SETTLE: Duration = Duration::from_secs(3);
    /// Smaller client areas are splash/loading windows, not the game.
    const MIN_CLIENT_WIDTH: u32 = 640;
    const MIN_CLIENT_HEIGHT: u32 = 360;
    /// Saved clips nobody claimed are deleted after this long.
    const UNCLAIMED_CLIP_TTL_MS: u64 = 10 * 60 * 1000;

    const CMD_SAVE_CLIP: u32 = 3;
    const RESP_NONE: u32 = 0;
    const RESP_CLIP_SAVED: u32 = 3;
    const RESP_ERROR: u32 = 5;
    const RESP_SAVE_STARTED: u32 = 6;
    const HEALTH_BACKEND_FAILED: u32 = 1 << 2;

    // ─── Shared memory (FTHR shared_memory.h, layout v4) ────────────────────────
    //
    // Byte-for-byte copy of fthr::SharedMemoryLayout. C++ bool is one byte, so
    // the bools are u8 here. The size assert catches most drift; the engine's
    // own contract checker owns the rest.
    #[repr(C)]
    #[allow(dead_code)]
    struct SharedLayout {
        ui_command: u32,
        ui_param1: u32,
        ui_param2: u32,
        ui_param3: u32,
        ui_string: [u16; 256],
        engine_response: u32,
        engine_param1: u32,
        engine_param2: u32,
        engine_param3: f32,
        engine_string: [u16; 512],
        is_recording: u8,
        is_initialized: u8,
        frames_captured: u64,
        bytes_written: u64,
        cfg_bitrate_kbps: u32,
        cfg_target_width: u32,
        cfg_target_height: u32,
        nvenc_active: u8,
        cfg_codec_pref: u32,
        cfg_preset: u32,
        active_codec: [u8; 64],
        multiband_enabled: u8,
        active_audio_mappings: [u8; 1024],
        capture_health_flags: u32,
        capture_generation: u32,
        content_sample_sequence: u32,
        content_suspicious_streak: u32,
        content_luma_mean: f32,
        content_luma_variance: f32,
    }
    const _: () = assert!(std::mem::size_of::<SharedLayout>() == 2736);

    macro_rules! rd {
        ($layout:expr, $field:ident) => {
            unsafe { std::ptr::read_volatile(std::ptr::addr_of!((*$layout).$field)) }
        };
    }
    macro_rules! wr {
        ($layout:expr, $field:ident, $value:expr) => {
            unsafe { std::ptr::write_volatile(std::ptr::addr_of_mut!((*$layout).$field), $value) }
        };
    }

    // ─── State ──────────────────────────────────────────────────────────────────

    struct Paths {
        engine_exe: Option<PathBuf>,
        pending_dir: PathBuf,
        log_path: Option<PathBuf>,
    }

    #[derive(Debug, Clone, PartialEq)]
    struct Geometry {
        monitor_path: String,
        /// Normalized crop inside the monitor, rounded so jitter doesn't restart.
        crop: [f64; 4],
        out_width: u32,
        out_height: u32,
        fps: u32,
        bitrate_kbps: u32,
    }

    struct Engine {
        child: Child,
        mapping: HANDLE,
        view: MEMORY_MAPPED_VIEW_ADDRESS,
        geometry: Geometry,
    }
    // The mapping is process-wide memory; access is serialized by STATE.
    unsafe impl Send for Engine {}

    impl Engine {
        fn layout(&self) -> *mut SharedLayout {
            self.view.Value as *mut SharedLayout
        }

        fn is_alive(&mut self) -> bool {
            matches!(self.child.try_wait(), Ok(None))
        }

        fn is_healthy(&mut self) -> bool {
            if !self.is_alive() || self.view.Value.is_null() {
                return false;
            }
            let layout = self.layout();
            rd!(layout, is_initialized) != 0
                && rd!(layout, capture_health_flags) & HEALTH_BACKEND_FAILED == 0
        }

        fn codec_name(&self) -> String {
            if self.view.Value.is_null() {
                return String::new();
            }
            let raw: [u8; 64] = rd!(self.layout(), active_codec);
            let end = raw.iter().position(|b| *b == 0).unwrap_or(raw.len());
            String::from_utf8_lossy(&raw[..end]).into_owned()
        }
    }

    impl Drop for Engine {
        fn drop(&mut self) {
            if !self.view.Value.is_null() {
                let _ = unsafe { UnmapViewOfFile(self.view) };
            }
            if !self.mapping.is_invalid() {
                let _ = unsafe { CloseHandle(self.mapping) };
            }
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }

    struct SessionClock {
        start_qpc_s: f64,
        start_unix_ms: u64,
    }

    struct CompletedClip {
        path: PathBuf,
        codec: String,
        width: u32,
        height: u32,
        clip_start_unix_ms: u64,
        clip_end_unix_ms: u64,
        session_start_unix_ms: u64,
        session_end_unix_ms: u64,
    }

    /// A session FTHR recorded, kept whether or not its clip was saved.
    pub(super) struct RecordedSpan {
        pub(super) start_unix_ms: u64,
        pub(super) end_unix_ms: u64,
    }

    /// A save that timed out. The engine may still answer it later.
    struct StaleSave {
        path: PathBuf,
        timed_out_at: Instant,
    }

    #[derive(Default)]
    struct State {
        engine: Option<Engine>,
        session: Option<SessionClock>,
        pending_saves: usize,
        completed: VecDeque<CompletedClip>,
        recorded: VecDeque<RecordedSpan>,
        game_missing_since: Option<Instant>,
        spawn_backoff_until: Option<Instant>,
        /// Last geometry seen and when it was first seen unchanged.
        settling_geometry: Option<(Geometry, Instant)>,
    }

    static PATHS: OnceCell<Paths> = OnceCell::new();
    static STATE: Lazy<Mutex<State>> = Lazy::new(|| Mutex::new(State::default()));
    static SAVES_DONE: Condvar = Condvar::new();
    /// Serializes SAVE_CLIP commands: the engine has one response slot. Holds
    /// the last timed-out save so the next one can drain its late reply.
    static SAVE_CHANNEL: Mutex<Option<StaleSave>> = Mutex::new(None);
    static JOB: OnceCell<usize> = OnceCell::new();

    // ─── Public API ─────────────────────────────────────────────────────────────

    /// Resolves engine paths and starts the watcher. Safe to call once at startup.
    pub fn init(app: &tauri::AppHandle, videos_dir: PathBuf) {
        let pending_dir = videos_dir.join("pending");
        let _ = std::fs::create_dir_all(&pending_dir);
        let _ = app.asset_protocol_scope().allow_directory(&videos_dir, false);
        clear_stale_pending(&pending_dir);

        let engine_exe = resolve_engine_exe(app);
        match &engine_exe {
            Some(path) => log::info!("fthr_recorder: engine at {}", path.display()),
            None => log::info!("fthr_recorder: engine not found; using JPEG replay capture"),
        }
        let log_path = app
            .path()
            .app_log_dir()
            .ok()
            .map(|dir| {
                let _ = std::fs::create_dir_all(&dir);
                dir.join("fthr-engine.log")
            });
        if PATHS
            .set(Paths {
                engine_exe: engine_exe.clone(),
                pending_dir,
                log_path,
            })
            .is_err()
        {
            return;
        }
        if engine_exe.is_some() {
            let _ = std::thread::Builder::new()
                .name("fthr-watch".into())
                .spawn(watch_loop);
        }
    }

    /// Called when a replay session starts. Returns true when FTHR will record
    /// this session; false means the caller should use its own capture.
    pub fn session_started(session_start: Instant) -> bool {
        let mut state = STATE.lock().unwrap();
        if state.session.is_some() {
            // Restarted without a stop: close out the previous span first.
            drop(state);
            session_stopped();
            state = STATE.lock().unwrap();
        }
        let Some(engine) = state.engine.as_mut() else {
            return false;
        };
        if !engine.is_healthy() {
            log::warn!("fthr_recorder: engine not ready at session start; using JPEG capture");
            return false;
        }
        let elapsed = session_start.elapsed();
        state.session = Some(SessionClock {
            start_qpc_s: qpc_now_s() - elapsed.as_secs_f64(),
            start_unix_ms: unix_now_ms().saturating_sub(elapsed.as_millis() as u64),
        });
        true
    }

    /// Called when a replay session stops. Saves the span in the background.
    pub fn session_stopped() {
        let mut state = STATE.lock().unwrap();
        let Some(session) = state.session.take() else {
            return;
        };
        let session_end_unix_ms = unix_now_ms();
        // Recorded even if the save below fails: JPEG capture was skipped for
        // this session, so the replay must be kept without video.
        state.recorded.push_back(RecordedSpan {
            start_unix_ms: session.start_unix_ms,
            end_unix_ms: session_end_unix_ms,
        });
        let Some(engine) = state.engine.as_ref() else {
            log::warn!("fthr_recorder: engine gone at session stop; no clip saved");
            return;
        };
        let layout = engine.layout() as usize;
        let codec = engine.codec_name();
        let (width, height) = (engine.geometry.out_width, engine.geometry.out_height);
        state.pending_saves += 1;
        drop(state);

        let elapsed_s = (qpc_now_s() - session.start_qpc_s).max(0.0);
        let duration_s = ((elapsed_s.ceil() as u32) + 2).clamp(1, BUFFER_SECONDS);
        let spawned = std::thread::Builder::new()
            .name("fthr-save".into())
            .spawn(move || {
                let result = save_clip(layout as *mut SharedLayout, duration_s);
                let mut state = STATE.lock().unwrap();
                state.pending_saves = state.pending_saves.saturating_sub(1);
                match result {
                    Ok((path, start_qpc_s, end_qpc_s)) => {
                        // Sample both clocks together to convert QPC to unix ms.
                        let now_qpc_s = qpc_now_s();
                        let now_unix_ms = unix_now_ms() as f64;
                        let to_unix = |qpc_s: f64| {
                            (now_unix_ms - (now_qpc_s - qpc_s) * 1000.0).max(0.0).round() as u64
                        };
                        log::info!(
                            "fthr_recorder: saved {} ({:.2}s requested, clip {:.2}s)",
                            path.display(),
                            duration_s,
                            end_qpc_s - start_qpc_s
                        );
                        state.completed.push_back(CompletedClip {
                            path,
                            codec,
                            width,
                            height,
                            clip_start_unix_ms: to_unix(start_qpc_s),
                            clip_end_unix_ms: to_unix(end_qpc_s),
                            session_start_unix_ms: session.start_unix_ms,
                            session_end_unix_ms,
                        });
                    }
                    Err(error) => log::warn!("fthr_recorder: clip save failed: {error}"),
                }
                SAVES_DONE.notify_all();
            });
        if spawned.is_err() {
            let mut state = STATE.lock().unwrap();
            state.pending_saves = state.pending_saves.saturating_sub(1);
        }
    }

    /// Claims the saved clip that covers this run and maps it onto the replay
    /// timeline. Waits for an in-flight save. The returned `path` points at the
    /// pending file; `replay_store` moves it into place.
    pub fn take_video_for_run(
        run_snapshot: Option<&crate::bridge::BridgeRunSnapshot>,
    ) -> RunVideo {
        // The snapshot start is the replay timeline's zero point (mouse
        // positions use it too). Its computed end can be far too early when
        // tracking started before the challenge (start + run duration), so the
        // window runs until the session actually stopped.
        let snapshot_start = run_snapshot.and_then(|s| s.started_at_unix_ms);
        let snapshot_end = run_snapshot.and_then(|s| s.ended_at_unix_ms);

        let mut state = STATE.lock().unwrap();
        let deadline = Instant::now() + WAIT_FOR_SAVE_TIMEOUT;
        while state.pending_saves > 0 {
            // Stop waiting once this run's clip is in; other saves may still be queued.
            if let Some(run_start) = snapshot_start {
                if best_overlap(&state.completed, run_start, snapshot_end).is_some() {
                    break;
                }
            }
            let now = Instant::now();
            if now >= deadline {
                log::warn!("fthr_recorder: timed out waiting for clip save");
                break;
            }
            state = SAVES_DONE.wait_timeout(state, deadline - now).unwrap().0;
        }
        let recorded = take_recorded_span(&mut state.recorded, snapshot_start, snapshot_end);
        if state.completed.is_empty() {
            return RunVideo { video: None, recorded };
        }

        let index = match snapshot_start {
            Some(run_start) => best_overlap(&state.completed, run_start, snapshot_end)
                .unwrap_or(state.completed.len() - 1),
            None => state.completed.len() - 1,
        };
        let Some(clip) = state.completed.remove(index) else {
            return RunVideo { video: None, recorded };
        };
        drop(state);

        let run_start = snapshot_start.unwrap_or(clip.session_start_unix_ms);
        let run_end = snapshot_end
            .unwrap_or(0)
            .max(clip.session_end_unix_ms)
            .max(run_start);
        let pauses: Vec<(u64, u64)> = run_snapshot
            .map(|s| {
                s.pause_windows
                    .iter()
                    .map(|w| (w.started_at_unix_ms, w.ended_at_unix_ms))
                    .collect()
            })
            .unwrap_or_default();
        let segments = build_segments(
            run_start,
            run_end,
            &pauses,
            clip.clip_start_unix_ms,
            clip.clip_end_unix_ms,
        );
        if segments.is_empty() {
            log::warn!("fthr_recorder: saved clip does not overlap the run; discarding");
            let _ = std::fs::remove_file(&clip.path);
            return RunVideo { video: None, recorded };
        }
        RunVideo {
            video: Some(ReplayVideo {
                file_name: String::new(),
                path: clip.path.to_string_lossy().into_owned(),
                codec: clip.codec,
                width: clip.width,
                height: clip.height,
                segments,
            }),
            recorded: true,
        }
    }

    /// Removes the recorded sessions that overlap this run. Returns true when
    /// FTHR recorded any part of it.
    pub(super) fn take_recorded_span(
        recorded: &mut VecDeque<RecordedSpan>,
        run_start: Option<u64>,
        run_end: Option<u64>,
    ) -> bool {
        let Some(run_start) = run_start else {
            // No run timing to match against: claim the latest session.
            return recorded.pop_back().is_some();
        };
        let before = recorded.len();
        recorded.retain(|span| {
            let end = run_end.unwrap_or(0).max(span.end_unix_ms);
            let overlaps = span.start_unix_ms < end && span.end_unix_ms > run_start;
            !overlaps
        });
        recorded.len() != before
    }

    // ─── Timeline mapping ───────────────────────────────────────────────────────

    fn best_overlap(
        clips: &VecDeque<CompletedClip>,
        run_start: u64,
        snapshot_end: Option<u64>,
    ) -> Option<usize> {
        clips
            .iter()
            .enumerate()
            .map(|(i, c)| {
                let run_end = snapshot_end.unwrap_or(0).max(c.session_end_unix_ms);
                let overlap = c.clip_end_unix_ms.min(run_end).saturating_sub(c.clip_start_unix_ms.max(run_start));
                (i, overlap)
            })
            .filter(|(_, overlap)| *overlap > 0)
            .max_by_key(|(_, overlap)| *overlap)
            .map(|(i, _)| i)
    }

    /// Splits the run into unpaused stretches and maps each onto the clip.
    /// Replay time excludes paused time, matching `screen_recorder`'s frames.
    pub(super) fn build_segments(
        run_start: u64,
        run_end: u64,
        pauses: &[(u64, u64)],
        clip_start: u64,
        clip_end: u64,
    ) -> Vec<ReplayVideoSegment> {
        // Clip pauses to the run and merge overlaps, so a pause that began
        // before the run (e.g. pause menu -> Restart) or a doubled pause window
        // is never subtracted twice.
        let mut clipped: Vec<(u64, u64)> = pauses
            .iter()
            .map(|(s, e)| ((*s).max(run_start), (*e).min(run_end)))
            .filter(|(s, e)| e > s)
            .collect();
        clipped.sort_unstable();
        let mut pauses: Vec<(u64, u64)> = Vec::with_capacity(clipped.len());
        for (start, end) in clipped {
            match pauses.last_mut() {
                Some(last) if start <= last.1 => last.1 = last.1.max(end),
                _ => pauses.push((start, end)),
            }
        }

        let mut active = Vec::new();
        let mut cursor = run_start;
        for (pause_start, pause_end) in &pauses {
            if *pause_end <= cursor {
                continue;
            }
            if *pause_start > cursor {
                active.push((cursor, (*pause_start).min(run_end)));
            }
            cursor = cursor.max(*pause_end);
            if cursor >= run_end {
                break;
            }
        }
        if cursor < run_end {
            active.push((cursor, run_end));
        }

        let paused_before = |abs: u64| -> u64 {
            pauses
                .iter()
                .map(|(s, e)| abs.min(*e).saturating_sub(*s))
                .sum()
        };

        active
            .into_iter()
            .filter_map(|(a, b)| {
                let start = a.max(clip_start);
                let end = b.min(clip_end);
                (end > start).then(|| ReplayVideoSegment {
                    replay_start_ms: start
                        .saturating_sub(run_start)
                        .saturating_sub(paused_before(start)),
                    video_start_ms: start - clip_start,
                    duration_ms: end - start,
                })
            })
            .collect()
    }

    // ─── Engine lifecycle ───────────────────────────────────────────────────────

    fn watch_loop() {
        loop {
            std::thread::sleep(WATCH_INTERVAL);
            watch_tick();
        }
    }

    fn watch_tick() {
        let geometry = resolve_geometry();
        let mut state = STATE.lock().unwrap();
        prune_unclaimed(&mut state);
        let busy = state.session.is_some() || state.pending_saves > 0;

        let Some(geometry) = geometry else {
            let since = *state.game_missing_since.get_or_insert_with(Instant::now);
            if !busy && state.engine.is_some() && since.elapsed() >= IDLE_SHUTDOWN {
                log::info!("fthr_recorder: game window gone; stopping engine");
                state.engine = None;
            }
            return;
        };
        state.game_missing_since = None;
        if busy {
            return;
        }

        let needs_restart = match state.engine.as_mut() {
            Some(engine) => !engine.is_alive() || engine.geometry != geometry,
            None => true,
        };
        if !needs_restart {
            state.settling_geometry = None;
            return;
        }
        let settled = match &state.settling_geometry {
            Some((seen, since)) if *seen == geometry => since.elapsed() >= GEOMETRY_SETTLE,
            _ => {
                state.settling_geometry = Some((geometry.clone(), Instant::now()));
                false
            }
        };
        if !settled {
            return;
        }
        if state.spawn_backoff_until.is_some_and(|until| Instant::now() < until) {
            return;
        }
        state.engine = None;
        drop(state);

        let spawned = spawn_engine(&geometry);
        let mut state = STATE.lock().unwrap();
        match spawned {
            Ok(engine) => {
                state.spawn_backoff_until = None;
                state.engine = Some(engine);
            }
            Err(error) => {
                log::warn!("fthr_recorder: engine start failed: {error}");
                state.spawn_backoff_until = Some(Instant::now() + Duration::from_secs(30));
            }
        }
    }

    fn spawn_engine(geometry: &Geometry) -> Result<Engine, String> {
        let paths = PATHS.get().ok_or("not initialized")?;
        let exe = paths.engine_exe.as_ref().ok_or("engine not found")?;
        let [cx, cy, cw, ch] = geometry.crop;
        let args: Vec<String> = vec![
            geometry.fps.to_string(),
            BUFFER_SECONDS.to_string(),
            geometry.out_width.to_string(),
            geometry.out_height.to_string(),
            geometry.bitrate_kbps.to_string(),
            "0".into(),                   // legacy raw-buffer budget (unused)
            "0".into(),                   // desktop capture: DXGI first, WGC fallback
            "0".into(),                   // target HWND (desktop mode ignores it)
            "1".into(),                   // scaling: fit
            geometry.monitor_path.clone(),
            CODEC_H264.to_string(),
            "4".into(),                   // encoder preset P4
            "0".into(),                   // retired multiband slot
            "0".into(),                   // system audio off
            String::new(),                // microphone endpoint
            "100".into(),                 // microphone gain
            "0".into(),                   // encoder: auto
            "1".into(),                   // crop enabled
            format!("{cx:.6}"),
            format!("{cy:.6}"),
            format!("{cw:.6}"),
            format!("{ch:.6}"),
            "0".into(),                   // audio layout: combined
        ];

        let mut command = Command::new(exe);
        command
            .args(&args)
            .env("FTHR_ENGINE_INSTANCE", INSTANCE_NAME)
            .stdin(Stdio::null())
            .creation_flags(0x0800_0000); // CREATE_NO_WINDOW
        if let Some(dir) = exe.parent() {
            command.current_dir(dir);
        }
        match paths.log_path.as_ref().and_then(|p| std::fs::File::create(p).ok()) {
            Some(log) => {
                let err = log.try_clone().map(Stdio::from).unwrap_or_else(|_| Stdio::null());
                command.stdout(Stdio::from(log)).stderr(err);
            }
            None => {
                command.stdout(Stdio::null()).stderr(Stdio::null());
            }
        }

        let child = command.spawn().map_err(|e| format!("spawn {}: {e}", exe.display()))?;
        assign_to_job(&child);
        let mut engine = Engine {
            child,
            mapping: HANDLE::default(),
            view: MEMORY_MAPPED_VIEW_ADDRESS::default(),
            geometry: geometry.clone(),
        };

        let deadline = Instant::now() + ENGINE_INIT_TIMEOUT;
        while engine.view.Value.is_null() {
            if !engine.is_alive() {
                return Err("engine exited during startup (see fthr-engine.log)".into());
            }
            if Instant::now() >= deadline {
                return Err("engine shared memory never appeared".into());
            }
            if let Ok(mapping) =
                unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS.0, false, SHARED_MEMORY_NAME) }
            {
                let view = unsafe {
                    MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, std::mem::size_of::<SharedLayout>())
                };
                engine.mapping = mapping;
                engine.view = view;
                if view.Value.is_null() {
                    return Err("could not map engine shared memory".into());
                }
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        while !engine.is_healthy() {
            if !engine.is_alive() {
                return Err("engine exited during startup (see fthr-engine.log)".into());
            }
            if Instant::now() >= deadline {
                return Err("engine did not finish initializing".into());
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        log::info!(
            "fthr_recorder: engine running codec={} {}x{}@{} {}kbps crop=[{:.3},{:.3},{:.3},{:.3}]",
            engine.codec_name(),
            geometry.out_width,
            geometry.out_height,
            geometry.fps,
            geometry.bitrate_kbps,
            cx,
            cy,
            cw,
            ch
        );
        Ok(engine)
    }

    /// Ties the engine's lifetime to AimMod's: the job kills it when we exit.
    fn assign_to_job(child: &Child) {
        let job = *JOB.get_or_init(|| unsafe {
            let Ok(job) = CreateJobObjectW(None, PCWSTR::null()) else {
                return 0;
            };
            let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            let _ = SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const core::ffi::c_void,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            job.0 as usize
        });
        if job == 0 {
            return;
        }
        let process = HANDLE(child.as_raw_handle());
        if let Err(error) = unsafe { AssignProcessToJobObject(HANDLE(job as *mut _), process) } {
            log::warn!("fthr_recorder: could not tie engine to AimMod's lifetime: {error}");
        }
    }

    // ─── Commands ───────────────────────────────────────────────────────────────

    /// Sends SAVE_CLIP and waits for the result. Returns the clip path and its
    /// QPC presentation interval.
    fn save_clip(layout: *mut SharedLayout, duration_s: u32) -> Result<(PathBuf, f64, f64), String> {
        let mut channel = SAVE_CHANNEL.lock().unwrap();
        if let Some(stale) = channel.take() {
            drain_stale_save(layout, stale);
        }
        let paths = PATHS.get().ok_or("not initialized")?;
        let path = paths.pending_dir.join(format!("{}.mp4", unix_now_ms()));
        let wide: Vec<u16> = path.as_os_str().to_string_lossy().encode_utf16().collect();
        if wide.len() >= 255 {
            return Err(format!("clip path too long: {}", path.display()));
        }

        let mut ui_string = [0u16; 256];
        ui_string[..wide.len()].copy_from_slice(&wide);
        // Payload first, command last: the engine may act on the command at once.
        wr!(layout, engine_response, RESP_NONE);
        wr!(layout, ui_string, ui_string);
        wr!(layout, ui_param1, duration_s);
        wr!(layout, ui_command, CMD_SAVE_CLIP);

        let deadline = Instant::now() + SAVE_TIMEOUT;
        loop {
            let response = rd!(layout, engine_response);
            match response {
                RESP_CLIP_SAVED => {
                    let text = read_engine_string(layout);
                    wr!(layout, engine_response, RESP_NONE);
                    // The reply carries no path, so make sure it's for our file.
                    if !path.is_file() {
                        return Err(format!(
                            "engine reported a save but {} is missing",
                            path.display()
                        ));
                    }
                    let (start, end) = parse_interval(&text)
                        .ok_or_else(|| format!("clip saved without an interval: {text:?}"))?;
                    return Ok((path, start, end));
                }
                RESP_ERROR => {
                    let text = read_engine_string(layout);
                    wr!(layout, engine_response, RESP_NONE);
                    let _ = std::fs::remove_file(&path);
                    return Err(text);
                }
                RESP_NONE | RESP_SAVE_STARTED => {}
                _ => {}
            }
            if Instant::now() >= deadline {
                // The engine may still finish this save. Remember it so the
                // next save drains that reply instead of taking it as its own.
                *channel = Some(StaleSave {
                    path,
                    timed_out_at: Instant::now(),
                });
                return Err("timed out waiting for the engine to save".into());
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    /// Waits briefly for a timed-out save's late reply, consumes it, and
    /// deletes its clip. Nothing claims that clip: its session was already
    /// stored without video.
    fn drain_stale_save(layout: *mut SharedLayout, stale: StaleSave) {
        let deadline = stale.timed_out_at + STALE_SAVE_GRACE;
        loop {
            match rd!(layout, engine_response) {
                RESP_CLIP_SAVED | RESP_ERROR => {
                    wr!(layout, engine_response, RESP_NONE);
                    log::info!("fthr_recorder: discarded a late reply from a timed-out save");
                    break;
                }
                _ => {}
            }
            if Instant::now() >= deadline {
                log::warn!("fthr_recorder: timed-out save never answered; continuing");
                break;
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        if let Err(error) = std::fs::remove_file(&stale.path) {
            if error.kind() != std::io::ErrorKind::NotFound {
                log::warn!(
                    "fthr_recorder: could not remove timed-out clip {}: {error}",
                    stale.path.display()
                );
            }
        }
    }

    fn read_engine_string(layout: *mut SharedLayout) -> String {
        let raw: [u16; 512] = rd!(layout, engine_string);
        let end = raw.iter().position(|c| *c == 0).unwrap_or(raw.len());
        String::from_utf16_lossy(&raw[..end])
    }

    fn parse_interval(text: &str) -> Option<(f64, f64)> {
        #[derive(serde::Deserialize)]
        struct Interval {
            start_qpc_s: f64,
            end_qpc_s: f64,
        }
        let interval: Interval = serde_json::from_str(text).ok()?;
        (interval.end_qpc_s > interval.start_qpc_s && interval.start_qpc_s > 0.0)
            .then_some((interval.start_qpc_s, interval.end_qpc_s))
    }

    // ─── Geometry ───────────────────────────────────────────────────────────────

    /// Game client area (per the replay framing setting) as a crop of its monitor.
    fn resolve_geometry() -> Option<Geometry> {
        let hwnd = crate::screen_recorder::resolve_capture_window_hwnds().into_iter().next()?;
        let client = client_rect_on_screen(hwnd)?;
        if client.width < MIN_CLIENT_WIDTH || client.height < MIN_CLIENT_HEIGHT {
            return None;
        }
        let capture = crate::screen_recorder::compute_capture_rect(&client);

        let hmon = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
        let mut info = MONITORINFOEXW::default();
        info.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
        if !unsafe { GetMonitorInfoW(hmon, &mut info as *mut MONITORINFOEXW as *mut MONITORINFO) }
            .as_bool()
        {
            return None;
        }
        let monitor = info.monitorInfo.rcMonitor;
        let mon_w = (monitor.right - monitor.left) as f64;
        let mon_h = (monitor.bottom - monitor.top) as f64;
        if mon_w <= 0.0 || mon_h <= 0.0 {
            return None;
        }
        let monitor_path = monitor_device_path(&info.szDevice)?;

        // Clamp the capture rect to the monitor; a window straddling two
        // monitors is captured on the one that holds most of it.
        let left = (capture.x as f64).max(monitor.left as f64);
        let top = (capture.y as f64).max(monitor.top as f64);
        let right = ((capture.x + capture.width as i32) as f64).min(monitor.right as f64);
        let bottom = ((capture.y + capture.height as i32) as f64).min(monitor.bottom as f64);
        if right - left < 16.0 || bottom - top < 16.0 {
            return None;
        }
        let round = |v: f64| (v * 10_000.0).round() / 10_000.0;
        let crop = [
            round((left - monitor.left as f64) / mon_w),
            round((top - monitor.top as f64) / mon_h),
            round((right - left) / mon_w),
            round((bottom - top) / mon_h),
        ];

        let crop_px_w = (right - left) as u32;
        let crop_px_h = (bottom - top) as u32;
        // NVENC rejects very small frames; keep at least 256px on each side.
        let out_width = even(crate::screen_recorder::replay_capture_width().min(crop_px_w).max(256));
        let out_height =
            even(((out_width as f64) * crop_px_h as f64 / crop_px_w as f64).round() as u32).max(256);
        let fps = crate::screen_recorder::replay_capture_fps().clamp(1, 240);
        let bits_per_pixel = crate::screen_recorder::replay_capture_video_bits_per_pixel();
        let bitrate_kbps = ((out_width as f64 * out_height as f64 * fps as f64 * bits_per_pixel) / 1000.0)
            .round()
            .clamp(1_000.0, 60_000.0) as u32;

        Some(Geometry {
            monitor_path,
            crop,
            out_width,
            out_height,
            fps,
            bitrate_kbps,
        })
    }

    fn even(v: u32) -> u32 {
        v & !1
    }

    fn client_rect_on_screen(hwnd: HWND) -> Option<crate::settings::RegionRect> {
        let mut origin = POINT { x: 0, y: 0 };
        let mut rect = RECT::default();
        if !unsafe { ClientToScreen(hwnd, &mut origin) }.as_bool()
            || unsafe { GetClientRect(hwnd, &mut rect) }.is_err()
        {
            return None;
        }
        let width = (rect.right - rect.left).max(0) as u32;
        let height = (rect.bottom - rect.top).max(0) as u32;
        (width > 0 && height > 0).then_some(crate::settings::RegionRect {
            x: origin.x,
            y: origin.y,
            width,
            height,
        })
    }

    /// `\\.\DISPLAY11` → the stable `\\?\DISPLAY#...` interface path FTHR expects.
    fn monitor_device_path(device: &[u16; 32]) -> Option<String> {
        let mut display = DISPLAY_DEVICEW {
            cb: std::mem::size_of::<DISPLAY_DEVICEW>() as u32,
            ..Default::default()
        };
        const EDD_GET_DEVICE_INTERFACE_NAME: u32 = 1;
        let ok = unsafe {
            EnumDisplayDevicesW(
                PCWSTR(device.as_ptr()),
                0,
                &mut display,
                EDD_GET_DEVICE_INTERFACE_NAME,
            )
        };
        if !ok.as_bool() {
            return None;
        }
        let end = display.DeviceID.iter().position(|c| *c == 0).unwrap_or(display.DeviceID.len());
        let path = String::from_utf16_lossy(&display.DeviceID[..end]);
        (!path.is_empty()).then_some(path)
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    fn resolve_engine_exe(app: &tauri::AppHandle) -> Option<PathBuf> {
        let mut candidates = Vec::new();
        if let Some(path) = std::env::var_os("AIMMOD_FTHR_ENGINE") {
            candidates.push(PathBuf::from(path));
        }
        if let Ok(dir) = app.path().resource_dir() {
            candidates.push(dir.join("fthr-engine").join("FTHRclips.exe"));
        }
        if let Some(dir) = std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)) {
            candidates.push(dir.join("fthr-engine").join("FTHRclips.exe"));
        }
        candidates.into_iter().find(|p| p.is_file())
    }

    fn prune_unclaimed(state: &mut State) {
        let now = unix_now_ms();
        state.completed.retain(|clip| {
            let keep = now.saturating_sub(clip.session_end_unix_ms) < UNCLAIMED_CLIP_TTL_MS;
            if !keep {
                let _ = std::fs::remove_file(&clip.path);
            }
            keep
        });
        state
            .recorded
            .retain(|span| now.saturating_sub(span.end_unix_ms) < UNCLAIMED_CLIP_TTL_MS);
    }

    /// Clips left in `pending` by a previous run were never attached to a replay.
    fn clear_stale_pending(dir: &Path) {
        if let Ok(entries) = std::fs::read_dir(dir) {
            for entry in entries.flatten() {
                let _ = std::fs::remove_file(entry.path());
            }
        }
    }

    fn qpc_now_s() -> f64 {
        let mut counter = 0i64;
        let mut frequency = 0i64;
        unsafe {
            let _ = QueryPerformanceCounter(&mut counter);
            let _ = QueryPerformanceFrequency(&mut frequency);
        }
        if frequency <= 0 {
            return 0.0;
        }
        counter as f64 / frequency as f64
    }

    fn unix_now_ms() -> u64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0)
    }
}

#[cfg(all(test, target_os = "windows"))]
mod tests {
    use super::imp::{RecordedSpan, build_segments, take_recorded_span};
    use std::collections::VecDeque;

    #[test]
    fn claims_only_the_recorded_session_that_overlaps_the_run() {
        let mut recorded = VecDeque::from([
            RecordedSpan { start_unix_ms: 1_000, end_unix_ms: 61_000 },
            RecordedSpan { start_unix_ms: 70_000, end_unix_ms: 130_000 },
        ]);
        // A run FTHR never saw is not claimed.
        assert!(!take_recorded_span(&mut recorded, Some(200_000), Some(260_000)));
        assert_eq!(recorded.len(), 2);
        // The run inside the second session claims it and leaves the first.
        assert!(take_recorded_span(&mut recorded, Some(72_000), Some(128_000)));
        assert_eq!(recorded.len(), 1);
        assert_eq!(recorded[0].start_unix_ms, 1_000);
        // Without run timing, the latest session is claimed.
        assert!(take_recorded_span(&mut recorded, None, None));
        assert!(recorded.is_empty());
    }

    #[test]
    fn pause_straddling_run_start_does_not_underflow() {
        // Pause 5s..15s, run re-anchored to start at 10s (restart from pause menu).
        let segments = build_segments(10_000, 70_000, &[(5_000, 15_000)], 0, 80_000);
        assert_eq!(segments.len(), 1);
        assert_eq!((segments[0].replay_start_ms, segments[0].video_start_ms, segments[0].duration_ms), (0, 15_000, 55_000));
    }

    #[test]
    fn overlapping_pauses_count_once() {
        let segments = build_segments(0, 60_000, &[(20_000, 25_000), (22_000, 30_000)], 0, 60_000);
        assert_eq!(segments.len(), 2);
        assert_eq!((segments[1].replay_start_ms, segments[1].video_start_ms, segments[1].duration_ms), (20_000, 30_000, 30_000));
    }

    #[test]
    fn maps_unpaused_run_onto_clip() {
        // Clip starts 2s before the run and ends 1s after it.
        let segments = build_segments(10_000, 70_000, &[], 8_000, 71_000);
        assert_eq!(segments.len(), 1);
        assert_eq!(segments[0].replay_start_ms, 0);
        assert_eq!(segments[0].video_start_ms, 2_000);
        assert_eq!(segments[0].duration_ms, 60_000);
    }

    #[test]
    fn skips_pauses_and_shifts_replay_time() {
        // 5s pause at run time 20s..25s.
        let segments = build_segments(0, 60_000, &[(20_000, 25_000)], 0, 60_000);
        assert_eq!(segments.len(), 2);
        assert_eq!((segments[0].replay_start_ms, segments[0].video_start_ms, segments[0].duration_ms), (0, 0, 20_000));
        assert_eq!((segments[1].replay_start_ms, segments[1].video_start_ms, segments[1].duration_ms), (20_000, 25_000, 35_000));
    }

    #[test]
    fn clips_run_to_available_video() {
        // Buffer only holds the last 30s of a 60s run.
        let segments = build_segments(0, 60_000, &[], 30_000, 60_500);
        assert_eq!(segments.len(), 1);
        assert_eq!((segments[0].replay_start_ms, segments[0].video_start_ms, segments[0].duration_ms), (30_000, 0, 30_000));
    }
}
