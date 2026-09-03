import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type FormEvent } from "react";
import { convertFileSrc, invoke, isTauri } from "@tauri-apps/api/core";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { open } from "@tauri-apps/plugin-dialog";
import {
  ArrowClockwise,
  Brain,
  CaretDown,
  ChartLineUp,
  Check,
  CheckCircle,
  DownloadSimple,
  FolderOpen,
  Funnel,
  GameController,
  Heart,
  ListBullets,
  MagnifyingGlass,
  PaintBrush,
  Play,
  Pause,
  Star,
  Target,
  Trophy,
  UploadSimple,
  User,
  WarningCircle,
  X,
} from "@phosphor-icons/react";
import { type OsuSkillset, type WorkspaceBeatmap, type WorkspaceReplay } from "./models";
import { mergeInstalledBeatmap } from "./beatmapMerge";
import { filtersForBeatmapProvider, type BeatmapSearchFilters } from "./beatmapSearchFilters";
import { OfficialProfileHeader } from "./OfficialProfileHeader";
import { OsuCoachingPanel } from "./OsuCoachingPanel";
import { OsuStatisticsDashboard } from "./OsuStatisticsDashboard";
import { ReplayAnalyticsPanel } from "./ReplayAnalyticsPanel";
import { SkinsWorkspace } from "./SkinsWorkspace";
import { mediaDiagnosticState, privateSourceId, recordOsuDiagnostic } from "./osuDiagnostics";
import "./OsuWorkspace.css";

type OsuTab = "beatmaps" | "skins" | "replays" | "statistics" | "leaderboards" | "coaching";
type ProviderChoice = "all" | "official" | "osuCollector";
type LibraryChoice = "all" | "installed" | "missing";

interface OsuLazerInstallation { dataPath: string; hasDatabase: boolean; hasFileStore: boolean }
interface OsuLazerStatus { detected: boolean; installations: OsuLazerInstallation[]; supportedImportExtensions: string[] }
interface OsuBeatmapProvider { id: string; name: string; status: string; capabilities: string[]; message: string }
interface OsuBeatmapSearchItem {
  provider: string; sourceId?: string; beatmapsetId: string; beatmapId: string | null; artist: string; title: string; creator: string;
  difficultyName: string | null; mode: string | null; starRating: number | null; bpm: number | null;
  lengthSeconds: number | null; status: string | null; coverImageUrl: string | null;
  playCount: number | null; favouriteCount: number | null;
  approachRate: number | null; circleSize: number | null; overallDifficulty: number | null; hpDrain: number | null;
}
interface OsuBeatmapSearchResponse { provider: string; items: OsuBeatmapSearchItem[]; total: number | null; nextOffset: number | null; error: string | null }
interface OsuBeatmapDownloadResult { provider: string; beatmapsetId: string; status: string; message: string }
interface OsuImportResult { path: string; fileName: string; kind: "beatmap" | "replay" | "unknown"; status: "opened" | "rejected" | "error"; message: string }
interface OsuReplayInspection {
  path: string; fileName: string; mode: string | null; gameVersion: number | null; beatmapHash: string | null;
  playerName: string | null; replayHash: string | null; counts: { count300: number; count100: number; count50: number; countGeki: number; countKatu: number; countMiss: number } | null;
  score: number | null; maxCombo: number | null; perfect: boolean | null; mods: { bitmask: number; names: string[] } | null;
  playedAt: string | null; parseError: string | null;
}
interface OsuLocalBeatmap {
  provider: string; beatmapsetId: string; beatmapId: string; artist: string; title: string; creator: string;
  difficultyName: string; mode: string;
  starRating: number | null; bpm: number | null; lengthSeconds: number | null; status: string | null;
  coverImageUrl: string | null; audioPath: string | null; previewTimeMs: number; userOffsetMs: number;
  skillsets: string[]; localState: string; plays: number | null; favorites: number | null;
  pp95: number | null; accuracy: number | null;
  circleSize: number | null; approachRate: number | null; overallDifficulty: number | null; hpDrain: number | null;
  contentHash: string; md5Hash: string;
}
interface OsuLocalReplay {
  path: string; fileName: string; storageSource: "export" | "lazerStore"; mode: string; playerName: string; score: number;
  maxCombo: number; perfect: boolean; mods: string[]; playedAt: string;
  counts: { count300: number; count100: number; count50: number; countMiss: number };
  beatmapHash: string; beatmapTitle: string | null; difficultyName: string | null; coverImageUrl: string | null;
}
interface OsuLocalLibraryResponse<T> { items: T[]; error: string | null }

const TABS: Array<{ id: OsuTab; label: string; icon: typeof Target }> = [
  { id: "beatmaps", label: "Beatmaps", icon: Target },
  { id: "skins", label: "Skins", icon: PaintBrush },
  { id: "replays", label: "Replays", icon: Play },
  { id: "statistics", label: "Statistics", icon: ChartLineUp },
  { id: "leaderboards", label: "Leaderboards", icon: Trophy },
  { id: "coaching", label: "Coaching", icon: Brain },
];
const SKILLSETS: Array<"Any" | OsuSkillset> = ["Any", "Aim", "Speed", "Reading", "Consistency", "Finger control"];
const TAB_KEY = "aimmod.osu.activeTab";
const PREVIEW_AUDIO_UNAVAILABLE_MESSAGE = "Install this beatmap in osu!lazer to play its locally stored audio.";
const PREVIEW_AUDIO_ERROR_MESSAGE = "AimMod could not play this beatmap's local audio file.";

function readTab(): OsuTab {
  const value = window.localStorage.getItem(TAB_KEY);
  return value === "skins" || value === "replays" || value === "statistics" || value === "leaderboards" || value === "coaching" ? value : "beatmaps";
}

function messageOf(reason: unknown) { return reason instanceof Error ? reason.message : String(reason); }
function localAssetUrl(value: string | null) {
  if (!value || !isTauri() || !value.startsWith("/")) return value;
  return convertFileSrc(value);
}
function localMediaUrl(value: string | null) {
  if (!value || !isTauri()) return null;
  const parts = value.split(/[\\/]/);
  const hash = parts[parts.length - 1]?.toLocaleLowerCase() ?? "";
  return /^[0-9a-f]{64}$/.test(hash) ? convertFileSrc(hash, "aimmod-media") : null;
}
function formatLength(seconds: number | null) { return seconds === null ? "Not supplied" : `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`; }
function formatBpm(value: number | null) {
  if (value === null || !Number.isFinite(value)) return "BPM N/A";
  return `${Number.isInteger(value) ? value.toFixed(0) : value.toFixed(1)} BPM`;
}

const DIFFICULTY_COLOUR_STOPS = [
  { stars: 0.1, colour: "#4290fb" },
  { stars: 1.25, colour: "#4fc0ff" },
  { stars: 2, colour: "#4fffd5" },
  { stars: 2.5, colour: "#7cff4f" },
  { stars: 3.3, colour: "#f6f05c" },
  { stars: 4.2, colour: "#ff8068" },
  { stars: 4.9, colour: "#ff4e6f" },
  { stars: 5.8, colour: "#c645b8" },
  { stars: 6.7, colour: "#6563de" },
  { stars: 7.7, colour: "#18158e" },
  { stars: 9, colour: "#000000" },
] as const;

function mixHex(left: string, right: string, amount: number) {
  const channel = (hex: string, offset: number) => Number.parseInt(hex.slice(offset, offset + 2), 16);
  const mixed = [1, 3, 5].map((offset) => Math.round(channel(left, offset) + (channel(right, offset) - channel(left, offset)) * amount));
  return `#${mixed.map((value) => value.toString(16).padStart(2, "0")).join("")}`;
}

