// ─── UE4SS deploy + injection command ───────────────────────────────────────

#[tauri::command]
fn inject_bridge(app: AppHandle, state: tauri::State<AppState>) -> Result<(), String> {
    let stats_dir = {
        let s = state.settings.lock().map_err(|e| e.to_string())?;
        s.stats_dir.clone()
    };
    let _ = bridge::start_log_tailer(app.clone(), &stats_dir);
    deploy_and_inject_ue4ss(&app, &stats_dir)
}

#[tauri::command]
fn ue4ss_get_recent_logs(limit: Option<usize>) -> Vec<String> {
    bridge::recent_logs(limit.unwrap_or(400))
}

#[tauri::command]
fn ue4ss_trigger_hot_reload() -> Result<(), String> {
    bridge::trigger_hot_reload()
}

#[derive(serde::Serialize)]
struct Ue4ssRuntimeFlags {
    profile: String,
    enable_pe_hook: bool,
    disable_pe_hook: bool,
    discovery: bool,
    safe_mode: bool,
    no_rust: bool,
    log_all_events: bool,
    object_debug: bool,
    non_ui_probe: bool,
    ui_counter_fallback: bool,
    score_ui_fallback: bool,
    hook_process_internal: bool,
    hook_process_local_script: bool,
    class_probe_hooks: bool,
    class_probe_scalar_reads: bool,
    class_probe_scan_all: bool,
    allow_unsafe_hooks: bool,
    native_hooks: bool,
    hook_process_event: bool,
    detour_callbacks: bool,
    direct_pull_invoke: bool,
    experimental_runtime: bool,
    ui_settext_hook: bool,
    ui_widget_probe: bool,
    in_game_overlay: bool,
}

#[tauri::command]
fn ue4ss_get_runtime_flags(state: tauri::State<AppState>) -> Result<Ue4ssRuntimeFlags, String> {
    let stats_dir = {
        let s = state.settings.lock().map_err(|e| e.to_string())?;
        s.stats_dir.clone()
    };
    let raw = bridge::get_runtime_flags(&stats_dir)?;
    Ok(Ue4ssRuntimeFlags {
        profile: raw.profile,
        enable_pe_hook: raw.enable_pe_hook,
        disable_pe_hook: raw.disable_pe_hook,
        discovery: raw.discovery,
        safe_mode: raw.safe_mode,
        no_rust: raw.no_rust,
        log_all_events: raw.log_all_events,
        object_debug: raw.object_debug,
        non_ui_probe: raw.non_ui_probe,
        ui_counter_fallback: raw.ui_counter_fallback,
        score_ui_fallback: raw.score_ui_fallback,
        hook_process_internal: raw.hook_process_internal,
        hook_process_local_script: raw.hook_process_local_script,
        class_probe_hooks: raw.class_probe_hooks,
        class_probe_scalar_reads: raw.class_probe_scalar_reads,
        class_probe_scan_all: raw.class_probe_scan_all,
        allow_unsafe_hooks: raw.allow_unsafe_hooks,
        native_hooks: raw.native_hooks,
        hook_process_event: raw.hook_process_event,
        detour_callbacks: raw.detour_callbacks,
        direct_pull_invoke: raw.direct_pull_invoke,
        experimental_runtime: raw.experimental_runtime,
        ui_settext_hook: raw.ui_settext_hook,
        ui_widget_probe: raw.ui_widget_probe,
        in_game_overlay: raw.in_game_overlay,
    })
}

#[tauri::command]
fn ue4ss_set_runtime_flag(
    key: String,
    enabled: bool,
    state: tauri::State<AppState>,
) -> Result<(), String> {
    let stats_dir = {
        let s = state.settings.lock().map_err(|e| e.to_string())?;
        s.stats_dir.clone()
    };
    bridge::set_runtime_flag(&stats_dir, &key, enabled)
}

#[tauri::command]
fn ue4ss_reload_runtime_flags(state: tauri::State<AppState>) -> Result<(), String> {
    let stats_dir = {
        let s = state.settings.lock().map_err(|e| e.to_string())?;
        s.stats_dir.clone()
    };
    bridge::request_runtime_flag_reload(&stats_dir)
}

#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
struct OverlayRuntimeNotice {
    visible: bool,
    kind: String,
    title: String,
    message: String,
}

fn hidden_overlay_runtime_notice() -> OverlayRuntimeNotice {
    OverlayRuntimeNotice {
        visible: false,
        kind: "warning".to_string(),
        title: String::new(),
        message: String::new(),
    }
}

fn overlay_notice_pid_tracker() -> &'static Mutex<Option<(u32, Instant)>> {
    static PID_TRACKER: OnceLock<Mutex<Option<(u32, Instant)>>> = OnceLock::new();
    PID_TRACKER.get_or_init(|| Mutex::new(None))
}

fn overlay_notice_game_pid_age(pid: u32) -> Duration {
    let tracker = overlay_notice_pid_tracker();
    let now = Instant::now();
    let Ok(mut state) = tracker.lock() else {
        return Duration::from_secs(0);
    };

    match *state {
        Some((tracked_pid, seen_at)) if tracked_pid == pid => now.duration_since(seen_at),
        _ => {
            *state = Some((pid, now));
            Duration::from_secs(0)
        }
    }
}

fn clear_overlay_notice_game_pid() {
    let tracker = overlay_notice_pid_tracker();
    if let Ok(mut state) = tracker.lock() {
        *state = None;
    }
}

fn current_overlay_runtime_notice() -> OverlayRuntimeNotice {
    let Some(pid) = bridge::current_game_pid() else {
        clear_overlay_notice_game_pid();
        return hidden_overlay_runtime_notice();
    };

    let pid_age = overlay_notice_game_pid_age(pid);
    let startup_grace = Duration::from_secs(6);
    let runtime_load_grace = Duration::from_secs(12);

    let runtime_loaded = bridge::is_ue4ss_loaded_for_pid(pid);
    if !runtime_loaded {
        if pid_age < runtime_load_grace || !bridge::is_current_game_ready_for_injection() {
            return hidden_overlay_runtime_notice();
        }
        return OverlayRuntimeNotice {
            visible: true,
            kind: "warning".to_string(),
            title: "Restart KovaaK's Required".to_string(),
            message:
                "AimMod could not restore its in-game bridge. Restart KovaaK's if the HUD stays offline."
                    .to_string(),
        };
    }

    let bridge_connected = bridge::is_bridge_dll_connected();
    let has_recent_state_snapshot = bridge::has_recent_state_snapshot_ack();
    let has_recent_stats_flow = bridge::has_recent_bridge_stats_flow();
    let stats_flow_stalled = bridge::is_bridge_stats_flow_stalled();
    let bridge_healthy = bridge_connected
        && (has_recent_state_snapshot || has_recent_stats_flow)
        && !stats_flow_stalled;

    if bridge::is_runtime_restart_required() && !bridge_healthy {
        if pid_age < startup_grace {
            return hidden_overlay_runtime_notice();
        }
        return OverlayRuntimeNotice {
            visible: true,
            kind: "warning".to_string(),
            title: "Restart KovaaK's Required".to_string(),
            message: "AimMod updated its in-game bridge. Restart KovaaK's to load the new version."
                .to_string(),
        };
    }

    if !bridge_connected {
        if pid_age < startup_grace {
            return hidden_overlay_runtime_notice();
        }
        return OverlayRuntimeNotice {
            visible: true,
            kind: "warning".to_string(),
            title: "Bridge Reconnect Failed".to_string(),
            message:
                "AimMod could not fully reconnect to the in-game bridge. Restart KovaaK's if stats stay offline."
                    .to_string(),
        };
    }

    if !has_recent_state_snapshot && !has_recent_stats_flow {
        if pid_age < startup_grace {
            return hidden_overlay_runtime_notice();
        }
        return OverlayRuntimeNotice {
            visible: true,
            kind: "warning".to_string(),
            title: "Restart KovaaK's Required".to_string(),
            message:
                "AimMod is connected, but the in-game state never came back. Restart KovaaK's if the HUD stays blank."
                    .to_string(),
        };
    }

    if stats_flow_stalled {
        return OverlayRuntimeNotice {
            visible: true,
            kind: "warning".to_string(),
            title: "Stats Are Not Updating".to_string(),
            message:
                "AimMod reconnected, but live stats are stalled. Restart KovaaK's if the HUD stays blank."
                    .to_string(),
        };
    }

    hidden_overlay_runtime_notice()
}

#[tauri::command]
fn get_overlay_runtime_notice() -> OverlayRuntimeNotice {
    current_overlay_runtime_notice()
}

#[tauri::command]
fn get_is_debug_build() -> bool {
    cfg!(debug_assertions)
}

#[tauri::command]
fn get_obs_overlay_url() -> String {
    "http://127.0.0.1:43115/browser-overlay.html?surface=obs".to_string()
}

#[tauri::command]
fn toggle_layout_huds(app: AppHandle) -> Result<(), String> {
    app.emit("toggle-layout-huds", ())
        .map_err(|e| e.to_string())
}

fn deploy_and_inject_ue4ss(app: &AppHandle, stats_dir: &str) -> Result<(), String> {
    // UE4SS payload is shipped as a Tauri resource folder (see bundle.resources).
    let resource_dir = app
        .path()
        .resource_dir()
        .map_err(|_| "Could not get resource directory")?;
    bridge::deploy_and_inject(resource_dir.as_path(), stats_dir)
}

#[cfg(target_os = "windows")]
fn start_ue4ss_reinject_monitor(app: AppHandle, stats_dir: String) {
    std::thread::Builder::new()
        .name("ue4ss-reinject-monitor".into())
        .spawn(move || {
            let poll_interval = std::time::Duration::from_secs(2);
            let reinject_cooldown = std::time::Duration::from_secs(8);
            let mut last_pid: Option<u32> = None;
            let mut last_attempt: Option<(u32, std::time::Instant)> = None;
            let mut last_notice = hidden_overlay_runtime_notice();

            loop {
                let current_pid = bridge::current_game_pid();

                if current_pid != last_pid {
                    match (last_pid, current_pid) {
                        (None, Some(pid)) => {
                            log::info!("Detected {pid} for KovaaK's process; monitoring UE4SS load state");
                        }
                        (Some(old), Some(new)) if old != new => {
                            log::warn!(
                                "Detected KovaaK's restart (pid {old} -> {new}); scheduling UE4SS reinjection"
                            );
                        }
                        (Some(old), None) => {
                            log::info!("KovaaK's process exited (pid {old})");
                        }
                        _ => {}
                    }
                    last_pid = current_pid;
                }

                match current_pid {
                    Some(pid) => {
                        let runtime_loaded = bridge::is_ue4ss_loaded_for_pid(pid);
                        let bridge_connected = bridge::is_bridge_dll_connected();

                        if runtime_loaded && bridge_connected {
                            last_attempt = None;
                        } else if runtime_loaded {
                            let can_attempt = match last_attempt {
                                Some((attempt_pid, at)) if attempt_pid == pid => {
                                    at.elapsed() >= reinject_cooldown
                                }
                                _ => true,
                            };

                            if can_attempt {
                                log::debug!(
                                    "UE4SS is loaded for KovaaK's pid {pid} but the bridge pipe is disconnected; waiting for mod-side reconnect"
                                );
                                last_attempt = Some((pid, std::time::Instant::now()));
                            }
                        } else {
                            let can_attempt = match last_attempt {
                                Some((attempt_pid, at)) if attempt_pid == pid => {
                                    at.elapsed() >= reinject_cooldown
                                }
                                _ => true,
                            };

                            if can_attempt {
                                log::warn!(
                                    "UE4SS not loaded for KovaaK's pid {pid}; attempting deploy/inject"
                                );
                                match deploy_and_inject_ue4ss(&app, &stats_dir) {
                                    Ok(()) => {
                                        log::info!(
                                            "UE4SS deploy/inject attempt finished for KovaaK's pid {pid}"
                                        );
                                    }
                                    Err(e) => {
                                        if bridge::is_injection_deferred_error(&e) {
                                            log::info!(
                                                "UE4SS deploy/inject deferred for KovaaK's pid {pid}: {e}"
                                            );
                                        } else {
                                            log::warn!(
                                                "UE4SS deploy/inject attempt failed for KovaaK's pid {pid}: {e}"
                                            );
                                        }
                                    }
                                }
                                last_attempt = Some((pid, std::time::Instant::now()));
                            }
                        }
                    }
                    None => {
                        last_attempt = None;
                    }
                }

                let next_notice = current_overlay_runtime_notice();
                if next_notice != last_notice {
                    let _ = app.emit("overlay-runtime-notice", &next_notice);
                    last_notice = next_notice;
                }

                std::thread::sleep(poll_interval);
            }
        })
        .ok();
}

