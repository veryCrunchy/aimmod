use serde::{Deserialize, Serialize};
use std::sync::OnceLock;
use tauri::AppHandle;

const STORE_PATH: &str = "settings.json";
const STORE_KEY: &str = "app_settings";
pub const DEFAULT_HUB_API_BASE_URL: &str = "https://aimmod.app";
pub const DEFAULT_REPLAY_CAPTURE_FPS: u32 = 24;
pub const DEFAULT_REPLAY_CAPTURE_WIDTH: u32 = 480;
pub const DEFAULT_REPLAY_KEEP_COUNT: u32 = 150;
pub const DEFAULT_REPLAY_CAPTURE_FRAMING: &str = "cropped";
pub const DEFAULT_REPLAY_CAPTURE_QUALITY: &str = "balanced";
pub const DEFAULT_REPLAY_MEDIA_UPLOAD_MODE: &str = "favorites_and_pb";
pub const DEFAULT_REPLAY_MEDIA_UPLOAD_QUALITY: &str = "standard";
pub const DEFAULT_POST_SESSION_SUMMARY_DURATION_SECS: u32 = 20;
pub const DEFAULT_COACHING_FOCUS_AREA: &str = "balanced";
pub const DEFAULT_COACHING_CHALLENGE_PREFERENCE: &str = "balanced";
pub const DEFAULT_COACHING_TIME_PREFERENCE: &str = "this_week";

/// A rectangle defining an on-screen region.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, Default, PartialEq, Eq)]
pub struct RegionRect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