function difficultyColour(stars: number | null) {
  if (stars === null || !Number.isFinite(stars)) return "#7e8294";
  if (stars <= DIFFICULTY_COLOUR_STOPS[0].stars) return DIFFICULTY_COLOUR_STOPS[0].colour;
  for (let index = 1; index < DIFFICULTY_COLOUR_STOPS.length; index += 1) {
    const high = DIFFICULTY_COLOUR_STOPS[index];
    if (stars > high.stars) continue;
    const low = DIFFICULTY_COLOUR_STOPS[index - 1];
    return mixHex(low.colour, high.colour, (stars - low.stars) / (high.stars - low.stars));
  }
  return DIFFICULTY_COLOUR_STOPS[DIFFICULTY_COLOUR_STOPS.length - 1].colour;
}

function difficultyStyle(stars: number | null) {
  return { "--difficulty-colour": difficultyColour(stars) } as CSSProperties;
}
function accuracyOf(replay: WorkspaceReplay) {
  if (!replay.counts) return null;
  const { count300, count100, count50, countMiss } = replay.counts;
  const total = count300 + count100 + count50 + countMiss;
  return total ? ((count300 * 300 + count100 * 100 + count50 * 50) / (total * 300)) * 100 : null;
}

function mapSearchItem(item: OsuBeatmapSearchItem, index: number): WorkspaceBeatmap {
  return {
    provider: item.provider,
    sourceId: item.sourceId,
    beatmapsetId: item.beatmapsetId,
    beatmapId: item.beatmapId ?? `${item.beatmapsetId}-${index}`,
    artist: item.artist,
    title: item.title,
    creator: item.creator,
    difficultyName: item.difficultyName ?? "Beatmap set",
    mode: item.mode ?? "osu",
    starRating: item.starRating,
    bpm: item.bpm,
    lengthSeconds: item.lengthSeconds,
    status: item.status ?? "Not supplied",
    coverImageUrl: item.coverImageUrl || null,
    audioUrl: null,
    audioPreviewTimeMs: null,
    skillsets: [],
    localState: "Not installed",
    plays: item.playCount,
    favorites: item.favouriteCount,
    pp95: null,
    accuracy: null,
    circleSize: item.circleSize,
    approachRate: item.approachRate,
    overallDifficulty: item.overallDifficulty,
    hpDrain: item.hpDrain,
  };
}

function mapLocalBeatmap(item: OsuLocalBeatmap, index: number): WorkspaceBeatmap {
  const knownSkillsets = new Set<OsuSkillset>(["Aim", "Speed", "Reading", "Consistency", "Finger control"]);
  return {
    provider: item.provider || "local",
    beatmapsetId: item.beatmapsetId || item.contentHash,
    beatmapId: item.beatmapId || `${item.contentHash}:${index}`,
    artist: item.artist || "Artist not supplied",
    title: item.title || item.contentHash,
    creator: item.creator || "Mapper not supplied",
    difficultyName: item.difficultyName || "Difficulty not supplied",
    mode: item.mode || "osu",
    starRating: item.starRating,
    bpm: item.bpm,
    lengthSeconds: item.lengthSeconds,
    status: item.status || "Local",
    coverImageUrl: localAssetUrl(item.coverImageUrl),
    audioUrl: localMediaUrl(item.audioPath),
    audioPreviewTimeMs: item.previewTimeMs >= 0 ? item.previewTimeMs : null,
    skillsets: item.skillsets.filter((value): value is OsuSkillset => knownSkillsets.has(value as OsuSkillset)),
    localState: "Installed",
    plays: item.plays,
    favorites: item.favorites,
    pp95: item.pp95,
    accuracy: item.accuracy,
    circleSize: item.circleSize,
    approachRate: item.approachRate,
    overallDifficulty: item.overallDifficulty,
    hpDrain: item.hpDrain,
  };
}

function replayFromLocal(item: OsuLocalReplay): WorkspaceReplay {
  return {
    path: item.path,
    fileName: item.fileName,
    storageSource: item.storageSource,
    mode: item.mode,
    playerName: item.playerName,
    score: item.score,
    maxCombo: item.maxCombo,
    perfect: item.perfect,
    mods: item.mods,
    playedAt: item.playedAt,
    counts: item.counts,
    beatmapTitle: item.beatmapTitle,
    difficultyName: item.difficultyName,
    coverImageUrl: item.coverImageUrl,
    parseError: null,
  };
}

function replayFromInspection(item: OsuReplayInspection): WorkspaceReplay {
  return {
    path: item.path,
    fileName: item.fileName,
    storageSource: null,
    mode: item.mode,
    playerName: item.playerName,
    score: item.score,
    maxCombo: item.maxCombo,
    perfect: item.perfect,
    mods: item.mods?.names ?? [],
    playedAt: item.playedAt,
    counts: item.counts ? {
      count300: item.counts?.count300 ?? 0,
      count100: item.counts?.count100 ?? 0,
      count50: item.counts?.count50 ?? 0,
      countMiss: item.counts?.countMiss ?? 0,
    } : null,
    beatmapTitle: item.fileName.replace(/\.osr$/i, "") || null,
    difficultyName: item.mode,
    coverImageUrl: null,
    parseError: item.parseError,
  };
}

function Metric({ label, value, accent }: { label: string; value: string; accent?: boolean }) {
  return <div className="osu-metric"><span>{label}</span><strong className={accent ? "accent" : ""}>{value}</strong></div>;
}

function RangeFilter({ label, min, max, step, low, high, format, onLow, onHigh }: {
  label: string; min: number; max: number; step: number; low: number; high: number;
  format: (value: number) => string; onLow: (value: number) => void; onHigh: (value: number) => void;
}) {
  const start = ((low - min) / (max - min)) * 100;
  const end = ((high - min) / (max - min)) * 100;
  return <div className="osu-filter-field osu-paired-range">
    <div className="osu-range-heading"><span>{label}</span><div><output>{format(low)}</output><i>to</i><output>{format(high)}</output></div></div>
    <div className="osu-dual-range" style={{ "--range-start": `${start}%`, "--range-end": `${end}%` } as CSSProperties}>
      <div className="osu-dual-track" aria-hidden="true"><span /></div>
      <input type="range" min={min} max={max} step={step} value={low} onChange={(event) => onLow(Math.min(Number(event.target.value), high - step))} onKeyDown={(event) => { if (event.key === "ArrowRight" || event.key === "ArrowUp") { event.preventDefault(); onLow(Math.min(low + step, high - step)); } if (event.key === "ArrowLeft" || event.key === "ArrowDown") { event.preventDefault(); onLow(Math.max(low - step, min)); } }} aria-label={`${label} minimum`} />
      <input type="range" min={min} max={max} step={step} value={high} onChange={(event) => onHigh(Math.max(Number(event.target.value), low + step))} onKeyDown={(event) => { if (event.key === "ArrowRight" || event.key === "ArrowUp") { event.preventDefault(); onHigh(Math.min(high + step, max)); } if (event.key === "ArrowLeft" || event.key === "ArrowDown") { event.preventDefault(); onHigh(Math.max(high - step, low + step)); } }} aria-label={`${label} maximum`} />
    </div>
  </div>;
}