#[cfg(not(target_os = "windows"))]
fn start_ue4ss_reinject_monitor(_app: AppHandle, _stats_dir: String) {}

mod app_version;
mod benchmark_overlay;
mod bridge;
mod coaching;
mod discord_rpc;
mod file_watcher;
mod hub_api;
mod hub_live_activity;
mod hub_sync;
mod kovaaks_api;
mod kovaaks_theme;
mod local_llm;
mod logger;
mod mouse_hook;
mod osu;
mod osu_coaching;
mod osu_diagnostics;
mod osu_library;
mod osu_profiles;
mod osu_realm_reader;
mod osu_replay_analytics;
mod osu_replay_host;
mod osu_skins;
mod overlay_service;
mod replay_store;
mod sapi;
mod scenario_index;
mod screen_recorder;
mod session_store;
mod settings;
mod stats_db;
mod steam_api;
mod steam_integration;
mod target_response;
mod window_tracker;

use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use tauri::{
    AppHandle, Emitter, Manager, Runtime,
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
};

pub use settings::{AppSettings, FriendProfile};

/// Global app state accessible from Tauri commands.
pub struct AppState {
    pub settings: Arc<Mutex<AppSettings>>,
    pub local_llm: Arc<Mutex<local_llm::LocalLlmRuntimeState>>,
}

// ─── Monitor helpers ──────────────────────────────────────────────────────────

#[derive(serde::Serialize, Clone)]
pub struct MonitorInfo {
    pub index: usize,
    pub name: String,
    pub width: u32,
    pub height: u32,
    pub x: i32,
    pub y: i32,
}

/// Physical screen origin + scale factor of the overlay window.
/// Used by frontend overlays to convert CSS coords → absolute screen pixels.
#[derive(serde::Serialize, Clone)]
pub struct OverlayOrigin {
    pub x: i32,
    pub y: i32,
    pub scale_factor: f64,
}

#[tauri::command]
fn get_overlay_origin(app: AppHandle) -> OverlayOrigin {
    let win = app.get_webview_window("overlay");
    let (x, y) = win
        .as_ref()
        .and_then(|w| w.outer_position().ok())
        .map(|p| (p.x, p.y))
        .unwrap_or((0, 0));
    let scale_factor = win
        .as_ref()
        .and_then(|w| w.scale_factor().ok())
        .unwrap_or(1.0);
    log::debug!("get_overlay_origin: ({x},{y}) scale={scale_factor}");
    OverlayOrigin { x, y, scale_factor }
}

#[cfg(target_os = "windows")]
fn configure_overlay_window(win: &tauri::WebviewWindow) {
    use windows::Win32::UI::WindowsAndMessaging::{
        GWL_EXSTYLE, GWL_STYLE, GetWindowLongW, SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE,
        SWP_NOOWNERZORDER, SWP_NOSIZE, SWP_NOZORDER, SetWindowDisplayAffinity, SetWindowLongW,
        SetWindowPos, WDA_EXCLUDEFROMCAPTURE, WDA_MONITOR, WS_EX_OVERLAPPEDWINDOW,
        WS_OVERLAPPEDWINDOW, WS_POPUP,
    };

    let _ = win.unmaximize();
    let _ = win.set_decorations(false);
    let _ = win.set_shadow(false);

    let Ok(hwnd) = win.hwnd() else {
        return;
    };
    let hwnd = windows::Win32::Foundation::HWND(hwnd.0 as _);

    let style = unsafe { GetWindowLongW(hwnd, GWL_STYLE) } as u32;
    let ex_style = unsafe { GetWindowLongW(hwnd, GWL_EXSTYLE) } as u32;
    let desired_style = (style & !WS_OVERLAPPEDWINDOW.0) | WS_POPUP.0;
    let desired_ex_style = ex_style & !WS_EX_OVERLAPPEDWINDOW.0;

    if desired_style != style || desired_ex_style != ex_style {
        unsafe {
            let _ = SetWindowLongW(hwnd, GWL_STYLE, desired_style as i32);
            let _ = SetWindowLongW(hwnd, GWL_EXSTYLE, desired_ex_style as i32);
            let _ = SetWindowPos(
                hwnd,
                None,
                0,
                0,
                0,
                0,
                SWP_NOMOVE
                    | SWP_NOSIZE
                    | SWP_NOZORDER
                    | SWP_NOOWNERZORDER
                    | SWP_NOACTIVATE
                    | SWP_FRAMECHANGED,
            );
        }
    }

    unsafe {
        if SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE).is_err() {
            let _ = SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
        }
    }
}

#[cfg(not(target_os = "windows"))]
fn configure_overlay_window(win: &tauri::WebviewWindow) {
    let _ = win.unmaximize();
    let _ = win.set_decorations(false);
}

pub(crate) fn apply_overlay_bounds(
    win: &tauri::WebviewWindow,
    x: i32,
    y: i32,
    width: u32,
    height: u32,
) {
    let _ = win.set_fullscreen(false);
    configure_overlay_window(win);

    #[cfg(target_os = "windows")]
    {
        use windows::Win32::UI::WindowsAndMessaging::{
            SWP_NOACTIVATE, SWP_NOOWNERZORDER, SWP_NOZORDER, SetWindowPos,
        };

        if let Ok(hwnd) = win.hwnd() {
            let hwnd = windows::Win32::Foundation::HWND(hwnd.0 as _);
            unsafe {
                let _ = SetWindowPos(
                    hwnd,
                    None,
                    x,
                    y,
                    width as i32,
                    height as i32,
                    SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE,
                );
            }
            return;
        }
    }

    let _ = win.set_position(tauri::PhysicalPosition::new(x, y));
    let _ = win.set_size(tauri::PhysicalSize::new(width, height));
}

/// Reposition the overlay window to cover the monitor at `index`.
///
/// Use a normal borderless window sized to the monitor instead of OS fullscreen.
/// Transparent fullscreen webviews are much more likely to force desktop
/// composition over the game surface, which can add present/input latency when
/// the overlay sits on the same monitor as KovaaK's.
pub fn apply_monitor(app: &AppHandle, index: usize) {
    let Some(win) = app.get_webview_window("overlay") else {
        return;
    };
    let monitors = win.available_monitors().unwrap_or_default();
    let monitor = monitors.get(index).or_else(|| monitors.first());
    let Some(m) = monitor else { return };

    let pos = m.position();
    let size = m.size();

    // Use physical monitor bounds directly. Converting to logical coordinates
    // here can apply the current window DPI instead of the target monitor DPI,
    // which leaves the overlay undersized on mixed-scale setups.
    apply_overlay_bounds(&win, pos.x, pos.y, size.width, size.height);

    // Notify the screen recorder that monitor placement changed so it can reset
    // any cached crop logging. Actual capture bounds are derived from the live
    // KovaaK client rect, not the overlay monitor.
    let monitor_rect = settings::RegionRect {
        x: pos.x,
        y: pos.y,
        width: size.width,
        height: size.height,
    };
    screen_recorder::update_monitor_rect(&monitor_rect);
}

// ─── Tauri Commands ────────────────────────────────────────────────────────────

#[tauri::command]
fn open_speech_settings() {
    // Opens Windows Speech settings (Time & Language > Speech) where the user
    // can install additional high-quality Neural / Natural voices.
    #[cfg(target_os = "windows")]
    let _ = std::process::Command::new("cmd")
        .args(["/C", "start", "ms-settings:speech"])
        .spawn();
}

#[tauri::command]
fn open_natural_voices_store() {
    // Opens Windows Accessibility > Narrator settings where the user can click
    // "Add more voices" to install the free Microsoft Neural voices (Aria, Jenny,
    // Guy, …) that are bundled with Windows but not installed by default.
    #[cfg(target_os = "windows")]
    let _ = std::process::Command::new("cmd")
        .args(["/C", "start", "ms-settings:easeofaccess-narrator"])
        .spawn();
}

/// Return display names of every TTS voice installed on this machine.
/// Reads both the legacy SAPI5 registry hive and the newer OneCore hive
/// (neural / Narrator voices) so all installed voices are returned.
#[tauri::command]
fn list_sapi_voices() -> Vec<String> {
    sapi::list_voices()
}

/// Speak `text` using the named voice (or the system default when `None`).
/// Kills any currently-playing speech first, then spawns a background
/// PowerShell process so the command returns immediately.
#[tauri::command]
fn speak_with_sapi(text: String, voice_name: Option<String>) {
    sapi::speak(&text, voice_name.as_deref());
}

#[tauri::command]
fn get_settings(state: tauri::State<AppState>) -> Result<AppSettings, String> {
    let s = state.settings.lock().map_err(|e| e.to_string())?;
    Ok(s.clone())
}

#[tauri::command]
fn save_settings(
    mut new_settings: AppSettings,
    state: tauri::State<AppState>,
    app: AppHandle,
) -> Result<(), String> {
    let persisted_hub_api_base_url = settings::load_persisted(&app)
        .map(|settings| settings.hub_api_base_url)
        .unwrap_or_else(|_| settings::DEFAULT_HUB_API_BASE_URL.to_string());

    new_settings.hub_api_base_url =
        settings::normalize_hub_api_base_url(&new_settings.hub_api_base_url);
    if new_settings.hub_api_base_url.trim().is_empty() {
        new_settings.hub_api_base_url = settings::DEFAULT_HUB_API_BASE_URL.to_string();
    }
    new_settings.replay_capture_width =
        settings::normalize_replay_capture_width(new_settings.replay_capture_width);
    new_settings.replay_capture_framing =
        settings::normalize_replay_capture_framing(&new_settings.replay_capture_framing);
    new_settings.replay_capture_quality =
        settings::normalize_replay_capture_quality(&new_settings.replay_capture_quality);
    new_settings.coaching_focus_area =
        settings::normalize_coaching_focus_area(&new_settings.coaching_focus_area);
    new_settings.coaching_challenge_preference = settings::normalize_coaching_challenge_preference(
        &new_settings.coaching_challenge_preference,
    );
    new_settings.coaching_time_preference =
        settings::normalize_coaching_time_preference(&new_settings.coaching_time_preference);
    settings::normalize_overlay_settings(&mut new_settings);

    let mut persisted_settings = new_settings.clone();
    if settings::hub_api_base_url_launch_override().is_some() {
        persisted_settings.hub_api_base_url = persisted_hub_api_base_url;
    }

    let mut effective_settings = persisted_settings.clone();
    settings::apply_runtime_overrides(&mut effective_settings);

    let mut s = state.settings.lock().map_err(|e| e.to_string())?;
    *s = effective_settings.clone();
    settings::persist(&app, &persisted_settings).map_err(|e| e.to_string())?;
    file_watcher::restart(&app, &effective_settings.stats_dir);
    mouse_hook::set_dpi(effective_settings.mouse_dpi);
    mouse_hook::set_feedback_enabled(effective_settings.live_feedback_enabled);
    mouse_hook::set_feedback_verbosity(effective_settings.live_feedback_verbosity);
    screen_recorder::set_replay_capture_fps(effective_settings.replay_capture_fps);
    screen_recorder::set_replay_capture_width(effective_settings.replay_capture_width);
    screen_recorder::set_replay_capture_framing(&effective_settings.replay_capture_framing);
    screen_recorder::set_replay_capture_quality(&effective_settings.replay_capture_quality);
    replay_store::apply_replay_retention(
        &app,
        Some(effective_settings.replay_keep_count as usize),
        None,
    );
    replay_store::maybe_install_ffmpeg_for_replay_media(app.clone(), effective_settings.clone());
    hub_sync::queue_pending_session_sync(&app);
    let _ = app.emit("settings-changed", ());
    Ok(())
}