/// Rich friend profile persisted in settings.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FriendProfile {
    pub username: String,
    pub steam_id: String,
    pub steam_account_name: String,
    pub avatar_url: String,
    pub country: String,
    pub kovaaks_plus: bool,
    #[serde(default)]
    pub bridge_managed: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct OverlaySurfaceAssignments {
    #[serde(default = "default_overlay_surface_assignment_active")]
    pub obs: String,
    #[serde(default = "default_overlay_surface_assignment_active")]
    pub in_game: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct OverlayTheme {
    #[serde(default = "default_overlay_color_sync_mode")]
    pub color_sync_mode: String,
    #[serde(default = "default_overlay_font_family")]
    pub font_family: String,
    #[serde(default = "default_overlay_font_weight_scale")]
    pub font_weight_scale: f64,
    #[serde(default = "default_overlay_text_transform")]
    pub text_transform_mode: String,
    #[serde(default = "default_overlay_primary_color")]
    pub primary_color: String,
    #[serde(default = "default_overlay_accent_color")]
    pub accent_color: String,
    #[serde(default = "default_overlay_danger_color")]
    pub danger_color: String,
    #[serde(default = "default_overlay_warning_color")]
    pub warning_color: String,
    #[serde(default = "default_overlay_info_color")]
    pub info_color: String,
    #[serde(default = "default_overlay_text_color")]
    pub text_color: String,
    #[serde(default = "default_overlay_muted_text_color")]
    pub muted_text_color: String,
    #[serde(default = "default_overlay_background_color")]
    pub background_color: String,
    #[serde(default = "default_overlay_background_gradient_start")]
    pub background_gradient_start: String,
    #[serde(default = "default_overlay_background_gradient_end")]
    pub background_gradient_end: String,
    #[serde(default = "default_overlay_surface_color")]
    pub surface_color: String,
    #[serde(default = "default_overlay_border_color")]
    pub border_color: String,
    #[serde(default = "default_overlay_glow_color")]
    pub glow_color: String,
    #[serde(default = "default_overlay_background_opacity")]
    pub background_opacity: f64,
    #[serde(default = "default_overlay_border_opacity")]
    pub border_opacity: f64,
    #[serde(default = "default_overlay_corner_radius")]
    pub corner_radius: f64,
    #[serde(default = "default_overlay_shadow_strength")]
    pub shadow_strength: f64,
    #[serde(default = "default_overlay_glass_blur")]
    pub glass_blur: f64,
    #[serde(default = "default_overlay_spacing_scale")]
    pub spacing_scale: f64,
    #[serde(default = "default_overlay_animation_preset")]
    pub animation_preset: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct OverlayWidgetStyle {
    #[serde(default = "default_true")]
    pub show_background: bool,
    #[serde(default = "default_true")]
    pub show_border: bool,
    #[serde(default = "default_true")]
    pub show_glow: bool,
    #[serde(default = "default_overlay_widget_opacity")]
    pub opacity: f64,
    #[serde(default = "default_overlay_widget_padding")]
    pub padding: f64,
    #[serde(default = "default_overlay_widget_font_scale")]
    pub font_scale: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct OverlayAnimationOverride {
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default)]
    pub preset: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct OverlayWidgetConfig {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub widget_type: String,
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default = "default_overlay_widget_content_mode")]
    pub content_mode: String,
    #[serde(default)]
    pub group_id: String,
    #[serde(default)]
    pub data_bindings: std::collections::HashMap<String, String>,
    #[serde(default = "default_overlay_widget_style")]
    pub style_overrides: OverlayWidgetStyle,
    #[serde(default = "default_overlay_animation_override")]
    pub animation_overrides: OverlayAnimationOverride,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct OverlayWidgetPlacement {
    #[serde(default = "default_true")]
    pub visible: bool,
    #[serde(default)]
    pub x: f64,
    #[serde(default)]
    pub y: f64,
    #[serde(default = "default_overlay_widget_width")]
    pub width: f64,
    #[serde(default = "default_overlay_widget_scale")]
    pub scale: f64,
    #[serde(default)]
    pub z_index: i32,
    #[serde(default = "default_overlay_anchor")]
    pub anchor: String,
    #[serde(default = "default_overlay_widget_opacity")]
    pub opacity: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SurfaceVariantConfig {
    #[serde(default)]
    pub surface_id: String,
    #[serde(default = "default_overlay_safe_area_padding")]
    pub safe_area_padding: f64,
    #[serde(default)]
    pub widget_layouts: std::collections::HashMap<String, OverlayWidgetPlacement>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct OverlayPreset {
    #[serde(default = "default_overlay_preset_id")]
    pub id: String,
    #[serde(default = "default_overlay_preset_name")]
    pub name: String,
    #[serde(default = "default_overlay_preset_description")]
    pub description: String,
    #[serde(default = "default_overlay_preset_version")]
    pub version: u32,
    #[serde(default = "default_overlay_author_name")]
    pub author_name: String,
    #[serde(default = "default_overlay_preview_accent")]
    pub preview_accent: String,
    #[serde(default)]
    pub preview_image_path: String,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default = "default_overlay_theme")]
    pub theme: OverlayTheme,
    #[serde(default = "default_overlay_widget_configs")]
    pub widgets: std::collections::HashMap<String, OverlayWidgetConfig>,
    #[serde(default = "default_surface_variants")]
    pub surface_variants: std::collections::HashMap<String, SurfaceVariantConfig>,
}

/// Persistent application settings.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppSettings {
    /// Path to KovaaK's stats directory.
    #[serde(default = "default_stats_dir")]
    pub stats_dir: String,
    /// Whether the overlay is currently visible.
    #[serde(default = "default_true")]
    pub overlay_visible: bool,
    /// Which monitor index to show the overlay on (0 = primary).
    #[serde(default)]
    pub monitor_index: usize,
    /// Friends to compare scores against (rich profiles from KovaaK's API).
    #[serde(default)]
    pub friends: Vec<FriendProfile>,
    /// Mouse DPI/CPI used to normalise smoothness metrics so they are comparable
    /// across different sensitivity setups. Defaults to 800.
    #[serde(default = "default_mouse_dpi")]
    pub mouse_dpi: u32,
    /// The username of the friend chosen as battle opponent in VS Mode.
    #[serde(default)]
    pub selected_friend: Option<String>,
    /// Whether live coaching notifications are enabled.
    #[serde(default = "default_true")]
    pub live_feedback_enabled: bool,
    /// Live feedback verbosity: 0=minimal, 1=standard, 2=verbose.
    #[serde(default = "default_feedback_verbosity")]
    pub live_feedback_verbosity: u8,
    /// Which coaching lane should receive extra emphasis.
    #[serde(default = "default_coaching_focus_area")]
    pub coaching_focus_area: String,
    /// How aggressively coaching should push progression over stability.
    #[serde(default = "default_coaching_challenge_preference")]
    pub coaching_challenge_preference: String,
    /// Whether coaching should optimize for the next session or longer-term structure.
    #[serde(default = "default_coaching_time_preference")]
    pub coaching_time_preference: String,
    /// Whether text-to-speech is used to read live coaching notifications aloud.
    #[serde(default)]
    pub live_feedback_tts_enabled: bool,
    /// Name of the selected TTS voice (matches SpeechSynthesisVoice.name in the browser).
    /// None means use the auto-selected best voice.
    #[serde(default)]
    pub live_feedback_tts_voice: Option<String>,
    /// Per-HUD visibility toggles.
    #[serde(default = "default_true")]
    pub hud_vsmode_visible: bool,
    #[serde(default = "default_true")]
    pub hud_smoothness_visible: bool,
    #[serde(default = "default_true")]
    pub hud_stats_visible: bool,
    #[serde(default = "default_true")]
    pub hud_feedback_visible: bool,
    /// Whether the post-session overview card is shown after each run.
    #[serde(default = "default_true")]
    pub hud_post_session_visible: bool,
    /// Whether AimMod should open the Session Stats window after a run finishes.
    #[serde(default)]
    pub open_stats_window_on_session_complete: bool,
    /// How long the post-session summary should stay on screen. Zero keeps it open until dismissed.
    #[serde(default = "default_post_session_summary_duration_secs")]
    pub post_session_summary_duration_secs: u32,
    /// Whether AimMod Hub sync is enabled.
    #[serde(default)]
    pub hub_sync_enabled: bool,
    /// Base URL for the AimMod Hub API.
    #[serde(default = "default_hub_api_base_url")]
    pub hub_api_base_url: String,
    /// Upload credential created automatically by AimMod Hub device linking.
    #[serde(default)]
    pub hub_upload_token: String,
    /// Display label for the linked AimMod Hub account.
    #[serde(default)]
    pub hub_account_label: String,
    /// Non-secret osu! username or numeric user ID selected for profile lookups.
    #[serde(default)]
    pub osu_user_identifier: String,
    /// Target replay capture framerate for recorded screen frames.
    #[serde(default = "default_replay_capture_fps")]
    pub replay_capture_fps: u32,
    /// Target width for encoded replay frames after downscaling.
    #[serde(default = "default_replay_capture_width")]
    pub replay_capture_width: u32,
    /// Capture quality preset for encoded replay frames.
    #[serde(default = "default_replay_capture_quality")]
    pub replay_capture_quality: String,
    /// How many non-favorited replays to keep locally. Zero means unlimited.
    #[serde(default = "default_replay_keep_count")]
    pub replay_keep_count: u32,
    /// Whether replay video should show the whole game window or a center crop.
    #[serde(default = "default_replay_capture_framing")]
    pub replay_capture_framing: String,
    /// Which replays should upload replay media to AimMod Hub.
    #[serde(default = "default_replay_media_upload_mode")]
    pub replay_media_upload_mode: String,
    /// Replay media upload quality preset. Higher presets can later be gated by subscription tier.
    #[serde(default = "default_replay_media_upload_quality")]
    pub replay_media_upload_quality: String,
    /// Accent color theme source: "kovaaks" reads Palette.ini, "custom" uses custom_accent_color, "default" uses built-in green.
    #[serde(default = "default_color_mode")]
    pub color_mode: String,
    /// Custom accent color hex string used when color_mode is "custom", e.g. "#ED6816".
    #[serde(default)]
    pub custom_accent_color: String,
    /// Optional override path for KovaaK's Palette.ini. Empty = auto-detect from %LOCALAPPDATA%.
    #[serde(default)]
    pub kovaaks_palette_path: String,
    /// Per-color overrides applied on top of KovaaK's palette (key = palette name, value = "#RRGGBB").
    /// Only active when color_mode is "kovaaks". Changes are also written back to Palette.ini.
    #[serde(default)]
    pub palette_color_overrides: std::collections::HashMap<String, String>,
    /// Opacity for all overlay HUDs (0.0–1.0). Defaults to 1.0 (fully opaque).
    #[serde(default = "default_hud_opacity")]
    pub hud_opacity: f64,
    /// Shared overlay preset library for OBS and the in-game overlay surfaces.
    #[serde(default = "default_overlay_presets")]
    pub overlay_presets: Vec<OverlayPreset>,
    /// Active fallback overlay preset used when a specific surface has no override.
    #[serde(default = "default_overlay_surface_assignment_active")]
    pub active_overlay_preset_id: String,
    /// Per-surface preset assignments.
    #[serde(default = "default_overlay_surface_assignments")]
    pub active_surface_assignments: OverlaySurfaceAssignments,
    /// Benchmarks selected for benchmark overlay widgets.
    #[serde(default)]
    pub overlay_selected_benchmark_ids: Vec<u32>,
    /// Preferred benchmark for the full benchmark widget.
    #[serde(default)]
    pub overlay_primary_benchmark_id: Option<u32>,
    /// Number of model layers to offload to GPU for the local coach.
    /// 0 = CPU only, -1 = all layers on GPU, any positive N = N layers on GPU (split mode).
    #[serde(default)]
    pub local_llm_gpu_layers: i32,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            stats_dir: default_stats_dir(),
            overlay_visible: true,
            monitor_index: 0,
            friends: Vec::new(),
            mouse_dpi: default_mouse_dpi(),
            selected_friend: None,
            live_feedback_enabled: true,
            live_feedback_verbosity: 1,
            coaching_focus_area: default_coaching_focus_area(),
            coaching_challenge_preference: default_coaching_challenge_preference(),
            coaching_time_preference: default_coaching_time_preference(),
            live_feedback_tts_enabled: false,
            live_feedback_tts_voice: None,
            hud_vsmode_visible: true,
            hud_smoothness_visible: true,
            hud_stats_visible: true,
            hud_feedback_visible: true,
            hud_post_session_visible: true,
            open_stats_window_on_session_complete: false,
            post_session_summary_duration_secs: default_post_session_summary_duration_secs(),
            hub_sync_enabled: false,
            hub_api_base_url: default_hub_api_base_url(),
            hub_upload_token: String::new(),
            hub_account_label: String::new(),
            osu_user_identifier: String::new(),
            replay_capture_fps: default_replay_capture_fps(),
            replay_capture_width: default_replay_capture_width(),
            replay_capture_quality: default_replay_capture_quality(),
            replay_keep_count: default_replay_keep_count(),
            replay_capture_framing: default_replay_capture_framing(),
            replay_media_upload_mode: default_replay_media_upload_mode(),
            replay_media_upload_quality: default_replay_media_upload_quality(),
            color_mode: default_color_mode(),
            custom_accent_color: String::new(),
            kovaaks_palette_path: String::new(),
            palette_color_overrides: std::collections::HashMap::new(),
            hud_opacity: default_hud_opacity(),
            overlay_presets: default_overlay_presets(),
            active_overlay_preset_id: default_overlay_surface_assignment_active(),
            active_surface_assignments: default_overlay_surface_assignments(),
            overlay_selected_benchmark_ids: Vec::new(),
            overlay_primary_benchmark_id: None,
            local_llm_gpu_layers: 0,
        }
    }
}