function BeatmapFilters({
  provider, setProvider, skillset, setSkillset, library, setLibrary, rankedStatus, setRankedStatus,
  minStars, setMinStars, maxStars, setMaxStars, minBpm, setMinBpm, maxBpm, setMaxBpm,
  minLength, setMinLength, maxLength, setMaxLength, minAr, setMinAr, maxAr, setMaxAr,
  minCs, setMinCs, maxCs, setMaxCs, minOd, setMinOd, maxOd, setMaxOd,
  onlyUnplayed, setOnlyUnplayed, onReset,
}: {
  provider: ProviderChoice; setProvider: (value: ProviderChoice) => void;
  skillset: "Any" | OsuSkillset; setSkillset: (value: "Any" | OsuSkillset) => void;
  library: LibraryChoice; setLibrary: (value: LibraryChoice) => void;
  rankedStatus: "any" | "ranked" | "loved"; setRankedStatus: (value: "any" | "ranked" | "loved") => void;
  minStars: number; setMinStars: (value: number) => void; maxStars: number; setMaxStars: (value: number) => void;
  minBpm: number; setMinBpm: (value: number) => void; maxBpm: number; setMaxBpm: (value: number) => void;
  minLength: number; setMinLength: (value: number) => void; maxLength: number; setMaxLength: (value: number) => void;
  minAr: number; setMinAr: (value: number) => void; maxAr: number; setMaxAr: (value: number) => void;
  minCs: number; setMinCs: (value: number) => void; maxCs: number; setMaxCs: (value: number) => void;
  minOd: number; setMinOd: (value: number) => void; maxOd: number; setMaxOd: (value: number) => void;
  onlyUnplayed: boolean; setOnlyUnplayed: (value: boolean) => void;
  onReset: () => void;
}) {
  return (
    <aside className="osu-filter-panel" aria-label="Beatmap filters">
      <div className="osu-panel-title"><Funnel size={18} /><div><strong>Filters</strong><span>Shape the library</span></div><button type="button" onClick={onReset}>Clear</button></div>
      <label className="osu-filter-field"><span>Provider</span><div className="osu-select-wrap"><select value={provider} onChange={(event) => setProvider(event.target.value as ProviderChoice)}><option value="all">All providers</option><option value="official">osu!</option><option value="osuCollector">osu!Collector</option></select><CaretDown size={13} /></div></label>
      <RangeFilter label="Star rating" min={0} max={10} step={0.1} low={minStars} high={maxStars} format={(value) => `${value.toFixed(1)}★`} onLow={setMinStars} onHigh={setMaxStars} />
      <fieldset>
        <legend>Ranked status</legend>
        {([['any', 'Any'], ['ranked', 'Ranked'], ['loved', 'Loved']] as const).map(([value, label]) => (
          <label className="osu-radio inline" key={value}><input type="radio" name="ranked-status" checked={rankedStatus === value} onChange={() => setRankedStatus(value)} /><span />{label}</label>
        ))}
      </fieldset>
      <RangeFilter label="BPM" min={0} max={500} step={5} low={minBpm} high={maxBpm} format={(value) => `${value}`} onLow={setMinBpm} onHigh={setMaxBpm} />
      <RangeFilter label="Length" min={0} max={900} step={15} low={minLength} high={maxLength} format={(value) => formatLength(value)} onLow={setMinLength} onHigh={setMaxLength} />
      <label className="osu-filter-field"><span>Skillset</span><div className="osu-select-wrap"><select value={skillset} onChange={(event) => setSkillset(event.target.value as typeof skillset)}>{SKILLSETS.map((item) => <option key={item}>{item}</option>)}</select><CaretDown size={13} /></div></label>
      <label className="osu-filter-field"><span>Local library</span><div className="osu-select-wrap"><select value={library} onChange={(event) => setLibrary(event.target.value as LibraryChoice)}><option value="all">All beatmaps</option><option value="installed">Installed</option><option value="missing">Not installed</option></select><CaretDown size={13} /></div></label>
      <label className="osu-check"><input type="checkbox" checked={onlyUnplayed} onChange={(event) => setOnlyUnplayed(event.target.checked)} /><span><Check size={11} weight="bold" /></span><strong>Only unplayed</strong></label>
      <details className="osu-advanced-filters"><summary>Advanced difficulty <CaretDown size={13} /></summary><div>
        <RangeFilter label="Approach rate" min={0} max={11} step={0.1} low={minAr} high={maxAr} format={(value) => value.toFixed(1)} onLow={setMinAr} onHigh={setMaxAr} />
        <RangeFilter label="Circle size" min={0} max={10} step={0.1} low={minCs} high={maxCs} format={(value) => value.toFixed(1)} onLow={setMinCs} onHigh={setMaxCs} />
        <RangeFilter label="Overall difficulty" min={0} max={11} step={0.1} low={minOd} high={maxOd} format={(value) => value.toFixed(1)} onLow={setMinOd} onHigh={setMaxOd} />
      </div></details>
    </aside>
  );
}

const BEATMAP_SET_RENDER_BATCH = 48;

function BeatmapRows({ maps, selectedId, queuedIds, playingAudioUrl, emptyTitle, emptyCopy, onSelect, onQueue, onToggleAudio }: {
  maps: WorkspaceBeatmap[]; selectedId: string; queuedIds: Set<string>;
  playingAudioUrl: string | null;
  emptyTitle: string; emptyCopy: string;
  onSelect: (id: string) => void; onQueue: (map: WorkspaceBeatmap) => void; onToggleAudio: (map: WorkspaceBeatmap) => void;
}) {
  const groupedSets = useMemo(() => {
    const sets = new Map<string, WorkspaceBeatmap[]>();
    for (const map of maps) {
      const key = map.beatmapsetId || `${map.provider}:${map.artist}:${map.title}:${map.creator}`;
      const difficulties = sets.get(key) ?? [];
      difficulties.push(map);
      sets.set(key, difficulties);
    }
    return [...sets.entries()].map(([setId, difficulties]) => [
      setId,
      [...difficulties].sort((left, right) => (left.starRating ?? Number.POSITIVE_INFINITY) - (right.starRating ?? Number.POSITIVE_INFINITY)),
    ] as const);
  }, [maps]);
  const [renderLimit, setRenderLimit] = useState(BEATMAP_SET_RENDER_BATCH);
  const loadMoreRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => { setRenderLimit(BEATMAP_SET_RENDER_BATCH); }, [maps]);
  useEffect(() => {
    const selectedSetIndex = groupedSets.findIndex(([, difficulties]) => difficulties.some((map) => map.beatmapId === selectedId));
    if (selectedSetIndex >= 0) setRenderLimit((current) => Math.max(current, selectedSetIndex + 1));
  }, [groupedSets, selectedId]);
  useEffect(() => {
    const sentinel = loadMoreRef.current;
    const scrollRoot = sentinel?.parentElement;
    if (!sentinel || !scrollRoot || renderLimit >= groupedSets.length) return;
    if (typeof IntersectionObserver === "undefined") {
      setRenderLimit(groupedSets.length);
      return;
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        setRenderLimit((current) => Math.min(groupedSets.length, current + BEATMAP_SET_RENDER_BATCH));
      }
    }, { root: scrollRoot, rootMargin: "360px 0px" });
    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [groupedSets.length, renderLimit]);

  if (!maps.length) return (
    <div className="osu-empty"><MagnifyingGlass size={30} /><strong>{emptyTitle}</strong><span>{emptyCopy}</span></div>
  );
  return <div className="osu-map-list">{groupedSets.slice(0, renderLimit).map(([setId, difficulties]) => {
    const active = difficulties.find((map) => map.beatmapId === selectedId) ?? difficulties[0];
    const selected = difficulties.some((map) => map.beatmapId === selectedId);
    const playing = Boolean(active.audioUrl && active.audioUrl === playingAudioUrl);
    const installedCount = difficulties.filter((map) => map.localState === "Installed").length;
    const allInstalled = installedCount === difficulties.length;
    const queued = queuedIds.has(active.beatmapsetId || active.beatmapId);
    const stars = difficulties.map((map) => map.starRating).filter((value): value is number => value !== null);
    const starRange = stars.length ? `${Math.min(...stars).toFixed(2)}–${Math.max(...stars).toFixed(2)}` : "N/A";
    return <article className={`osu-map-row osu-map-set ${selected ? "selected" : ""} ${active.coverImageUrl ? "has-image" : ""}`} key={setId}>
      {active.coverImageUrl && <img className="osu-map-backdrop" src={active.coverImageUrl} alt="" referrerPolicy="no-referrer" />}
      <div className="osu-map-set-body">
        <div className="osu-map-main">
          <button type="button" className={`osu-map-play ${playing ? "playing" : ""}`} onClick={() => onToggleAudio(active)} disabled={!active.audioUrl} aria-label={active.audioUrl ? `${playing ? "Pause" : "Play"} ${active.title}` : `${active.title} audio is not available locally`} title={active.audioUrl ? `${playing ? "Pause" : "Play"} local beatmap audio` : "Install this beatmap to play its local audio"}>{playing ? <Pause size={18} weight="fill" /> : <Play size={18} weight="fill" />}</button>
          <button type="button" className="osu-map-select" onClick={() => onSelect(active.beatmapId)} aria-label={`Open ${active.title} ${active.difficultyName}`}>
            <span className="osu-map-copy"><strong>{active.title}</strong><span>{active.artist}</span><small>mapped by {active.creator}</small><span className="osu-map-badges"><b className="status">{active.status}</b><b>{difficulties.length} {difficulties.length === 1 ? "difficulty" : "difficulties"}</b>{installedCount > 0 && !allInstalled && <b>{installedCount} local</b>}</span></span>
            <span className="osu-map-facts"><strong style={difficultyStyle(stars.length ? Math.max(...stars) : null)}><Star weight="fill" size={12} /> {starRange}</strong><small>{formatLength(active.lengthSeconds)}</small><small>{formatBpm(active.bpm)}</small></span>
          </button>
        </div>
        <div className="osu-map-difficulties" aria-label={`${active.title} difficulties`}>{difficulties.map((map) => <button type="button" key={map.beatmapId} style={difficultyStyle(map.starRating)} className={map.beatmapId === active.beatmapId ? "active" : ""} onClick={() => onSelect(map.beatmapId)} title={`${map.difficultyName}${map.starRating === null ? "" : ` · ${map.starRating.toFixed(2)} stars`}`}><i aria-hidden="true" /><span>{map.difficultyName}</span><strong>{map.starRating === null ? "N/A" : `${map.starRating.toFixed(2)}★`}</strong></button>)}</div>
      </div>
      {allInstalled ? <span className="osu-local-check" title="All filtered difficulties are installed in osu!lazer"><CheckCircle size={17} weight="fill" /></span> : <button type="button" className={`osu-icon-button ${queued ? "queued" : ""}`} onClick={() => onQueue(active)} aria-label={queued ? "Remove from import queue" : "Add beatmap set to import queue"}>
        {queued ? <Check size={17} weight="bold" /> : <DownloadSimple size={17} />}
      </button>}
    </article>;
  })}{renderLimit < groupedSets.length && <div className="osu-map-load-more" ref={loadMoreRef} aria-hidden="true"><span /></div>}</div>;
}

