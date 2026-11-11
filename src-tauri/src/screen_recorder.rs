use std::collections::VecDeque;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering};
use std::sync::mpsc;
use std::time::Instant;

use once_cell::sync::Lazy;

use crate::settings::RegionRect;

#[cfg(target_os = "windows")]
use dxgi_capture_rs::{CaptureError, DXGIManager};
#[cfg(target_os = "windows")]
use windows::Win32::Foundation::{HWND, LPARAM, POINT, RECT};
#[cfg(target_os = "windows")]
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_OUTPUT_DESC, IDXGIAdapter1, IDXGIFactory1, IDXGIOutput,
};
#[cfg(target_os = "windows")]
use windows::Win32::Graphics::Gdi::{
    ClientToScreen, GetMonitorInfoW, HMONITOR, MONITOR_DEFAULTTONEAREST, MONITORINFO,
    MonitorFromWindow,
};
#[cfg(target_os = "windows")]
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GW_OWNER, GetClientRect, GetForegroundWindow, GetWindow, GetWindowTextW,
    GetWindowThreadProcessId, IsWindow, IsWindowVisible,
};
#[cfg(target_os = "windows")]
use windows::core::BOOL;

/// Screen recorder: captures replay image snapshots from the game client area
/// during active sessions for post-session replay underlay.
///
/// Capture backend: DXGI Desktop Duplication API — GPU-bound, low-overhead
/// monitor capture with per-adapter D3D11 device for multi-GPU correctness.

// ─── Types ─────────────────────────────────────────────────────────────────────

/// A single captured video frame for post-session mouse-path underlay.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ScreenFrame {
    /// Milliseconds since session start.
    pub timestamp_ms: u64,
    /// JPEG-encoded frame, base64-encoded for Tauri IPC transport.
    pub jpeg_b64: String,
}

#[derive(Debug, Clone)]
struct CompletedFrameCapture {
    started_at_unix_ms: Option<u64>,
    ended_at_unix_ms: Option<u64>,
    frames: Vec<ScreenFrame>,
}

#[derive(Debug)]
struct PendingFrame {
    timestamp_ms: u64,
    bgra: Vec<u8>,
    width: u32,
    height: u32,
}

// ─── Config ─────────────────────────────────────────────────────────────────────

/// Keep up to 3 minutes of frames at the user-configured capture rate.
const MAX_CAPTURE_BUFFER_SECS: usize = 180;
/// Keep a short queue of completed session frame buffers so a fast restart does
/// not erase frames before the file-watcher persists the previous run.
const MAX_COMPLETED_SESSIONS: usize = 8;
/// Keep each encoder queue short while still absorbing brief spikes.
const MAX_PENDING_ENCODE_FRAMES: usize = 6;
/// Use a small worker pool for JPEG encoding so high target FPS settings are
/// less likely to bottleneck on a single encoding thread.
const MAX_ENCODER_WORKERS: usize = 4;

/// Output width after downscaling; height is preserved from the source aspect ratio.
const DEFAULT_OUTPUT_WIDTH: u32 = crate::settings::DEFAULT_REPLAY_CAPTURE_WIDTH;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
enum ReplayCaptureFraming {
    Cropped = 0,
    Fullscreen = 1,
}