pub fn load_default() -> AppSettings {
    let mut settings = AppSettings::default();
    apply_runtime_overrides(&mut settings);
    settings
}

pub fn init_runtime_launch_overrides() {
    let _ = hub_api_base_url_launch_override();
}

pub fn hub_api_base_url_launch_override() -> Option<String> {
    static OVERRIDE: OnceLock<Option<String>> = OnceLock::new();
    OVERRIDE
        .get_or_init(parse_hub_api_base_url_launch_override)
        .clone()
}

fn parse_hub_api_base_url_launch_override() -> Option<String> {
    let mut args = std::env::args();
    while let Some(arg) = args.next() {
        if let Some(value) = arg.strip_prefix("--hub-api-base-url=") {
            let normalized = normalize_hub_api_base_url(value);
            return if normalized.is_empty() {
                None
            } else {
                Some(normalized)
            };
        }

        if arg == "--hub-api-base-url" {
            let next = args.next().unwrap_or_default();
            let normalized = normalize_hub_api_base_url(&next);
            return if normalized.is_empty() {
                None
            } else {
                Some(normalized)
            };
        }
    }

    None
}

pub fn apply_runtime_overrides(settings: &mut AppSettings) {
    if let Some(override_url) = hub_api_base_url_launch_override() {
        settings.hub_api_base_url = override_url;
    } else if is_localhost_url(&settings.hub_api_base_url) {
        // A localhost URL was saved from a previous dev-flag run but the flag
        // is not present now — reset to the production default.
        settings.hub_api_base_url = DEFAULT_HUB_API_BASE_URL.to_string();
    }
}

