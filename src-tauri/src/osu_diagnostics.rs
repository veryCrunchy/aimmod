use serde::Deserialize;
use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

const DEDUPE_WINDOW: Duration = Duration::from_secs(1);
const RATE_WINDOW: Duration = Duration::from_secs(60);
const MAX_EVENTS_PER_WINDOW: u32 = 80;

#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OsuClientDiagnostic {
    pub area: String,
    pub event: String,
    pub source_id: Option<String>,
    pub media_error_code: Option<u16>,
    pub network_state: Option<u16>,
    pub ready_state: Option<u16>,
}

struct DiagnosticLimiter {
    window_started_at: Instant,
    sent_in_window: u32,
    last_sent: HashMap<String, Instant>,
}

impl DiagnosticLimiter {
    fn new(now: Instant) -> Self {
        Self {
            window_started_at: now,
            sent_in_window: 0,
            last_sent: HashMap::new(),
        }
    }

    fn accepts(&mut self, key: &str, now: Instant) -> bool {
        if now.duration_since(self.window_started_at) >= RATE_WINDOW {
            self.window_started_at = now;
            self.sent_in_window = 0;
            self.last_sent
                .retain(|_, sent_at| now.duration_since(*sent_at) < DEDUPE_WINDOW);
        }
        if self.sent_in_window >= MAX_EVENTS_PER_WINDOW {
            return false;
        }
        if self
            .last_sent
            .get(key)
            .is_some_and(|sent_at| now.duration_since(*sent_at) < DEDUPE_WINDOW)
        {
            return false;
        }
        self.last_sent.insert(key.to_string(), now);
        self.sent_in_window += 1;
        true
    }
}

fn limiter() -> &'static Mutex<DiagnosticLimiter> {
    static LIMITER: OnceLock<Mutex<DiagnosticLimiter>> = OnceLock::new();
    LIMITER.get_or_init(|| Mutex::new(DiagnosticLimiter::new(Instant::now())))
}

fn safe_token(value: &str, maximum: usize) -> String {
    value
        .chars()
        .filter(|character| character.is_ascii_alphanumeric() || matches!(character, '_' | '-'))
        .take(maximum)
        .collect()
}

fn safe_source_id(value: &str) -> Option<String> {
    let normalized = value.to_ascii_lowercase();
    if normalized.len() == 64 && normalized.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Some(normalized);
    }
    if matches!(
        normalized.as_str(),
        "beatmaps" | "skins" | "replays" | "statistics" | "leaderboards" | "coaching"
    ) {
        return Some(normalized);
    }
    for prefix in ["audio-", "replay-", "item-"] {
        if let Some(suffix) = normalized.strip_prefix(prefix) {
            if suffix.len() == 8 && suffix.bytes().all(|byte| byte.is_ascii_hexdigit()) {
                return Some(normalized);
            }
        }
    }
    None
}

pub fn record(diagnostic: OsuClientDiagnostic) {
    let area = safe_token(&diagnostic.area, 32);
    let event = safe_token(&diagnostic.event, 48);
    let source = diagnostic.source_id.as_deref().and_then(safe_source_id);
    if !matches!(
        area.as_str(),
        "workspace" | "previewAudio" | "replayAnalysis" | "nativeReplay"
    ) || event.is_empty()
    {
        return;
    }

    let key = format!(
        "{area}:{event}:{}:{:?}:{:?}:{:?}",
        source.as_deref().unwrap_or("none"),
        diagnostic.media_error_code,
        diagnostic.network_state,
        diagnostic.ready_state,
    );
    let Ok(mut guard) = limiter().lock() else {
        return;
    };
    if !guard.accepts(&key, Instant::now()) {
        return;
    }
    drop(guard);

    log::info!(
        target: "aimmod::osu_diagnostics",
        "osu_diag area={area} event={event} source={} media_error={} network={} ready={}",
        source.as_deref().unwrap_or("none"),
        diagnostic.media_error_code.map_or_else(|| "none".to_string(), |value| value.to_string()),
        diagnostic.network_state.map_or_else(|| "none".to_string(), |value| value.to_string()),
        diagnostic.ready_state.map_or_else(|| "none".to_string(), |value| value.to_string()),
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn safe_token_removes_paths_and_punctuation() {
        assert_eq!(
            safe_token("/home/name/private replay.osr", 72),
            "homenameprivatereplayosr"
        );
        assert_eq!(safe_token("loaded-metadata", 8), "loaded-m");
        assert_eq!(safe_source_id("/home/name/private replay.osr"), None);
        assert_eq!(
            safe_source_id("replay-deadbeef"),
            Some("replay-deadbeef".to_string())
        );
        assert_eq!(safe_source_id(&"A".repeat(64)), Some("a".repeat(64)));
    }

    #[test]
    fn limiter_deduplicates_and_caps_a_window() {
        let started = Instant::now();
        let mut limiter = DiagnosticLimiter::new(started);
        assert!(limiter.accepts("same", started));
        assert!(!limiter.accepts("same", started + Duration::from_millis(500)));
        assert!(limiter.accepts("same", started + Duration::from_secs(2)));
        for index in 2..MAX_EVENTS_PER_WINDOW {
            assert!(limiter.accepts(&format!("event-{index}"), started + Duration::from_secs(2)));
        }
        assert!(!limiter.accepts("over-limit", started + Duration::from_secs(2)));
        assert!(limiter.accepts("new-window", started + RATE_WINDOW));
    }
}