impl ReplayCaptureFraming {
    fn from_settings(value: &str) -> Self {
        match value.trim() {
            "fullscreen" => Self::Fullscreen,
            _ => Self::Cropped,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
enum ReplayCaptureQuality {
    Balanced = 0,
    High = 1,
    Ultra = 2,
}

impl ReplayCaptureQuality {
    fn from_settings(value: &str) -> Self {
        match value.trim() {
            "high" => Self::High,
            "ultra" => Self::Ultra,
            _ => Self::Balanced,
        }
    }

    fn jpeg_quality(self, target_fps: u32) -> u8 {
        let base: u8 = match self {
            Self::Balanced => 65,
            Self::High => 78,
            Self::Ultra => 88,
        };

        if target_fps >= 50 {
            let reduction: u8 = match self {
                Self::Balanced => 0,
                Self::High => 6,
                Self::Ultra => 10,
            };
            base.saturating_sub(reduction)
        } else {
            base
        }
    }

    fn resize_filter(self, target_fps: u32) -> image::imageops::FilterType {
        if target_fps >= 50 {
            return image::imageops::FilterType::Triangle;
        }

        match self {
            Self::Balanced => image::imageops::FilterType::Triangle,
            Self::High => image::imageops::FilterType::CatmullRom,
            Self::Ultra => image::imageops::FilterType::Lanczos3,
        }
    }
}

// ─── State ─────────────────────────────────────────────────────────────────────

static RECORDING: AtomicBool = AtomicBool::new(false);
static PAUSED: AtomicBool = AtomicBool::new(false);
static GENERATION: AtomicU64 = AtomicU64::new(0);
static REPLAY_CAPTURE_FPS: AtomicU64 =
    AtomicU64::new(crate::settings::DEFAULT_REPLAY_CAPTURE_FPS as u64);
static REPLAY_CAPTURE_OUTPUT_WIDTH: AtomicU64 = AtomicU64::new(DEFAULT_OUTPUT_WIDTH as u64);
static REPLAY_CAPTURE_FRAMING: AtomicU8 = AtomicU8::new(ReplayCaptureFraming::Cropped as u8);
static REPLAY_CAPTURE_QUALITY: AtomicU8 = AtomicU8::new(ReplayCaptureQuality::Balanced as u8);
static NEXT_ENCODER_WORKER: AtomicU64 = AtomicU64::new(0);
static DROPPED_FRAMES_DUE_TO_BACKPRESSURE: AtomicU64 = AtomicU64::new(0);
/// True while the current session is recorded by the FTHR engine instead of
/// the JPEG capture loop.
static USING_FTHR: AtomicBool = AtomicBool::new(false);
static FRAMES: Lazy<Mutex<Vec<ScreenFrame>>> = Lazy::new(|| Mutex::new(Vec::new()));
static COMPLETED_FRAMES: Lazy<Mutex<VecDeque<CompletedFrameCapture>>> =
    Lazy::new(|| Mutex::new(VecDeque::new()));
static SESSION_START: Lazy<Mutex<Option<Instant>>> = Lazy::new(|| Mutex::new(None));
static SESSION_START_UNIX_MS: Lazy<Mutex<Option<u64>>> = Lazy::new(|| Mutex::new(None));
static LAST_CAPTURE_RECT: Lazy<Mutex<Option<RegionRect>>> = Lazy::new(|| Mutex::new(None));
static ENCODER_TX: Lazy<Mutex<Vec<mpsc::SyncSender<PendingFrame>>>> =
    Lazy::new(|| Mutex::new(Vec::new()));
static ENCODER_WORKERS: Lazy<Mutex<Vec<std::thread::JoinHandle<()>>>> =
    Lazy::new(|| Mutex::new(Vec::new()));

// ─── Public API ────────────────────────────────────────────────────────────────

/// The recorder derives its crop from the live KovaaK client area, not the
/// selected overlay monitor. This hook remains so monitor changes can clear the
/// last logged rect and force a fresh capture-rect log on the next session.
pub fn update_monitor_rect(_monitor: &RegionRect) {
    *LAST_CAPTURE_RECT.lock().unwrap() = None;
}

pub fn set_replay_capture_fps(fps: u32) {
    let clamped = fps.clamp(6, 60).max(1);
    REPLAY_CAPTURE_FPS.store(clamped as u64, Ordering::SeqCst);
}

pub fn replay_capture_fps() -> u32 {
    REPLAY_CAPTURE_FPS.load(Ordering::SeqCst).max(1) as u32
}

pub fn set_replay_capture_width(width: u32) {
    let clamped = crate::settings::normalize_replay_capture_width(width);
    REPLAY_CAPTURE_OUTPUT_WIDTH.store(clamped as u64, Ordering::SeqCst);
}

pub fn replay_capture_width() -> u32 {
    REPLAY_CAPTURE_OUTPUT_WIDTH.load(Ordering::SeqCst).max(1) as u32
}

pub fn set_replay_capture_framing(framing: &str) {
    let next = ReplayCaptureFraming::from_settings(framing);
    REPLAY_CAPTURE_FRAMING.store(next as u8, Ordering::SeqCst);
}

fn replay_capture_framing() -> ReplayCaptureFraming {
    match REPLAY_CAPTURE_FRAMING.load(Ordering::SeqCst) {
        value if value == ReplayCaptureFraming::Fullscreen as u8 => {
            ReplayCaptureFraming::Fullscreen
        }
        _ => ReplayCaptureFraming::Cropped,
    }
}

pub fn set_replay_capture_quality(quality: &str) {
    let next = ReplayCaptureQuality::from_settings(quality);
    REPLAY_CAPTURE_QUALITY.store(next as u8, Ordering::SeqCst);
}

/// Bits per pixel per frame for FTHR's H.264 encoder at the current quality
/// (about 1.5x what HEVC needed for the same look).
pub(crate) fn replay_capture_video_bits_per_pixel() -> f64 {
    match replay_capture_quality() {
        ReplayCaptureQuality::Balanced => 0.15,
        ReplayCaptureQuality::High => 0.24,
        ReplayCaptureQuality::Ultra => 0.36,
    }
}

fn replay_capture_quality() -> ReplayCaptureQuality {
    match REPLAY_CAPTURE_QUALITY.load(Ordering::SeqCst) {
        value if value == ReplayCaptureQuality::High as u8 => ReplayCaptureQuality::High,
        value if value == ReplayCaptureQuality::Ultra as u8 => ReplayCaptureQuality::Ultra,
        _ => ReplayCaptureQuality::Balanced,
    }
}

fn current_session_elapsed_ms() -> u64 {
    SESSION_START
        .lock()
        .unwrap()
        .map_or(0, |t| t.elapsed().as_millis() as u64)
}

/// Start frame capture for a new session, sharing the caller's `session_start`
/// instant so screen-frame timestamps are on the exact same clock as the mouse
/// position timestamps produced by `mouse_hook::start_session_tracking()`.
pub fn start(session_start: Instant) {
    let generation = GENERATION.fetch_add(1, Ordering::SeqCst) + 1;

    stop_capture_backend();
    finish_encoder_worker();

    let start_unix_ms = unix_now_ms();
    let _ = queue_completed_frames(start_unix_ms, "restart");
    DROPPED_FRAMES_DUE_TO_BACKPRESSURE.store(0, Ordering::SeqCst);
    NEXT_ENCODER_WORKER.store(0, Ordering::SeqCst);
    FRAMES.lock().unwrap().clear();
    *SESSION_START.lock().unwrap() = Some(session_start);
    *SESSION_START_UNIX_MS.lock().unwrap() = Some(start_unix_ms);
    start_encoder_worker(generation);
    PAUSED.store(false, Ordering::SeqCst);
    RECORDING.store(true, Ordering::SeqCst);

    // FTHR records HEVC video when its engine is ready; otherwise fall back to
    // the JPEG capture loop for this session.
    let use_fthr = crate::fthr_recorder::session_started(session_start);
    USING_FTHR.store(use_fthr, Ordering::SeqCst);
    if use_fthr {
        log::info!("Screen recorder started (gen {generation}, backend=fthr)");
        return;
    }

    #[cfg(target_os = "windows")]
    start_capture_backend(generation);
}

/// Stop frame capture.
pub fn stop() {
    PAUSED.store(false, Ordering::SeqCst);
    if RECORDING.swap(false, Ordering::SeqCst) {
        if USING_FTHR.swap(false, Ordering::SeqCst) {
            crate::fthr_recorder::session_stopped();
        }
        stop_capture_backend();
        finish_encoder_worker();
        let captured_frames = queue_completed_frames(unix_now_ms(), "stop");
        let dropped_frames = DROPPED_FRAMES_DUE_TO_BACKPRESSURE.swap(0, Ordering::SeqCst);
        let queued_sessions = COMPLETED_FRAMES.lock().unwrap().len();
        log::info!(
            "Screen recorder stopped (frames={}, dropped_frames={}, queued_sessions={})",
            captured_frames,
            dropped_frames,
            queued_sessions
        );
    }
}

/// Drain all recorded frames and clear the internal buffer.
pub fn drain_frames() -> Vec<ScreenFrame> {
    if let Some(capture) = COMPLETED_FRAMES.lock().unwrap().pop_front() {
        return capture.frames;
    }
    std::mem::take(&mut *FRAMES.lock().unwrap())
}

/// Return all recorded frames without removing them.
/// The buffer is cleared automatically when the next session starts.
pub fn get_frames() -> Vec<ScreenFrame> {
    let live = FRAMES.lock().unwrap().clone();
    if !live.is_empty() {
        return live;
    }
    COMPLETED_FRAMES
        .lock()
        .unwrap()
        .back()
        .map(|capture| capture.frames.clone())
        .unwrap_or_default()
}

/// Drain frames and return a replay-quality subset for persistent storage.
pub fn drain_frames_for_replay() -> Vec<ScreenFrame> {
    drain_frames()
}

pub fn take_frames_for_run(
    run_snapshot: Option<&crate::bridge::BridgeRunSnapshot>,
) -> Vec<ScreenFrame> {
    let mut completed = COMPLETED_FRAMES.lock().unwrap();
    if completed.is_empty() {
        drop(completed);
        return drain_frames_for_replay();
    }

    if let Some(snapshot) = run_snapshot {
        let (matched, remaining) =
            partition_matching_frame_captures(std::mem::take(&mut *completed), snapshot);
        *completed = remaining;
        if !matched.is_empty() {
            drop(completed);
            return merge_frame_captures(matched, snapshot);
        }
    }

    let fallback = completed.pop_back();
    drop(completed);
    fallback.map(|capture| capture.frames).unwrap_or_default()
}

// ─── Backend lifecycle ─────────────────────────────────────────────────────────

#[cfg(target_os = "windows")]
fn start_capture_backend(generation: u64) {
    std::thread::Builder::new()
        .name(format!("screen-capture-{generation}"))
        .spawn(move || dxgi_capture_loop(generation))
        .expect("failed to spawn capture thread");
}

#[cfg(target_os = "windows")]
pub(crate) fn resolve_capture_window_hwnds() -> Vec<HWND> {
    let bridge_pid = crate::bridge::current_game_pid();
    let tracked = crate::window_tracker::get_game_hwnd();
    let foreground = unsafe { GetForegroundWindow() };
    let mut candidates = Vec::new();

    let mut push_candidate = |hwnd: HWND, required_pid: Option<u32>| {
        if hwnd.is_invalid() || candidates.contains(&hwnd) {
            return;
        }
        if required_pid.is_some_and(|pid| window_pid(hwnd) != Some(pid)) {
            return;
        }
        if is_hwnd_capture_candidate(hwnd) {
            candidates.push(hwnd);
        }
    };

    if let Some(hwnd) = tracked {
        push_candidate(hwnd, bridge_pid);
    }

    if !foreground.is_invalid() {
        push_candidate(foreground, bridge_pid);
    }

    if let Some(pid) = bridge_pid {
        for hwnd in find_capture_windows_for_pid(pid) {
            push_candidate(hwnd, Some(pid));
        }
    }

    if let Some(hwnd) = tracked {
        push_candidate(hwnd, None);
    }

    if !foreground.is_invalid() {
        push_candidate(foreground, None);
    }

    candidates
}

#[cfg(target_os = "windows")]
fn window_pid(hwnd: HWND) -> Option<u32> {
    if hwnd.is_invalid() {
        return None;
    }
    let mut pid = 0u32;
    unsafe {
        let _ = GetWindowThreadProcessId(hwnd, Some(&mut pid));
    }
    if pid == 0 { None } else { Some(pid) }
}

#[cfg(target_os = "windows")]
fn is_hwnd_capture_candidate(hwnd: HWND) -> bool {
    if hwnd.is_invalid() {
        return false;
    }
    if !unsafe { IsWindow(Some(hwnd)) }.as_bool() {
        return false;
    }
    if !unsafe { IsWindowVisible(hwnd) }.as_bool() {
        return false;
    }
    let has_owner = match unsafe { GetWindow(hwnd, GW_OWNER) } {
        Ok(owner) => {
            let owner: HWND = owner;
            !owner.is_invalid()
        }
        Err(_) => false,
    };
    if has_owner {
        return false;
    }

    let mut rect = RECT::default();
    if unsafe { GetClientRect(hwnd, &mut rect) }.is_err() {
        return false;
    }
    let width = rect.right.saturating_sub(rect.left);
    let height = rect.bottom.saturating_sub(rect.top);
    width > 0 && height > 0
}

#[cfg(target_os = "windows")]
fn find_capture_windows_for_pid(pid: u32) -> Vec<HWND> {
    let tracked_preferred =
        crate::window_tracker::get_game_hwnd().filter(|hwnd| window_pid(*hwnd) == Some(pid));
    let fg = unsafe { GetForegroundWindow() };
    let foreground_preferred = if window_pid(fg) == Some(pid) {
        Some(fg)
    } else {
        None
    };
    let preferred_hwnd = tracked_preferred
        .or(foreground_preferred)
        .unwrap_or(HWND::default());

    struct EnumCtx {
        pid: u32,
        preferred_hwnd: HWND,
        ranked: Vec<(i64, HWND)>,
    }

    unsafe extern "system" fn enum_windows_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let ctx = unsafe { &mut *(lparam.0 as *mut EnumCtx) };
        let mut win_pid = 0u32;
        let _ = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut win_pid)) };
        if win_pid != ctx.pid {
            return BOOL(1);
        }
        if !is_hwnd_capture_candidate(hwnd) {
            return BOOL(1);
        }