fn is_localhost_url(url: &str) -> bool {
    let lower = url.trim().to_ascii_lowercase();
    lower.starts_with("http://localhost")
        || lower.starts_with("https://localhost")
        || lower.starts_with("http://127.0.0.1")
        || lower.starts_with("https://127.0.0.1")
        || lower.starts_with("localhost:")
        || lower.starts_with("127.0.0.1:")
}

pub fn load_persisted(app: &AppHandle) -> anyhow::Result<AppSettings> {
    use tauri_plugin_store::StoreExt;
    let store = app.store(STORE_PATH)?;
    if let Some(val) = store.get(STORE_KEY) {
        match serde_json::from_value::<AppSettings>(val.clone()) {
            Ok(mut settings) => {
                settings.hub_api_base_url = normalize_hub_api_base_url(&settings.hub_api_base_url);
                if settings.hub_api_base_url.trim().is_empty() {
                    settings.hub_api_base_url = default_hub_api_base_url();
                }
                if settings.replay_capture_fps == 0 {
                    settings.replay_capture_fps = default_replay_capture_fps();
                }
                settings.replay_capture_width =
                    normalize_replay_capture_width(settings.replay_capture_width);
                settings.replay_capture_framing =
                    normalize_replay_capture_framing(&settings.replay_capture_framing);
                settings.replay_capture_quality =
                    normalize_replay_capture_quality(&settings.replay_capture_quality);
                settings.post_session_summary_duration_secs =
                    normalize_post_session_summary_duration_secs(
                        settings.post_session_summary_duration_secs,
                    );
                settings.replay_media_upload_mode =
                    normalize_replay_media_upload_mode(&settings.replay_media_upload_mode);
                settings.replay_media_upload_quality =
                    normalize_replay_media_upload_quality(&settings.replay_media_upload_quality);
                settings.coaching_focus_area =
                    normalize_coaching_focus_area(&settings.coaching_focus_area);
                settings.coaching_challenge_preference = normalize_coaching_challenge_preference(
                    &settings.coaching_challenge_preference,
                );
                settings.coaching_time_preference =
                    normalize_coaching_time_preference(&settings.coaching_time_preference);
                normalize_overlay_settings(&mut settings);
                return Ok(settings);
            }
            Err(e) => {
                log::error!("Failed to deserialize settings: {e}");
                log::error!(
                    "Raw settings JSON: {}",
                    serde_json::to_string_pretty(&val)
                        .unwrap_or_else(|_| "<unserializable>".into())
                );
                log::warn!("Falling back to default settings");
                return Ok(AppSettings::default());
            }
        }
    }
    Ok(AppSettings::default())
}

pub fn load(app: &AppHandle) -> anyhow::Result<AppSettings> {
    let mut settings = load_persisted(app)?;
    apply_runtime_overrides(&mut settings);
    Ok(settings)
}

pub fn persist(app: &AppHandle, settings: &AppSettings) -> anyhow::Result<()> {
    use tauri_plugin_store::StoreExt;
    let store = app.store(STORE_PATH)?;
    // If the hub URL was injected via launch flag, don't persist it — the flag
    // is ephemeral and writing it would break the next flag-free launch.
    let to_save;
    let settings = if hub_api_base_url_launch_override().is_some() {
        to_save = AppSettings {
            hub_api_base_url: DEFAULT_HUB_API_BASE_URL.to_string(),
            ..settings.clone()
        };
        &to_save
    } else {
        settings
    };
    store.set(STORE_KEY.to_string(), serde_json::to_value(settings)?);
    store.save()?;
    Ok(())
}

pub fn normalize_overlay_settings(settings: &mut AppSettings) {
    if settings.overlay_presets.is_empty() {
        settings.overlay_presets = migrated_classic_presets_from(settings);
    }

    for preset in &mut settings.overlay_presets {
        normalize_overlay_preset(preset);
    }

    settings.overlay_selected_benchmark_ids.sort_unstable();
    settings.overlay_selected_benchmark_ids.dedup();
    if settings
        .overlay_primary_benchmark_id
        .is_some_and(|wanted| !settings.overlay_selected_benchmark_ids.contains(&wanted))
    {
        settings.overlay_primary_benchmark_id =
            settings.overlay_selected_benchmark_ids.first().copied();
    }

    if settings.active_overlay_preset_id.trim().is_empty()
        || !settings
            .overlay_presets
            .iter()
            .any(|preset| preset.id == settings.active_overlay_preset_id)
    {
        settings.active_overlay_preset_id = settings
            .overlay_presets
            .first()
            .map(|preset| preset.id.clone())
            .unwrap_or_else(default_overlay_surface_assignment_active);
    }

    for assignment in [
        &mut settings.active_surface_assignments.obs,
        &mut settings.active_surface_assignments.in_game,
    ] {
        if assignment.trim().is_empty()
            || !settings
                .overlay_presets
                .iter()
                .any(|preset| &preset.id == assignment)
        {
            *assignment = settings.active_overlay_preset_id.clone();
        }
    }
}