function BeatmapDetail({ map, maps, queued, onSelect, onQueue }: { map: WorkspaceBeatmap | null; maps: WorkspaceBeatmap[]; queued: boolean; onSelect: (id: string) => void; onQueue: (map: WorkspaceBeatmap) => void }) {
  if (!map) return <aside className="osu-detail-panel osu-empty"><Target size={32} /><strong>Select a beatmap</strong><span>Map details, local scores, and replay matches appear here.</span></aside>;
  const difficulties = maps
    .filter((candidate) => candidate.beatmapsetId === map.beatmapsetId)
    .sort((left, right) => (left.starRating ?? Number.POSITIVE_INFINITY) - (right.starRating ?? Number.POSITIVE_INFINITY));
  return (
    <aside className="osu-detail-panel" style={difficultyStyle(map.starRating)}>
      {map.coverImageUrl ? <div className="osu-detail-cover"><img src={map.coverImageUrl} alt="" referrerPolicy="no-referrer" /><div><span>{map.artist}</span><h2>{map.title}</h2><p>{map.difficultyName} · mapped by {map.creator}</p></div></div> : <div className="osu-detail-heading"><span>{map.artist}</span><h2>{map.title}</h2><p>{map.difficultyName} · mapped by {map.creator}</p></div>}
      <div className="osu-detail-tags"><span className="ranked">{map.status}</span><span>{map.provider === "local" ? "Local library" : map.provider === "official" ? "osu!" : "osu!Collector"}</span><span>{map.localState}</span></div>
      {difficulties.length > 0 && <div className="osu-detail-difficulties" aria-label="Beatmap set difficulties">{difficulties.map((difficulty) => <button type="button" key={difficulty.beatmapId} style={difficultyStyle(difficulty.starRating)} className={difficulty.beatmapId === map.beatmapId ? "active" : ""} onClick={() => onSelect(difficulty.beatmapId)}><i aria-hidden="true" /><span>{difficulty.difficultyName}</span><strong>{difficulty.starRating === null ? "N/A" : `${difficulty.starRating.toFixed(2)}★`}</strong></button>)}</div>}
      <div className="osu-detail-metrics"><Metric label="Stars" value={map.starRating === null ? "Not supplied" : `${map.starRating.toFixed(2)}★`} accent /><Metric label="BPM" value={map.bpm === null ? "Not supplied" : formatBpm(map.bpm).replace(" BPM", "")} /><Metric label="Length" value={formatLength(map.lengthSeconds)} /><Metric label="95% PP" value={map.pp95 === null ? "Not supplied" : `${map.pp95}pp`} /></div>
      <div className="osu-section-label">Difficulty</div>
      <div className="osu-difficulty-grid"><Metric label="CS" value={map.circleSize === null ? "Not supplied" : map.circleSize.toFixed(1)} /><Metric label="AR" value={map.approachRate === null ? "Not supplied" : map.approachRate.toFixed(1)} /><Metric label="OD" value={map.overallDifficulty === null ? "Not supplied" : map.overallDifficulty.toFixed(1)} /><Metric label="HP" value={map.hpDrain === null ? "Not supplied" : map.hpDrain.toFixed(1)} /></div>
      <div className="osu-section-label">Skillset</div>
      <div className="osu-chip-row">{map.skillsets.length ? map.skillsets.map((item) => <span key={item}>{item}</span>) : <span>Unclassified</span>}</div>
      <div className="osu-detail-history"><div><Play size={14} /><span>{map.plays === null ? "Play count not supplied" : `${map.plays.toLocaleString()} plays`}</span></div><div><Heart size={14} /><span>{map.favorites === null ? "Favorites not supplied" : `${map.favorites.toLocaleString()} favorites`}</span></div><div><ChartLineUp size={14} /><span>{map.accuracy === null ? "No local score" : `${map.accuracy.toFixed(2)}% latest`}</span></div></div>
      {map.localState === "Installed" ? <div className="osu-local-installed"><CheckCircle size={18} weight="fill" />Available in the local lazer library</div> : <button type="button" className={`osu-primary-action ${queued ? "queued" : ""}`} onClick={() => onQueue(map)}>{queued ? <><CheckCircle size={20} weight="fill" />Queued for import</> : <><DownloadSimple size={20} />Download and import</>}</button>}
    </aside>
  );
}

