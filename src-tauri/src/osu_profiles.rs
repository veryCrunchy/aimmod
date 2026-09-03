use once_cell::sync::Lazy;
use reqwest::{Client, StatusCode, redirect::Policy};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;
use std::time::Duration;
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::AppHandle;

const GET_OFFICIAL_USER_PROFILE_PATH: &str = "/aimmod.osu.v1.OsuService/GetOfficialUserProfile";
const MAX_LAZER_CONFIG_BYTES: u64 = 2 * 1024 * 1024;
const OSU_ME_URL: &str = "https://osu.ppy.sh/api/v2/me/osu";

static OSU_SESSION_CLIENT: Lazy<Client> = Lazy::new(|| {
    Client::builder()
        .timeout(Duration::from_secs(12))
        .redirect(Policy::none())
        .user_agent("AimMod/1.8 (local osu!lazer session bridge)")
        .build()
        .expect("failed to build the local osu! session client")
});

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuLazerSessionState {
    pub status: String,
    pub username: Option<String>,
}

#[derive(Debug)]
struct OsuLazerSessionCredentials {
    state: OsuLazerSessionState,
    access_token: Option<String>,
}

fn config_value<'a>(contents: &'a str, wanted: &str) -> Option<&'a str> {
    contents.lines().find_map(|line| {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with(';') {
            return None;
        }
        let (key, value) = line.split_once('=')?;
        key.trim()
            .eq_ignore_ascii_case(wanted)
            .then_some(value.trim())
    })
}

fn parse_bool(value: Option<&str>, default: bool) -> bool {
    value
        .map(str::trim)
        .and_then(|value| match value.to_ascii_lowercase().as_str() {
            "true" | "1" | "yes" => Some(true),
            "false" | "0" | "no" => Some(false),
            _ => None,
        })
        .unwrap_or(default)
}

fn parse_lazer_session(contents: &str, now_unix: i64) -> OsuLazerSessionState {
    let username = config_value(contents, "Username")
        .map(str::trim)
        .filter(|value| !value.is_empty() && value.len() <= 64)
        .map(str::to_string);
    if !parse_bool(config_value(contents, "SavePassword"), true) {
        return OsuLazerSessionState {
            status: "notStored".to_string(),
            username,
        };
    }

    let token = config_value(contents, "Token").unwrap_or_default();
    if token.is_empty() {
        return OsuLazerSessionState {
            status: "signedOut".to_string(),
            username,
        };
    }

    // lazer serialises access-token | expiry | refresh-token. AimMod only checks
    // that a remembered session exists and is current. Secret material never
    // leaves this function and is never logged or returned to the webview.
    let mut parts = token.split('|');
    let has_access_token = parts.next().is_some_and(|value| !value.is_empty());
    let expires_at = parts.next().and_then(|value| value.parse::<i64>().ok());
    let has_refresh_token = parts.next().is_some_and(|value| !value.is_empty());
    let current = expires_at.is_some_and(|expiry| expiry > now_unix + 30);
    OsuLazerSessionState {
        status: if has_access_token && has_refresh_token {
            if current { "signedIn" } else { "remembered" }
        } else {
            "stale"
        }
        .to_string(),
        username,
    }
}

fn parse_lazer_session_credentials(contents: &str, now_unix: i64) -> OsuLazerSessionCredentials {
    let state = parse_lazer_session(contents, now_unix);
    let access_token = if state.status == "signedIn" {
        config_value(contents, "Token")
            .and_then(|value| value.split('|').next())
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
    } else {
        None
    };
    OsuLazerSessionCredentials {
        state,
        access_token,
    }
}

fn read_lazer_session(path: &Path) -> Option<(SystemTime, OsuLazerSessionCredentials)> {
    let metadata = path.metadata().ok()?;
    if !metadata.is_file() || metadata.len() > MAX_LAZER_CONFIG_BYTES {
        return None;
    }
    let contents = fs::read_to_string(path).ok()?;
    let now_unix = SystemTime::now().duration_since(UNIX_EPOCH).ok()?.as_secs() as i64;
    Some((
        metadata.modified().unwrap_or(SystemTime::UNIX_EPOCH),
        parse_lazer_session_credentials(&contents, now_unix),
    ))
}