fn normalize_overlay_preset(preset: &mut OverlayPreset) {
    if preset.id.trim().is_empty() {
        preset.id = default_overlay_preset_id();
    }
    if preset.name.trim().is_empty() {
        preset.name = default_overlay_preset_name();
    }
    if preset.version == 0 {
        preset.version = default_overlay_preset_version();
    }
    match preset.theme.color_sync_mode.trim() {
        "preset" | "app" => {}
        _ => {
            preset.theme.color_sync_mode = default_overlay_color_sync_mode();
        }
    }
    if preset.widgets.is_empty() {
        preset.widgets = default_overlay_widget_configs();
    }
    if preset.surface_variants.is_empty() {
        preset.surface_variants = default_surface_variants();
    }

    preset.surface_variants.remove("desktop_private");

    for (widget_id, default_widget) in default_overlay_widget_configs() {
        preset.widgets.entry(widget_id).or_insert(default_widget);
    }

    for (surface_id, default_surface) in default_surface_variants() {
        let surface = preset
            .surface_variants
            .entry(surface_id.clone())
            .or_insert(default_surface);
        for (widget_id, placement) in default_overlay_layouts(&surface_id) {
            surface.widget_layouts.entry(widget_id).or_insert(placement);
        }
    }
}

fn migrated_classic_presets_from(settings: &AppSettings) -> Vec<OverlayPreset> {
    let mut preset = default_overlay_presets()
        .into_iter()
        .next()
        .unwrap_or_else(default_overlay_preset);
    preset.name = "Migrated Classic".to_string();
    preset.description =
        "Imported from AimMod's legacy HUD visibility settings and prepared for OBS/in-game editing."
            .to_string();

    let widget_visibility = [
        ("vsmode", settings.hud_vsmode_visible),
        ("smoothness", settings.hud_smoothness_visible),
        ("live_stats", settings.hud_stats_visible),
        ("coaching_toast", settings.hud_feedback_visible),
        ("post_run_summary", settings.hud_post_session_visible),
        ("header", true),
        ("progress_bar", true),
        ("pb_pace", true),
    ];

    for (widget_id, visible) in widget_visibility {
        if let Some(widget) = preset.widgets.get_mut(widget_id) {
            widget.enabled = visible;
        }
        for surface in preset.surface_variants.values_mut() {
            if let Some(layout) = surface.widget_layouts.get_mut(widget_id) {
                layout.visible = visible;
            }
        }
    }

    vec![preset]
}

fn overlay_widget_ids() -> [&'static str; 11] {
    [
        "header",
        "live_stats",
        "progress_bar",
        "pb_pace",
        "vsmode",
        "smoothness",
        "coaching_toast",
        "post_run_summary",
        "benchmark_current",
        "benchmark_page",
        "mouse_path",
    ]
}