#[tauri::command]
fn reset_settings(state: tauri::State<AppState>, app: AppHandle) -> Result<AppSettings, String> {
    let persisted_defaults = AppSettings::default();
    let mut defaults = persisted_defaults.clone();
    settings::apply_runtime_overrides(&mut defaults);
    let mut s = state.settings.lock().map_err(|e| e.to_string())?;
    *s = defaults.clone();
    settings::persist(&app, &persisted_defaults).map_err(|e| e.to_string())?;
    file_watcher::restart(&app, &defaults.stats_dir);
    mouse_hook::set_dpi(defaults.mouse_dpi);
    mouse_hook::set_feedback_enabled(defaults.live_feedback_enabled);
    mouse_hook::set_feedback_verbosity(defaults.live_feedback_verbosity);
    screen_recorder::set_replay_capture_fps(defaults.replay_capture_fps);
    screen_recorder::set_replay_capture_width(defaults.replay_capture_width);
    screen_recorder::set_replay_capture_framing(&defaults.replay_capture_framing);
    screen_recorder::set_replay_capture_quality(&defaults.replay_capture_quality);
    replay_store::apply_replay_retention(&app, Some(defaults.replay_keep_count as usize), None);
    let _ = app.emit("settings-changed", ());
    Ok(defaults)
}

