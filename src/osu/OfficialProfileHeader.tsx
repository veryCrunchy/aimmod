import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { invoke, isTauri } from "@tauri-apps/api/core";
import { ArrowClockwise, CheckCircle, PencilSimple, User, X } from "@phosphor-icons/react";

interface OfficialStatistics {
  pp: number | null;
  globalRank: number | null;
  countryRank: number | null;
  hitAccuracy: number | null;
  playCount: number | null;
  playTimeSeconds: number | null;
  totalScore: number | null;
  rankedScore: number | null;
  maximumCombo: number | null;
  levelCurrent: number | null;
  levelProgress: number | null;
  gradeCounts?: { ssh: number; ss: number; sh: number; s: number; a: number } | null;
}

interface OfficialTeam {
  id: number | string;
  name: string;
  shortName: string;
  flagUrl?: string | null;
}

interface OfficialProfile {
  userId: number;
  username: string;
  countryCode: string;
  avatarUrl?: string | null;
  coverUrl?: string | null;
  defaultRuleset: string;
  isActive: boolean;
  isOnline: boolean;
  isSupporter: boolean;
  joinDate?: string | null;
  lastVisit?: string | null;
  statistics?: OfficialStatistics | null;
  team?: OfficialTeam | null;
}

interface OfficialProfileResponse {
  profile: OfficialProfile | null;
  provider?: { available?: boolean; message?: string | null } | null;
}

interface LocalPlayer {
  playerId: number;
  playerName: string;
  lastPlayedAt: string;
}

interface LazerSessionState {
  status: "signedIn" | "remembered" | "signedOut" | "notStored" | "stale" | "unavailable";
  username: string | null;
}

type ProfileSource = "lazer" | "local" | "manual" | null;
const PROFILE_OVERRIDE_KEY = "aimmod.osu.manualProfileOverride";

function number(value: number | null | undefined, suffix = "") {
  return value === null || value === undefined ? "Not supplied" : `${value.toLocaleString()}${suffix}`;
}

function identifierFrom(value: unknown) {
  if (typeof value === "string") return value;
  if (value && typeof value === "object" && "identifier" in value) {
    const identifier = (value as { identifier?: unknown }).identifier;
    return typeof identifier === "string" ? identifier : "";
  }
  return "";
}