fn default_mouse_dpi() -> u32 {
    800
}
fn default_true() -> bool {
    true
}
fn default_feedback_verbosity() -> u8 {
    1
}
fn default_coaching_focus_area() -> String {
    DEFAULT_COACHING_FOCUS_AREA.to_string()
}
fn default_coaching_challenge_preference() -> String {
    DEFAULT_COACHING_CHALLENGE_PREFERENCE.to_string()
}
fn default_coaching_time_preference() -> String {
    DEFAULT_COACHING_TIME_PREFERENCE.to_string()
}
fn default_hub_api_base_url() -> String {
    DEFAULT_HUB_API_BASE_URL.to_string()
}
pub fn normalize_hub_api_base_url(value: &str) -> String {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return String::new();
    }

    let normalized = if let Some(rest) = trimmed.strip_prefix("https://api.aimmod.app") {
        format!("https://aimmod.app{}", rest)
    } else if let Some(rest) = trimmed.strip_prefix("http://api.aimmod.app") {
        format!("https://aimmod.app{}", rest)
    } else if let Some(rest) = trimmed.strip_prefix("api.aimmod.app") {
        format!(
            "https://aimmod.app{}",
            if rest.starts_with('/') {
                rest.to_string()
            } else {
                format!("/{}", rest)
            }
        )
    } else if let Some(rest) = trimmed.strip_prefix("aimmod.app") {
        format!(
            "https://aimmod.app{}",
            if rest.is_empty() || rest.starts_with('/') {
                rest.to_string()
            } else {
                format!("/{}", rest)
            }
        )
    } else {
        trimmed.to_string()
    };

    normalized.trim_end_matches('/').to_string()
}
fn default_replay_capture_fps() -> u32 {
    DEFAULT_REPLAY_CAPTURE_FPS
}
fn default_replay_capture_width() -> u32 {
    DEFAULT_REPLAY_CAPTURE_WIDTH
}
fn default_replay_keep_count() -> u32 {
    DEFAULT_REPLAY_KEEP_COUNT
}
fn default_replay_capture_framing() -> String {
    DEFAULT_REPLAY_CAPTURE_FRAMING.to_string()
}
fn default_replay_capture_quality() -> String {
    DEFAULT_REPLAY_CAPTURE_QUALITY.to_string()
}
fn default_replay_media_upload_mode() -> String {
    DEFAULT_REPLAY_MEDIA_UPLOAD_MODE.to_string()
}
fn default_replay_media_upload_quality() -> String {
    DEFAULT_REPLAY_MEDIA_UPLOAD_QUALITY.to_string()
}
fn default_color_mode() -> String {
    "kovaaks".to_string()
}
fn default_hud_opacity() -> f64 {
    1.0
}
fn default_post_session_summary_duration_secs() -> u32 {
    DEFAULT_POST_SESSION_SUMMARY_DURATION_SECS
}
fn default_overlay_surface_assignment_active() -> String {
    "classic_migrated".to_string()
}
fn default_overlay_surface_assignments() -> OverlaySurfaceAssignments {
    OverlaySurfaceAssignments {
        obs: default_overlay_surface_assignment_active(),
        in_game: default_overlay_surface_assignment_active(),
    }
}
fn default_overlay_font_family() -> String {
    "\"JetBrains Mono\", monospace".to_string()
}
fn default_overlay_color_sync_mode() -> String {
    "app".to_string()
}
fn default_overlay_font_weight_scale() -> f64 {
    1.0
}
fn default_overlay_text_transform() -> String {
    "uppercase".to_string()
}
fn default_overlay_primary_color() -> String {
    "#00f5a0".to_string()
}
fn default_overlay_accent_color() -> String {
    "#5cf0ff".to_string()
}
fn default_overlay_danger_color() -> String {
    "#ff4d6d".to_string()
}
fn default_overlay_warning_color() -> String {
    "#ffd166".to_string()
}
fn default_overlay_info_color() -> String {
    "#7cc6fe".to_string()
}
fn default_overlay_text_color() -> String {
    "#f7fffb".to_string()
}
fn default_overlay_muted_text_color() -> String {
    "#8fd3bb".to_string()
}
fn default_overlay_background_color() -> String {
    "#071411".to_string()
}
fn default_overlay_background_gradient_start() -> String {
    "#0b231e".to_string()
}
fn default_overlay_background_gradient_end() -> String {
    "#08110f".to_string()
}
fn default_overlay_surface_color() -> String {
    "#12342d".to_string()
}
fn default_overlay_border_color() -> String {
    "#00f5a0".to_string()
}
fn default_overlay_glow_color() -> String {
    "#00f5a0".to_string()
}
fn default_overlay_background_opacity() -> f64 {
    0.72
}
fn default_overlay_border_opacity() -> f64 {
    0.45
}
fn default_overlay_corner_radius() -> f64 {
    18.0
}
fn default_overlay_shadow_strength() -> f64 {
    0.7
}
fn default_overlay_glass_blur() -> f64 {
    14.0
}
fn default_overlay_spacing_scale() -> f64 {
    1.0
}
fn default_overlay_animation_preset() -> String {
    "smooth".to_string()
}
fn default_overlay_widget_opacity() -> f64 {
    1.0
}
fn default_overlay_widget_padding() -> f64 {
    1.0
}
fn default_overlay_widget_font_scale() -> f64 {
    1.0
}
fn default_overlay_widget_content_mode() -> String {
    "live".to_string()
}
fn default_overlay_widget_width() -> f64 {
    320.0
}
fn default_overlay_widget_scale() -> f64 {
    1.0
}
fn default_overlay_anchor() -> String {
    "top-left".to_string()
}
fn default_overlay_safe_area_padding() -> f64 {
    32.0
}
fn default_overlay_preset_id() -> String {
    "classic_migrated".to_string()
}
fn default_overlay_preset_name() -> String {
    "Classic Stream".to_string()
}
fn default_overlay_preset_description() -> String {
    "AimMod's default competitive HUD preset for OBS and the in-game overlay.".to_string()
}
fn default_overlay_preset_version() -> u32 {
    1
}
fn default_overlay_author_name() -> String {
    "AimMod".to_string()
}
fn default_overlay_preview_accent() -> String {
    default_overlay_primary_color()
}
fn default_overlay_widget_style() -> OverlayWidgetStyle {
    OverlayWidgetStyle {
        show_background: true,
        show_border: true,
        show_glow: true,
        opacity: default_overlay_widget_opacity(),
        padding: default_overlay_widget_padding(),
        font_scale: default_overlay_widget_font_scale(),
    }
}
fn default_overlay_animation_override() -> OverlayAnimationOverride {
    OverlayAnimationOverride {
        enabled: true,
        preset: String::new(),
    }
}
fn default_overlay_theme() -> OverlayTheme {
    OverlayTheme {
        color_sync_mode: default_overlay_color_sync_mode(),
        font_family: default_overlay_font_family(),
        font_weight_scale: default_overlay_font_weight_scale(),
        text_transform_mode: default_overlay_text_transform(),
        primary_color: default_overlay_primary_color(),
        accent_color: default_overlay_accent_color(),
        danger_color: default_overlay_danger_color(),
        warning_color: default_overlay_warning_color(),
        info_color: default_overlay_info_color(),
        text_color: default_overlay_text_color(),
        muted_text_color: default_overlay_muted_text_color(),
        background_color: default_overlay_background_color(),
        background_gradient_start: default_overlay_background_gradient_start(),
        background_gradient_end: default_overlay_background_gradient_end(),
        surface_color: default_overlay_surface_color(),
        border_color: default_overlay_border_color(),
        glow_color: default_overlay_glow_color(),
        background_opacity: default_overlay_background_opacity(),
        border_opacity: default_overlay_border_opacity(),
        corner_radius: default_overlay_corner_radius(),
        shadow_strength: default_overlay_shadow_strength(),
        glass_blur: default_overlay_glass_blur(),
        spacing_scale: default_overlay_spacing_scale(),
        animation_preset: default_overlay_animation_preset(),
    }
}
fn default_overlay_widget_configs() -> std::collections::HashMap<String, OverlayWidgetConfig> {
    let mut out = std::collections::HashMap::new();
    for widget_id in overlay_widget_ids() {
        // mouse_path is disabled by default — users opt in via Overlay Studio
        let enabled = widget_id != "mouse_path";
        out.insert(
            widget_id.to_string(),
            OverlayWidgetConfig {
                id: widget_id.to_string(),
                widget_type: widget_id.to_string(),
                enabled,
                content_mode: default_overlay_widget_content_mode(),
                group_id: String::new(),
                data_bindings: std::collections::HashMap::new(),
                style_overrides: default_overlay_widget_style(),
                animation_overrides: default_overlay_animation_override(),
            },
        );
    }
    out
}
fn default_overlay_layouts(
    surface_id: &str,
) -> std::collections::HashMap<String, OverlayWidgetPlacement> {
    let mut out = std::collections::HashMap::new();
    // (widget_id, x, y, width, scale, z_index, visible)
    let entries: [(&str, f64, f64, f64, f64, i32, bool); 11] = match surface_id {
        "obs" => [
            ("header", 40.0, 28.0, 440.0, 1.0, 1, true),
            ("live_stats", 40.0, 118.0, 360.0, 1.0, 2, true),
            ("progress_bar", 40.0, 330.0, 420.0, 1.0, 3, true),
            ("pb_pace", 40.0, 382.0, 340.0, 1.0, 4, true),
            ("vsmode", 1490.0, 70.0, 340.0, 1.0, 2, true),
            ("smoothness", 1490.0, 232.0, 270.0, 1.0, 3, true),
            ("benchmark_current", 1450.0, 368.0, 360.0, 1.0, 4, true),
            ("coaching_toast", 1290.0, 762.0, 520.0, 1.0, 5, true),
            ("post_run_summary", 1230.0, 420.0, 620.0, 1.0, 6, true),
            ("benchmark_page", 40.0, 460.0, 900.0, 0.8, 7, true),
            ("mouse_path", 1480.0, 560.0, 380.0, 1.0, 8, false),
        ],
        "in_game" => [
            ("header", 24.0, 18.0, 360.0, 0.9, 1, true),
            ("live_stats", 24.0, 92.0, 300.0, 0.9, 2, true),
            ("progress_bar", 24.0, 270.0, 320.0, 0.9, 3, true),
            ("pb_pace", 24.0, 314.0, 280.0, 0.9, 4, true),
            ("vsmode", 1520.0, 54.0, 300.0, 0.85, 2, true),
            ("smoothness", 1520.0, 198.0, 240.0, 0.85, 3, true),
            ("benchmark_current", 1490.0, 328.0, 300.0, 0.82, 4, true),
            ("coaching_toast", 1280.0, 760.0, 520.0, 0.85, 5, true),
            ("post_run_summary", 1240.0, 416.0, 560.0, 0.85, 6, true),
            ("benchmark_page", 40.0, 432.0, 820.0, 0.68, 7, true),
            ("mouse_path", 1520.0, 548.0, 340.0, 0.85, 8, false),
        ],
        _ => [
            ("header", 16.0, 16.0, 420.0, 1.0, 1, true),
            ("live_stats", 16.0, 108.0, 300.0, 1.0, 2, true),
            ("progress_bar", 16.0, 292.0, 340.0, 1.0, 3, true),
            ("pb_pace", 16.0, 338.0, 300.0, 1.0, 4, true),
            ("vsmode", 16.0, 394.0, 320.0, 1.0, 2, true),
            ("smoothness", 1592.0, 24.0, 240.0, 1.0, 3, true),
            ("benchmark_current", 1508.0, 142.0, 320.0, 0.95, 4, true),
            ("coaching_toast", 1240.0, 764.0, 520.0, 1.0, 5, true),
            ("post_run_summary", 1090.0, 404.0, 560.0, 1.0, 6, true),
            ("benchmark_page", 420.0, 30.0, 920.0, 0.76, 7, true),
            ("mouse_path", 1490.0, 560.0, 380.0, 1.0, 8, false),
        ],
    };
    for (widget_id, x, y, width, scale, z_index, visible) in entries {
        out.insert(
            widget_id.to_string(),
            OverlayWidgetPlacement {
                visible,
                x,
                y,
                width,
                scale,
                z_index,
                anchor: default_overlay_anchor(),
                opacity: default_overlay_widget_opacity(),
            },
        );
    }
    out
}
fn default_surface_variants() -> std::collections::HashMap<String, SurfaceVariantConfig> {
    let mut out = std::collections::HashMap::new();
    for surface_id in ["obs", "in_game"] {
        out.insert(
            surface_id.to_string(),
            SurfaceVariantConfig {
                surface_id: surface_id.to_string(),
                safe_area_padding: default_overlay_safe_area_padding(),
                widget_layouts: default_overlay_layouts(surface_id),
            },
        );
    }
    out
}
fn default_overlay_preset() -> OverlayPreset {
    OverlayPreset {
        id: default_overlay_preset_id(),
        name: default_overlay_preset_name(),
        description: default_overlay_preset_description(),
        version: default_overlay_preset_version(),
        author_name: default_overlay_author_name(),
        preview_accent: default_overlay_preview_accent(),
        preview_image_path: String::new(),
        tags: vec![
            "aimmod".to_string(),
            "competitive".to_string(),
            "stream".to_string(),
        ],
        theme: default_overlay_theme(),
        widgets: default_overlay_widget_configs(),
        surface_variants: default_surface_variants(),
    }
}
fn default_overlay_presets() -> Vec<OverlayPreset> {
    vec![default_overlay_preset()]
}
fn normalize_post_session_summary_duration_secs(value: u32) -> u32 {
    value.min(600)
}
pub fn normalize_replay_capture_width(value: u32) -> u32 {
    value.clamp(320, 1920)
}
pub fn normalize_replay_capture_framing(value: &str) -> String {
    match value.trim() {
        "fullscreen" | "cropped" => value.trim().to_string(),
        _ => default_replay_capture_framing(),
    }
}
pub fn normalize_replay_capture_quality(value: &str) -> String {
    match value.trim() {
        "balanced" | "high" | "ultra" => value.trim().to_string(),
        _ => default_replay_capture_quality(),
    }
}
fn normalize_replay_media_upload_mode(value: &str) -> String {
    match value.trim() {
        "off" | "favorites" | "favorites_and_pb" | "all" => value.trim().to_string(),
        _ => default_replay_media_upload_mode(),
    }
}
fn normalize_replay_media_upload_quality(value: &str) -> String {
    match value.trim() {
        "standard" | "high" | "ultra" => value.trim().to_string(),
        _ => default_replay_media_upload_quality(),
    }
}
pub fn normalize_coaching_focus_area(value: &str) -> String {
    match value.trim() {
        "balanced" | "precision" | "speed" | "control" | "consistency" | "endurance"
        | "transfer" => value.trim().to_string(),
        _ => default_coaching_focus_area(),
    }
}
pub fn normalize_coaching_challenge_preference(value: &str) -> String {
    match value.trim() {
        "steady" | "balanced" | "aggressive" => value.trim().to_string(),
        _ => default_coaching_challenge_preference(),
    }
}
pub fn normalize_coaching_time_preference(value: &str) -> String {
    match value.trim() {
        "next_session" | "this_week" | "long_term" => value.trim().to_string(),
        _ => default_coaching_time_preference(),
    }
}