        let mut rect = RECT::default();
        if unsafe { GetClientRect(hwnd, &mut rect) }.is_err() {
            return BOOL(1);
        }
        let width = rect.right.saturating_sub(rect.left) as i64;
        let height = rect.bottom.saturating_sub(rect.top) as i64;
        if width <= 0 || height <= 0 {
            return BOOL(1);
        }

        let mut score = width.saturating_mul(height);
        if hwnd == ctx.preferred_hwnd {
            score = score.saturating_add(10_000_000_000);
        }
        if looks_like_kovaaks_game_window(hwnd) {
            score = score.saturating_add(8_000_000_000);
        }
        ctx.ranked.push((score, hwnd));

        BOOL(1)
    }

    let mut ctx = EnumCtx {
        pid,
        preferred_hwnd,
        ranked: Vec::new(),
    };
    unsafe {
        let _ = EnumWindows(
            Some(enum_windows_proc),
            LPARAM((&mut ctx as *mut EnumCtx) as isize),
        );
    }
    ctx.ranked.sort_by(|a, b| b.0.cmp(&a.0));
    ctx.ranked.into_iter().map(|(_, hwnd)| hwnd).collect()
}

#[cfg(target_os = "windows")]
fn looks_like_kovaaks_game_window(hwnd: HWND) -> bool {
    let mut title_buf = [0u16; 512];
    let title_len = unsafe { GetWindowTextW(hwnd, &mut title_buf) };
    if title_len <= 0 {
        return false;
    }

    let title = String::from_utf16_lossy(&title_buf[..title_len as usize]);
    title.contains("FPSAimTrainer") || title.contains("FPS Aim Trainer")
}