fn latest_lazer_session() -> Option<OsuLazerSessionCredentials> {
    crate::osu::lazer_data_candidates()
        .into_iter()
        .filter_map(|root| read_lazer_session(&root.join("game.ini")))
        .max_by_key(|(modified, _)| *modified)
        .map(|(_, session)| session)
}

pub fn get_lazer_session_state() -> OsuLazerSessionState {
    latest_lazer_session()
        .map(|session| session.state)
        .unwrap_or_else(|| OsuLazerSessionState {
            status: "unavailable".to_string(),
            username: None,
        })
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuOfficialUserProfileRequest {
    pub identifier: String,
    pub lookup_key: String,
    pub ruleset: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuOfficialUserGradeCounts {
    pub ssh: u32,
    pub ss: u32,
    pub sh: u32,
    pub s: u32,
    pub a: u32,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuOfficialUserStatistics {
    pub pp: f64,
    pub global_rank: u32,
    pub country_rank: u32,
    pub hit_accuracy: f64,
    pub play_count: u32,
    pub play_time_seconds: u64,
    pub total_score: u64,
    pub ranked_score: u64,
    pub maximum_combo: u32,
    pub level_current: u32,
    pub level_progress: u32,
    pub grade_counts: OsuOfficialUserGradeCounts,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuOfficialUserTeam {
    pub id: u64,
    pub name: String,
    pub short_name: String,
    pub flag_url: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuOfficialUserProfile {
    pub user_id: u64,
    pub username: String,
    pub country_code: String,
    pub avatar_url: String,
    pub cover_url: String,
    pub default_ruleset: String,
    pub is_active: bool,
    pub is_online: bool,
    pub is_supporter: bool,
    pub join_date: String,
    pub last_visit: String,
    pub statistics: Option<OsuOfficialUserStatistics>,
    pub team: Option<OsuOfficialUserTeam>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct OsuOfficialProviderStatus {
    pub configured: bool,
    pub available: bool,
    pub message: String,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OsuOfficialUserProfileResponse {
    pub profile: Option<OsuOfficialUserProfile>,
    pub provider: Option<OsuOfficialProviderStatus>,
}

fn deserialize_u64ish<'de, D>(deserializer: D) -> Result<u64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = serde_json::Value::deserialize(deserializer)?;
    match value {
        serde_json::Value::Number(number) => number
            .as_u64()
            .ok_or_else(|| serde::de::Error::custom("expected an unsigned integer")),
        serde_json::Value::String(value) => value
            .parse::<u64>()
            .map_err(|_| serde::de::Error::custom("expected an unsigned integer string")),
        _ => Err(serde::de::Error::custom("expected an unsigned integer")),
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubOfficialUserTeam {
    #[serde(deserialize_with = "deserialize_u64ish")]
    id: u64,
    name: String,
    short_name: String,
    flag_url: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubOfficialUserStatistics {
    pp: f64,
    global_rank: u32,
    country_rank: u32,
    hit_accuracy: f64,
    play_count: u32,
    #[serde(deserialize_with = "deserialize_u64ish")]
    play_time_seconds: u64,
    #[serde(deserialize_with = "deserialize_u64ish")]
    total_score: u64,
    #[serde(deserialize_with = "deserialize_u64ish")]
    ranked_score: u64,
    maximum_combo: u32,
    level_current: u32,
    level_progress: u32,
    grade_counts: OsuOfficialUserGradeCounts,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubOfficialUserProfile {
    #[serde(deserialize_with = "deserialize_u64ish")]
    user_id: u64,
    username: String,
    country_code: String,
    avatar_url: String,
    cover_url: String,
    default_ruleset: String,
    is_active: bool,
    is_online: bool,
    is_supporter: bool,
    join_date_iso: String,
    last_visit_iso: String,
    statistics: Option<HubOfficialUserStatistics>,
    team: Option<HubOfficialUserTeam>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubProviderStatus {
    configured: bool,
    available: bool,
    message: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct HubOfficialUserProfileResponse {
    profile: Option<HubOfficialUserProfile>,
    provider: Option<HubProviderStatus>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuApiLevel {
    current: u32,
    progress: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuApiGradeCounts {
    ssh: u32,
    ss: u32,
    sh: u32,
    s: u32,
    a: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuApiStatistics {
    pp: Option<f64>,
    global_rank: Option<u32>,
    country_rank: Option<u32>,
    hit_accuracy: f64,
    play_count: u32,
    play_time: u64,
    total_score: u64,
    ranked_score: u64,
    maximum_combo: u32,
    level: OsuApiLevel,
    grade_counts: OsuApiGradeCounts,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuApiTeam {
    id: u64,
    name: String,
    short_name: String,
    flag_url: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
struct OsuApiMe {
    id: u64,
    username: String,
    country_code: String,
    avatar_url: String,
    cover_url: String,
    playmode: String,
    is_active: bool,
    is_online: bool,
    is_supporter: bool,
    join_date: String,
    last_visit: Option<String>,
    statistics: Option<OsuApiStatistics>,
    team: Option<OsuApiTeam>,
}

fn map_lazer_profile(profile: OsuApiMe) -> OsuOfficialUserProfile {
    OsuOfficialUserProfile {
        user_id: profile.id,
        username: profile.username,
        country_code: profile.country_code,
        avatar_url: profile.avatar_url,
        cover_url: profile.cover_url,
        default_ruleset: profile.playmode,
        is_active: profile.is_active,
        is_online: profile.is_online,
        is_supporter: profile.is_supporter,
        join_date: profile.join_date,
        last_visit: profile.last_visit.unwrap_or_default(),
        statistics: profile
            .statistics
            .map(|statistics| OsuOfficialUserStatistics {
                pp: statistics.pp.unwrap_or_default(),
                global_rank: statistics.global_rank.unwrap_or_default(),
                country_rank: statistics.country_rank.unwrap_or_default(),
                hit_accuracy: statistics.hit_accuracy,
                play_count: statistics.play_count,
                play_time_seconds: statistics.play_time,
                total_score: statistics.total_score,
                ranked_score: statistics.ranked_score,
                maximum_combo: statistics.maximum_combo,
                level_current: statistics.level.current,
                level_progress: statistics.level.progress,
                grade_counts: OsuOfficialUserGradeCounts {
                    ssh: statistics.grade_counts.ssh,
                    ss: statistics.grade_counts.ss,
                    sh: statistics.grade_counts.sh,
                    s: statistics.grade_counts.s,
                    a: statistics.grade_counts.a,
                },
            }),
        team: profile.team.map(|team| OsuOfficialUserTeam {
            id: team.id,
            name: team.name,
            short_name: team.short_name,
            flag_url: team.flag_url,
        }),
    }
}

async fn get_profile_from_lazer_session(
    identifier: &str,
) -> Result<Option<OsuOfficialUserProfile>, String> {
    let Some(session) = latest_lazer_session() else {
        return Ok(None);
    };
    let Some(username) = session.state.username.as_deref() else {
        return Ok(None);
    };
    if !username.eq_ignore_ascii_case(identifier) {
        return Ok(None);
    }
    let Some(access_token) = session.access_token.as_deref() else {
        return Ok(None);
    };

    let response = OSU_SESSION_CLIENT
        .get(OSU_ME_URL)
        .bearer_auth(access_token)
        .header("Accept", "application/json")
        .send()
        .await
        .map_err(|error| format!("The active osu!lazer session could not reach osu!: {error}"))?;
    if response.status() == StatusCode::UNAUTHORIZED {
        return Ok(None);
    }
    if !response.status().is_success() {
        return Err(format!(
            "The active osu!lazer session returned HTTP {}.",
            response.status().as_u16()
        ));
    }
    let profile = response
        .json::<OsuApiMe>()
        .await
        .map_err(|error| format!("osu! returned an unreadable active profile: {error}"))?;
    if profile.id == 0 || profile.username.is_empty() {
        return Err("osu! returned an incomplete active profile.".to_string());
    }
    Ok(Some(map_lazer_profile(profile)))
}

fn hub_lookup_key(value: &str) -> Result<&'static str, String> {
    match value {
        "id" => Ok("OFFICIAL_USER_LOOKUP_KEY_ID"),
        "username" => Ok("OFFICIAL_USER_LOOKUP_KEY_USERNAME"),
        _ => Err("Choose an osu! user ID or username lookup.".to_string()),
    }
}

fn hub_ruleset(value: &str) -> Result<&'static str, String> {
    match value {
        "osu" => Ok("RULESET_OSU"),
        "taiko" => Ok("RULESET_TAIKO"),
        "catch" => Ok("RULESET_CATCH"),
        "mania" => Ok("RULESET_MANIA"),
        _ => Err("AimMod does not recognize this osu! ruleset.".to_string()),
    }
}

fn desktop_ruleset(value: String) -> String {
    match value.as_str() {
        "RULESET_OSU" => "osu",
        "RULESET_TAIKO" => "taiko",
        "RULESET_CATCH" => "catch",
        "RULESET_MANIA" => "mania",
        _ => "unknown",
    }
    .to_string()
}

fn map_profile(profile: HubOfficialUserProfile) -> OsuOfficialUserProfile {
    OsuOfficialUserProfile {
        user_id: profile.user_id,
        username: profile.username,
        country_code: profile.country_code,
        avatar_url: profile.avatar_url,
        cover_url: profile.cover_url,
        default_ruleset: desktop_ruleset(profile.default_ruleset),
        is_active: profile.is_active,
        is_online: profile.is_online,
        is_supporter: profile.is_supporter,
        join_date: profile.join_date_iso,
        last_visit: profile.last_visit_iso,
        statistics: profile
            .statistics
            .map(|statistics| OsuOfficialUserStatistics {
                pp: statistics.pp,
                global_rank: statistics.global_rank,
                country_rank: statistics.country_rank,
                hit_accuracy: statistics.hit_accuracy,
                play_count: statistics.play_count,
                play_time_seconds: statistics.play_time_seconds,
                total_score: statistics.total_score,
                ranked_score: statistics.ranked_score,
                maximum_combo: statistics.maximum_combo,
                level_current: statistics.level_current,
                level_progress: statistics.level_progress,
                grade_counts: statistics.grade_counts,
            }),
        team: profile.team.map(|team| OsuOfficialUserTeam {
            id: team.id,
            name: team.name,
            short_name: team.short_name,
            flag_url: team.flag_url,
        }),
    }
}

pub async fn get_official_user_profile(
    app: &AppHandle,
    request: OsuOfficialUserProfileRequest,
) -> Result<OsuOfficialUserProfileResponse, String> {
    let identifier = request.identifier.trim();
    if identifier.is_empty() || identifier.len() > 64 {
        return Err("Enter an osu! user ID or username.".to_string());
    }
    if request.lookup_key == "id"
        && (!identifier.bytes().all(|byte| byte.is_ascii_digit()) || identifier == "0")
    {
        return Err("An osu! user ID must be a positive decimal number.".to_string());
    }

    if let Some(profile) = get_profile_from_lazer_session(identifier).await? {
        return Ok(OsuOfficialUserProfileResponse {
            profile: Some(profile),
            provider: Some(OsuOfficialProviderStatus {
                configured: true,
                available: true,
                message: "Following the active local osu!lazer session.".to_string(),
            }),
        });
    }

    let payload = serde_json::json!({
        "identifier": identifier,
        "lookupKey": hub_lookup_key(&request.lookup_key)?,
        "ruleset": hub_ruleset(&request.ruleset)?,
    });
    let response: HubOfficialUserProfileResponse =
        crate::hub_api::post_connect_json(app, GET_OFFICIAL_USER_PROFILE_PATH, &payload)
            .await
            .map_err(|error| {
                format!("Could not load the official osu! profile from AimMod Hub: {error}")
            })?;
    Ok(OsuOfficialUserProfileResponse {
        profile: response.profile.map(map_profile),
        provider: response.provider.map(|provider| OsuOfficialProviderStatus {
            configured: provider.configured,
            available: provider.available,
            message: provider.message,
        }),
    })
}

#[cfg(test)]
mod tests {
    use super::{
        HubOfficialUserProfileResponse, OsuApiMe, hub_lookup_key, hub_ruleset, map_lazer_profile,
        map_profile, parse_lazer_session, parse_lazer_session_credentials,
    };

    #[test]
    fn reports_lazer_session_without_exposing_token_material() {
        let signed_in = parse_lazer_session(
            "Username = verycrunchy\nSavePassword = True\nToken = access|2000000000|refresh\n",
            1_900_000_000,
        );
        assert_eq!(signed_in.status, "signedIn");
        assert_eq!(signed_in.username.as_deref(), Some("verycrunchy"));

        let remembered = parse_lazer_session(
            "Username = verycrunchy\nSavePassword = True\nToken = expired|1800000000|refresh\n",
            1_900_000_000,
        );
        assert_eq!(remembered.status, "remembered");

        let signed_out = parse_lazer_session(
            "Username = remembered\nSavePassword = True\nToken = \n",
            1_900_000_000,
        );
        assert_eq!(signed_out.status, "signedOut");
        assert_eq!(signed_out.username.as_deref(), Some("remembered"));

        let not_stored = parse_lazer_session(
            "Username = local\nSavePassword = False\nToken = access|2000000000|refresh\n",
            1_900_000_000,
        );
        assert_eq!(not_stored.status, "notStored");

        let credentials = parse_lazer_session_credentials(
            "Username = verycrunchy\nSavePassword = True\nToken = access|2000000000|refresh\n",
            1_900_000_000,
        );
        assert_eq!(credentials.state.status, "signedIn");
        assert_eq!(credentials.access_token.as_deref(), Some("access"));
        let public_state = serde_json::to_string(&credentials.state).unwrap();
        assert!(!public_state.contains("access"));
        assert!(!public_state.contains("refresh"));
    }

    #[test]
    fn maps_nullable_lazer_profile_fields() {
        let response: OsuApiMe = serde_json::from_value(serde_json::json!({
            "id": 25200488,
            "username": "veryCrunchy",
            "country_code": "NL",
            "avatar_url": "https://a.ppy.sh/25200488",
            "cover_url": "https://assets.ppy.sh/user-profile-covers/25200488/test.jpeg",
            "playmode": "osu",
            "last_visit": null,
            "statistics": {
                "pp": null,
                "global_rank": null,
                "country_rank": null,
                "hit_accuracy": 98.5,
                "play_count": 42,
                "play_time": 3600,
                "total_score": 100,
                "ranked_score": 90,
                "maximum_combo": 123,
                "level": {"current": 10, "progress": 50},
                "grade_counts": {"ssh": 1, "ss": 2, "sh": 3, "s": 4, "a": 5}
            }
        }))
        .unwrap();
        let profile = map_lazer_profile(response);
        assert_eq!(profile.user_id, 25_200_488);
        assert_eq!(profile.last_visit, "");
        assert_eq!(profile.statistics.unwrap().global_rank, 0);
    }

    #[test]
    fn maps_official_profile_connect_json_without_fabricating_fields() {
        let response: HubOfficialUserProfileResponse = serde_json::from_value(serde_json::json!({
            "profile": {
                "userId": "25200488",
                "username": "verycrunchy",
                "countryCode": "NL",
                "avatarUrl": "https://a.ppy.sh/25200488",
                "coverUrl": "https://assets.ppy.sh/user-profile-covers/25200488/test.jpeg",
                "defaultRuleset": "RULESET_OSU",
                "statistics": {"pp": 1234.5, "playTimeSeconds": "3600", "totalScore": "42", "rankedScore": "40"}
            },
            "provider": {"configured": true, "available": true}
        })).unwrap();
        let profile = map_profile(response.profile.unwrap());
        assert_eq!(profile.user_id, 25_200_488);
        assert_eq!(profile.username, "verycrunchy");
        assert_eq!(profile.default_ruleset, "osu");
        assert_eq!(profile.statistics.unwrap().play_time_seconds, 3600);
    }

    #[test]
    fn validates_profile_lookup_enums() {
        assert_eq!(hub_lookup_key("id").unwrap(), "OFFICIAL_USER_LOOKUP_KEY_ID");
        assert_eq!(
            hub_lookup_key("username").unwrap(),
            "OFFICIAL_USER_LOOKUP_KEY_USERNAME"
        );
        assert_eq!(hub_ruleset("mania").unwrap(), "RULESET_MANIA");
        assert!(hub_lookup_key("email").is_err());
    }
}