#[tauri::command]
fn get_hub_sync_status(app: AppHandle) -> Result<hub_sync::HubSyncOverview, String> {
    hub_sync::get_sync_overview(&app).map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_start_device_link(
    app: AppHandle,
    base_url: Option<String>,
) -> Result<hub_sync::HubDeviceLinkSession, String> {
    hub_sync::start_device_link(&app, base_url)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_poll_device_link(
    app: AppHandle,
    base_url: Option<String>,
    device_code: String,
) -> Result<hub_sync::HubDeviceLinkPollStatus, String> {
    hub_sync::poll_device_link(&app, base_url, device_code)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn hub_disconnect(app: AppHandle) -> Result<(), String> {
    hub_sync::clear_linked_account(&app).map_err(|e| e.to_string())
}

#[tauri::command]
fn hub_force_full_resync(app: AppHandle) -> Result<(), String> {
    hub_sync::force_full_resync(&app).map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_overview(app: AppHandle) -> Result<hub_api::HubOverviewResponse, String> {
    hub_api::get_overview(&app).await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_search(app: AppHandle, query: String) -> Result<hub_api::HubSearchResponse, String> {
    hub_api::search(&app, query)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_list_replays(
    app: AppHandle,
    query: Option<String>,
    scenario_name: Option<String>,
    handle: Option<String>,
    limit: Option<u32>,
) -> Result<hub_api::HubReplayListResponse, String> {
    hub_api::list_replays(&app, query, scenario_name, handle, limit)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_profile(
    app: AppHandle,
    handle: String,
) -> Result<hub_api::HubProfileResponse, String> {
    hub_api::get_profile(&app, handle)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_scenario(
    app: AppHandle,
    slug: String,
) -> Result<hub_api::HubScenarioPageResponse, String> {
    hub_api::get_scenario(&app, slug)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_benchmark_page(
    app: AppHandle,
    handle: String,
    benchmark_id: u32,
) -> Result<hub_api::HubBenchmarkPageResponse, String> {
    hub_api::get_benchmark_page(&app, handle, benchmark_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_list_benchmarks(app: AppHandle) -> Result<hub_api::HubBenchmarkListResponse, String> {
    hub_api::list_benchmarks(&app)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_run(app: AppHandle, run_id: String) -> Result<hub_api::HubRunResponse, String> {
    hub_api::get_run(&app, run_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_player_scenario_history(
    app: AppHandle,
    handle: String,
    scenario_slug: String,
) -> Result<hub_api::HubPlayerScenarioHistoryResponse, String> {
    hub_api::get_player_scenario_history(&app, handle, scenario_slug)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_aim_profile(
    app: AppHandle,
    handle: String,
) -> Result<hub_api::HubAimProfileResponse, String> {
    hub_api::get_aim_profile(&app, handle)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_get_aim_fingerprint(
    app: AppHandle,
    handle: String,
) -> Result<hub_api::HubAimFingerprintResponse, String> {
    hub_api::get_aim_fingerprint(&app, handle)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn hub_query_coaching_knowledge(
    app: AppHandle,
    query: hub_api::HubCoachingKnowledgeQuery,
) -> Result<hub_api::HubCoachingKnowledgeResponse, String> {
    hub_api::query_coaching_knowledge(&app, query)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn get_local_llm_runtime_status(
    app: AppHandle,
    state: tauri::State<'_, AppState>,
) -> Result<local_llm::LocalLlmRuntimeStatus, String> {
    local_llm::get_runtime_status(&app, &state)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn install_local_llm_assets(
    app: AppHandle,
    state: tauri::State<'_, AppState>,
) -> Result<local_llm::LocalLlmRuntimeStatus, String> {
    local_llm::install_assets(&app, &state)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn uninstall_local_llm_assets(
    app: AppHandle,
    state: tauri::State<'_, AppState>,
) -> Result<local_llm::LocalLlmRuntimeStatus, String> {
    local_llm::uninstall_assets(&app, &state)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn stop_local_llm_runtime(
    app: AppHandle,
    state: tauri::State<'_, AppState>,
) -> Result<local_llm::LocalLlmRuntimeStatus, String> {
    local_llm::stop_runtime(&app, &state)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn local_llm_generate_coaching_reply(
    app: AppHandle,
    state: tauri::State<'_, AppState>,
    request: local_llm::LocalCoachChatRequest,
) -> Result<local_llm::LocalCoachChatResponse, String> {
    local_llm::generate_coaching_reply(&app, &state, request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn local_llm_stream_coaching_reply(
    app: AppHandle,
    state: tauri::State<'_, AppState>,
    request: local_llm::LocalCoachChatRequest,
    stream_id: String,
) -> Result<local_llm::LocalCoachChatResponse, String> {
    local_llm::stream_coaching_reply(&app, &state, request, stream_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn read_kovaaks_palette(app: AppHandle) -> kovaaks_theme::KovaaksPalette {
    let settings = settings::load(&app).unwrap_or_default();
    let path = if settings.kovaaks_palette_path.trim().is_empty() {
        kovaaks_theme::default_palette_path()
    } else {
        settings.kovaaks_palette_path.trim().to_string()
    };
    kovaaks_theme::read_palette(&path)
}

#[tauri::command]
fn write_kovaaks_palette_colors(
    app: AppHandle,
    colors: std::collections::HashMap<String, String>,
) -> Result<(), String> {
    let settings = settings::load(&app).unwrap_or_default();
    let path = if settings.kovaaks_palette_path.trim().is_empty() {
        kovaaks_theme::default_palette_path()
    } else {
        settings.kovaaks_palette_path.trim().to_string()
    };
    kovaaks_theme::write_palette_colors(&path, &colors)
}

// ─── Friend / API commands ─────────────────────────────────────────────────────

/// Persist the username of the friend chosen as battle opponent (pass None to clear).
#[tauri::command]
fn set_selected_friend(
    username: Option<String>,
    state: tauri::State<AppState>,
    app: AppHandle,
) -> Result<(), String> {
    let mut s = state.settings.lock().map_err(|e| e.to_string())?;
    s.selected_friend = username.clone();
    let cloned = s.clone();
    drop(s);
    settings::persist(&app, &cloned).map_err(|e| e.to_string())?;
    // Notify the overlay so VSMode refreshes without requiring an app restart.
    let _ = app.emit("selected-friend-changed", &username);
    Ok(())
}

/// Return the list of friend profiles stored in settings.
#[tauri::command]
async fn get_friends(state: tauri::State<'_, AppState>) -> Result<Vec<FriendProfile>, String> {
    let manual_friends = {
        let s = match state.settings.lock() {
            Ok(guard) => guard,
            Err(err) => return Err(err.to_string()),
        };
        s.friends
            .iter()
            .cloned()
            .map(|mut friend| {
                friend.bridge_managed = false;
                friend
            })
            .collect::<Vec<_>>()
    };

    let live_friends = current_bridge_friend_profiles().await;
    if live_friends.is_empty() {
        return Ok(manual_friends);
    }

    let mut manual_used = vec![false; manual_friends.len()];
    let mut merged = Vec::with_capacity(live_friends.len() + manual_friends.len());

    for live_friend in live_friends {
        if let Some((index, manual_friend)) = manual_friends
            .iter()
            .enumerate()
            .find(|(_, manual_friend)| same_friend_identity(&live_friend, manual_friend))
        {
            manual_used[index] = true;
            merged.push(merge_live_and_manual_friend(&live_friend, manual_friend));
        } else {
            merged.push(live_friend);
        }
    }

    for (index, manual_friend) in manual_friends.into_iter().enumerate() {
        if !manual_used[index] {
            merged.push(manual_friend);
        }
    }

    merged.sort_by(|left, right| {
        let left_key = first_nonempty_string(&[
            left.steam_account_name.as_str(),
            left.username.as_str(),
            left.steam_id.as_str(),
        ])
        .to_ascii_lowercase();
        let right_key = first_nonempty_string(&[
            right.steam_account_name.as_str(),
            right.username.as_str(),
            right.steam_id.as_str(),
        ])
        .to_ascii_lowercase();
        left_key.cmp(&right_key)
    });

    Ok(merged)
}

async fn current_bridge_friend_profiles() -> Vec<FriendProfile> {
    let Some(snapshot) = bridge::current_kovaaks_friend_scores() else {
        return Vec::new();
    };

    let mut unique = Vec::new();
    for entry in snapshot.entries {
        let Some(friend) = friend_profile_from_bridge_score_entry(&entry) else {
            continue;
        };
        let duplicate = unique.iter().any(|existing: &FriendProfile| {
            if !friend.steam_id.is_empty() && !existing.steam_id.is_empty() {
                existing.steam_id == friend.steam_id
            } else {
                existing.username.eq_ignore_ascii_case(&friend.username)
            }
        });

        if !duplicate {
            unique.push(enrich_friend_profile_from_steam(friend).await);
        }
    }

    unique.sort_by(|left, right| {
        let left_key = first_nonempty_string(&[
            left.steam_account_name.as_str(),
            left.username.as_str(),
            left.steam_id.as_str(),
        ])
        .to_ascii_lowercase();
        let right_key = first_nonempty_string(&[
            right.steam_account_name.as_str(),
            right.username.as_str(),
            right.steam_id.as_str(),
        ])
        .to_ascii_lowercase();
        left_key.cmp(&right_key)
    });

    unique
}

fn looks_like_steam_id(value: &str) -> bool {
    let trimmed = value.trim();
    trimmed.len() >= 17 && trimmed.bytes().all(|byte| byte.is_ascii_digit())
}

fn same_friend_identity(left: &FriendProfile, right: &FriendProfile) -> bool {
    if !left.steam_id.is_empty() && !right.steam_id.is_empty() {
        return left.steam_id == right.steam_id;
    }

    left.username.eq_ignore_ascii_case(&right.username)
}

fn merge_live_and_manual_friend(live: &FriendProfile, manual: &FriendProfile) -> FriendProfile {
    let live_username = live.username.trim();
    let manual_username = manual.username.trim();
    let username = if live_username.is_empty() || looks_like_steam_id(live_username) {
        first_nonempty_string(&[manual_username, live_username, live.steam_id.as_str()])
    } else {
        live_username.to_string()
    };

    FriendProfile {
        username,
        steam_id: first_nonempty_string(&[live.steam_id.as_str(), manual.steam_id.as_str()]),
        steam_account_name: first_nonempty_string(&[
            live.steam_account_name.as_str(),
            manual.steam_account_name.as_str(),
            manual.username.as_str(),
        ]),
        avatar_url: first_nonempty_string(&[live.avatar_url.as_str(), manual.avatar_url.as_str()]),
        country: first_nonempty_string(&[live.country.as_str(), manual.country.as_str()]),
        kovaaks_plus: live.kovaaks_plus || manual.kovaaks_plus,
        bridge_managed: true,
    }
}

fn bridge_linked_identity_for_provider<'a>(
    user: &'a bridge::BridgeCurrentUserProfile,
    provider: &str,
) -> Option<&'a bridge::BridgeLinkedIdentity> {
    user.linked_accounts
        .iter()
        .find(|account| account.provider.eq_ignore_ascii_case(provider))
}

fn bridge_user_display_name(user: &bridge::BridgeCurrentUserProfile) -> String {
    let steam_identity = bridge_linked_identity_for_provider(user, "steam");
    first_nonempty_string(&[
        user.display_name.as_str(),
        user.steam_name.as_str(),
        steam_identity
            .map(|account| account.display_name.as_str())
            .unwrap_or(""),
        steam_identity
            .map(|account| account.username.as_str())
            .unwrap_or(""),
        user.username.as_str(),
        user.steam_id.as_str(),
    ])
}

fn friend_profile_from_bridge_user(
    user: &bridge::BridgeCurrentUserProfile,
) -> Option<FriendProfile> {
    let username = bridge_user_display_name(user);
    if username.is_empty() {
        return None;
    }

    let steam_identity = bridge_linked_identity_for_provider(user, "steam");

    Some(FriendProfile {
        username,
        steam_id: user.steam_id.trim().to_string(),
        steam_account_name: first_nonempty_string(&[
            user.steam_name.as_str(),
            user.display_name.as_str(),
            steam_identity
                .map(|account| account.display_name.as_str())
                .unwrap_or(""),
            steam_identity
                .map(|account| account.username.as_str())
                .unwrap_or(""),
            user.username.as_str(),
        ]),
        avatar_url: user.avatar_url.trim().to_string(),
        country: String::new(),
        kovaaks_plus: false,
        bridge_managed: true,
    })
}

fn friend_profile_from_bridge_score_entry(
    entry: &bridge::BridgeFriendScoreEntry,
) -> Option<FriendProfile> {
    let steam_id = entry.steam_id.trim();
    let display_name = entry.steam_account_name.trim();
    if steam_id.is_empty() && display_name.is_empty() {
        return None;
    }

    Some(FriendProfile {
        username: first_nonempty_string(&[display_name, steam_id]),
        steam_id: steam_id.to_string(),
        steam_account_name: display_name.to_string(),
        avatar_url: String::new(),
        country: String::new(),
        kovaaks_plus: entry.kovaaks_plus_active,
        bridge_managed: true,
    })
}

async fn enrich_friend_profile_from_steam(mut profile: FriendProfile) -> FriendProfile {
    let steam_id = profile.steam_id.trim().to_string();
    if steam_id.is_empty() {
        return profile;
    }
    if !profile.avatar_url.trim().is_empty()
        && !profile.steam_account_name.trim().is_empty()
        && profile.steam_account_name.trim() != steam_id
    {
        return profile;
    }

    match steam_api::resolve_steam_user(&steam_id).await {
        Ok(steam) => {
            if profile.avatar_url.trim().is_empty() {
                profile.avatar_url = steam.avatar_url;
            }
            if profile.steam_account_name.trim().is_empty()
                || profile.steam_account_name.trim() == steam_id
            {
                profile.steam_account_name = steam.display_name.clone();
            }
            if profile.username.trim().is_empty() || profile.username.trim() == steam_id {
                profile.username = first_nonempty_string(&[
                    profile.steam_account_name.as_str(),
                    steam.display_name.as_str(),
                    steam.steam_id.as_str(),
                ]);
            }
            profile
        }
        Err(err) => {
            log::debug!(
                "identity: steam profile enrichment failed steam_id='{}': {}",
                steam_id,
                err
            );
            profile
        }
    }
}

fn normalize_vs_scenario_name(name: &str) -> String {
    let normalized = name.trim().to_ascii_lowercase();
    for suffix in [" - challenge start", " - challenge"] {
        if let Some(stripped) = normalized.strip_suffix(suffix) {
            return stripped.trim().to_string();
        }
    }
    normalized
}

fn current_bridge_friend_score(
    scenario_name: &str,
    username: &str,
    steam_id: Option<&str>,
    steam_account_name: Option<&str>,
) -> Option<f64> {
    let snapshot = bridge::current_kovaaks_friend_scores()?;
    if normalize_vs_scenario_name(&snapshot.scenario_name)
        != normalize_vs_scenario_name(scenario_name)
    {
        return None;
    }

    let wanted_steam_id = steam_id.unwrap_or("").trim();
    let wanted_username = username.trim();
    let wanted_display_name = steam_account_name.unwrap_or("").trim();

    snapshot.entries.into_iter().find_map(|entry| {
        let entry_steam_id = entry.steam_id.trim();
        let entry_display_name = entry.steam_account_name.trim();
        let matches = (!wanted_steam_id.is_empty() && entry_steam_id == wanted_steam_id)
            || (!wanted_display_name.is_empty()
                && entry_display_name.eq_ignore_ascii_case(wanted_display_name))
            || (!wanted_username.is_empty()
                && entry_display_name.eq_ignore_ascii_case(wanted_username))
            || (!wanted_username.is_empty() && entry_steam_id == wanted_username);
        matches.then_some(entry.score)
    })
}

fn first_nonempty_string(candidates: &[&str]) -> String {
    for candidate in candidates {
        let trimmed = candidate.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }
    String::new()
}

#[derive(Clone)]
struct CurrentIdentityCacheEntry {
    cache_key: String,
    profile: Option<FriendProfile>,
    cached_at: Instant,
}

fn current_identity_cache() -> &'static Mutex<Option<CurrentIdentityCacheEntry>> {
    static CACHE: OnceLock<Mutex<Option<CurrentIdentityCacheEntry>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(None))
}

fn cached_current_identity(cache_key: &str, max_age: Duration) -> Option<Option<FriendProfile>> {
    let Ok(cache) = current_identity_cache().lock() else {
        return None;
    };
    let entry = cache.as_ref()?;
    if entry.cache_key != cache_key || entry.cached_at.elapsed() > max_age {
        return None;
    }
    Some(entry.profile.clone())
}

fn store_current_identity_cache(cache_key: String, profile: Option<FriendProfile>) {
    if let Ok(mut cache) = current_identity_cache().lock() {
        *cache = Some(CurrentIdentityCacheEntry {
            cache_key,
            profile,
            cached_at: Instant::now(),
        });
    }
}

fn bridge_identity_cache_key(user: &bridge::BridgeCurrentUserProfile) -> String {
    let linked_count = user.linked_accounts.len();
    format!(
        "bridge:{}:{}:{}:{}:{}",
        user.username.trim(),
        user.display_name.trim(),
        user.kovaaks_user_id.trim(),
        user.steam_id.trim(),
        linked_count
    )
}

async fn resolve_current_identity_profile() -> Option<FriendProfile> {
    if let Some(user) = bridge::current_kovaaks_user().as_ref() {
        let cache_key = bridge_identity_cache_key(user);
        if let Some(profile) = cached_current_identity(&cache_key, Duration::from_secs(15)) {
            return profile;
        }
        if let Some(user) = friend_profile_from_bridge_user(user) {
            let user = enrich_friend_profile_from_steam(user).await;
            store_current_identity_cache(cache_key, Some(user.clone()));
            log::info!(
                "identity: resolved current user from bridge username='{}' steam_id='{}'",
                user.username,
                user.steam_id
            );
            return Some(user);
        }
    }

    let steam_id = match steam_integration::get_active_steam_id() {
        Some(value) => value,
        None => {
            log::info!("identity: active Steam user not available for fallback resolution");
            return None;
        }
    };
    let cache_key = format!("steam:{steam_id}");
    if let Some(profile) = cached_current_identity(&cache_key, Duration::from_secs(30)) {
        return profile;
    }

    log::debug!("identity: no live bridge user snapshot; falling back to Steam lookup");

    let steam = match steam_api::resolve_steam_user(&steam_id).await {
        Ok(profile) => profile,
        Err(err) => {
            log::warn!("identity: failed to resolve Steam profile for fallback: {err}");
            return None;
        }
    };

    let profile = FriendProfile {
        username: first_nonempty_string(&[steam.display_name.as_str(), steam.steam_id.as_str()]),
        steam_id: steam.steam_id.clone(),
        steam_account_name: steam.display_name.clone(),
        avatar_url: steam.avatar_url.clone(),
        country: String::new(),
        kovaaks_plus: false,
        bridge_managed: false,
    };

    store_current_identity_cache(cache_key, Some(profile.clone()));
    log::debug!(
        "identity: resolved fallback current user username='{}' steam_id='{}'",
        profile.username,
        profile.steam_id
    );
    Some(profile)
}

#[tauri::command]
fn get_live_friend_scores() -> Option<bridge::BridgeFriendScoresSnapshot> {
    bridge::current_kovaaks_friend_scores()
}

#[tauri::command]
async fn get_current_kovaaks_user() -> Option<FriendProfile> {
    resolve_current_identity_profile().await
}

/// Look up a user and persist them as a friend.
///
/// `search_type` controls the lookup strategy:
///   - `"kovaaks"`  → exact KovaaK's webapp username match
///   - `"steam"`    → resolve via Steam community XML (no API key needed),
///                    then cross-reference KovaaK's by steamId
///   - `None` / auto → Steam64 IDs / steamcommunity.com URLs go straight to steam path;
///                     everything else tries KovaaK's username first, then falls back to
///                     Steam (so vanity URLs like "aimicantaim" also work)
///
/// Friends resolved via Steam with no linked KovaaK's account are stored with
/// their Steam64 ID as `username`. Those entries can still be tracked in the
/// live bridge list, but score lookups require a real KovaaK's username.
#[tauri::command]
async fn add_friend(
    username: String,
    search_type: Option<String>,
    state: tauri::State<'_, AppState>,
    app: AppHandle,
) -> Result<FriendProfile, String> {
    let trimmed = username.trim().to_string();

    let looks_like_steam =
        steam_api::is_steam64_id(&trimmed) || trimmed.contains("steamcommunity.com");

    /// Resolve via Steam community XML then cross-reference KovaaK's.
    /// Returns a UserProfile with a real KovaaK's username when one is linked,
    /// or using the Steam64 ID as the username when no KovaaK's account is found.
    async fn resolve_via_steam(input: &str) -> Result<kovaaks_api::UserProfile, String> {
        let steam = steam_api::resolve_steam_user(input)
            .await
            .map_err(|e| e.to_string())?;

        let kovaaks = kovaaks_api::find_user_by_steam_id(&steam.steam_id, &steam.display_name)
            .await
            .unwrap_or(None);

        Ok(match kovaaks {
            Some(p) => p,
            None => kovaaks_api::UserProfile {
                username: steam.steam_id.clone(),
                steam_id: steam.steam_id,
                steam_account_name: steam.display_name,
                avatar_url: steam.avatar_url,
                country: String::new(),
                kovaaks_plus: false,
            },
        })
    }

    let profile = match search_type.as_deref() {
        Some("steam") => resolve_via_steam(&trimmed).await?,
        Some("kovaaks") => kovaaks_api::fetch_user_profile(&trimmed)
            .await
            .map_err(|e| e.to_string())?
            .ok_or_else(|| format!("No KovaaK's account found for '{}'", trimmed))?,
        _ => {
            // Auto: if input is an obvious Steam ID/URL go straight to Steam path.
            // Otherwise try KovaaK's username first, then fall back to Steam so that
            // vanity URLs (e.g. "aimicantaim") and other Steam inputs also work.
            if looks_like_steam {
                resolve_via_steam(&trimmed).await?
            } else {
                match kovaaks_api::fetch_user_profile(&trimmed).await {
                    Ok(Some(p)) => p,
                    _ => resolve_via_steam(&trimmed).await?,
                }
            }
        }
    };

    let friend = FriendProfile {
        username: profile.username.clone(),
        steam_id: profile.steam_id.clone(),
        steam_account_name: profile.steam_account_name.clone(),
        avatar_url: profile.avatar_url.clone(),
        country: profile.country.clone(),
        kovaaks_plus: profile.kovaaks_plus,
        bridge_managed: false,
    };
    let mut s = state.settings.lock().map_err(|e| e.to_string())?;
    // Deduplicate by steam_id (if available) or username.
    let already_exists = if !friend.steam_id.is_empty() {
        s.friends.iter().any(|f| f.steam_id == friend.steam_id)
    } else {
        s.friends
            .iter()
            .any(|f| f.username.eq_ignore_ascii_case(&friend.username))
    };
    if !already_exists {
        s.friends.push(friend.clone());
    }
    let cloned = s.clone();
    drop(s);
    settings::persist(&app, &cloned).map_err(|e| e.to_string())?;
    Ok(friend)
}

/// Remove a friend by username and persist.
#[tauri::command]
fn remove_friend(
    username: String,
    state: tauri::State<AppState>,
    app: AppHandle,
) -> Result<(), String> {
    let mut s = state.settings.lock().map_err(|e| e.to_string())?;
    s.friends
        .retain(|f| !f.username.eq_ignore_ascii_case(&username));
    let cloned = s.clone();
    drop(s);
    settings::persist(&app, &cloned).map_err(|e| e.to_string())
}

/// Fetch a friend's best score for a specific scenario from the KovaaK's API.
/// Prefers the in-game bridge snapshot and falls back to local PB for self-VS.
#[tauri::command]
async fn fetch_friend_score(
    app: AppHandle,
    username: String,
    scenario_name: String,
    steam_id: Option<String>,
    steam_account_name: Option<String>,
) -> Result<Option<f64>, String> {
    let normalized_username = username.trim().to_string();
    let normalized_steam_id = steam_id
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
        .or_else(|| {
            steam_api::is_steam64_id(&normalized_username).then(|| normalized_username.clone())
        });
    let normalized_display_name = steam_account_name
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string);

    if let Some(score) = current_bridge_friend_score(
        &scenario_name,
        &normalized_username,
        normalized_steam_id.as_deref(),
        normalized_display_name.as_deref(),
    ) {
        log::info!(
            "vsmode: fetch_friend_score via bridge username='{}' steam_id='{}' scenario='{}' -> {:?}",
            normalized_username,
            normalized_steam_id.as_deref().unwrap_or(""),
            scenario_name,
            Some(score)
        );
        return Ok(Some(score));
    }

    let active_bridge_user = bridge::current_kovaaks_user();
    let is_self = active_bridge_user.as_ref().is_some_and(|user| {
        let current_steam_id = user.steam_id.trim();
        !current_steam_id.is_empty()
            && normalized_steam_id
                .as_deref()
                .is_some_and(|wanted| wanted.trim() == current_steam_id)
    });

    if is_self {
        let local_pb = session_store::get_personal_best_for_scenario(
            &app,
            &normalize_vs_scenario_name(&scenario_name),
        )
        .map(|value| value as f64);
        log::info!(
            "vsmode: fetch_friend_score via local_pb username='{}' scenario='{}' -> {:?}",
            normalized_username,
            scenario_name,
            local_pb
        );
        return Ok(local_pb);
    }

    log::info!(
        "vsmode: no in-game friend score available username='{}' steam_id='{}' display_name='{}' scenario='{}'",
        normalized_username,
        normalized_steam_id.as_deref().unwrap_or(""),
        normalized_display_name.as_deref().unwrap_or(""),
        scenario_name
    );
    Ok(None)
}

/// Fetch a user's most-played scenarios (up to 10) sorted by play count.
/// Returns empty for friends with no linked KovaaK's account (steam64 username).
#[tauri::command]
async fn fetch_friend_most_played(
    username: String,
) -> Result<Vec<kovaaks_api::MostPlayedEntry>, String> {
    if steam_api::is_steam64_id(&username) {
        return Ok(vec![]);
    }
    kovaaks_api::fetch_most_played(&username, 10)
        .await
        .map_err(|e| e.to_string())
}

/// Returns the Steam 64-bit ID and display name of the currently logged-in Steam user.
/// Reads the Windows registry — instant, no network, no API key.
/// Returns `null` when Steam is not running or the platform is not Windows.
#[derive(serde::Serialize)]
pub struct ActiveSteamUser {
    pub steam_id: String,
    pub display_name: String,
    pub avatar_url: String,
}

#[tauri::command]
async fn get_active_steam_user() -> Option<ActiveSteamUser> {
    let steam_id = steam_integration::get_active_steam_id()?;
    // Resolve the full profile (display name + avatar) from the community XML.
    match steam_api::resolve_steam_user(&steam_id).await {
        Ok(p) => Some(ActiveSteamUser {
            steam_id: p.steam_id,
            display_name: p.display_name,
            avatar_url: p.avatar_url,
        }),
        Err(e) => {
            log::warn!("get_active_steam_user: profile fetch failed: {e}");
            // Return with just the ID and no display info rather than failing.
            Some(ActiveSteamUser {
                steam_id: steam_id.clone(),
                display_name: steam_id,
                avatar_url: String::new(),
            })
        }
    }
}

/// Import all Steam friends as FriendProfiles.
///
/// Priority order:
///   1. Read `localconfig.vdf` from the local Steam install — works even for private lists.
///   2. Fall back to the public Steam community XML if the VDF is unavailable.
///
/// Resolves all IDs concurrently (12 at a time) so 150 friends imports in ~10 s
/// instead of several minutes.  Emits `steam-import-progress` events as friends
/// resolve so the frontend can show a live counter.
///
/// Already-added friends (matched by steam_id) are skipped.
/// Returns the newly added FriendProfiles.
#[tauri::command]
async fn import_steam_friends(
    _state: tauri::State<'_, AppState>,
    _app: AppHandle,
) -> Result<Vec<FriendProfile>, String> {
    let live_friends = current_bridge_friend_profiles().await;
    if live_friends.is_empty() {
        return Err(
            "No live KovaaK friends are available from the mod bridge yet. Start KovaaK's with the bridge active first."
                .to_string(),
        );
    }

    log::info!(
        "import_steam_friends: compatibility path returning {} live bridge friends",
        live_friends.len()
    );
    Ok(live_friends)
}

/// Validate an OCR-read scenario name and return the canonical spelling, or null if
/// the name is unrecognisable garbage.
///
/// Resolution order:
///   1. Local index (instant, offline) — fuzzy-matched against all CSV filenames the
///      user has produced so far.  Covers all the scenarios they actually play.
///   2. KovaaK's public API — broader catalogue, handles scenarios never played before.
///
/// Returning the canonical name (not just true/false) means OCR confusables like
/// "Vl Bot" are corrected to "V1 Bot" before the name is stored in the frontend.
#[tauri::command]
async fn validate_scenario(scenario_name: String) -> Option<String> {
    // 1. Try the local index first — fast, no network.
    if scenario_index::len() > 0 {
        if let Some(canonical) = scenario_index::fuzzy_match(&scenario_name) {
            log::info!(
                "validate_scenario: local match {:?} → {:?}",
                scenario_name,
                canonical
            );
            return Some(canonical);
        }
    }
    // 2. Fall back to the KovaaK's API for scenarios not yet in the local index.
    let result = kovaaks_api::validate_scenario_name(&scenario_name).await;
    log::info!(
        "validate_scenario: API result for {:?} = {:?}",
        scenario_name,
        result
    );
    result
}

#[tauri::command]
async fn validate_username(username: String) -> Result<Option<FriendProfile>, String> {
    let profile = kovaaks_api::fetch_user_profile(&username)
        .await
        .map_err(|e| e.to_string())?;
    Ok(profile.map(|p| FriendProfile {
        username: p.username,
        steam_id: p.steam_id,
        steam_account_name: p.steam_account_name,
        avatar_url: p.avatar_url,
        country: p.country,
        kovaaks_plus: p.kovaaks_plus,
        bridge_managed: false,
    }))
}

#[tauri::command]
fn start_mouse_hook(app: AppHandle) -> Result<(), String> {
    mouse_hook::start(app).map_err(|e| e.to_string())
}

#[tauri::command]
fn stop_mouse_hook() {
    mouse_hook::stop();
}

#[tauri::command]
fn get_session_mouse_data() -> Vec<mouse_hook::MetricPoint> {
    mouse_hook::get_session_buffer()
}

#[tauri::command]
fn get_session_raw_positions() -> Vec<mouse_hook::RawPositionPoint> {
    mouse_hook::get_raw_positions()
}

#[tauri::command]
fn get_session_screen_frames() -> Vec<screen_recorder::ScreenFrame> {
    screen_recorder::get_frames()
}

#[tauri::command]
fn load_session_replay(app: AppHandle, session_id: String) -> Option<replay_store::ReplayData> {
    replay_store::load_replay(&app, &session_id)
}

#[tauri::command]
fn get_session_replay_payload(
    app: AppHandle,
    session_id: String,
) -> Option<replay_store::ReplayPayloadData> {
    replay_store::load_replay_payload(&app, &session_id)
}

#[tauri::command]
fn set_session_replay_favorite(
    app: AppHandle,
    session_id: String,
    is_favorite: bool,
) -> Result<(), String> {
    replay_store::set_replay_favorite(&app, &session_id, is_favorite)
}

#[tauri::command]
fn delete_session_replay(app: AppHandle, session_id: String) -> Result<(), String> {
    replay_store::delete_replay(&app, &session_id)
}

#[tauri::command]
fn export_session_replay_video(app: AppHandle, session_id: String) -> Result<String, String> {
    replay_store::export_replay_video(&app, &session_id)
        .map(|path| path.to_string_lossy().into_owned())
}

#[tauri::command]
fn get_ffmpeg_status(app: AppHandle) -> replay_store::FfmpegStatus {
    replay_store::get_ffmpeg_status(&app)
}

#[tauri::command]
fn install_ffmpeg_for_replays(app: AppHandle) -> Result<replay_store::FfmpegStatus, String> {
    replay_store::install_ffmpeg_for_app(&app)
}

#[tauri::command]
fn get_session_run_summary(
    app: AppHandle,
    session_id: String,
) -> Option<bridge::BridgeRunSnapshot> {
    match stats_db::get_run_summary(&app, &session_id) {
        Ok(Some(summary)) => Some(summary),
        Ok(None) => {
            let _ = replay_store::load_replay(&app, &session_id);
            match stats_db::get_run_summary(&app, &session_id) {
                Ok(summary) => summary,
                Err(error) => {
                    log::warn!("could not backfill run summary for {}: {error}", session_id);
                    None
                }
            }
        }
        Err(error) => {
            log::warn!("could not load run summary for {}: {error}", session_id);
            None
        }
    }
}

#[tauri::command]
fn get_session_run_timeline(
    app: AppHandle,
    session_id: String,
) -> Vec<bridge::BridgeRunTimelinePoint> {
    match stats_db::get_run_timeline(&app, &session_id) {
        Ok(timeline) if !timeline.is_empty() => timeline,
        Ok(_) => {
            let _ = replay_store::load_replay(&app, &session_id);
            match stats_db::get_run_timeline(&app, &session_id) {
                Ok(timeline) => timeline,
                Err(error) => {
                    log::warn!(
                        "could not backfill run timeline for {}: {error}",
                        session_id
                    );
                    vec![]
                }
            }
        }
        Err(error) => {
            log::warn!("could not load run timeline for {}: {error}", session_id);
            vec![]
        }
    }
}

#[tauri::command]
fn get_session_shot_telemetry(
    app: AppHandle,
    session_id: String,
) -> Vec<bridge::BridgeShotTelemetryEvent> {
    match stats_db::get_shot_telemetry(&app, &session_id) {
        Ok(events) if !events.is_empty() => events,
        Ok(_) => {
            let _ = replay_store::load_replay(&app, &session_id);
            match stats_db::get_shot_telemetry(&app, &session_id) {
                Ok(events) => events,
                Err(error) => {
                    log::warn!(
                        "could not backfill shot telemetry for {}: {error}",
                        session_id
                    );
                    vec![]
                }
            }
        }
        Err(error) => {
            log::warn!("could not load shot telemetry for {}: {error}", session_id);
            vec![]
        }
    }
}

#[tauri::command]
fn get_session_replay_context_windows(
    app: AppHandle,
    session_id: String,
) -> Vec<stats_db::SessionReplayContextWindow> {
    match stats_db::get_replay_context_windows(&app, &session_id) {
        Ok(windows) if !windows.is_empty() => windows,
        Ok(_) => {
            let _ = replay_store::load_replay(&app, &session_id);
            match stats_db::get_replay_context_windows(&app, &session_id) {
                Ok(windows) => windows,
                Err(error) => {
                    log::warn!(
                        "could not backfill replay context windows for {}: {error}",
                        session_id
                    );
                    vec![]
                }
            }
        }
        Err(error) => {
            log::warn!(
                "could not load replay context windows for {}: {error}",
                session_id
            );
            vec![]
        }
    }
}

#[tauri::command]
fn get_session_target_response_analysis(
    app: AppHandle,
    session_id: String,
) -> Option<target_response::TargetResponseAnalysis> {
    match stats_db::get_target_response_analysis(&app, &session_id) {
        Ok(Some(analysis)) => Some(analysis),
        Ok(None) => {
            let replay = replay_store::load_replay(&app, &session_id);
            match stats_db::get_target_response_analysis(&app, &session_id) {
                Ok(Some(analysis)) => Some(analysis),
                Ok(None) => {
                    let analysis = replay
                        .as_ref()
                        .and_then(target_response::analyze_target_responses);
                    if let Some(analysis) = analysis.as_ref() {
                        let _ =
                            stats_db::upsert_target_response_analysis(&app, &session_id, analysis);
                    }
                    analysis
                }
                Err(error) => {
                    log::warn!(
                        "could not backfill target response analysis for {}: {error}",
                        session_id
                    );
                    replay
                        .as_ref()
                        .and_then(target_response::analyze_target_responses)
                }
            }
        }
        Err(error) => {
            log::warn!(
                "could not load target response analysis for {}: {error}",
                session_id
            );
            replay_store::load_replay(&app, &session_id)
                .as_ref()
                .and_then(target_response::analyze_target_responses)
                .inspect(|analysis| {
                    let _ = stats_db::upsert_target_response_analysis(&app, &session_id, analysis);
                })
        }
    }
}

#[tauri::command]
fn get_session_sql_audit(app: AppHandle, session_id: String) -> Option<stats_db::SessionSqlAudit> {
    match stats_db::audit_session_sql(&app, &session_id) {
        Ok(audit) => {
            let _ = stats_db::persist_session_sql_audit(&app, &audit);
            Some(audit)
        }
        Err(error) => {
            log::warn!("could not load sql audit for {}: {error}", session_id);
            None
        }
    }
}

#[tauri::command]
fn get_repo_sql_audit_summary(
    app: AppHandle,
    failing_session_limit: Option<usize>,
) -> Option<stats_db::RepoSqlAuditSummary> {
    match stats_db::refresh_repo_sql_audit(&app, failing_session_limit) {
        Ok(summary) => Some(summary),
        Err(error) => {
            log::warn!("could not refresh repo sql audit summary: {error}");
            None
        }
    }
}

#[tauri::command]
fn replay_play_in_game(
    app: AppHandle,
    session_id: String,
    speed: Option<f64>,
) -> Result<(), String> {
    let replay = replay_store::load_replay(&app, &session_id)
        .ok_or_else(|| format!("replay not found: {session_id}"))?;
    let run_snapshot = replay
        .run_snapshot
        .ok_or_else(|| "replay has no run snapshot".to_string())?;
    let tick_stream = run_snapshot
        .tick_stream_v1
        .ok_or_else(|| "replay has no tick_stream_v1 payload".to_string())?;
    bridge::start_in_game_replay_stream(&session_id, tick_stream, speed.unwrap_or(1.0))
}

#[tauri::command]
fn replay_stop_in_game() -> Result<(), String> {
    bridge::stop_in_game_replay_stream()
}

#[tauri::command]
fn get_monitors(app: AppHandle) -> Vec<MonitorInfo> {
    let Some(win) = app.get_webview_window("overlay") else {
        return vec![];
    };
    win.available_monitors()
        .unwrap_or_default()
        .into_iter()
        .enumerate()
        .map(|(i, m)| {
            let pos = m.position();
            let size = m.size();
            MonitorInfo {
                index: i,
                name: m
                    .name()
                    .map(|s| s.to_string())
                    .unwrap_or_else(|| format!("Monitor {}", i + 1)),
                width: size.width,
                height: size.height,
                x: pos.x,
                y: pos.y,
            }
        })
        .collect()
}

#[tauri::command]
fn set_overlay_monitor(
    index: usize,
    state: tauri::State<AppState>,
    app: AppHandle,
) -> Result<(), String> {
    {
        let mut s = state.settings.lock().map_err(|e| e.to_string())?;
        s.monitor_index = index;
        let cloned = s.clone();
        drop(s);
        settings::persist(&app, &cloned).map_err(|e| e.to_string())?;
    }
    apply_monitor(&app, index);
    Ok(())
}

/// Emit toggle-settings event to the overlay window (called from tray or frontend).
#[tauri::command]
fn toggle_settings(app: AppHandle) -> Result<(), String> {
    app.emit("toggle-settings", ()).map_err(|e| e.to_string())
}

#[tauri::command]
fn toggle_overlay(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window("overlay") {
        if win.is_visible().unwrap_or(false) {
            win.hide().map_err(|e| e.to_string())?;
        } else {
            win.show().map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

#[tauri::command]
fn quit_app(app: AppHandle) {
    osu_replay_host::stop_running_host();
    app.exit(0);
}

#[tauri::command]
fn open_logs_window(app: AppHandle) -> Result<(), String> {
    // The logs window is pre-created at startup (hidden) so we just show it.
    // Avoids any dynamic window creation which can deadlock from a command handler.
    if let Some(win) = app.get_webview_window("logs") {
        win.show().map_err(|e| e.to_string())?;
        win.set_focus().map_err(|e| e.to_string())?;
    } else {
        log::error!("logs window not found — check tauri.conf.json");
    }
    Ok(())
}

#[tauri::command]
fn open_stats_window(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window("stats") {
        let _ = win.unminimize();
        win.show().map_err(|e| e.to_string())?;
        let _ = win.maximize();
        win.set_focus().map_err(|e| e.to_string())?;
    } else {
        log::error!("stats window not found — check tauri.conf.json");
    }
    Ok(())
}

#[tauri::command]
fn get_osu_lazer_status() -> osu::OsuLazerStatus {
    osu::get_lazer_status()
}

#[tauri::command]
fn record_osu_diagnostic(diagnostic: osu_diagnostics::OsuClientDiagnostic) {
    osu_diagnostics::record(diagnostic);
}

#[tauri::command]
async fn list_osu_local_beatmaps() -> osu_library::OsuLocalBeatmapLibrary {
    osu_library::list_local_beatmaps().await
}

#[tauri::command]
async fn list_osu_local_replays() -> osu_library::OsuLocalReplayLibrary {
    osu_library::list_local_replays().await
}

#[tauri::command]
async fn list_osu_local_scores() -> osu_realm_reader::OsuLocalScoreLibrary {
    osu_realm_reader::list_local_scores().await
}

#[tauri::command]
async fn get_osu_local_player() -> Result<Option<osu_realm_reader::OsuLocalPlayer>, String> {
    osu_realm_reader::get_local_player().await
}

#[tauri::command]
async fn get_osu_replay_theme() -> Result<osu_realm_reader::OsuReplayTheme, String> {
    osu_realm_reader::get_replay_theme().await
}

#[tauri::command]
async fn watch_osu_replay_in_aimmod_lazer(
    replay_path: String,
) -> Result<osu_replay_host::NativeReplayLaunchResult, String> {
    osu_replay_host::watch_replay(replay_path).await
}

#[tauri::command]
async fn list_installed_osu_lazer_skins()
-> Result<Vec<osu_realm_reader::OsuInstalledLazerSkin>, String> {
    osu_realm_reader::list_installed_lazer_skins().await
}

#[tauri::command]
async fn get_osu_beatmap_providers(app: AppHandle) -> Result<Vec<osu::OsuBeatmapProvider>, String> {
    osu::get_beatmap_providers(&app).await
}

#[tauri::command]
async fn search_osu_beatmaps(
    app: AppHandle,
    request: osu::OsuBeatmapSearchRequest,
) -> osu::OsuBeatmapSearchResponse {
    osu::search_beatmaps(&app, request).await
}

#[tauri::command]
async fn get_osu_beatmap_item(
    app: AppHandle,
    request: osu::OsuBeatmapItemRequest,
) -> osu::OsuBeatmapItemResponse {
    osu::get_beatmap_item(&app, request).await
}

#[tauri::command]
async fn download_osu_beatmap(
    app: AppHandle,
    request: osu::OsuBeatmapDownloadRequest,
) -> osu::OsuBeatmapDownloadResult {
    osu::download_beatmap(&app, request).await
}

#[tauri::command]
fn import_osu_lazer_files(app: AppHandle, paths: Vec<String>) -> Vec<osu::OsuImportResult> {
    osu::import_files(&app, paths)
}

#[tauri::command]
fn inspect_osu_replay_files(paths: Vec<String>) -> Vec<osu::OsuReplayInspection> {
    osu::inspect_replay_files(paths)
}

#[tauri::command]
fn analyze_osu_replay_files(
    paths: Vec<String>,
) -> osu_replay_analytics::OsuReplayAnalyticsResponse {
    osu_replay_analytics::analyze_replay_files(paths)
}

#[tauri::command]
fn analyze_osu_replay(path: String) -> Result<osu_coaching::OsuReplayCoachingAnalysis, String> {
    osu_coaching::analyze_replay_file(&path)
}

#[tauri::command]
fn open_osu_local_replay(app: AppHandle, path: String) -> osu::OsuImportResult {
    osu::open_local_replay(&app, path)
}

#[tauri::command]
async fn get_osu_official_user_profile(
    app: AppHandle,
    request: osu_profiles::OsuOfficialUserProfileRequest,
) -> Result<osu_profiles::OsuOfficialUserProfileResponse, String> {
    osu_profiles::get_official_user_profile(&app, request).await
}

#[tauri::command]
fn get_osu_lazer_session_state() -> osu_profiles::OsuLazerSessionState {
    osu_profiles::get_lazer_session_state()
}

#[tauri::command]
fn get_osu_user_identifier(state: tauri::State<AppState>) -> Result<String, String> {
    let settings = state.settings.lock().map_err(|error| error.to_string())?;
    Ok(settings.osu_user_identifier.clone())
}

#[tauri::command]
fn set_osu_user_identifier(
    identifier: String,
    state: tauri::State<AppState>,
    app: AppHandle,
) -> Result<String, String> {
    let identifier = identifier.trim().to_string();
    if identifier.len() > 64 || identifier.chars().any(char::is_control) {
        return Err("Enter an osu! username or numeric user ID up to 64 characters.".to_string());
    }
    if !identifier.is_empty()
        && identifier.bytes().all(|byte| byte.is_ascii_digit())
        && identifier.bytes().all(|byte| byte == b'0')
    {
        return Err("An osu! user ID must be a positive decimal number.".to_string());
    }
    let persisted = {
        let mut settings = state.settings.lock().map_err(|error| error.to_string())?;
        settings.osu_user_identifier = identifier.clone();
        settings.clone()
    };
    settings::persist(&app, &persisted).map_err(|error| error.to_string())?;
    Ok(identifier)
}

#[tauri::command]
async fn get_osu_skin_providers(app: AppHandle) -> Result<Vec<osu_skins::OsuSkinProvider>, String> {
    osu_skins::get_skin_providers(&app).await
}

#[tauri::command]
async fn search_osu_skins(
    app: AppHandle,
    request: osu_skins::OsuSkinSearchRequest,
) -> osu_skins::OsuSkinSearchResponse {
    osu_skins::search_skins(&app, request).await
}

#[tauri::command]
async fn get_osu_skin(
    app: AppHandle,
    request: osu_skins::OsuSkinDetailRequest,
) -> osu_skins::OsuSkinDetailResponse {
    osu_skins::get_skin(&app, request).await
}

#[tauri::command]
async fn install_osu_skin(
    app: AppHandle,
    request: osu_skins::OsuSkinInstallRequest,
) -> osu_skins::OsuSkinInstallResult {
    osu_skins::install_skin(&app, request).await
}

#[tauri::command]
fn list_installed_osu_skins(app: AppHandle) -> Result<Vec<osu_skins::ManagedOsuSkin>, String> {
    osu_skins::list_installed_skins(&app)
}

#[tauri::command]
fn remove_installed_osu_skin(app: AppHandle, id: String) -> osu_skins::OsuSkinRemoveResult {
    osu_skins::remove_installed_skin(&app, id)
}

#[tauri::command]
fn import_osu_skin_files(
    app: AppHandle,
    paths: Vec<String>,
) -> Vec<osu_skins::OsuSkinInstallResult> {
    osu_skins::import_skin_files(&app, paths)
}

#[tauri::command]
fn get_session_history_page(
    app: AppHandle,
    offset: Option<usize>,
    limit: Option<usize>,
) -> session_store::SessionHistoryPage {
    let offset = offset.unwrap_or(0);
    let limit = limit.unwrap_or(500).clamp(1, 2_000);
    session_store::get_session_page(&app, offset, limit)
}

#[tauri::command]
fn get_global_coaching_overview(
    app: AppHandle,
    date_range: Option<String>,
) -> Result<coaching::GlobalCoachingOverview, String> {
    coaching::get_global_overview(&app, date_range.as_deref()).map_err(|e| e.to_string())
}

#[tauri::command]
fn get_scenario_coaching_overview(
    app: AppHandle,
    scenario_name: String,
    date_range: Option<String>,
) -> Result<coaching::ScenarioCoachingOverview, String> {
    coaching::get_scenario_overview(&app, &scenario_name, date_range.as_deref())
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn get_session_run_coaching_analysis(
    app: AppHandle,
    session_id: String,
) -> Result<coaching::SessionRunCoachingAnalysis, String> {
    coaching::get_session_run_coaching_analysis(&app, &session_id).map_err(|e| e.to_string())
}

#[tauri::command]
fn save_coaching_state_snapshot(
    app: AppHandle,
    snapshot: stats_db::CoachingStateSnapshot,
) -> Result<(), String> {
    stats_db::upsert_coaching_state_snapshot(&app, &snapshot).map_err(|e| e.to_string())
}

#[tauri::command]
fn get_coaching_state_snapshot(
    app: AppHandle,
    snapshot_kind: String,
) -> Result<Option<stats_db::CoachingStateSnapshot>, String> {
    stats_db::get_coaching_state_snapshot(&app, &snapshot_kind).map_err(|e| e.to_string())
}

#[tauri::command]
fn save_coaching_recommendation_evaluations(
    app: AppHandle,
    evaluations: Vec<stats_db::CoachingRecommendationEvaluation>,
) -> Result<usize, String> {
    stats_db::upsert_coaching_recommendation_evaluations(&app, &evaluations)
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn get_coaching_recommendation_evaluations(
    app: AppHandle,
    snapshot_kind: Option<String>,
    status: Option<String>,
) -> Result<Vec<stats_db::CoachingRecommendationEvaluation>, String> {
    stats_db::get_coaching_recommendation_evaluations(
        &app,
        snapshot_kind.as_deref(),
        status.as_deref(),
    )
    .map_err(|e| e.to_string())
}

#[tauri::command]
fn save_coaching_user_feedback(
    app: AppHandle,
    feedback: stats_db::CoachingUserFeedbackRecord,
) -> Result<(), String> {
    stats_db::upsert_coaching_user_feedback(&app, &feedback).map_err(|e| e.to_string())
}

#[tauri::command]
fn get_coaching_user_feedback(
    app: AppHandle,
    snapshot_kind: Option<String>,
) -> Result<Vec<stats_db::CoachingUserFeedbackRecord>, String> {
    stats_db::get_coaching_user_feedback(&app, snapshot_kind.as_deref()).map_err(|e| e.to_string())
}

#[tauri::command]
fn get_recent_session_scenarios(
    app: AppHandle,
    limit: Option<usize>,
) -> Vec<session_store::RecentScenarioRecord> {
    session_store::get_recent_scenarios(&app, limit.unwrap_or(15).clamp(1, 100))
}

#[tauri::command]
fn get_personal_best_for_scenario(app: AppHandle, scenario_name: String) -> Option<u32> {
    let score = session_store::get_personal_best_for_scenario(&app, &scenario_name);
    log::info!(
        "vsmode: get_personal_best_for_scenario scenario='{}' -> {:?}",
        scenario_name,
        score
    );
    score
}

#[tauri::command]
fn clear_session_history(app: AppHandle) {
    session_store::clear_sessions(&app);
}

#[tauri::command]
fn import_session_csv_history(
    app: AppHandle,
    state: tauri::State<AppState>,
) -> Result<file_watcher::CsvImportSummary, String> {
    let stats_dir = {
        let settings = state.settings.lock().map_err(|e| e.to_string())?;
        settings.stats_dir.clone()
    };
    file_watcher::import_csv_history(&app, &stats_dir)
}

#[tauri::command]
fn get_log_buffer() -> Vec<logger::LogEntry> {
    logger::get_buffer()
}

#[tauri::command]
fn clear_log_buffer() {
    logger::clear_buffer();
}

#[tauri::command]
async fn search_scenarios(
    query: String,
    page: u64,
    max: u64,
) -> Result<kovaaks_api::ScenarioPage, String> {
    kovaaks_api::search_scenarios(&query, page, max)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn get_leaderboard_page(
    leaderboard_id: u64,
    page: u64,
    max: u64,
) -> Result<kovaaks_api::LeaderboardPage, String> {
    kovaaks_api::get_leaderboard_page(leaderboard_id, page, max)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn get_scenario_details(leaderboard_id: u64) -> Result<kovaaks_api::ScenarioDetails, String> {
    kovaaks_api::get_scenario_details(leaderboard_id)
        .await
        .map_err(|e| e.to_string())
}

#[derive(serde::Serialize)]
struct CursorPos {
    x: i32,
    y: i32,
}

/// Return the current cursor position in physical screen coordinates.
/// Used by the frontend to implement cursor-proximity passthrough toggling.
#[tauri::command]
fn get_cursor_pos() -> CursorPos {
    #[cfg(target_os = "windows")]
    {
        use windows::Win32::Foundation::POINT;
        use windows::Win32::UI::WindowsAndMessaging::GetCursorPos;
        let mut pt = POINT { x: 0, y: 0 };
        unsafe {
            let _ = GetCursorPos(&mut pt);
        }
        return CursorPos { x: pt.x, y: pt.y };
    }
    #[allow(unreachable_code)]
    CursorPos { x: 0, y: 0 }
}

#[tauri::command]
fn set_mouse_passthrough(app: AppHandle, enabled: bool) -> Result<(), String> {
    #[cfg(target_os = "linux")]
    {
        let _ = app;
        window_tracker::set_force_show(!enabled);
        return Ok(());
    }

    #[cfg(not(target_os = "linux"))]
    if let Some(win) = app.get_webview_window("overlay") {
        win.set_ignore_cursor_events(enabled)
            .map_err(|e| e.to_string())?;
    }
    // When passthrough is OFF (settings open), keep overlay visible
    // even if KovaaK's isn't the foreground window.
    window_tracker::set_force_show(!enabled);
    Ok(())
}

#[tauri::command]
fn get_app_version_label() -> String {
    app_version::display_version_label()
}

// ─── Single-instance helper ────────────────────────────────────────────────────

/// Kills any previously running instance of the overlay.
///
/// On Windows we query `tasklist` by executable name so we never accidentally
/// kill an unrelated process that happens to have reused an old PID (a known
/// hazard of the naive PID-file approach).
///
/// On other platforms we fall back to a PID file.
fn focus_primary_window(app: &AppHandle) {
    for label in ["stats", "logs", "overlay"] {
        let Some(window) = app.get_webview_window(label) else {
            continue;
        };
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
        break;
    }
}

fn apply_window_titles(app: &AppHandle) {
    let app_title = app_version::app_name_with_version();

    if let Some(window) = app.get_webview_window("overlay") {
        let _ = window.set_title(&app_title);
    }
    if let Some(window) = app.get_webview_window("logs") {
        let _ = window.set_title(&format!("{app_title} — Logs"));
    }
    if let Some(window) = app.get_webview_window("stats") {
        let _ = window.set_title(&format!("{app_title} — Session Stats"));
    }
}

// ─── App Entry Point ───────────────────────────────────────────────────────────

pub fn run() {
    // Our logger emits to stderr AND to the Tauri logs window
    logger::init().unwrap_or_else(|_| eprintln!("logger already set"));
    settings::init_runtime_launch_overrides();
    if let Some(override_url) = settings::hub_api_base_url_launch_override() {
        log::info!("Using launch-time AimMod Hub override: {override_url}");
    }

    let initial_settings = settings::load_default();
    let app_state = AppState {
        settings: Arc::new(Mutex::new(initial_settings)),
        local_llm: local_llm::default_runtime_state(),
    };
    let context: tauri::Context<tauri::Wry> = tauri::generate_context!();
    if !tauri::is_dev() {
        for required in ["index.html", "logs.html", "stats.html"] {
            let key = tauri::utils::assets::AssetKey::from(required);
            let asset = context.assets().get(&key).unwrap_or_else(|| {
                panic!("production frontend asset {required} is missing or invalid")
            });
            assert!(
                !asset.is_empty(),
                "production frontend asset {required} is empty"
            );
        }
        log::info!("Embedded production frontend validated");
    }

    tauri::Builder::default()
        .register_uri_scheme_protocol("aimmod-media", |_context, request| {
            osu::media_protocol_response(request)
        })
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            log::info!("single-instance: forwarding launch to existing AimMod instance");
            focus_primary_window(app);
        }))
        .plugin(tauri_plugin_store::Builder::default().build())
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_process::init())
        .manage(app_state)
        .invoke_handler(tauri::generate_handler![
            quit_app,
            open_logs_window,
            open_stats_window,
            get_osu_lazer_status,
            record_osu_diagnostic,
            list_osu_local_beatmaps,
            list_osu_local_replays,
            list_osu_local_scores,
            get_osu_local_player,
            get_osu_replay_theme,
            watch_osu_replay_in_aimmod_lazer,
            list_installed_osu_lazer_skins,
            get_osu_beatmap_providers,
            search_osu_beatmaps,
            get_osu_beatmap_item,
            download_osu_beatmap,
            import_osu_lazer_files,
            inspect_osu_replay_files,
            analyze_osu_replay_files,
            analyze_osu_replay,
            open_osu_local_replay,
            get_osu_official_user_profile,
            get_osu_lazer_session_state,
            get_osu_user_identifier,
            set_osu_user_identifier,
            get_osu_skin_providers,
            search_osu_skins,
            get_osu_skin,
            install_osu_skin,
            list_installed_osu_skins,
            remove_installed_osu_skin,
            import_osu_skin_files,
            get_session_history_page,
            get_global_coaching_overview,
            get_scenario_coaching_overview,
            get_session_run_coaching_analysis,
            save_coaching_state_snapshot,
            get_coaching_state_snapshot,
            save_coaching_recommendation_evaluations,
            get_coaching_recommendation_evaluations,
            save_coaching_user_feedback,
            get_coaching_user_feedback,
            get_recent_session_scenarios,
            get_personal_best_for_scenario,
            clear_session_history,
            import_session_csv_history,
            get_overlay_origin,
            get_log_buffer,
            clear_log_buffer,
            get_settings,
            save_settings,
            reset_settings,
            get_hub_sync_status,
            hub_start_device_link,
            hub_poll_device_link,
            hub_disconnect,
            hub_force_full_resync,
            hub_get_overview,
            hub_search,
            hub_list_replays,
            hub_get_profile,
            hub_get_scenario,
            hub_get_benchmark_page,
            hub_list_benchmarks,
            hub_get_run,
            hub_get_player_scenario_history,
            hub_get_aim_profile,
            hub_get_aim_fingerprint,
            hub_query_coaching_knowledge,
            get_local_llm_runtime_status,
            install_local_llm_assets,
            uninstall_local_llm_assets,
            stop_local_llm_runtime,
            local_llm_generate_coaching_reply,
            local_llm_stream_coaching_reply,
            read_kovaaks_palette,
            write_kovaaks_palette_colors,
            get_friends,
            get_live_friend_scores,
            get_current_kovaaks_user,
            add_friend,
            remove_friend,
            set_selected_friend,
            fetch_friend_score,
            fetch_friend_most_played,
            get_active_steam_user,
            import_steam_friends,
            validate_username,
            validate_scenario,
            start_mouse_hook,
            stop_mouse_hook,
            get_session_mouse_data,
            get_session_raw_positions,
            get_session_screen_frames,
            load_session_replay,
            get_session_replay_payload,
            set_session_replay_favorite,
            delete_session_replay,
            export_session_replay_video,
            get_ffmpeg_status,
            install_ffmpeg_for_replays,
            get_session_run_summary,
            get_session_run_timeline,
            get_session_shot_telemetry,
            get_session_replay_context_windows,
            get_session_target_response_analysis,
            get_session_sql_audit,
            get_repo_sql_audit_summary,
            replay_play_in_game,
            replay_stop_in_game,
            toggle_settings,
            toggle_overlay,
            set_mouse_passthrough,
            get_monitors,
            set_overlay_monitor,
            get_cursor_pos,
            get_app_version_label,
            open_speech_settings,
            open_natural_voices_store,
            search_scenarios,
            get_leaderboard_page,
            get_scenario_details,
            inject_bridge,
            ue4ss_get_recent_logs,
            ue4ss_trigger_hot_reload,
            ue4ss_get_runtime_flags,
            ue4ss_set_runtime_flag,
            ue4ss_reload_runtime_flags,
            get_overlay_runtime_notice,
            get_is_debug_build,
            get_obs_overlay_url,
            toggle_layout_huds,
            // list_sapi_voices and speak_with_sapi are preserved in lib.rs + sapi.rs
            // for future use but not registered until a working voice backend is confirmed.
        ])
        .setup(|app| {
            // Local beatmap backgrounds and audio are immutable content-addressed
            // blobs. Only detected osu!lazer file-store roots are exposed to the
            // webview asset protocol, including configured and sandboxed installs.
            osu::allow_lazer_file_store_assets(app.handle());

            // Load persisted settings
            let loaded = settings::load(app.handle()).unwrap_or_default();

            {
                let state = app.state::<AppState>();
                let mut s = state.settings.lock().unwrap();
                *s = loaded.clone();
            }

            // Load the persistent scenario validation cache from the app data dir.
            if let Ok(data_dir) = app.path().app_data_dir() {
                kovaaks_api::load_cache(&data_dir.join("validation_cache.json"));
            }

            // Register the app handle so the logger can emit live events
            logger::register_app(app.handle().clone());
            log::info!(
                "AimMod starting up — log file: {}",
                logger::log_file_path().display()
            );
            apply_window_titles(app.handle());
            discord_rpc::start();
            discord_rpc::update_presence_from_bridge(discord_rpc::BridgePresenceState {
                game_state_code: 0,
                game_state: "AimMod Running".to_string(),
                scenario_name: None,
                scenario_type: None,
                scenario_subtype: None,
                score_per_minute: None,
                accuracy_pct: None,
                kills: None,
                elapsed_secs: None,
                time_remaining_secs: None,
                queue_time_remaining_secs: None,
            });

            session_store::initialize(app.handle());
            {
                let app_handle = app.handle().clone();
                let _ = std::thread::Builder::new()
                    .name("session-classification-backfill".into())
                    .spawn(move || {
                        match stats_db::backfill_session_classifications(&app_handle) {
                            Ok(0) => {}
                            Ok(updated) => {
                                log::info!(
                                    "stats_db: backfilled stored scenario classification for {} session(s)",
                                    updated
                                );
                            }
                            Err(error) => {
                                log::warn!(
                                    "stats_db: stored scenario classification backfill failed: {error}"
                                );
                            }
                        }
                        hub_sync::queue_pending_session_sync(&app_handle);
                        hub_sync::queue_pending_mouse_path_sync(&app_handle);
                    });
            }
            {
                let app_handle = app.handle().clone();
                let _ = std::thread::Builder::new()
                    .name("hub-sync-pending".into())
                    .spawn(move || loop {
                        hub_sync::queue_pending_session_sync(&app_handle);
                        hub_sync::queue_pending_mouse_path_sync(&app_handle);
                        std::thread::sleep(Duration::from_secs(300));
                    });
            }

            // Start file watcher
            file_watcher::start(app.handle().clone(), &loaded.stats_dir);

            // Start mouse hook (captures events; metrics only emitted during sessions)
            let _ = mouse_hook::start(app.handle().clone());
            mouse_hook::set_dpi(loaded.mouse_dpi);
            mouse_hook::set_feedback_enabled(loaded.live_feedback_enabled);
            mouse_hook::set_feedback_verbosity(loaded.live_feedback_verbosity);
            screen_recorder::set_replay_capture_fps(loaded.replay_capture_fps);
            screen_recorder::set_replay_capture_width(loaded.replay_capture_width);
            screen_recorder::set_replay_capture_framing(&loaded.replay_capture_framing);
            screen_recorder::set_replay_capture_quality(&loaded.replay_capture_quality);
            replay_store::maybe_install_ffmpeg_for_replay_media(app.handle().clone(), loaded.clone());
            replay_store::apply_replay_retention(&app.handle(), Some(loaded.replay_keep_count as usize), None);

            // Start pipe server before injection so early UE4SS events are not lost.
            bridge::start(app.handle().clone());
            {
                let state = app.state::<AppState>();
                overlay_service::start(app.handle().clone(), state.settings.clone());
                hub_live_activity::start(app.handle().clone(), state.settings.clone());
            }
            if let Err(e) = bridge::start_log_tailer(app.handle().clone(), &loaded.stats_dir) {
                log::warn!("Failed to start UE4SS log tailer: {e}");
            }
            // Deploy UE4SS runtime/mod payload and manually inject UE4SS.dll.
            if let Err(e) = deploy_and_inject_ue4ss(app.handle(), &loaded.stats_dir) {
                if bridge::is_injection_deferred_error(&e) {
                    log::info!("Deferred UE4SS injection: {e}");
                } else {
                    log::error!("Failed to deploy/inject UE4SS runtime: {e}");
                }
            }
            start_ue4ss_reinject_monitor(app.handle().clone(), loaded.stats_dir.clone());

            // Build system tray (Windows only)
            #[cfg(not(target_os = "linux"))]
            setup_tray(app)?;

            // Configure the game overlay on supported platforms. On Linux the
            // hidden GTK window is not realized yet, and asking Tao to alter
            // its input shape would panic before the stats window can open.
            #[cfg(not(target_os = "linux"))]
            if let Some(win) = app.get_webview_window("overlay") {
                let _ = win.set_ignore_cursor_events(true);
                // Force the window background to fully transparent so the DWM
                // compositor does not tint or dim the game colours underneath.
                let _ = win.set_background_color(Some(tauri::window::Color(0, 0, 0, 0)));
                configure_overlay_window(&win);
                let _ = win.show();
            }
            // Ensure monitor placement + screen recorder capture rect are initialized
            // on startup from persisted settings (without requiring manual monitor re-select).
            apply_monitor(app.handle(), loaded.monitor_index);
            window_tracker::set_force_show(false);
            window_tracker::start(app.handle().clone());
            let initial_game_focus = window_tracker::is_game_focused();
            log::info!("Window tracker started (initial game focus={initial_game_focus})");

            // Keep the stats window alive when the user clicks X so that its
            // session-complete listener remains registered.  Closing hides
            // instead of destroying; the window is shown again on the next
            // session-complete event or via the tray menu.
            if let Some(stats_win) = app.get_webview_window("stats") {
                let w = stats_win.clone();
                stats_win.on_window_event(move |event| {
                    if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                        api.prevent_close();
                        w.hide().ok();
                    }
                });
            }

            Ok(())
        })
        .build(context)
        .expect("error while building tauri application")
        .run(|app_handle, event| {
            if let tauri::RunEvent::Exit = event {
                osu_replay_host::stop_running_host();
                // Kill the llama-server child process so it doesn't become an
                // orphan that blocks the port on the next launch.
                local_llm::kill_child_if_running(app_handle);
            }
        });
}

#[cfg(test)]
mod embedded_frontend_tests {
    use std::path::PathBuf;
    use tauri::utils::assets::AssetKey;

    #[test]
    fn production_context_contains_frontend_entrypoints() {
        // Tauri intentionally uses an empty embedded asset store in debug
        // builds because the webview is pointed at `devUrl`. Validate the
        // release inputs from disk in that case; release builds additionally
        // exercise the generated embedded context below.
        if cfg!(debug_assertions) {
            let dist = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../dist");
            for required in ["index.html", "logs.html", "stats.html"] {
                let bytes = std::fs::read(dist.join(required))
                    .unwrap_or_else(|error| panic!("missing {required}: {error}"));
                assert!(!bytes.is_empty(), "{required} decoded to an empty asset");
            }
            return;
        }

        let context: tauri::Context<tauri::Wry> = tauri::generate_context!();
        let assets = context.assets();
        let keys = assets
            .iter()
            .map(|(key, _)| key.into_owned())
            .collect::<Vec<_>>();

        for required in ["index.html", "logs.html", "stats.html"] {
            let required_key = AssetKey::from(required);
            assert!(
                keys.iter().any(|key| key == required_key.as_ref()),
                "missing {required}; embedded keys: {keys:?}"
            );
            let bytes = assets
                .get(&required_key)
                .unwrap_or_else(|| panic!("{required} is present but cannot be decoded"));
            assert!(!bytes.is_empty(), "{required} decoded to an empty asset");
        }
    }
}

fn setup_tray<R: Runtime>(app: &tauri::App<R>) -> Result<(), Box<dyn std::error::Error>> {
    let open_settings =
        MenuItem::with_id(app, "open_settings", "Settings  (F8)", true, None::<&str>)?;
    let toggle_debug_state_hud = MenuItem::with_id(
        app,
        "toggle_debug_state_hud",
        "Toggle Debug State HUD  (F9)",
        true,
        None::<&str>,
    )?;
    let open_stats = MenuItem::with_id(app, "open_stats", "Session Stats", true, None::<&str>)?;
    let toggle_overlay =
        MenuItem::with_id(app, "toggle_overlay", "Toggle AimMod", true, None::<&str>)?;
    let separator = tauri::menu::PredefinedMenuItem::separator(app)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;

    let menu = Menu::with_items(
        app,
        &[
            &open_settings,
            &toggle_debug_state_hud,
            &open_stats,
            &toggle_overlay,
            &separator,
            &quit,
        ],
    )?;

    let mut tray_builder = TrayIconBuilder::with_id("main")
        .menu(&menu)
        .tooltip(app_version::app_name_with_version());
    if let Some(icon) = app.default_window_icon() {
        tray_builder = tray_builder.icon(icon.clone());
    }
    let _tray = tray_builder
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open_settings" => {
                let _ = app.emit("toggle-settings", ());
            }
            "toggle_debug_state_hud" => {
                let _ = app.emit("toggle-debug-state-overlay", ());
            }
            "open_stats" => {
                if let Some(win) = app.get_webview_window("stats") {
                    let _ = win.show();
                    let _ = win.set_focus();
                }
            }
            "toggle_overlay" => {
                if let Some(win) = app.get_webview_window("overlay") {
                    if win.is_visible().unwrap_or(false) {
                        let _ = win.hide();
                    } else {
                        let _ = win.show();
                    }
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|_tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {}
        })
        .build(app)?;

    Ok(())
}