const KOVAAKS_STATS_SUFFIX: &str = r"steamapps\common\FPSAimTrainer\FPSAimTrainer\stats";

fn default_stats_dir() -> String {
    #[cfg(target_os = "windows")]
    {
        if let Some(dir) = detect_kovaaks_stats_dir() {
            log::info!("Auto-detected KovaaK's stats dir: {dir}");
            return dir;
        }
        // Hard-coded fallback (default Steam location on C:)
        log::warn!("Could not auto-detect KovaaK's install; using default path");
        format!(r"C:\Program Files (x86)\Steam\{KOVAAKS_STATS_SUFFIX}")
    }
    #[cfg(not(target_os = "windows"))]
    {
        std::env::var("HOME").unwrap_or_default() + "/kovaaks-stats"
    }
}

/// Walk all Steam library folders (via registry + libraryfolders.vdf) and return
/// the first path that contains FPSAimTrainer/stats, or None.
#[cfg(target_os = "windows")]
fn detect_kovaaks_stats_dir() -> Option<String> {
    use winreg::RegKey;
    use winreg::enums::{HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE};

    // Try both registry locations Steam uses
    let steam_path = RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Valve\Steam")
        .and_then(|k| k.get_value::<String, _>("SteamPath"))
        .or_else(|_| {
            RegKey::predef(HKEY_LOCAL_MACHINE)
                .open_subkey(r"SOFTWARE\WOW6432Node\Valve\Steam")
                .and_then(|k| k.get_value::<String, _>("InstallPath"))
        })
        .ok()?;

    // Collect all Steam library roots from libraryfolders.vdf
    let vdf_path = std::path::Path::new(&steam_path).join(r"steamapps\libraryfolders.vdf");
    let mut libraries: Vec<String> = vec![steam_path.replace('/', r"\")];

    if let Ok(vdf) = std::fs::read_to_string(&vdf_path) {
        // Parse lines like:   "path"    "D:\\SteamLibrary"
        for line in vdf.lines() {
            let trimmed = line.trim();
            if trimmed.starts_with(r#""path""#) {
                // Extract the value between the second pair of quotes
                let _parts: Vec<&str> = trimmed.splitn(3, '"').collect();
                // parts: ["", "path", "    \"D:\\SteamLibrary\""]
                // Better: split on all quotes and take 4th token
                let tokens: Vec<&str> = trimmed
                    .split('"')
                    .filter(|s| !s.trim().is_empty())
                    .collect();
                // tokens[0]="path", tokens[1]="D:\\SteamLibrary"
                if let Some(path) = tokens.get(1) {
                    // Unescape double-backslashes from VDF
                    let normalized = path.replace(r"\\", r"\");
                    libraries.push(normalized);
                }
            }
        }
    }

    // Check each library root for FPSAimTrainer stats
    for lib in &libraries {
        let candidate = std::path::Path::new(lib).join(KOVAAKS_STATS_SUFFIX);
        if candidate.exists() {
            return Some(candidate.to_string_lossy().into_owned());
        }
    }

    None
}