export function OfficialProfileHeader() {
  const desktop = isTauri();
  const [identifier, setIdentifier] = useState("");
  const [draft, setDraft] = useState("");
  const [profile, setProfile] = useState<OfficialProfile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [editing, setEditing] = useState(false);
  const [localPlayer, setLocalPlayer] = useState<LocalPlayer | null>(null);
  const [lazerSession, setLazerSession] = useState<LazerSessionState | null>(null);
  const [source, setSource] = useState<ProfileSource>(null);
  const identifierRef = useRef("");
  const manualOverrideRef = useRef(window.localStorage.getItem(PROFILE_OVERRIDE_KEY) === "true");
  const profileRequestRef = useRef(0);
  const identitySyncRef = useRef(0);
  const lastSessionKeyRef = useRef("");
  const localDetectionDoneRef = useRef(false);

  const loadProfile = useCallback(async (nextIdentifier: string) => {
    if (!desktop || !nextIdentifier) return;
    const requestId = ++profileRequestRef.current;
    setLoading(true);
    setError(null);
    try {
      const response = await invoke<OfficialProfileResponse>("get_osu_official_user_profile", {
        request: {
          identifier: nextIdentifier,
          lookupKey: /^\d+$/.test(nextIdentifier) ? "id" : "username",
          ruleset: "osu",
        },
      });
      if (requestId !== profileRequestRef.current) return;
      setProfile(response.profile);
      if (!response.profile) setError(response.provider?.message || "The official osu! provider did not return a profile.");
    } catch (reason) {
      if (requestId !== profileRequestRef.current) return;
      setProfile(null);
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      if (requestId === profileRequestRef.current) setLoading(false);
    }
  }, [desktop]);

  const useIdentifier = useCallback((nextIdentifier: string, nextSource: ProfileSource) => {
    const clean = nextIdentifier.trim();
    if (!clean) return;
    const changed = identifierRef.current !== clean;
    identifierRef.current = clean;
    setIdentifier(clean);
    setDraft(clean);
    setSource(nextSource);
    if (changed) void loadProfile(clean);
  }, [loadProfile]);

  useEffect(() => {
    if (!desktop) return;
    let cancelled = false;
    let persistedIdentifier = "";

    const detectLocalPlayer = async () => {
      if (localDetectionDoneRef.current) return;
      localDetectionDoneRef.current = true;
      try {
        const detected = await invoke<LocalPlayer | null>("get_osu_local_player");
        if (cancelled) return;
        setLocalPlayer(detected);
        if (detected && !manualOverrideRef.current) {
          useIdentifier(String(detected.playerId), "local");
        } else if (!identifierRef.current && persistedIdentifier) {
          useIdentifier(persistedIdentifier, "manual");
        }
      } catch (reason) {
        if (!cancelled && !identifierRef.current) {
          if (persistedIdentifier) useIdentifier(persistedIdentifier, "manual");
          else setError(reason instanceof Error ? reason.message : String(reason));
        }
      }
    };

    const syncIdentity = async () => {
      if (manualOverrideRef.current) return;
      const syncId = ++identitySyncRef.current;
      try {
        const session = await invoke<LazerSessionState>("get_osu_lazer_session_state");
        if (cancelled || syncId !== identitySyncRef.current || manualOverrideRef.current) return;
        setLazerSession(session);
        const sessionKey = `${session.status}:${session.username || ""}`;
        const sessionChanged = lastSessionKeyRef.current !== sessionKey;
        lastSessionKeyRef.current = sessionKey;
        if ((session.status === "signedIn" || session.status === "remembered") && session.username) {
          useIdentifier(session.username, "lazer");
          return;
        }
        if (session.status === "signedOut") {
          profileRequestRef.current += 1;
          identifierRef.current = "";
          setIdentifier("");
          setDraft("");
          setProfile(null);
          setSource(null);
          setError(null);
          setLoading(false);
          return;
        }
        if (!sessionChanged && localDetectionDoneRef.current) return;
        localDetectionDoneRef.current = false;
      } catch {
        if (cancelled || syncId !== identitySyncRef.current || manualOverrideRef.current) return;
        setLazerSession({ status: "unavailable", username: null });
      }
      await detectLocalPlayer();
    };

    void invoke<unknown>("get_osu_user_identifier").then((value) => {
      if (cancelled) return;
      persistedIdentifier = identifierFrom(value).trim();
      if (manualOverrideRef.current && persistedIdentifier) {
        useIdentifier(persistedIdentifier, "manual");
        return;
      }
      void syncIdentity();
    }).catch(() => {
      if (!cancelled) void syncIdentity();
    });

    const interval = window.setInterval(() => {
      if (document.visibilityState === "visible" && !manualOverrideRef.current) void syncIdentity();
    }, 10_000);
    const onFocus = () => {
      if (!manualOverrideRef.current) void syncIdentity();
    };
    window.addEventListener("focus", onFocus);
    return () => {
      cancelled = true;
      window.clearInterval(interval);
      window.removeEventListener("focus", onFocus);
    };
  }, [desktop, useIdentifier]);

  const save = useCallback(async (event: FormEvent) => {
    event.preventDefault();
    const nextIdentifier = draft.trim();
    if (!desktop || !nextIdentifier) return;
    setLoading(true);
    setError(null);
    try {
      await invoke("set_osu_user_identifier", { identifier: nextIdentifier });
      manualOverrideRef.current = true;
      identitySyncRef.current += 1;
      window.localStorage.setItem(PROFILE_OVERRIDE_KEY, "true");
      identifierRef.current = nextIdentifier;
      setIdentifier(nextIdentifier);
      setSource("manual");
      await loadProfile(nextIdentifier);
      setEditing(false);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
      setLoading(false);
    }
  }, [desktop, draft, loadProfile]);

  const followLocalPlayer = useCallback(() => {
    manualOverrideRef.current = false;
    window.localStorage.removeItem(PROFILE_OVERRIDE_KEY);
    if ((lazerSession?.status === "signedIn" || lazerSession?.status === "remembered") && lazerSession.username) {
      useIdentifier(lazerSession.username, "lazer");
    } else if (localPlayer) {
      useIdentifier(String(localPlayer.playerId), "local");
    }
    setEditing(false);
  }, [lazerSession, localPlayer, useIdentifier]);

  if (!desktop) return null;

  if (editing) return <section className="osu-profile-strip configure compact">
    <User size={18} />
    <div className="osu-profile-config-copy"><strong>Choose a different osu! profile</strong><span>{error || "AimMod follows lazer's remembered player automatically when that session is stored."}</span></div>
    <form onSubmit={(event) => void save(event)}><input value={draft} onChange={(event) => setDraft(event.target.value)} placeholder="Username or user ID" aria-label="osu! username or user ID" autoFocus /><button type="submit" disabled={loading || !draft.trim()}>{loading ? "Loading" : "Use profile"}</button>{(lazerSession?.status === "signedIn" || lazerSession?.status === "remembered" || localPlayer) && <button type="button" className="osu-profile-follow" onClick={followLocalPlayer}>{lazerSession?.status === "signedIn" || lazerSession?.status === "remembered" ? "Follow lazer" : "Follow local player"}</button>}<button type="button" className="osu-profile-cancel" onClick={() => { setEditing(false); setDraft(identifier); }} aria-label="Close profile setup"><X size={15} /></button></form>
  </section>;

  if (!profile) return <section className="osu-profile-optional">
    <User size={15} />
    <span>{lazerSession?.status === "signedOut" ? "osu!lazer is signed out" : source === "lazer" ? `${lazerSession?.username || identifier} followed from lazer${error ? "; online profile unavailable" : ""}` : source === "local" && localPlayer ? `${localPlayer.playerName} detected from local plays${error ? "; online profile unavailable" : ""}` : identifier ? `${identifier} profile unavailable` : "No osu! player found yet"}</span>
    <button type="button" onClick={() => setEditing(true)}>{identifier ? "Change" : "Choose profile"}</button>
    {identifier && <button type="button" className="icon" onClick={() => void loadProfile(identifier)} disabled={loading} aria-label="Retry official osu! profile" title={error || undefined}><ArrowClockwise size={14} /></button>}
  </section>;

  const stats = profile.statistics;
  return <section className="osu-profile-strip loaded">
    {profile.coverUrl && <img className="osu-profile-cover" src={profile.coverUrl} alt="" referrerPolicy="no-referrer" />}
    <div className="osu-profile-shade" />
    {profile.avatarUrl ? <img className="osu-profile-avatar" src={profile.avatarUrl} alt={`${profile.username} avatar`} referrerPolicy="no-referrer" /> : <User className="osu-profile-avatar-fallback" size={25} />}
    <div className="osu-profile-identity"><span>{source === "lazer" ? "Following osu!lazer" : source === "local" ? "Following local player" : profile.countryCode || "osu! profile"}</span><strong>{profile.username}</strong><small>{profile.countryCode ? `${profile.countryCode} · ` : ""}osu! user {profile.userId}{profile.team ? ` · ${profile.team.name}` : ""}</small></div>
    <div className="osu-profile-stat"><span>Performance</span><strong>{number(stats?.pp, "pp")}</strong></div>
    <div className="osu-profile-stat"><span>Global rank</span><strong>{stats?.globalRank ? `#${stats.globalRank.toLocaleString()}` : "Not supplied"}</strong></div>
    <div className="osu-profile-stat"><span>Accuracy</span><strong>{stats?.hitAccuracy === null || stats?.hitAccuracy === undefined ? "Not supplied" : `${stats.hitAccuracy.toFixed(2)}%`}</strong></div>
    <div className="osu-profile-stat"><span>Play count</span><strong>{number(stats?.playCount)}</strong></div>
    <div className="osu-profile-state">{profile.isOnline ? <><CheckCircle size={14} weight="fill" />Online</> : profile.lastVisit ? `Last seen ${profile.lastVisit}` : "Offline"}</div>
    <button type="button" className="osu-profile-refresh" onClick={() => void loadProfile(identifier)} disabled={loading} aria-label="Refresh official osu! profile"><ArrowClockwise size={16} /></button>
    <button type="button" className="osu-profile-refresh" onClick={() => setEditing(true)} aria-label="Change osu! account"><PencilSimple size={16} /></button>
  </section>;
}