function ReplayWorkspace({ replays, loading, error, onAdd, onOpen, onRefresh }: { replays: WorkspaceReplay[]; loading: boolean; error: string | null; onAdd: () => void; onOpen: (replay: WorkspaceReplay) => void; onRefresh: () => void }) {
  const [selectedPath, setSelectedPath] = useState(replays[0]?.path ?? "");
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<"all" | "misses" | "clean">("all");
  const [page, setPage] = useState(0);
  const selected = replays.find((item) => item.path === selectedPath) ?? replays[0];
  const filtered = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return [...replays]
      .filter((replay) => {
        if (filter === "misses" && !(replay.counts && replay.counts.countMiss > 0)) return false;
        if (filter === "clean" && replay.counts?.countMiss !== 0) return false;
        return !needle || [replay.beatmapTitle, replay.difficultyName, replay.playerName, replay.fileName, replay.mods.join(" ")].some((value) => value?.toLocaleLowerCase().includes(needle));
      })
      .sort((left, right) => (right.playedAt ?? "").localeCompare(left.playedAt ?? ""));
  }, [filter, query, replays]);
  const pageSize = 60;
  const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
  const visibleReplays = filtered.slice(page * pageSize, (page + 1) * pageSize);
  useEffect(() => { if (!selectedPath && replays[0]) setSelectedPath(replays[0].path); }, [replays, selectedPath]);
  useEffect(() => { setPage(0); }, [filter, query]);
  useEffect(() => { if (page >= pageCount) setPage(pageCount - 1); }, [page, pageCount]);
  return <div className="osu-replay-layout">
    <section className="osu-replay-library"><div className="osu-section-toolbar"><div><span>Replay library</span><strong>{loading ? "Reading local exports" : `${replays.length} files`}</strong></div><div className="osu-toolbar-actions"><button type="button" className="osu-icon-button" onClick={onRefresh} disabled={loading} aria-label="Refresh local replays"><ArrowClockwise size={15} /></button><button type="button" className="osu-secondary-action" onClick={onAdd}><FolderOpen size={15} />Add replays</button></div></div>
      {error && <div className="osu-library-notice" role="status"><WarningCircle size={16} /><span>{error}</span></div>}
      {replays.length ? <><div className="osu-replay-tools"><label><MagnifyingGlass size={16} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search maps, difficulties, players, or mods" /></label><div>{(["all", "misses", "clean"] as const).map((value) => <button type="button" key={value} className={filter === value ? "active" : ""} onClick={() => setFilter(value)}>{value === "all" ? "All" : value === "misses" ? "With misses" : "Miss-free"}</button>)}</div></div><div className="osu-replay-list">{visibleReplays.map((replay) => { const accuracy = accuracyOf(replay); return <button type="button" key={replay.path} className={selected?.path === replay.path ? "selected" : ""} onClick={() => setSelectedPath(replay.path)}>{replay.coverImageUrl && <img src={replay.coverImageUrl} alt="" referrerPolicy="no-referrer" />}<span><strong>{replay.beatmapTitle || replay.fileName}</strong><small>{replay.difficultyName || "Difficulty not supplied"} · {replay.mods.join(" + ") || "No Mod"}</small><small>{replay.playerName || "Player not supplied"} · {replay.playedAt || "Date not supplied"}{replay.storageSource ? ` · ${replay.storageSource === "export" ? "Export" : "lazer store"}` : ""}</small></span><b>{accuracy === null ? "N/A" : `${accuracy.toFixed(2)}%`}</b></button>; })}{visibleReplays.length === 0 && <div className="osu-empty compact"><MagnifyingGlass size={26} /><strong>No matching replays</strong><span>Change the search or miss filter.</span></div>}</div><div className="osu-replay-pagination"><span>{filtered.length ? `${page * pageSize + 1}-${Math.min((page + 1) * pageSize, filtered.length)} of ${filtered.length}` : "0 replays"}</span><div><button type="button" onClick={() => setPage((value) => Math.max(0, value - 1))} disabled={page === 0}>Previous</button><b>{page + 1} / {pageCount}</b><button type="button" onClick={() => setPage((value) => Math.min(pageCount - 1, value + 1))} disabled={page + 1 >= pageCount}>Next</button></div></div></> : <div className="osu-empty"><Play size={30} /><strong>{loading ? "Reading local replays" : "No local replays found"}</strong><span>{loading ? "AimMod is checking the detected osu!lazer storage folders." : "AimMod checks lazer's local store and exported .osr files. You can also add an .osr file here."}</span>{!loading && <button type="button" className="osu-secondary-action" onClick={onAdd}><FolderOpen size={15} />Add .osr files</button>}</div>}
    </section>
    {selected ? <section className="osu-replay-detail"><div className={`osu-replay-hero ${selected.coverImageUrl ? "has-image" : ""}`}>{selected.coverImageUrl && <img src={selected.coverImageUrl} alt="" referrerPolicy="no-referrer" />}<div><span>{selected.playerName || "Player not supplied"}</span><h2>{selected.beatmapTitle || selected.fileName}</h2><p>{selected.difficultyName || "Difficulty not supplied"} · {selected.mods.join(" + ") || "No Mod"}</p></div></div>
      <div className="osu-score-grid"><Metric label="Score" value={selected.score === null ? "Not supplied" : selected.score.toLocaleString()} /><Metric label="Accuracy" value={accuracyOf(selected) === null ? "Not supplied" : `${accuracyOf(selected)!.toFixed(2)}%`} accent /><Metric label="Combo" value={selected.maxCombo === null ? "Not supplied" : `${selected.maxCombo}x`} /><Metric label="Miss" value={selected.counts === null ? "Not supplied" : String(selected.counts.countMiss)} /></div>
      {selected.counts && <div className="osu-hit-strip"><span className="hit-300">{selected.counts.count300} × 300</span><span>{selected.counts.count100} × 100</span><span>{selected.counts.count50} × 50</span><span className="miss">{selected.counts.countMiss} misses</span></div>}
      {selected.parseError && <div className="osu-library-notice"><WarningCircle size={16} /><span>{selected.parseError}</span></div>}
      {!selected.parseError && <ReplayAnalyticsPanel selected={selected} />}
      <button type="button" className="osu-secondary-action osu-open-in-lazer" onClick={() => onOpen(selected)}><Play size={17} />Open in osu!lazer</button>
    </section> : <section className="osu-replay-detail osu-empty"><Play size={32} /><strong>Select a replay</strong><span>Score details appear after AimMod finds or imports an exported .osr file.</span></section>}
  </div>;
}

function ConnectedDataState({ kind }: { kind: "leaderboards" | "coaching" }) {
  return <div className="osu-single-page osu-empty"><User size={36} /><strong>Official osu! data required</strong><span>{kind === "leaderboards" ? "Connect the official osu! provider and select a beatmap to load its leaderboard." : "Connect the official osu! provider to build coaching from your play history."}</span></div>;
}