/// Finds the global DXGI desktop output index (across all adapters) whose
/// `DXGI_OUTPUT_DESC.Monitor` matches the given `HMONITOR`. Returns 0 if not found.
#[cfg(target_os = "windows")]
fn find_dxgi_output_for_hmonitor(hmon: HMONITOR) -> usize {
    let Ok(factory) = (unsafe { CreateDXGIFactory1::<IDXGIFactory1>() }) else {
        return 0;
    };
    let mut global_idx = 0usize;
    'adapters: for adapter_i in 0u32.. {
        let Ok(adapter): Result<IDXGIAdapter1, _> = (unsafe { factory.EnumAdapters1(adapter_i) })
        else {
            break 'adapters;
        };
        for output_i in 0u32.. {
            let Ok(output): Result<IDXGIOutput, _> = (unsafe { adapter.EnumOutputs(output_i) })
            else {
                break;
            };
            let Ok(desc): Result<DXGI_OUTPUT_DESC, _> = (unsafe { output.GetDesc() }) else {
                continue;
            };
            if desc.AttachedToDesktop.as_bool() {
                if desc.Monitor == hmon {
                    return global_idx;
                }
                global_idx += 1;
            }
        }
    }
    0
}

/// DXGI Desktop Duplication capture loop. Acquires the monitor output that
/// owns the game window (multi-GPU safe), captures full-monitor frames, and
/// crops to the game client area before queuing for encoding.
#[cfg(target_os = "windows")]
fn dxgi_capture_loop(generation: u64) {
    // Wait for the game HWND to become available.
    let hwnd = loop {
        if GENERATION.load(Ordering::Relaxed) != generation || !RECORDING.load(Ordering::Relaxed) {
            return;
        }
        if let Some(h) = resolve_capture_window_hwnds().into_iter().next() {
            break h;
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    };

    // Map game window → monitor → DXGI output index.
    let hmon = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
    let dxgi_output_index = find_dxgi_output_for_hmonitor(hmon);

    // Monitor virtual-screen origin for window→monitor coordinate translation.
    let monitor_origin: (i32, i32) = {
        let mut mi: MONITORINFO = unsafe { std::mem::zeroed() };
        mi.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
        unsafe { GetMonitorInfoW(hmon, &mut mi as *mut MONITORINFO) };
        (mi.rcMonitor.left, mi.rcMonitor.top)
    };

    let mut manager = match DXGIManager::new(0) {
        Ok(mut m) => {
            m.set_capture_source_index(dxgi_output_index);
            log::info!(
                "Screen recorder started (gen {generation}, backend=dxgi, output_index={dxgi_output_index})"
            );
            m
        }
        Err(e) => {
            log::error!("screen_recorder: DXGI init failed: {e}");
            return;
        }
    };

    let mut last_frame_ts_ms = 0u64;
    loop {
        if GENERATION.load(Ordering::Relaxed) != generation || !RECORDING.load(Ordering::Relaxed) {
            return;
        }

        if PAUSED.load(Ordering::Relaxed) {
            std::thread::sleep(std::time::Duration::from_millis(33));
            continue;
        }

        // FPS gate — sleep until next frame is due.
        let ts_ms = current_session_elapsed_ms();
        let min_interval_ms = (1000 / replay_capture_fps().max(1) as u64).max(1);
        if last_frame_ts_ms != 0 {
            let next_due = last_frame_ts_ms.saturating_add(min_interval_ms);
            if ts_ms < next_due {
                std::thread::sleep(std::time::Duration::from_millis(
                    (next_due - ts_ms).min(min_interval_ms),
                ));
                continue;
            }
        }

        // Find current game HWND (might change if game restarts).
        let cur_hwnd = match resolve_capture_window_hwnds().into_iter().next() {
            Some(h) => h,
            None => {
                std::thread::sleep(std::time::Duration::from_millis(50));
                continue;
            }
        };

        // Get client area position and size.
        let (client_screen_x, client_screen_y, client_w, client_h) = {
            let mut pt = POINT { x: 0, y: 0 };
            let mut cr = RECT::default();
            if unsafe { ClientToScreen(cur_hwnd, &mut pt) }.as_bool()
                && unsafe { GetClientRect(cur_hwnd, &mut cr) }.is_ok()
            {
                let w = cr.right.saturating_sub(cr.left) as u32;
                let h = cr.bottom.saturating_sub(cr.top) as u32;
                (pt.x, pt.y, w, h)
            } else {
                std::thread::sleep(std::time::Duration::from_millis(50));
                continue;
            }
        };

        if client_w == 0 || client_h == 0 {
            std::thread::sleep(std::time::Duration::from_millis(50));
            continue;
        }

        // Acquire the latest monitor frame (timeout=0 → immediate).
        let (pixels, (mon_w, mon_h)) = match manager.capture_frame() {
            Ok(frame) => frame,
            Err(CaptureError::Timeout) => {
                // No new frame available yet; spin briefly.
                std::thread::sleep(std::time::Duration::from_millis(1));
                continue;
            }
            Err(CaptureError::AccessLost) => {
                // Display config changed (resolution/fullscreen toggle). Recreate.
                match DXGIManager::new(0) {
                    Ok(mut m) => {
                        m.set_capture_source_index(dxgi_output_index);
                        manager = m;
                        log::info!("screen_recorder: DXGI manager recreated after AccessLost");
                    }
                    Err(e) => {
                        log::warn!("screen_recorder: DXGI recreate failed: {e}");
                        std::thread::sleep(std::time::Duration::from_millis(500));
                    }
                }
                continue;
            }
            Err(e) => {
                log::warn!("screen_recorder: DXGI capture error: {e}");
                std::thread::sleep(std::time::Duration::from_millis(50));
                continue;
            }
        };
        let mon_w = mon_w as u32;
        let mon_h = mon_h as u32;

        // Translate client screen coordinates to monitor-local coordinates.
        let client_local_x: i32 = client_screen_x - monitor_origin.0;
        let client_local_y: i32 = client_screen_y - monitor_origin.1;
        let win_x = client_local_x.clamp(0, mon_w as i32) as u32;
        let win_y = client_local_y.clamp(0, mon_h as i32) as u32;
        let win_right = (win_x + client_w).min(mon_w);
        let win_bottom = (win_y + client_h).min(mon_h);
        if win_right <= win_x || win_bottom <= win_y {
            continue;
        }
        let win_w = win_right - win_x;
        let win_h = win_bottom - win_y;

        // Apply framing crop (fullscreen = whole window, cropped = centre half).
        let window_bounds = RegionRect {
            x: 0,
            y: 0,
            width: win_w,
            height: win_h,
        };
        let framing = compute_capture_rect(&window_bounds);
        let final_x = win_x.saturating_add(framing.x.max(0) as u32);
        let final_y = win_y.saturating_add(framing.y.max(0) as u32);
        let final_w = framing.width.min(mon_w.saturating_sub(final_x));
        let final_h = framing.height.min(mon_h.saturating_sub(final_y));
        if final_w == 0 || final_h == 0 {
            continue;
        }

        // Log crop change.
        let capture_rect = RegionRect {
            x: final_x as i32,
            y: final_y as i32,
            width: final_w,
            height: final_h,
        };
        {
            let mut last = LAST_CAPTURE_RECT.lock().unwrap();
            if *last != Some(capture_rect) {
                log::info!(
                    "screen_recorder: capture rect ({},{}) {}\u{d7}{} within monitor {}\u{d7}{}",
                    final_x,
                    final_y,
                    final_w,
                    final_h,
                    mon_w,
                    mon_h,
                );
                *last = Some(capture_rect);
            }
        }

        // Copy the cropped region out of the full-monitor BGRA8 pixel vec.
        let mut bgra = vec![0u8; (final_w * final_h * 4) as usize];
        let row_bytes = final_w as usize;
        for row in 0..final_h as usize {
            let src_start = (final_y as usize + row) * mon_w as usize + final_x as usize;
            let dst_start = row * row_bytes;
            for (col, px) in pixels[src_start..src_start + row_bytes].iter().enumerate() {
                let d = (dst_start + col) * 4;
                bgra[d] = px.b;
                bgra[d + 1] = px.g;
                bgra[d + 2] = px.r;
                bgra[d + 3] = px.a;
            }
        }

        queue_captured_frame(PendingFrame {
            timestamp_ms: ts_ms,
            bgra,
            width: final_w,
            height: final_h,
        });
        last_frame_ts_ms = ts_ms;
    }
}

fn stop_capture_backend() {
    // The DXGI capture loop watches GENERATION and RECORDING atomics; it exits
    // automatically when either changes. No explicit stop handle needed.
}

#[cfg(target_os = "windows")]
fn queue_captured_frame(frame: PendingFrame) {
    let senders = ENCODER_TX.lock().unwrap();
    if !senders.is_empty() {
        let mut pending = Some(frame);
        let start_index = NEXT_ENCODER_WORKER.fetch_add(1, Ordering::Relaxed) as usize;
        let worker_count = senders.len();
        let mut queued = false;

        for offset in 0..worker_count {
            let index = (start_index + offset) % worker_count;
            let Some(frame) = pending.take() else {
                break;
            };
            match senders[index].try_send(frame) {
                Ok(()) => {
                    queued = true;
                    break;
                }
                Err(mpsc::TrySendError::Full(frame)) => {
                    pending = Some(frame);
                }
                Err(mpsc::TrySendError::Disconnected(frame)) => {
                    pending = Some(frame);
                }
            }
        }

        if !queued {
            DROPPED_FRAMES_DUE_TO_BACKPRESSURE.fetch_add(1, Ordering::Relaxed);
        }
    }
}

// ─── Recording pipeline ────────────────────────────────────────────────────────

fn unix_now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

fn start_encoder_worker(generation: u64) {
    let worker_count = encoder_worker_count();
    let mut senders = Vec::with_capacity(worker_count);
    let mut handles = Vec::with_capacity(worker_count);

    for worker_index in 0..worker_count {
        let (tx, rx) = mpsc::sync_channel::<PendingFrame>(MAX_PENDING_ENCODE_FRAMES);
        senders.push(tx);

        let handle = std::thread::Builder::new()
            .name(format!("screen-encoder-{generation}-{worker_index}"))
            .spawn(move || {
                while let Ok(frame) = rx.recv() {
                    if let Some(encoded) = encode_pending_frame(frame) {
                        let mut frames = FRAMES.lock().unwrap();
                        if frames.len() < max_frames() {
                            frames.push(encoded);
                        }
                    }
                }
            })
            .expect("failed to spawn screen-encoder thread");
        handles.push(handle);
    }

    *ENCODER_TX.lock().unwrap() = senders;
    *ENCODER_WORKERS.lock().unwrap() = handles;
}

fn finish_encoder_worker() {
    ENCODER_TX.lock().unwrap().clear();
    let mut workers = ENCODER_WORKERS.lock().unwrap();
    for handle in workers.drain(..) {
        let _ = handle.join();
    }
}

fn encoder_worker_count() -> usize {
    let available = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(2);
    available.saturating_sub(1).clamp(1, MAX_ENCODER_WORKERS)
}

fn queue_completed_frames(ended_at_unix_ms: u64, reason: &str) -> usize {
    let mut frames = std::mem::take(&mut *FRAMES.lock().unwrap());
    frames.sort_by_key(|frame| frame.timestamp_ms);
    let frame_count = frames.len();
    let started_at_unix_ms = SESSION_START_UNIX_MS.lock().unwrap().take();
    if frames.is_empty() {
        return 0;
    }

    let capture = CompletedFrameCapture {
        started_at_unix_ms,
        ended_at_unix_ms: Some(ended_at_unix_ms),
        frames,
    };
    let mut completed = COMPLETED_FRAMES.lock().unwrap();
    completed.push_back(capture);
    while completed.len() > MAX_COMPLETED_SESSIONS {
        let _ = completed.pop_front();
    }
    log::info!(
        "screen_recorder: queued replay frames ({reason}) frames={} queued_sessions={}",
        frame_count,
        completed.len()
    );
    frame_count
}

fn max_frames() -> usize {
    (replay_capture_fps() as usize).saturating_mul(MAX_CAPTURE_BUFFER_SECS)
}

// ─── Replay merge helpers ──────────────────────────────────────────────────────

fn partition_matching_frame_captures(
    mut captures: VecDeque<CompletedFrameCapture>,
    snapshot: &crate::bridge::BridgeRunSnapshot,
) -> (Vec<CompletedFrameCapture>, VecDeque<CompletedFrameCapture>) {
    let mut matched = Vec::new();
    let mut remaining = VecDeque::new();
    while let Some(capture) = captures.pop_front() {
        if frame_capture_matches_run(&capture, snapshot) {
            matched.push(capture);
        } else {
            remaining.push_back(capture);
        }
    }
    (matched, remaining)
}

fn frame_capture_matches_run(
    capture: &CompletedFrameCapture,
    snapshot: &crate::bridge::BridgeRunSnapshot,
) -> bool {
    let capture_duration_ms = capture.frames.last().map(|f| f.timestamp_ms).unwrap_or(0);
    let run_duration_ms = snapshot
        .duration_secs
        .map(|secs| (secs.max(0.0) * 1000.0).round() as u64)
        .unwrap_or(0);

    match (
        capture.started_at_unix_ms,
        capture.ended_at_unix_ms,
        snapshot.started_at_unix_ms,
        snapshot.ended_at_unix_ms,
    ) {
        (Some(c_start), Some(c_end), Some(r_start), Some(r_end)) => {
            let overlap_start = c_start.max(r_start);
            let overlap_end = c_end.min(r_end);
            overlap_end > overlap_start
                || c_start.abs_diff(r_start) <= 2_500
                || c_end.abs_diff(r_end) <= 2_500
        }
        _ if run_duration_ms > 0 && capture_duration_ms > 0 => {
            capture_duration_ms.abs_diff(run_duration_ms) <= 5_000
        }
        _ => false,
    }
}

fn pause_contains_abs_ms(
    pause_windows: &[crate::bridge::BridgeRunPauseWindow],
    abs_ms: u64,
) -> bool {
    pause_windows
        .iter()
        .any(|window| abs_ms >= window.started_at_unix_ms && abs_ms < window.ended_at_unix_ms)
}

fn paused_duration_before_abs_ms(
    pause_windows: &[crate::bridge::BridgeRunPauseWindow],
    abs_ms: u64,
) -> u64 {
    pause_windows
        .iter()
        .map(|window| {
            if abs_ms <= window.started_at_unix_ms {
                0
            } else {
                abs_ms
                    .min(window.ended_at_unix_ms)
                    .saturating_sub(window.started_at_unix_ms)
            }
        })
        .sum()
}

fn merge_frame_captures(
    mut captures: Vec<CompletedFrameCapture>,
    snapshot: &crate::bridge::BridgeRunSnapshot,
) -> Vec<ScreenFrame> {
    captures.sort_by_key(|capture| capture.started_at_unix_ms.unwrap_or(u64::MAX));
    let capture_count = captures.len();
    let base_start_ms = snapshot
        .started_at_unix_ms
        .or_else(|| captures.iter().filter_map(|c| c.started_at_unix_ms).min())
        .unwrap_or(0);
    let run_duration_ms = snapshot
        .duration_secs
        .map(|secs| (secs.max(0.0) * 1000.0).round() as u64);

    let mut frames = Vec::new();
    for capture in captures {
        if let Some(capture_start_ms) = capture.started_at_unix_ms {
            frames.extend(capture.frames.into_iter().filter_map(|mut frame| {
                let abs_ms = capture_start_ms.saturating_add(frame.timestamp_ms);
                if abs_ms < base_start_ms {
                    return None;
                }
                if pause_contains_abs_ms(&snapshot.pause_windows, abs_ms) {
                    return None;
                }
                let paused_before = paused_duration_before_abs_ms(&snapshot.pause_windows, abs_ms);
                frame.timestamp_ms = abs_ms
                    .saturating_sub(base_start_ms)
                    .saturating_sub(paused_before);
                Some(frame)
            }));
        } else {
            frames.extend(capture.frames);
        }
    }

    frames.sort_by_key(|frame| frame.timestamp_ms);
    if let Some(limit_ms) = run_duration_ms.map(|ms| ms.saturating_add(1_500)) {
        frames.retain(|frame| frame.timestamp_ms <= limit_ms);
    }
    log::info!(
        "screen_recorder: merged replay frame segments={} frames={}",
        capture_count,
        frames.len()
    );
    frames
}

// ─── Encoding ──────────────────────────────────────────────────────────────────

fn encode_pending_frame(frame: PendingFrame) -> Option<ScreenFrame> {
    use image::codecs::jpeg::JpegEncoder;

    let mut rgb = Vec::with_capacity((frame.width * frame.height * 3) as usize);
    for px in frame.bgra.chunks_exact(4) {
        rgb.push(px[2]);
        rgb.push(px[1]);
        rgb.push(px[0]);
    }

    let img =
        image::ImageBuffer::<image::Rgb<u8>, Vec<u8>>::from_raw(frame.width, frame.height, rgb)?;

    let quality = replay_capture_quality();
    let target_fps = replay_capture_fps();
    let out_w = replay_capture_width().min(frame.width).max(1);
    let out_h = ((frame.height as f32 / frame.width as f32) * out_w as f32).round() as u32;
    let resized =
        image::imageops::resize(&img, out_w, out_h.max(1), quality.resize_filter(target_fps));

    let mut buf = Vec::new();
    JpegEncoder::new_with_quality(&mut buf, quality.jpeg_quality(target_fps))
        .encode_image(&image::DynamicImage::ImageRgb8(resized))
        .ok()?;

    Some(ScreenFrame {
        timestamp_ms: frame.timestamp_ms,
        jpeg_b64: base64_encode(&buf),
    })
}

pub(crate) fn compute_capture_rect(bounds: &RegionRect) -> RegionRect {
    match replay_capture_framing() {
        ReplayCaptureFraming::Fullscreen => *bounds,
        ReplayCaptureFraming::Cropped => {
            let cap_w = (bounds.width / 2).max(320).min(bounds.width);
            let cap_h = (bounds.height / 2).max(180).min(bounds.height);
            let cap_x = bounds.x + (bounds.width as i32 - cap_w as i32) / 2;
            let cap_y = bounds.y + (bounds.height as i32 - cap_h as i32) / 2;
            RegionRect {
                x: cap_x,
                y: cap_y,
                width: cap_w,
                height: cap_h,
            }
        }
    }
}

/// RFC 4648 standard base64 encoder — avoids adding a dependency.
fn base64_encode(data: &[u8]) -> String {
    const T: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = chunk.get(1).copied().unwrap_or(0) as u32;
        let b2 = chunk.get(2).copied().unwrap_or(0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(T[((n >> 18) & 63) as usize] as char);
        out.push(T[((n >> 12) & 63) as usize] as char);
        out.push(if chunk.len() > 1 {
            T[((n >> 6) & 63) as usize] as char
        } else {
            '='
        });
        out.push(if chunk.len() > 2 {
            T[(n & 63) as usize] as char
        } else {
            '='
        });
    }
    out
}