export function OsuWorkspace() {
  const [tab, setTab] = useState<OsuTab>(readTab);
  const [query, setQuery] = useState("");
  const [provider, setProvider] = useState<ProviderChoice>("all");
  const [skillset, setSkillset] = useState<"Any" | OsuSkillset>("Any");
  const [library, setLibrary] = useState<LibraryChoice>("all");
  const [rankedStatus, setRankedStatus] = useState<"any" | "ranked" | "loved">("any");
  const [minStars, setMinStars] = useState(0);
  const [maxStars, setMaxStars] = useState(10);
  const [minBpm, setMinBpm] = useState(0);
  const [maxBpm, setMaxBpm] = useState(500);
  const [minLength, setMinLength] = useState(0);
  const [maxLength, setMaxLength] = useState(900);
  const [minAr, setMinAr] = useState(0);
  const [maxAr, setMaxAr] = useState(11);
  const [minCs, setMinCs] = useState(0);
  const [maxCs, setMaxCs] = useState(10);
  const [minOd, setMinOd] = useState(0);
  const [maxOd, setMaxOd] = useState(11);
  const [onlyUnplayed, setOnlyUnplayed] = useState(false);
  const [sort, setSort] = useState("relevance");
  const [maps, setMaps] = useState<WorkspaceBeatmap[]>([]);
  const [localMaps, setLocalMaps] = useState<WorkspaceBeatmap[]>([]);
  const [hasSearched, setHasSearched] = useState(false);
  const [selectedId, setSelectedId] = useState("");
  const [queuedIds, setQueuedIds] = useState<Set<string>>(() => new Set());
  const [providers, setProviders] = useState<OsuBeatmapProvider[]>([]);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [providerNotice, setProviderNotice] = useState<string | null>(null);
  const [localBeatmapError, setLocalBeatmapError] = useState<string | null>(null);
  const [localBeatmapsLoading, setLocalBeatmapsLoading] = useState(false);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [client, setClient] = useState<OsuLazerStatus | null>(null);
  const [replays, setReplays] = useState<WorkspaceReplay[]>([]);
  const [replayError, setReplayError] = useState<string | null>(null);
  const [replaysLoading, setReplaysLoading] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [playingAudioUrl, setPlayingAudioUrl] = useState<string | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const loadedAudioUrlRef = useRef<string | null>(null);
  const audioRequestRef = useRef(0);
  const activeTabRef = useRef(tab);
  activeTabRef.current = tab;

  useEffect(() => () => {
    audioRequestRef.current += 1;
    const audio = audioRef.current;
    if (!audio) return;
    recordOsuDiagnostic({ area: "previewAudio", event: "dispose", sourceId: privateSourceId(loadedAudioUrlRef.current, "audio"), ...mediaDiagnosticState(audio) });
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    audioRef.current = null;
    loadedAudioUrlRef.current = null;
  }, []);

  useEffect(() => {
    if (tab === "beatmaps") return;
    audioRequestRef.current += 1;
    const audio = audioRef.current;
    if (audio) {
      recordOsuDiagnostic({ area: "previewAudio", event: "pause-on-tab-change", sourceId: privateSourceId(loadedAudioUrlRef.current, "audio"), ...mediaDiagnosticState(audio) });
      audio.pause();
      audio.removeAttribute("src");
      audio.load();
      loadedAudioUrlRef.current = null;
    }
    setPlayingAudioUrl(null);
    setActionMessage((current) => current === PREVIEW_AUDIO_UNAVAILABLE_MESSAGE || current === PREVIEW_AUDIO_ERROR_MESSAGE ? null : current);
  }, [tab]);

  const refreshClient = useCallback(async () => {
    if (!isTauri()) { setClient(null); return; }
    try { setClient(await invoke<OsuLazerStatus>("get_osu_lazer_status")); } catch { setClient(null); }
  }, []);

  const refreshLocalBeatmaps = useCallback(async () => {
    if (!isTauri()) { setLocalMaps([]); setLocalBeatmapError(null); return; }
    setLocalBeatmapsLoading(true);
    try {
      const response = await invoke<OsuLocalLibraryResponse<OsuLocalBeatmap>>("list_osu_local_beatmaps");
      setLocalMaps(response.items.map(mapLocalBeatmap));
      setLocalBeatmapError(response.error);
    } catch (reason) {
      setLocalMaps([]);
      setLocalBeatmapError(messageOf(reason));
    } finally {
      setLocalBeatmapsLoading(false);
    }
  }, []);

  const refreshLocalReplays = useCallback(async () => {
    if (!isTauri()) { setReplays([]); setReplayError(null); return; }
    recordOsuDiagnostic({ area: "workspace", event: "replay-library-load-start" });
    setReplaysLoading(true);
    try {
      const response = await invoke<OsuLocalLibraryResponse<OsuLocalReplay>>("list_osu_local_replays");
      setReplays(response.items.map(replayFromLocal));
      setReplayError(response.error);
      recordOsuDiagnostic({ area: "workspace", event: response.error ? "replay-library-load-partial" : "replay-library-load-complete" });
    } catch (reason) {
      setReplays([]);
      setReplayError(messageOf(reason));
      recordOsuDiagnostic({ area: "workspace", event: "replay-library-load-error" });
    } finally {
      setReplaysLoading(false);
    }
  }, []);

  useEffect(() => { void refreshClient(); }, [refreshClient]);
  useEffect(() => { void refreshLocalBeatmaps(); void refreshLocalReplays(); }, [refreshLocalBeatmaps, refreshLocalReplays]);
  useEffect(() => {
    window.localStorage.setItem(TAB_KEY, tab);
    recordOsuDiagnostic({ area: "workspace", event: "tab-change", sourceId: tab });
  }, [tab]);
  useEffect(() => {
    if (!isTauri()) { setProviders([]); return; }
    void invoke<OsuBeatmapProvider[]>("get_osu_beatmap_providers").then(setProviders).catch(() => setProviders([]));
  }, []);

  const visibleMaps = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase();
    const matchingLocal = hasSearched && normalizedQuery ? localMaps.filter((map) => [map.artist, map.title, map.creator, map.difficultyName].some((value) => value.toLocaleLowerCase().includes(normalizedQuery))) : localMaps;
    const unique = new Map<string, WorkspaceBeatmap>();
    for (const map of matchingLocal) unique.set(map.beatmapId, map);
    for (const map of maps) {
      const installed = unique.get(map.beatmapId);
      unique.set(map.beatmapId, installed ? mergeInstalledBeatmap(map, installed) : map);
    }
    let next = [...unique.values()];
    next = next.filter((map) => map.mode.toLocaleLowerCase() === "osu");
    if (provider !== "all") next = next.filter((map) => map.provider === provider);
    if (skillset !== "Any") next = next.filter((map) => map.skillsets.includes(skillset));
    if (library === "installed") next = next.filter((map) => map.localState === "Installed");
    if (library === "missing") next = next.filter((map) => map.localState !== "Installed");
    if (rankedStatus !== "any") next = next.filter((map) => map.status.toLowerCase() === rankedStatus);
    next = next.filter((map) => map.starRating === null || (map.starRating >= minStars && map.starRating <= maxStars));
    next = next.filter((map) => (map.bpm === null || (map.bpm >= minBpm && map.bpm <= maxBpm)) && (map.lengthSeconds === null || (map.lengthSeconds >= minLength && map.lengthSeconds <= maxLength)));
    next = next.filter((map) => map.approachRate === null || (map.approachRate >= minAr && map.approachRate <= maxAr));
    next = next.filter((map) => map.circleSize === null || (map.circleSize >= minCs && map.circleSize <= maxCs));
    next = next.filter((map) => map.overallDifficulty === null || (map.overallDifficulty >= minOd && map.overallDifficulty <= maxOd));
    if (onlyUnplayed) next = next.filter((map) => map.accuracy === null);
    if (sort === "stars-high") next.sort((a,b) => (b.starRating ?? -1)-(a.starRating ?? -1));
    if (sort === "stars-low") next.sort((a,b) => (a.starRating ?? Number.POSITIVE_INFINITY)-(b.starRating ?? Number.POSITIVE_INFINITY));
    if (sort === "bpm") next.sort((a,b) => (b.bpm ?? -1)-(a.bpm ?? -1));
    return next;
  }, [hasSearched, library, localMaps, maps, maxAr, maxBpm, maxCs, maxLength, maxOd, maxStars, minAr, minBpm, minCs, minLength, minOd, minStars, onlyUnplayed, provider, query, rankedStatus, skillset, sort]);
  const selectedMap = visibleMaps.find((map) => map.beatmapId === selectedId) ?? visibleMaps[0] ?? null;
  const visibleBeatmapSetCount = new Set(visibleMaps.map((map) => map.beatmapsetId || `${map.provider}:${map.artist}:${map.title}:${map.creator}`)).size;

  const search = useCallback(async () => {
    setSearching(true); setSearchError(null); setProviderNotice(null); setActionMessage(null); setHasSearched(true);
    if (!isTauri()) { setMaps([]); setSearchError("Beatmap providers are available in AimMod desktop."); setSearching(false); return; }
    const providerIds = provider === "all" ? providers.map((item) => item.id) : [provider];
    if (!providerIds.length) { setMaps([]); setSearchError("No beatmap providers responded. Check provider configuration and try again."); setSearching(false); return; }
    try {
      const commonFilters: BeatmapSearchFilters = { mode: "osu", status: rankedStatus === "any" ? null : rankedStatus, minStarRating: minStars, maxStarRating: maxStars, minBpm, maxBpm, minLengthSeconds: minLength, maxLengthSeconds: maxLength, minApproachRate: minAr, maxApproachRate: maxAr, minCircleSize: minCs, maxCircleSize: maxCs, minOverallDifficulty: minOd, maxOverallDifficulty: maxOd, sort, descending: sort === "stars-high" || sort === "bpm" };
      const responses = await Promise.all(providerIds.map((providerId) => invoke<OsuBeatmapSearchResponse>("search_osu_beatmaps", { request: { provider: providerId, query: query.trim(), filters: filtersForBeatmapProvider(providerId, commonFilters), offset: 0, limit: 50 } })));
      const items = responses.flatMap((response) => response.items);
      setMaps(items.map(mapSearchItem));
      const errors = responses.map((response) => response.error).filter((error): error is string => Boolean(error));
      const allProvidersFailed = responses.length > 0 && errors.length === responses.length;
      setSearchError(allProvidersFailed ? errors.join(" ") : null);
      setProviderNotice(errors.length && !allProvidersFailed ? errors.join(" ") : null);
    } catch (reason) { setMaps([]); setSearchError(messageOf(reason)); }
    finally { setSearching(false); }
  }, [maxAr, maxBpm, maxCs, maxLength, maxOd, maxStars, minAr, minBpm, minCs, minLength, minOd, minStars, provider, providers, query, rankedStatus, sort]);

  const toggleQueue = useCallback(async (map: WorkspaceBeatmap) => {
    if (map.localState === "Installed") return;
    const queueKey = map.beatmapsetId || map.beatmapId;
    if (queuedIds.has(queueKey)) { setQueuedIds((current) => { const next = new Set(current); next.delete(queueKey); return next; }); return; }
    setActionMessage(null);
    if (!isTauri()) { setQueuedIds((current) => new Set(current).add(queueKey)); return; }
    try {
      const result = await invoke<OsuBeatmapDownloadResult>("download_osu_beatmap", { request: { provider: map.provider, sourceId: map.sourceId ?? null, beatmapsetId: map.beatmapsetId } });
      setActionMessage(result.message);
      if (result.status === "downloaded" || result.status === "opened" || result.status === "queued") setQueuedIds((current) => new Set(current).add(queueKey));
    } catch (reason) { setActionMessage(messageOf(reason)); }
  }, [queuedIds]);

  const toggleBeatmapAudio = useCallback(async (map: WorkspaceBeatmap) => {
    setSelectedId(map.beatmapId);
    if (!map.audioUrl) {
      setActionMessage(PREVIEW_AUDIO_UNAVAILABLE_MESSAGE);
      return;
    }

    const request = ++audioRequestRef.current;
    let audio = audioRef.current;
    if (!audio) {
      audio = new Audio();
      audio.preload = "metadata";
      audio.loop = true;
      const reportMediaEvent = (event: string) => {
        const currentAudio = audioRef.current;
        if (!currentAudio) return;
        recordOsuDiagnostic({ area: "previewAudio", event, sourceId: privateSourceId(loadedAudioUrlRef.current, "audio"), ...mediaDiagnosticState(currentAudio) });
      };
      audio.addEventListener("loadstart", () => reportMediaEvent("load-start"));
      audio.addEventListener("loadedmetadata", () => reportMediaEvent("metadata-loaded"));
      audio.addEventListener("canplay", () => reportMediaEvent("can-play"));
      audio.addEventListener("playing", () => reportMediaEvent("playing"));
      audio.addEventListener("pause", () => reportMediaEvent("pause"));
      audio.addEventListener("stalled", () => reportMediaEvent("stalled"));
      audio.addEventListener("waiting", () => reportMediaEvent("waiting"));
      audio.onended = () => {
        reportMediaEvent("ended");
        setPlayingAudioUrl(null);
      };
      audio.onerror = () => {
        reportMediaEvent("error");
        setPlayingAudioUrl(null);
        if (activeTabRef.current === "beatmaps") setActionMessage(PREVIEW_AUDIO_ERROR_MESSAGE);
      };
      audioRef.current = audio;
    }

    if (loadedAudioUrlRef.current === map.audioUrl && !audio.paused) {
      recordOsuDiagnostic({ area: "previewAudio", event: "pause-request", sourceId: privateSourceId(map.audioUrl, "audio"), ...mediaDiagnosticState(audio) });
      audio.pause();
      setPlayingAudioUrl(null);
      return;
    }

    if (loadedAudioUrlRef.current !== map.audioUrl) {
      audio.pause();
      loadedAudioUrlRef.current = map.audioUrl;
      audio.src = map.audioUrl;
      recordOsuDiagnostic({ area: "previewAudio", event: "source-set", sourceId: privateSourceId(map.audioUrl, "audio"), ...mediaDiagnosticState(audio) });
      audio.load();
      if (audio.readyState < HTMLMediaElement.HAVE_METADATA) {
        await new Promise<void>((resolve) => {
          const finish = () => {
            audio.removeEventListener("loadedmetadata", finish);
            audio.removeEventListener("error", finish);
            resolve();
          };
          audio.addEventListener("loadedmetadata", finish, { once: true });
          audio.addEventListener("error", finish, { once: true });
        });
      }
      if (request !== audioRequestRef.current) return;
      const durationMs = Number.isFinite(audio.duration) ? audio.duration * 1000 : 0;
      const requestedPreview = map.audioPreviewTimeMs;
      const previewMs = requestedPreview !== null && requestedPreview <= durationMs
        ? requestedPreview
        : durationMs * 0.4;
      audio.currentTime = Math.max(0, previewMs - 30) / 1000;
    }

    setActionMessage(null);
    recordOsuDiagnostic({ area: "previewAudio", event: "play-request", sourceId: privateSourceId(map.audioUrl, "audio"), ...mediaDiagnosticState(audio) });
    void audio.play().then(() => {
      recordOsuDiagnostic({ area: "previewAudio", event: "play-resolved", sourceId: privateSourceId(map.audioUrl, "audio"), ...mediaDiagnosticState(audio) });
      if (request === audioRequestRef.current) setPlayingAudioUrl(map.audioUrl);
    }).catch(() => {
      if (request !== audioRequestRef.current) return;
      recordOsuDiagnostic({ area: "previewAudio", event: "play-rejected", sourceId: privateSourceId(map.audioUrl, "audio"), ...mediaDiagnosticState(audio) });
      setPlayingAudioUrl(null);
      if (activeTabRef.current === "beatmaps") setActionMessage(PREVIEW_AUDIO_ERROR_MESSAGE);
    });
  }, []);

  const importPaths = useCallback(async (paths: string[]) => {
    if (!paths.length || !isTauri()) return;
    try {
      const results = await invoke<OsuImportResult[]>("import_osu_lazer_files", { paths });
      setActionMessage(results.map((item) => item.message).join(" "));
      void refreshClient();
      void refreshLocalBeatmaps();
      void refreshLocalReplays();
      window.setTimeout(() => { void refreshLocalBeatmaps(); void refreshLocalReplays(); }, 1800);
    } catch (reason) { setActionMessage(messageOf(reason)); }
  }, [refreshClient, refreshLocalBeatmaps, refreshLocalReplays]);

  const inspectReplayPaths = useCallback(async (paths: string[]) => {
    if (!paths.length || !isTauri()) return;
    try { const items = await invoke<OsuReplayInspection[]>("inspect_osu_replay_files", { paths }); setReplays((current) => { const next = new Map(current.map((item) => [item.path, item])); for (const item of items.map(replayFromInspection)) next.set(item.path, item); return [...next.values()]; }); setTab("replays"); } catch (reason) { setActionMessage(messageOf(reason)); }
  }, []);

  const chooseFiles = useCallback(async (kind: "beatmap" | "replay") => {
    if (!isTauri()) return;
    const selected = await open({ directory: false, multiple: true, title: kind === "beatmap" ? "Import beatmaps into osu!lazer" : "Add replays to AimMod", filters: [{ name: kind === "beatmap" ? "osu! beatmaps" : "osu! replays", extensions: [kind === "beatmap" ? "osz" : "osr"] }] });
    if (!selected) return;
    const paths = Array.isArray(selected) ? selected : [selected];
    if (kind === "beatmap") await importPaths(paths); else await inspectReplayPaths(paths);
  }, [importPaths, inspectReplayPaths]);

  const openReplay = useCallback(async (replay: WorkspaceReplay) => {
    if (!isTauri()) return;
    if (!replay.storageSource) { await importPaths([replay.path]); return; }
    try {
      const result = await invoke<OsuImportResult>("open_osu_local_replay", { path: replay.path });
      setActionMessage(result.message);
    } catch (reason) {
      setActionMessage(messageOf(reason));
    }
  }, [importPaths]);

  useEffect(() => {
    if (!isTauri()) return;
    let stop: (() => void) | null = null; let disposed = false;
    void getCurrentWebview().onDragDropEvent((event) => {
      if (event.payload.type === "over") setDragging(true);
      if (event.payload.type === "leave") setDragging(false);
      if (event.payload.type === "drop") { setDragging(false); const replayPaths = event.payload.paths.filter((path) => path.toLowerCase().endsWith(".osr")); const mapPaths = event.payload.paths.filter((path) => path.toLowerCase().endsWith(".osz")); if (replayPaths.length) void inspectReplayPaths(replayPaths); if (mapPaths.length) void importPaths(mapPaths); }
    }).then((unlisten) => { if (disposed) unlisten(); else stop = unlisten; });
    return () => { disposed = true; stop?.(); };
  }, [importPaths, inspectReplayPaths]);

  return <main className={`osu-workspace ${dragging ? "is-dragging" : ""}`}>
    <header className="osu-product-header"><div className="osu-wordmark"><span>aimmod</span><strong>!lazer</strong></div><nav aria-label="osu workspace">{TABS.map(({ id, label, icon: Icon }) => <button type="button" key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)} aria-current={tab === id ? "page" : undefined}><Icon size={16} />{label}</button>)}</nav><div className={`osu-client-pill ${client?.detected ? "connected" : ""}`} title={client?.installations[0]?.dataPath}><span />{client?.detected ? "osu!lazer detected" : "osu!lazer not detected"}<button type="button" onClick={() => { void refreshClient(); void refreshLocalBeatmaps(); void refreshLocalReplays(); }} aria-label="Refresh osu!lazer connection and local library"><ArrowClockwise size={13} /></button></div></header>
    <OfficialProfileHeader />

    {tab === "beatmaps" && <div className="osu-beatmap-workspace">
      <BeatmapFilters
        provider={provider} setProvider={setProvider} skillset={skillset} setSkillset={setSkillset}
        library={library} setLibrary={setLibrary} rankedStatus={rankedStatus} setRankedStatus={setRankedStatus}
        minStars={minStars} setMinStars={setMinStars} maxStars={maxStars} setMaxStars={setMaxStars}
        minBpm={minBpm} setMinBpm={setMinBpm} maxBpm={maxBpm} setMaxBpm={setMaxBpm}
        minLength={minLength} setMinLength={setMinLength} maxLength={maxLength} setMaxLength={setMaxLength}
        minAr={minAr} setMinAr={setMinAr} maxAr={maxAr} setMaxAr={setMaxAr}
        minCs={minCs} setMinCs={setMinCs} maxCs={maxCs} setMaxCs={setMaxCs}
        minOd={minOd} setMinOd={setMinOd} maxOd={maxOd} setMaxOd={setMaxOd}
        onlyUnplayed={onlyUnplayed} setOnlyUnplayed={setOnlyUnplayed}
        onReset={() => { setProvider("all"); setSkillset("Any"); setLibrary("all"); setRankedStatus("any"); setMinStars(0); setMaxStars(10); setMinBpm(0); setMaxBpm(500); setMinLength(0); setMaxLength(900); setMinAr(0); setMaxAr(11); setMinCs(0); setMaxCs(10); setMinOd(0); setMaxOd(11); setOnlyUnplayed(false); }}
      />
      <section className="osu-search-results"><form className="osu-search-bar" onSubmit={(event: FormEvent) => { event.preventDefault(); void search(); }}><MagnifyingGlass size={18} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search beatmaps, artists, mappers, or difficulties" autoComplete="off" /><button type="submit" disabled={searching}>{searching ? "Searching" : "Search"}</button></form><div className="osu-result-toolbar"><strong>{localBeatmapsLoading && !visibleMaps.length ? "Reading local library" : `${visibleBeatmapSetCount} sets · ${visibleMaps.length} difficulties`}</strong><div><button type="button" className="osu-icon-button" onClick={() => void refreshLocalBeatmaps()} disabled={localBeatmapsLoading} aria-label="Refresh local beatmaps"><ArrowClockwise size={15} /></button><span>Sort by</span><div className="osu-select-wrap compact"><select value={sort} onChange={(event) => setSort(event.target.value)}><option value="relevance">Relevance</option><option value="stars-high">Stars, high to low</option><option value="stars-low">Stars, low to high</option><option value="bpm">BPM</option></select><CaretDown size={12} /></div><button type="button" className="osu-icon-button" aria-label="List view"><ListBullets size={16} /></button></div></div>{localBeatmapError && <div className="osu-library-notice" role="status"><WarningCircle size={16} /><span>{localBeatmapError}</span><button type="button" onClick={() => void refreshLocalBeatmaps()}>Retry</button></div>}{providerNotice && <div className="osu-provider-notice" role="status"><WarningCircle size={16} /><span>Some providers could not be searched. Results from the available providers are shown.</span><details><summary>Details</summary><p>{providerNotice}</p></details></div>}{searchError && <div className="osu-inline-error" role="alert"><WarningCircle size={16} /><span>{searchError}</span><button type="button" onClick={() => void search()}>Try again</button></div>}<BeatmapRows maps={visibleMaps} selectedId={selectedMap?.beatmapId ?? ""} queuedIds={queuedIds} playingAudioUrl={playingAudioUrl} emptyTitle={localBeatmapsLoading ? "Reading local beatmaps" : hasSearched ? "No beatmaps found" : isTauri() ? "No local beatmaps found" : "Local beatmaps are available in AimMod desktop"} emptyCopy={localBeatmapsLoading ? "AimMod is reading standard .osu files from the detected lazer file store." : hasSearched ? "Change the provider or loosen the filters, then search again." : isTauri() ? "Import an .osz file or check that osu!lazer storage was detected." : "The browser route does not load sample library data."} onSelect={setSelectedId} onQueue={(map) => void toggleQueue(map)} onToggleAudio={toggleBeatmapAudio} /></section>
      <BeatmapDetail map={selectedMap} maps={visibleMaps} queued={selectedMap ? queuedIds.has(selectedMap.beatmapsetId || selectedMap.beatmapId) : false} onSelect={setSelectedId} onQueue={(map) => void toggleQueue(map)} />
    </div>}

    {tab === "skins" && <SkinsWorkspace onMessage={setActionMessage} />}
    {tab === "replays" && <ReplayWorkspace replays={replays} loading={replaysLoading} error={replayError} onAdd={() => void chooseFiles("replay")} onOpen={(replay) => void openReplay(replay)} onRefresh={() => void refreshLocalReplays()} />}
    {tab === "statistics" && <OsuStatisticsDashboard />}
    {tab === "leaderboards" && <ConnectedDataState kind="leaderboards" />}
    {tab === "coaching" && <OsuCoachingPanel replays={replays} />}

    {tab === "beatmaps" && <footer className="osu-action-dock"><button type="button" className="osu-dock-back" onClick={() => setQueuedIds(new Set())}><X size={16} />Clear queue</button><div><FolderOpen size={18} /><span>Local library</span><strong>{localBeatmapsLoading ? "Reading" : `${localMaps.length} maps · ${replays.length} replays`}</strong></div><div><DownloadSimple size={18} /><span>Import queue</span><strong>{queuedIds.size} sets</strong></div><button type="button" className="osu-dock-import" onClick={() => void chooseFiles("beatmap")}><UploadSimple size={21} /><span>Import local files</span></button></footer>}
    {actionMessage && <div className="osu-toast" role="status"><GameController size={17} /><span>{actionMessage}</span><button type="button" onClick={() => setActionMessage(null)} aria-label="Dismiss"><X size={14} /></button></div>}
    {dragging && <div className="osu-drop-overlay"><UploadSimple size={40} /><strong>Drop beatmaps or replays</strong><span>.osz imports to lazer · .osr opens in replay review</span></div>}
  </main>;
}
