import { Suspense, lazy, useEffect, useMemo, useRef, useState } from "react";
import { useAppTheme } from "../hooks/useAppTheme";
import { invoke } from "@tauri-apps/api/core";
import { listen, emit } from "@tauri-apps/api/event";
import type { Update } from "@tauri-apps/plugin-updater";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { LeaderboardBrowser, ScenarioLeaderboardPanel } from "./LeaderboardBrowser";
import { DebugTab } from "./DebugTab";
import { HubBrowserPanel } from "./HubBrowser";
import { OsuWorkspace } from "../osu/OsuWorkspace";
import { MousePathViewer } from "./MousePathViewer";
import type {
  ReplayPayloadData,
  BridgeRunSnapshot,
  BridgeRunTimelinePoint,
  BridgeShotTelemetryEvent,
  BridgeReplayContextWindow,
} from "../types/mouse";
import type { StatsPanelReading } from "../types/overlay";
import {
  LineChart,
  Line,
  BarChart,
  Bar,
  ComposedChart,
  Brush,
  Cell,
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  Radar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
  ReferenceLine,
  ReferenceArea,
  TooltipProps,
} from "recharts";
import { Badge, Dot, Btn, SectionLabel } from "../design/ui";
import { C, scenarioColor, SCENARIO_LABELS, accentAlpha } from "../design/tokens";
import { ShortcutHelpModal } from "../components/ShortcutHelpModal";
import { useUpdater } from "../hooks/useUpdater";
import type { AppSettings } from "../types/settings";
import {
  type CoachingUserFeedbackRecord,
  formatRunWindow as formatUnifiedRunWindow,
  normalizeAccuracyPct,
  normalizeBridgeRunTimeline,
  runMomentAction as buildUnifiedRunMomentAction,
  type PlayerLearningProfile,
  type TargetResponseAnalysis,
  type TargetResponseEpisode,
} from "../coaching/engine";

const SettingsTab = lazy(() =>
  import("../settings/Settings").then((m) => ({ default: m.Settings })),
);

// ─── Types ─────────────────────────────────────────────────────────────────────

interface SmoothnessSnapshot {
  composite: number;
  jitter: number;
  overshoot_rate: number;
  velocity_std: number;
  path_efficiency: number;
  avg_speed: number;
  click_timing_cv: number;
  correction_ratio: number;
  directional_bias: number;
}

interface StatsPanelSnapshot {
  scenario_type: string;
  scenario_subtype?: string | null;
  kills: number | null;
  avg_kps: number | null;
  accuracy_pct: number | null;
  total_damage: number | null;
  avg_ttk_ms: number | null;
  best_ttk_ms: number | null;
  ttk_std_ms: number | null;
  accuracy_trend: number | null;
}

interface ShotTimingSnapshot {
  paired_shot_hits: number;
  avg_fire_to_hit_ms: number | null;
  p90_fire_to_hit_ms: number | null;
  avg_shots_to_hit: number | null;
  corrective_shot_ratio: number | null;
}

interface SessionRecord {
  id: string;
  scenario: string;
  score: number;
  accuracy: number;
  kills: number;
  deaths: number;
  duration_secs: number;
  damage_done?: number;
  timestamp: string;
  smoothness: SmoothnessSnapshot | null;
  stats_panel: StatsPanelSnapshot | null;
  shot_timing?: ShotTimingSnapshot | null;
  has_replay: boolean;
  replay_is_favorite: boolean;
  replay_positions_count: number;
  replay_metrics_count: number;
  replay_frames_count: number;
}

interface SessionCsvImportSummary {
  stats_dir: string;
  scanned: number;
  parsed: number;
  imported: number;
  skipped_existing: number;
  failed: number;
  total_after: number;
}

interface SessionHistoryPage {
  records: SessionRecord[];
  total: number;
  offset: number;
  limit: number;
  has_more: boolean;
}

interface AnalyticsSessionRecord extends SessionRecord {
  normalizedScenario: string;
  timestampMs: number;
  isDurationOutlier: boolean;
  isZeroSignal: boolean;
  isReliableForAnalysis: boolean;
  qualityIssues: string[];
}

interface CoachingPersistenceStatus {
  snapshotUpdatedAtMs: number | null;
  pendingCount: number;
  improvedCount: number;
  flatCount: number;
  regressedCount: number;
}

type CoachingCardFeedback = CoachingUserFeedbackRecord["feedback"];

interface GlobalCoachingLearningState {
  sampleCount: number;
  settledSampleCount: number;
  warmupSampleCount: number;
  normalizedVariancePct: number | null;
  warmupTaxPct: number | null;
  avgBlockFadePct: number | null;
  switchPenaltyPct: number | null;
  momentumDeltaPct: number | null;
  retentionAfterGapPct: number | null;
  dominantFamily: string | null;
  dominantFamilySharePct: number | null;
  familyDiversity: number;
}

interface BehaviorPatternFeatures {
  sampleCount: number;
  settledSampleCount: number;
  warmupConsistencyPct: number | null;
  readinessPct: number | null;
  adaptationPct: number | null;
  endurancePct: number | null;
  transferPct: number | null;
  precisionPct: number | null;
  controlPct: number | null;
  consistencyPct: number | null;
  learningEfficiencyPct: number | null;
  tempoPct: number | null;
  switchResiliencePct: number | null;
  retainedFormPct: number | null;
  fatiguePressurePct: number | null;
  correctionLoadPct: number | null;
  hesitationLoadPct: number | null;
  volatilityPct: number | null;
  momentumPct: number | null;
  precisionTempoBiasPct: number | null;
}

interface PracticeProfile {
  sessionCount: number;
  activeDays: number;
  spanDays: number;
  daysPerWeek: number;
  sessionsPerActiveDay: number;
  avgBlockRuns: number;
  avgBlockMinutes: number;
  maxBlockMinutes: number;
  scenarioDiversity: number;
  dominantScenario: string;
  dominantScenarioShare: number;
  avgUniqueScenariosPerBlock: number;
  avgScenarioSwitchesPerBlock: number;
  switchRate: number;
  topScenarios: { scenario: string; share: number; count: number }[];
}

interface GlobalCoachingOverview {
  practiceProfile: PracticeProfile | null;
  warmupIds: string[];
  learningState: GlobalCoachingLearningState | null;
  behaviorFeatures: BehaviorPatternFeatures | null;
  playerLearningProfile: PlayerLearningProfile | null;
  globalCards: CoachingCardData[];
  coachingPersistenceStatus: CoachingPersistenceStatus | null;
}

interface ScenarioWarmupStats {
  dropPct: number;
  avgWarmupSessions: number;
  blockCount: number;
  settleInLabel: string;
  action: string;
}

interface ScenarioCoachingOverview {
  scenarioType: string;
  scoreCvPct: number | null;
  slopePtsPerRun: number | null;
  avgScore: number | null;
  isPlateau: boolean;
  p10Score: number | null;
  p50Score: number | null;
  p90Score: number | null;
  warmupStats: ScenarioWarmupStats | null;
  coachingCards: CoachingCardData[];
}

function estimateReplayBridgeBaseTs(
  events: BridgeShotTelemetryEvent[],
  timeline: BridgeRunTimelinePoint[],
): number {
  const sortedEvents = [...events].sort((a, b) => a.ts_ms - b.ts_ms);
  if (sortedEvents.length === 0) return 0;

  const estimateOffsetMs = (field: "shots_fired" | "shots_hit", total: number): number | null => {
    const point = timeline.find((entry) => {
      const value = field === "shots_fired" ? entry.shots_fired : entry.shots_hit;
      return value != null && Number.isFinite(value) && value + 0.0001 >= total;
    });
    return point ? point.t_sec * 1000 : null;
  };

  let cumulativeFired = 0;
  let cumulativeHit = 0;
  const bases: number[] = [];

  for (const event of sortedEvents.slice(0, 64)) {
    const weight = Math.max(1, event.count ?? 1);
    let offsetMs: number | null = null;
    if (event.event === "shot_fired") {
      cumulativeFired += weight;
      offsetMs = estimateOffsetMs("shots_fired", event.total ?? cumulativeFired);
    } else if (event.event === "shot_hit") {
      cumulativeHit += weight;
      offsetMs = estimateOffsetMs("shots_hit", event.total ?? cumulativeHit);
    }

    if (offsetMs != null) {
      bases.push(Math.max(0, event.ts_ms - offsetMs));
      if (bases.length >= 12) break;
    }
  }

  if (bases.length === 0) return sortedEvents[0]?.ts_ms ?? 0;
  bases.sort((a, b) => a - b);
  return bases[Math.floor(bases.length / 2)] ?? sortedEvents[0]?.ts_ms ?? 0;
}

interface BridgeParsedEvent {
  ev: string;
  value?: number | null;
  delta?: number | null;
  total?: number | null;
  raw?: string;
}

interface ShotTelemetryContextRow {
  key: string;
  label: string;
  startMs: number;
  endMs: number;
  firedCount: number;
  hitCount: number;
  accuracyPct: number | null;
  avgBotCount: number | null;
  nearestLabel: string;
  nearestDistance: number | null;
  yawError: number | null;
  pitchError: number | null;
  source: "sql" | "derived";
  contextKind: string | null;
  phase: string | null;
  primaryTargetShare: number | null;
  avgScorePerMinute: number | null;
  avgKillsPerSecond: number | null;
  avgTimelineAccuracyPct: number | null;
  avgDamageEfficiency: number | null;
}

interface ReplayContextCoachingSignal {
  id: string;
  contextKey: string;
  title: string;
  badge: string;
  badgeColor: string;
  detail: string;
  action: string;
  startMs: number;
  endMs: number;
}

type AimAxisKey = "precision" | "speed" | "control" | "consistency" | "decisiveness" | "rhythm";

interface AimAxisProfile {
  key: AimAxisKey;
  label: string;
  volatility: number;
}

type Tab = "summary" | "mechanics" | "coaching" | "replay" | "leaderboard" | "benchmarks";
type DateRangePreset = "all" | "30d" | "90d" | "365d";

interface HubBenchmarkRankVisual {
  rankIndex: number;
  rankName: string;
  iconUrl: string;
  color: string;
}

interface HubScenarioBenchmarkRank {
  benchmarkId: number;
  benchmarkName: string;
  benchmarkIconUrl: string;
  categoryName: string;
  scenarioScore: number;
  leaderboardRank: number;
  leaderboardId: number;
  scenarioRank: HubBenchmarkRankVisual | null;
}

interface HubBenchmarkThreshold {
  rankIndex: number;
  rankName: string;
  score: number;
  iconUrl: string;
  color: string;
}

interface HubBenchmarkScenarioEntry {
  scenarioName: string;
  scenarioSlug: string;
  score: number;
  leaderboardRank: number;
  scenarioRank: HubBenchmarkRankVisual | null;
  thresholds: HubBenchmarkThreshold[];
}

interface HubBenchmarkCategoryPage {
  categoryName: string;
  scenarios: HubBenchmarkScenarioEntry[];
}

interface HubBenchmarkPageResponse {
  userHandle: string;
  benchmarkId: number;
  benchmarkName: string;
  benchmarkIconUrl: string;
  overallRank: HubBenchmarkRankVisual | null;
  categories: HubBenchmarkCategoryPage[];
}

interface BenchmarkThresholdLine {
  score: number;
  rankName: string;
  color: string;
  benchmarkName: string;
}

// ─── Constants ─────────────────────────────────────────────────────────────────

const SEV_COLOR = {
  high: "#ff4d4d",
  medium: "#ffd700",
  low: "#00b4ff",
  good: "#00f5a0",
} as const;

const CARD_STYLE: React.CSSProperties = {
  background: C.surface,
  border: `1px solid ${C.border}`,
  borderRadius: 10,
  padding: "14px 18px",
  flex: 1,
  minWidth: 120,
};

const CHART_STYLE: React.CSSProperties = {
  background: C.glass,
  border: `1px solid ${C.border}`,
  borderRadius: 12,
  padding: "16px 20px",
  backdropFilter: "blur(16px) saturate(180%)",
};

const TOOLTIP_STYLE: React.CSSProperties = {
  background: C.glassDark,
  border: `1px solid ${C.border}`,
  borderRadius: 8,
  fontFamily: "'JetBrains Mono', monospace",
  fontSize: 12,
};

const HISTORY_PAGE_SIZE = 500;
const STATS_WINDOW_STORAGE_KEYS = {
  search: "stats-window:search",
  selectedScenario: "stats-window:selected-scenario",
  rootMode: "stats-window:root-mode",
  sessionsPane: "stats-window:sessions-pane",
  scenarioTab: "stats-window:scenario-tab",
  sessionFilter: "stats-window:session-filter",
  scenarioSort: "stats-window:scenario-sort",
  dateRange: "stats-window:date-range",
  compareScenario: "stats-window:compare-scenario",
  hubNoticeDismissed: "stats-window:hub-notice-dismissed",
  updateNoticeDismissedVersion: "stats-window:update-notice-dismissed-version",
  favoriteBenchmarks: "stats-window:favorite-benchmarks",
} as const;

function readStoredValue(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStoredValue(key: string, value: string | null) {
  try {
    if (!value) {
      window.localStorage.removeItem(key);
      return;
    }
    window.localStorage.setItem(key, value);
  } catch {
    // Ignore storage failures in restricted environments.
  }
}

function readStoredNumberArray(key: string): number[] {
  const raw = readStoredValue(key);
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((value) => Number(value))
      .filter((value) => Number.isFinite(value) && value > 0);
  } catch {
    return [];
  }
}

function writeStoredNumberArray(key: string, values: number[]) {
  const unique = [...new Set(values.filter((value) => Number.isFinite(value) && value > 0))];
  writeStoredValue(key, unique.length > 0 ? JSON.stringify(unique) : null);
}

interface DrillRecommendation {
  label: string;
  query: string;
}

interface ScenarioSummary {
  sessions: number;
  best: number;
  avgScore: number;
  recentAvg: number;
  avgAccuracy: number | null;
  totalDurationSecs: number;
  latestTimestamp: string | null;
}

interface StatsHubSyncStatus {
  pendingCount: number;
  lastSuccessAtUnixMs: number | null;
  lastError: string | null;
  lastErrorAtUnixMs: number | null;
  lastUploadedSessionId: string | null;
  lastReplayMediaUploadAtUnixMs?: number | null;
  lastReplayMediaError?: string | null;
  syncInProgress: boolean;
}

interface StatsHubSyncOverview {
  configured: boolean;
  enabled: boolean;
  accountLabel: string | null;
  status: StatsHubSyncStatus;
}

function formatHubUserError(message: string | null | undefined): string | null {
  const raw = message?.trim();
  if (!raw) return null;
  const lower = raw.toLowerCase();
  if (
    lower.includes("error sending request")
    || lower.includes("connection refused")
    || lower.includes("failed to connect")
    || lower.includes("dns error")
    || lower.includes("timeout")
    || lower.includes("timed out")
    || lower.includes("network")
    || lower.includes("certificate")
    || lower.includes("tls")
  ) {
    return "Could not connect to AimMod Hub. Retry later.";
  }
  return raw;
}

// ─── Helpers ───────────────────────────────────────────────────────────────────

/**
 * Strip the KovaaK's timestamp suffix from a scenario name so that runs of
 * the same scenario are grouped together regardless of how the name was stored.
 *
 * Handles all known formats, e.g.:
 *   "VT Aether Novice S5 - Challenge - 2026.02.25-12.15.10 Stats" → "VT Aether Novice S5"
 *   "Gridshot Ultimate - Challenge Start - 2024.01.15-12.30.45"   → "Gridshot Ultimate"
 */
function normalizeScenario(name: string): string {
  const m = name.match(/\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}/);
  if (!m || m.index === undefined) return name;
  const sep = name.lastIndexOf(" - ", m.index);
  return sep >= 0 ? name.slice(0, sep) : name;
}

function slugifyScenarioName(value: string): string {
  const normalized = value.toLowerCase().trim();
  let out = "";
  let lastDash = false;
  for (const ch of normalized) {
    const isAlphaNum = (ch >= "a" && ch <= "z") || (ch >= "0" && ch <= "9");
    if (isAlphaNum) {
      out += ch;
      lastDash = false;
      continue;
    }
    if ((ch === " " || ch === "-" || ch === "_" || ch === "'" || ch === ".") && !lastDash && out.length > 0) {
      out += "-";
      lastDash = true;
    }
  }
  return out.replace(/-+$/g, "");
}

const BENCHMARK_RANK_NAME_COLORS: Record<string, string> = {
  iron: "#6b8c8c",
  bronze: "#b07840",
  silver: "#9098a0",
  gold: "#c0a030",
  platinum: "#6eaec0",
  diamond: "#38c8c0",
  jade: "#38c868",
  master: "#a840c8",
  grandmaster: "#e04040",
  nova: "#ff8820",
};

const BENCHMARK_RANK_PALETTE = [
  "#c8956c",
  "#b0b8b0",
  "#e8c84a",
  "#7ec8e3",
  "#c084fc",
  "#60e0a0",
  "#f87171",
];

function benchmarkPaletteIndex(rankIndex: number | null | undefined): number {
  return Math.max(0, (rankIndex ?? 1) - 1);
}

function resolveBenchmarkColor(
  apiColor: string | null | undefined,
  rankName?: string | null,
  paletteIdx = 0,
): string {
  const normalizedRankName = rankName?.trim().toLowerCase();
  if (normalizedRankName) {
    const known = BENCHMARK_RANK_NAME_COLORS[normalizedRankName];
    if (known) return known;
  }

  const trimmedColor = apiColor?.trim();
  const normalizedColor = trimmedColor?.toLowerCase();
  if (trimmedColor && normalizedColor && normalizedColor !== "#ffffff" && normalizedColor !== "#fff" && normalizedColor !== "white") {
    return trimmedColor;
  }

  return BENCHMARK_RANK_PALETTE[Math.abs(paletteIdx) % BENCHMARK_RANK_PALETTE.length];
}

function parseTimestamp(ts: string): Date | null {
  if (!ts) return null;
  const [datePart, timePart] = ts.split("-");
  if (!datePart || !timePart) return null;
  const [y, mo, d] = datePart.split(".").map(Number);
  const [h, mi, s] = timePart.split(".").map(Number);
  return new Date(y, mo - 1, d, h, mi, s);
}

function formatDateTime(ts: string): string {
  const d = parseTimestamp(ts);
  if (!d) return ts;
  return d.toLocaleString("en-US", {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function relativeTime(ts: string): string {
  const d = parseTimestamp(ts);
  if (!d) return "";
  const diffMs = Date.now() - d.getTime();
  const diffMins = Math.floor(diffMs / 60_000);
  if (diffMins < 60) return diffMins <= 1 ? "just now" : `${diffMins}m ago`;
  const diffHours = Math.floor(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  if (diffDays < 7) return `${diffDays}d ago`;
  const diffWeeks = Math.floor(diffDays / 7);
  if (diffWeeks < 5) return `${diffWeeks}w ago`;
  const diffMonths = Math.floor(diffDays / 30);
  return `${diffMonths}mo ago`;
}

function formatPlayTime(secs: number): string {
  const h = Math.floor(secs / 3600);
  const m = Math.floor((secs % 3600) / 60);
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

function getDateRangeCutoff(range: DateRangePreset): number | null {
  if (range === "all") return null;
  const now = Date.now();
  if (range === "30d") return now - 30 * 24 * 60 * 60 * 1000;
  if (range === "90d") return now - 90 * 24 * 60 * 60 * 1000;
  return now - 365 * 24 * 60 * 60 * 1000;
}

function withinDateRange(timestamp: string, range: DateRangePreset): boolean {
  const cutoff = getDateRangeCutoff(range);
  if (cutoff == null) return true;
  const ts = parseTimestamp(timestamp)?.getTime();
  return ts != null && ts >= cutoff;
}

function summarizeScenario(records: SessionRecord[]): ScenarioSummary | null {
  if (records.length === 0) return null;
  const ordered = [...records].sort((a, b) =>
    (parseTimestamp(a.timestamp)?.getTime() ?? 0) - (parseTimestamp(b.timestamp)?.getTime() ?? 0),
  );
  const accuracyRecords = records.filter((record) => record.accuracy > 0);
  const latest = ordered[ordered.length - 1] ?? null;

  return {
    sessions: records.length,
    best: Math.max(...records.map((record) => record.score), 0),
    avgScore: mean(records.map((record) => record.score)),
    recentAvg: mean(ordered.slice(-5).map((record) => record.score)),
    avgAccuracy: accuracyRecords.length > 0 ? mean(accuracyRecords.map((record) => record.accuracy)) : null,
    totalDurationSecs: records.reduce((sum, record) => sum + (record.duration_secs ?? 0), 0),
    latestTimestamp: latest?.timestamp ?? null,
  };
}

function formatTelemetryOffset(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(ms >= 10_000 ? 0 : 1)}s`;
}

function isTrackingScenario(scenarioType: string | null | undefined): boolean {
  if (!scenarioType) return false;
  return scenarioType === "PureTracking" || scenarioType.includes("Tracking");
}

function isTargetSwitchingScenario(scenarioType: string | null | undefined): boolean {
  return scenarioType === "TargetSwitching" || scenarioType === "MultiHitClicking";
}

function isStaticClickingScenario(scenarioType: string | null | undefined): boolean {
  return scenarioType === "StaticClicking" || scenarioType === "OneShotClicking";
}

function isDynamicClickingScenario(scenarioType: string | null | undefined): boolean {
  return scenarioType === "DynamicClicking" || scenarioType === "MovingClicking" || scenarioType === "ReactiveClicking";
}

function isAccuracyScenario(scenarioType: string | null | undefined): boolean {
  return scenarioType === "AccuracyDrill";
}

function rangesOverlap(startA: number, endA: number, startB: number, endB: number): boolean {
  return startA <= endB && endA >= startB;
}

function sliceRowsToRange<T extends { timestamp_ms: number }>(rows: T[], startMs: number, endMs: number): T[] {
  if (rows.length === 0) return rows;
  let startIndex = rows.findIndex((row) => row.timestamp_ms >= startMs);
  if (startIndex === -1) return [];
  let endIndex = rows.length - 1;
  while (endIndex >= 0 && rows[endIndex].timestamp_ms > endMs) {
    endIndex -= 1;
  }
  if (endIndex < startIndex) return [];
  startIndex = Math.max(0, startIndex - 1);
  endIndex = Math.min(rows.length - 1, endIndex + 1);
  return rows.slice(startIndex, endIndex + 1);
}

function fmtScore(n: number) {
  if (!Number.isFinite(n)) return "--";
  const rounded = Math.round(n);
  if (Math.abs(n - rounded) < 0.0001) return rounded.toLocaleString();
  return n.toLocaleString(undefined, {
    minimumFractionDigits: 1,
    maximumFractionDigits: 2,
  });
}

function fmtDuration(secs: number) {
  if (secs < 60) return `${Math.round(secs)}s`;
  return `${Math.floor(secs / 60)}m ${Math.round(secs % 60)}s`;
}

function fmtLatencyMs(value: number | null | undefined) {
  if (value == null || !Number.isFinite(value)) return "—";
  return `${value.toFixed(0)}ms`;
}

function mean(arr: number[]) {
  return arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0;
}

type ShotTelemetryDisplayMode = "context" | "samples";

function formatTargetResponseKind(kind: TargetResponseEpisode["kind"]): string {
  return kind === "path_change" ? "Path break" : "Target switch";
}

function formatShotTelemetryEntitySuffix(entityId: string): string {
  const compact = entityId.replace(/[^a-zA-Z0-9]/g, "");
  if (!compact) return entityId;
  return compact.slice(-6).toUpperCase();
}

function formatShotTelemetryTargetLabel(
  profile: string | null | undefined,
  entityId: string,
  duplicateProfile: boolean,
): string {
  const baseLabel = profile?.trim() || entityId;
  if (!duplicateProfile) return baseLabel;
  return `${baseLabel} · ${formatShotTelemetryEntitySuffix(entityId)}`;
}

function selectShotTelemetryWindowMs(durationSecs: number | null, sampleCount: number): number {
  if (durationSecs != null) {
    if (durationSecs <= 45) return 5_000;
    if (durationSecs <= 120) return 10_000;
    if (durationSecs <= 300) return 15_000;
    return 30_000;
  }
  if (sampleCount <= 24) return 5_000;
  if (sampleCount <= 72) return 10_000;
  return 15_000;
}

function formatTelemetryWindowLabel(startMs: number, endMs: number): string {
  const start = formatTelemetryOffset(startMs);
  const end = formatTelemetryOffset(endMs);
  if (start === end) return start;
  return `${start}-${end}`;
}

function formatReplayMomentSourceLabel(source: "sql" | "derived"): string {
  return source === "sql" ? "saved moment" : "estimated moment";
}

function formatReplayMomentPhaseLabel(phase: string | null | undefined): string | null {
  if (!phase) return null;
  switch (phase) {
    case "opening":
      return "start of run";
    case "mid":
      return "mid run";
    case "closing":
      return "end of run";
    default:
      return phase.replace(/_/g, " ");
  }
}

function formatReplayMomentContextLabel(contextKind: string | null | undefined): string | null {
  if (!contextKind) return null;
  switch (contextKind) {
    case "mixed_cluster":
      return "mixed targets";
    case "metric_shift":
      return "pace shift";
    case "target_focus":
      return "single-target focus";
    default:
      return contextKind.replace(/_/g, " ");
  }
}

function scenarioTypeSortRank(type: string | null | undefined) {
  if (!type || type === "Unknown") return "zzzz-unknown";
  return type.toLowerCase();
}

function stddev(arr: number[]): number {
  if (arr.length < 2) return 0;
  const m = mean(arr);
  return Math.sqrt(mean(arr.map((v) => (v - m) ** 2)));
}

function median(arr: number[]): number {
  if (arr.length === 0) return 0;
  const sorted = [...arr].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
}

function medianAbsoluteDeviation(arr: number[], center = median(arr)): number {
  if (arr.length === 0) return 0;
  return median(arr.map((value) => Math.abs(value - center)));
}

function buildAnalyticsRecords(records: SessionRecord[]): AnalyticsSessionRecord[] {
  const durationsByScenario = new Map<string, number[]>();

  for (const record of records) {
    const normalizedScenario = normalizeScenario(record.scenario);
    const durations = durationsByScenario.get(normalizedScenario) ?? [];
    if (Number.isFinite(record.duration_secs) && record.duration_secs > 0) {
      durations.push(record.duration_secs);
    }
    durationsByScenario.set(normalizedScenario, durations);
  }

  const durationWindows = new Map<string, { lower: number; upper: number }>();
  for (const [scenario, durations] of durationsByScenario.entries()) {
    if (durations.length === 0) continue;
    const durationMedian = median(durations);
    const durationMad = medianAbsoluteDeviation(durations, durationMedian);
    const spreadFloor = Math.max(8, durationMedian * 0.3);
    const spread = Math.max(spreadFloor, durationMad * 10);
    durationWindows.set(scenario, {
      lower: Math.max(5, durationMedian - spread),
      upper: Math.max(durationMedian * 2.25, durationMedian + spread),
    });
  }

  return records.map((record) => {
    const normalizedScenario = normalizeScenario(record.scenario);
    const timestampMs = parseTimestamp(record.timestamp)?.getTime() ?? 0;
    const durationWindow = durationWindows.get(normalizedScenario);
    const isDurationOutlier = durationWindow
      ? record.duration_secs < durationWindow.lower || record.duration_secs > durationWindow.upper
      : false;
    const isZeroSignal = record.score <= 0
      && record.kills === 0
      && (record.damage_done ?? 0) <= 0;
    const qualityIssues: string[] = [];

    if (isDurationOutlier) qualityIssues.push("duration_outlier");
    if (isZeroSignal) qualityIssues.push("empty_run");
    if (!Number.isFinite(timestampMs) || timestampMs <= 0) qualityIssues.push("bad_timestamp");
    if (!Number.isFinite(record.score)) qualityIssues.push("bad_score");

    return {
      ...record,
      normalizedScenario,
      timestampMs,
      isDurationOutlier,
      isZeroSignal,
      isReliableForAnalysis:
        timestampMs > 0
        && Number.isFinite(record.score)
        && Number.isFinite(record.duration_secs)
        && record.duration_secs > 0
        && !isDurationOutlier
        && !isZeroSignal,
      qualityIssues,
    };
  });
}

function linearRegression(xs: number[], ys: number[]): { slope: number; intercept: number } {
  const n = xs.length;
  if (n < 2) return { slope: 0, intercept: n === 1 ? ys[0] : 0 };
  const sx = xs.reduce((a, b) => a + b, 0);
  const sy = ys.reduce((a, b) => a + b, 0);
  const sxy = xs.reduce((a, xi, i) => a + xi * ys[i], 0);
  const sxx = xs.reduce((a, xi) => a + xi ** 2, 0);
  const slope = (n * sxy - sx * sy) / (n * sxx - sx ** 2);
  const intercept = (sy - slope * sx) / n;
  return { slope, intercept };
}

function rollingMean(arr: number[], window: number): (number | null)[] {
  return arr.map((_, i) => {
    if (i < window - 1) return null;
    const slice = arr.slice(i - window + 1, i + 1);
    return Math.round(mean(slice));
  });
}

function percentileOf(sortedArr: number[], p: number): number {
  if (!sortedArr.length) return 0;
  const idx = Math.max(0, Math.ceil((sortedArr.length * p) / 100) - 1);
  return sortedArr[idx];
}

function clampNumber(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function metricDistribution(values: number[]): { median: number; p25: number; p75: number } | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return {
    median: percentileOf(sorted, 50),
    p25: percentileOf(sorted, 25),
    p75: percentileOf(sorted, 75),
  };
}

function scaleToScore(value: number, min: number, max: number): number {
  if (max <= min) return 0;
  return clampNumber(((value - min) / (max - min)) * 100, 0, 100);
}

function scaleToVolatility(iqr: number, span: number): number {
  if (span <= 0) return 0;
  return clampNumber((iqr / span) * 100, 0, 100);
}

function buildReplayContextCoaching(
  rows: ShotTelemetryContextRow[],
  scenarioType: string,
): ReplayContextCoachingSignal[] {
  if (rows.length === 0) return [];

  const ordered = [...rows].sort((a, b) => a.startMs - b.startMs);
  const accuracyDist = metricDistribution(
    ordered.map((row) => row.accuracyPct).filter((value): value is number => value != null),
  );
  const yawDist = metricDistribution(
    ordered.map((row) => row.yawError).filter((value): value is number => value != null),
  );
  const kpsDist = metricDistribution(
    ordered.map((row) => row.avgKillsPerSecond).filter((value): value is number => value != null),
  );
  const damageDist = metricDistribution(
    ordered.map((row) => row.avgDamageEfficiency).filter((value): value is number => value != null),
  );
  const tracking = isTrackingScenario(scenarioType);
  const signals: ReplayContextCoachingSignal[] = [];

  for (let index = 0; index < ordered.length; index += 1) {
    const row = ordered[index];
    const prev = index > 0 ? ordered[index - 1] : null;

    if (
      row.firedCount >= 6
      && row.accuracyPct != null
      && accuracyDist
      && row.accuracyPct <= Math.min(72, accuracyDist.median - 8)
    ) {
      signals.push({
        id: `${row.key}-correction`,
        contextKey: row.key,
        title: "Corrective-shot spike",
        badge: "Correction",
        badgeColor: "#00b4ff",
        detail: `${row.label} fell to ${row.accuracyPct.toFixed(1)}% accuracy across ${row.firedCount} fired shots, which is where extra recovery shots start stacking up.`,
        action: "Replay this window and look for the first miss that forces a second adjustment. Slow the entry slightly until first-shot conversion stabilizes.",
        startMs: row.startMs,
        endMs: row.endMs,
      });
    }

    if (
      row.yawError != null
      && yawDist
      && row.yawError >= Math.max(6, yawDist.median * 1.35)
      && row.firedCount >= 4
    ) {
      signals.push({
        id: `${row.key}-overshoot`,
        contextKey: row.key,
        title: tracking ? "Tracking overshoot pocket" : "Overshoot burst",
        badge: tracking ? "Tracking" : "Overshoot",
        badgeColor: "#ff9f43",
        detail: `${row.label} averaged ${row.yawError.toFixed(1)}° yaw error${row.nearestLabel !== "—" ? ` against ${row.nearestLabel}` : ""}, a clear sign that entry speed outran your stop control in this segment.`,
        action: tracking
          ? "Use this window as a smoothing rep: match target speed first, then let damage efficiency climb back before pushing pace."
          : "Replay this burst and brake earlier on approach. The goal is to land inside the target zone instead of correcting back through it.",
        startMs: row.startMs,
        endMs: row.endMs,
      });
    }

    if (
      prev
      && row.avgBotCount != null
      && row.avgBotCount >= 1.8
      && row.avgKillsPerSecond != null
      && kpsDist
      && row.avgKillsPerSecond <= kpsDist.median * 0.72
      && row.primaryTargetShare != null
      && row.primaryTargetShare <= 0.58
      && row.nearestLabel !== prev.nearestLabel
    ) {
      signals.push({
        id: `${row.key}-hesitation`,
        contextKey: row.key,
        title: "Target-switch hesitation",
        badge: "Switching",
        badgeColor: "#ffd700",
        detail: `${row.label} slowed to ${(row.avgKillsPerSecond ?? 0).toFixed(2)} KPS while juggling ${row.avgBotCount.toFixed(1)} bots and no single target owned more than ${Math.round(row.primaryTargetShare * 100)}% of the window.`,
        action: "Replay this segment and pre-plan the next target before the current shot finishes. The hesitation is happening between confirmations, not during the flick itself.",
        startMs: row.startMs,
        endMs: row.endMs,
      });
    }
  }

  if (tracking && ordered.length >= 3 && damageDist) {
    const splitIndex = Math.max(1, Math.floor(ordered.length / 2));
    const early = ordered.slice(0, splitIndex);
    const late = ordered.slice(splitIndex);
    const earlyDamage = mean(early.map((row) => row.avgDamageEfficiency).filter((value): value is number => value != null));
    const lateDamage = mean(late.map((row) => row.avgDamageEfficiency).filter((value): value is number => value != null));
    const earlyYaw = mean(early.map((row) => row.yawError).filter((value): value is number => value != null));
    const lateYaw = mean(late.map((row) => row.yawError).filter((value): value is number => value != null));

    if (lateDamage > 0 && earlyDamage > 0 && lateDamage <= earlyDamage - 8 && lateYaw >= earlyYaw + 1.5) {
      const lateStart = late[0]?.startMs ?? 0;
      const lateEnd = late[late.length - 1]?.endMs ?? lateStart;
      signals.push({
        id: "tracking-stability-decay",
        contextKey: late[0]?.key ?? ordered[ordered.length - 1].key,
        title: "Tracking stability decay",
        badge: "Decay",
        badgeColor: "#a78bfa",
        detail: `Late-run tracking damage efficiency dropped ${earlyDamage.toFixed(1)}% → ${lateDamage.toFixed(1)}% while yaw error climbed ${earlyYaw.toFixed(1)}° → ${lateYaw.toFixed(1)}°. Contact quality is fading as the run progresses.`,
        action: "Use the late segment as an endurance rep. Keep cursor speed even and reduce reactivity spikes before trying to recover score.",
        startMs: lateStart,
        endMs: lateEnd,
      });
    }
  }

  return signals.slice(0, 5);
}

interface RunMomentInsight {
  id: string;
  level: "good" | "tip" | "warning";
  title: string;
  detail: string;
  metric: "spm" | "accuracy" | "kps" | "damage_eff";
  startSec: number;
  endSec: number;
}

interface SessionRunCoachingAnalysis {
  keyMoments: RunMomentInsight[];
}

interface LocalLlmRuntimeStatus {
  state: string;
  detail: string;
  canStart: boolean;
  assetRoot: string;
  manifestPath: string;
  runnerPath: string;
  modelPath: string;
  endpoint: string;
  modelId: string;
  pid: number | null;
  launchedAtUnixMs: number | null;
}

interface LocalCoachInputCard {
  title: string;
  badge: string;
  body: string;
  tip: string;
  signals: string[];
}

interface LocalCoachKnowledgePreview {
  id: string;
  title: string;
  summary: string;
  actions: string[];
  avoid: string[];
}

interface LocalCoachVisualPoint {
  label: string;
  value: number;
  secondaryValue?: number | null;
  note: string;
  values?: Record<string, number>;
}

interface LocalCoachVisualSeries {
  key: string;
  label: string;
  kind: string;
  color: string;
}

interface LocalCoachVisual {
  id: string;
  kind: string;
  title: string;
  subtitle: string;
  primaryLabel: string;
  secondaryLabel: string;
  points: LocalCoachVisualPoint[];
  series: LocalCoachVisualSeries[];
  detailLines: string[];
}

interface LocalCoachFact {
  key: string;
  label: string;
  valueText: string;
  numericValue?: number;
  boolValue?: boolean;
  direction: string;
  confidence: string;
}

interface LocalCoachTurn {
  question: string;
  answer: string;
}

interface LocalCoachChatResponse {
  message: string;
  model: string;
  runtimeStatus: LocalLlmRuntimeStatus;
  knowledgeItems: LocalCoachKnowledgePreview[];
  visuals: LocalCoachVisual[];
}

interface LocalCoachStreamEvent {
  streamId: string;
  kind: string;
  delta: string;
  content: string;
  done: boolean;
  error: string | null;
}

const LOCAL_COACH_STREAM_EVENT = "local-coach-stream";
const LOCAL_COACH_VISUAL_REF_RE = /!?\[\[(?:visual:)?([^[\]]+)\]\]/gi;

function LocalCoachMarkdown({ content }: { content: string }) {
  return (
    <div
      style={{
        background: "rgba(255,255,255,0.03)",
        border: `1px solid ${C.border}`,
        borderRadius: 12,
        padding: "12px 14px",
        fontSize: 12,
        color: C.textSub,
        lineHeight: 1.75,
      }}
    >
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          h1: ({ children }) => <h1 style={{ margin: "0 0 10px", fontSize: 18, lineHeight: 1.25, color: C.text }}>{children}</h1>,
          h2: ({ children }) => <h2 style={{ margin: "14px 0 8px", fontSize: 15, lineHeight: 1.3, color: C.text }}>{children}</h2>,
          h3: ({ children }) => <h3 style={{ margin: "12px 0 6px", fontSize: 13, lineHeight: 1.35, color: C.text }}>{children}</h3>,
          p: ({ children }) => <p style={{ margin: "0 0 10px" }}>{children}</p>,
          ul: ({ children }) => <ul style={{ margin: "0 0 10px", paddingLeft: 18 }}>{children}</ul>,
          ol: ({ children }) => <ol style={{ margin: "0 0 10px", paddingLeft: 18 }}>{children}</ol>,
          li: ({ children }) => <li style={{ marginBottom: 4 }}>{children}</li>,
          strong: ({ children }) => <strong style={{ color: C.text, fontWeight: 700 }}>{children}</strong>,
          code: (({ className, children }: any) =>
            !className ? (
              <code
                style={{
                  background: "rgba(255,255,255,0.06)",
                  border: `1px solid ${C.borderSub}`,
                  borderRadius: 6,
                  padding: "1px 5px",
                  fontSize: "0.95em",
                  color: C.text,
                }}
              >
                {children}
              </code>
            ) : (
              <code>{children}</code>
            )) as any,
          pre: ({ children }) => (
            <pre
              style={{
                margin: "0 0 10px",
                padding: "10px 12px",
                overflowX: "auto",
                borderRadius: 10,
                background: "rgba(0,0,0,0.22)",
                border: `1px solid ${C.borderSub}`,
                color: C.text,
              }}
            >
              {children}
            </pre>
          ),
          blockquote: ({ children }) => (
            <blockquote
              style={{
                margin: "0 0 10px",
                padding: "2px 0 2px 12px",
                borderLeft: `3px solid ${C.accentBorder}`,
                color: C.textSub,
              }}
            >
              {children}
            </blockquote>
          ),
          img: ({ alt, src }) => (
            <div
              style={{
                margin: "0 0 10px",
                padding: "10px 12px",
                borderRadius: 10,
                border: `1px solid ${C.borderSub}`,
                background: "rgba(255,255,255,0.04)",
                color: C.textSub,
                fontSize: 11,
              }}
            >
              Invalid generated chart reference{alt ? `: ${alt}` : ""}{src ? ` (${src})` : ""}.
            </div>
          ),
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  );
}

function normalizeInlineMarkdownSegment(segment: string): string {
  return segment
    .replace(/^\n+/, "")
    .replace(/\n+$/, "")
    .replace(/[ \t]+\n/g, "\n");
}

function effectiveVisualSeries(visual: LocalCoachVisual): LocalCoachVisualSeries[] {
  if (visual.series?.length) {
    return visual.series.map((series) => ({
      key: series.key,
      label: series.label || series.key,
      kind: series.kind || (visual.kind === "bar" ? "bar" : "line"),
      color: series.color || "",
    }));
  }

  const inferred: LocalCoachVisualSeries[] = [
    {
      key: "value",
      label: visual.primaryLabel || "Value",
      kind: visual.kind === "bar" ? "bar" : "line",
      color: "",
    },
  ];
  if (visual.secondaryLabel && visual.points.some((point) => point.secondaryValue != null || point.values?.secondaryValue != null)) {
    inferred.push({
      key: "secondaryValue",
      label: visual.secondaryLabel,
      kind: visual.kind === "bar" ? "bar" : "line",
      color: "",
    });
  }
  return inferred;
}

function visualChartData(visual: LocalCoachVisual): Array<Record<string, number | string | null>> {
  return visual.points.map((point) => ({
    label: point.label,
    note: point.note,
    value: point.value,
    secondaryValue: point.secondaryValue ?? null,
    ...(point.values ?? {}),
  }));
}

function visualSeriesColor(series: LocalCoachVisualSeries, index: number): string {
  if (series.color?.trim()) return series.color.trim();
  const palette = [C.accent, "#ffb400", "#53d3ff", "#7ef29a", "#ff7aa2", "#9f8cff"];
  return palette[index % palette.length];
}

function seriesNumericValues(
  entry: LocalCoachVisualSeries,
  chartData: Array<Record<string, number | string | null>>,
): number[] {
  return chartData
    .map((row) => row[entry.key])
    .filter((value): value is number => typeof value === "number" && Number.isFinite(value));
}

function visualAxisAssignment(
  series: LocalCoachVisualSeries[],
  chartData: Array<Record<string, number | string | null>>,
): Record<string, "left" | "right"> {
  const assignments: Record<string, "left" | "right"> = {};
  if (series.length <= 1) {
    for (const entry of series) assignments[entry.key] = "left";
    return assignments;
  }

  const stats = series
    .map((entry) => {
      const values = seriesNumericValues(entry, chartData);
      const maxAbs = values.reduce((acc, value) => Math.max(acc, Math.abs(value)), 0);
      return { entry, maxAbs };
    })
    .sort((a, b) => b.maxAbs - a.maxAbs);

  for (const { entry } of stats) assignments[entry.key] = "left";

  const [largest, second] = stats;
  if (!largest || largest.maxAbs <= 0) {
    return assignments;
  }

  const secondMax = second?.maxAbs ?? 0;
  const ratio = secondMax > 0 ? largest.maxAbs / secondMax : Infinity;
  if (ratio < 8) {
    return assignments;
  }

  assignments[largest.entry.key] = "right";
  return assignments;
}

function LocalCoachVisualCard({ visual }: { visual: LocalCoachVisual }) {
  const series = effectiveVisualSeries(visual);
  const chartData = visualChartData(visual);
  const axisBySeries = visualAxisAssignment(series, chartData);
  const hasRightAxis = series.some((entry) => axisBySeries[entry.key] === "right");

  return (
    <div
      style={{
        borderRadius: 12,
        border: `1px solid ${C.border}`,
        background: "rgba(255,255,255,0.02)",
        padding: 12,
        display: "flex",
        flexDirection: "column",
        gap: 8,
      }}
    >
      <div>
        <div style={{ fontSize: 12, fontWeight: 700, color: C.text }}>{visual.title}</div>
        {visual.subtitle && (
          <div style={{ marginTop: 4, fontSize: 11, color: C.textSub, lineHeight: 1.5 }}>
            {visual.subtitle}
          </div>
        )}
      </div>
      <ResponsiveContainer width="100%" height={220}>
        {visual.kind === "combo" ? (
          <ComposedChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
            <XAxis dataKey="label" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} interval={0} angle={-18} textAnchor="end" height={48} />
            <YAxis yAxisId="left" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} width={40} />
            {hasRightAxis ? <YAxis yAxisId="right" orientation="right" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} width={52} /> : null}
            <Tooltip content={<MiniTooltip />} />
            {series.map((entry, index) =>
              entry.kind === "bar" ? (
                <Bar yAxisId={axisBySeries[entry.key] ?? "left"} key={entry.key} dataKey={entry.key} fill={visualSeriesColor(entry, index)} radius={[6, 6, 0, 0]} name={entry.label} />
              ) : (
                <Line yAxisId={axisBySeries[entry.key] ?? "left"} key={entry.key} type="monotone" dataKey={entry.key} stroke={visualSeriesColor(entry, index)} strokeWidth={2.2} dot={false} name={entry.label} />
              ),
            )}
          </ComposedChart>
        ) : visual.kind === "bar" ? (
          <BarChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
            <XAxis dataKey="label" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} interval={0} angle={-18} textAnchor="end" height={48} />
            <YAxis yAxisId="left" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} width={40} />
            {hasRightAxis ? <YAxis yAxisId="right" orientation="right" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} width={52} /> : null}
            <Tooltip content={<MiniTooltip />} />
            {series.map((entry, index) => (
              <Bar yAxisId={axisBySeries[entry.key] ?? "left"} key={entry.key} dataKey={entry.key} fill={visualSeriesColor(entry, index)} radius={[6, 6, 0, 0]} name={entry.label} />
            ))}
          </BarChart>
        ) : (
          <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
            <XAxis dataKey="label" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} interval={0} angle={-18} textAnchor="end" height={48} />
            <YAxis yAxisId="left" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} width={40} />
            {hasRightAxis ? <YAxis yAxisId="right" orientation="right" tick={{ fill: "rgba(255,255,255,0.45)", fontSize: 10 }} width={52} /> : null}
            <Tooltip content={<MiniTooltip />} />
            {series.map((entry, index) => (
              <Line yAxisId={axisBySeries[entry.key] ?? "left"} key={entry.key} type="monotone" dataKey={entry.key} stroke={visualSeriesColor(entry, index)} strokeWidth={2.2} dot={false} name={entry.label} />
            ))}
          </LineChart>
        )}
      </ResponsiveContainer>
      <div style={{ display: "flex", gap: 10, flexWrap: "wrap", fontSize: 10, color: C.textFaint }}>
        {series.map((entry) => (
          <span key={entry.key}>{entry.label}</span>
        ))}
      </div>
      {visual.detailLines?.length ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: 11, color: C.textSub, lineHeight: 1.6 }}>
          {visual.detailLines.map((line, index) => (
            <div key={`${visual.id}-detail-${index}`}>{line}</div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function LocalCoachVisuals({ visuals, heading = "Additional visuals" }: { visuals: LocalCoachVisual[]; heading?: string }) {
  if (visuals.length === 0) return null;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
      <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 700 }}>
        {heading}
      </div>
      <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
        {visuals.map((visual) => (
          <div key={visual.id} style={{ flex: "1 1 300px", minWidth: 260 }}>
            <LocalCoachVisualCard visual={visual} />
          </div>
        ))}
      </div>
    </div>
  );
}

function LocalCoachResponseBody({ content, visuals }: { content: string; visuals: LocalCoachVisual[] }) {
  const byId = new Map(visuals.map((visual) => [visual.id.toLowerCase(), visual]));
  const parts: Array<{ key: string; kind: "markdown" | "visual"; value: string }> = [];
  let lastIndex = 0;

  for (const match of content.matchAll(LOCAL_COACH_VISUAL_REF_RE)) {
    const full = match[0];
    const rawId = match[1];
    const offset = match.index ?? 0;
    const before = content.slice(lastIndex, offset);
    const normalizedBefore = normalizeInlineMarkdownSegment(before);
    if (normalizedBefore.trim()) {
      parts.push({
        key: `text-${lastIndex}`,
        kind: "markdown",
        value: normalizedBefore,
      });
    }
    parts.push({
      key: `visual-${offset}`,
      kind: "visual",
      value: rawId.trim().toLowerCase(),
    });
    lastIndex = offset + full.length;
  }

  const tail = content.slice(lastIndex);
  const normalizedTail = normalizeInlineMarkdownSegment(tail);
  if (normalizedTail.trim()) {
    parts.push({
      key: `text-tail-${lastIndex}`,
      kind: "markdown",
      value: normalizedTail,
    });
  }

  const hasInlineRefs = parts.some((part) => part.kind === "visual");
  const referencedVisualIds = new Set(
    parts
      .filter((part) => part.kind === "visual")
      .map((part) => part.value.toLowerCase()),
  );
  const extraVisuals = visuals.filter(
    (visual) => !referencedVisualIds.has(visual.id.toLowerCase()),
  );

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      {parts.length > 0 ? (
        parts.map((part) => {
          if (part.kind === "markdown") {
            return <LocalCoachMarkdown key={part.key} content={part.value} />;
          }
          const visual = byId.get(part.value);
          if (!visual) return null;
          return <LocalCoachVisualCard key={part.key} visual={visual} />;
        })
      ) : (
        <LocalCoachMarkdown content={content} />
      )}
      {extraVisuals.length > 0 && (
        <LocalCoachVisuals
          visuals={extraVisuals}
          heading={hasInlineRefs ? "Additional visuals" : "Coach visuals"}
        />
      )}
    </div>
  );
}

function runAvg(values: number[]): number | null {
  if (values.length === 0) return null;
  return values.reduce((sum, val) => sum + val, 0) / values.length;
}

function runValuesInRange(
  points: BridgeRunTimelinePoint[],
  startSec: number,
  endSec: number,
  pick: (point: BridgeRunTimelinePoint) => number | null,
): number[] {
  return points
    .filter((point) => point.t_sec >= startSec && point.t_sec <= endSec)
    .map(pick)
    .filter((val): val is number => val != null && Number.isFinite(val));
}

interface RunWindowShotStats {
  shotsFired: number;
  shotsHit: number;
  hitRatePct: number;
  shotsPerHit: number;
}

function runShotStatsInRange(
  points: BridgeRunTimelinePoint[],
  startSec: number,
  endSec: number,
): RunWindowShotStats | null {
  const window = points.filter((point) => point.t_sec >= startSec && point.t_sec <= endSec);
  if (window.length < 2) return null;

  const firedVals = window
    .map((point) => point.shots_fired)
    .filter((val): val is number => val != null && Number.isFinite(val));
  const hitVals = window
    .map((point) => point.shots_hit)
    .filter((val): val is number => val != null && Number.isFinite(val));

  if (firedVals.length < 2 || hitVals.length < 2) return null;

  const shotsFired = Math.max(0, Math.max(...firedVals) - Math.min(...firedVals));
  const shotsHit = Math.max(0, Math.max(...hitVals) - Math.min(...hitVals));
  if (shotsFired < 6) return null;

  const hitRatePct = shotsFired > 0 ? (shotsHit / shotsFired) * 100 : 0;
  const shotsPerHit = shotsHit > 0 ? shotsFired / shotsHit : shotsFired;

  return {
    shotsFired,
    shotsHit,
    hitRatePct,
    shotsPerHit,
  };
}

export function buildRunMomentInsights(
  points: BridgeRunTimelinePoint[],
  durationSecs: number | null | undefined,
): RunMomentInsight[] {
  if (points.length < 4) return [];
  const totalSecs = Math.max(1, Math.round(durationSecs ?? points[points.length - 1].t_sec));

  const earlyStart = 0;
  const earlyEnd = Math.max(1, Math.floor(totalSecs / 3));
  const lateStart = Math.max(0, Math.floor((totalSecs * 2) / 3));
  const lateEnd = totalSecs;

  const earlySpm = runAvg(runValuesInRange(points, earlyStart, earlyEnd, (p) => p.score_per_minute));
  const lateSpm = runAvg(runValuesInRange(points, lateStart, lateEnd, (p) => p.score_per_minute));
  const earlyAcc = runAvg(runValuesInRange(points, earlyStart, earlyEnd, (p) => p.accuracy_pct));
  const lateAcc = runAvg(runValuesInRange(points, lateStart, lateEnd, (p) => p.accuracy_pct));
  const earlyShotStats = runShotStatsInRange(points, earlyStart, earlyEnd);
  const lateShotStats = runShotStatsInRange(points, lateStart, lateEnd);

  const midStart = earlyEnd;
  const midEnd = Math.max(midStart + 1, lateStart);
  const thirds = [
    { label: "opening", startSec: earlyStart, endSec: earlyEnd },
    { label: "mid-run", startSec: midStart, endSec: midEnd },
    { label: "closing", startSec: lateStart, endSec: lateEnd },
  ]
    .map((window) => ({
      ...window,
      stats: runShotStatsInRange(points, window.startSec, window.endSec),
    }))
    .filter((window) => window.stats != null) as Array<{
    label: string;
    startSec: number;
    endSec: number;
    stats: RunWindowShotStats;
  }>;

  const peakSpmPoint = points
    .filter((p) => p.score_per_minute != null)
    .reduce<BridgeRunTimelinePoint | null>((best, curr) => {
      if (curr.score_per_minute == null) return best;
      if (!best || best.score_per_minute == null) return curr;
      return curr.score_per_minute > best.score_per_minute ? curr : best;
    }, null);

  const minAccPoint = points
    .filter((p) => p.accuracy_pct != null)
    .reduce<BridgeRunTimelinePoint | null>((worst, curr) => {
      if (curr.accuracy_pct == null) return worst;
      if (!worst || worst.accuracy_pct == null) return curr;
      return curr.accuracy_pct < worst.accuracy_pct ? curr : worst;
    }, null);

  const moments: RunMomentInsight[] = [];

  if (
    earlySpm != null
    && lateSpm != null
    && earlySpm > 0
    && (earlySpm - lateSpm) / earlySpm > 0.12
  ) {
    const accDelta = earlyAcc != null && lateAcc != null ? lateAcc - earlyAcc : null;
    const improvedAccuracy = accDelta != null && accDelta >= 2.5;

    moments.push({
      id: "moment-late-spm-fade",
      level: improvedAccuracy ? "tip" : "warning",
      title: improvedAccuracy ? "Speed→Accuracy Trade-off Late" : "Late-Run Pace Drop",
      detail: improvedAccuracy
        ? `Pace fell from ${Math.round(earlySpm)} to ${Math.round(lateSpm)} SPM while accuracy improved by ${accDelta!.toFixed(1)}%. Keep this control and add pace back gradually.`
        : `Pace fell from ${Math.round(earlySpm)} to ${Math.round(lateSpm)} SPM in the final third without a meaningful accuracy gain.`,
      metric: "spm",
      startSec: lateStart,
      endSec: lateEnd,
    });
  }

  if (earlyAcc != null && lateAcc != null && lateAcc - earlyAcc >= 3) {
    moments.push({
      id: "moment-accuracy-build",
      level: "good",
      title: "Accuracy Stabilized",
      detail:
        earlyShotStats && lateShotStats
          ? `Accuracy improved ${earlyAcc.toFixed(1)}% → ${lateAcc.toFixed(1)}%, and shots-per-hit improved ${earlyShotStats.shotsPerHit.toFixed(2)} → ${lateShotStats.shotsPerHit.toFixed(2)}.`
          : `Accuracy improved from ${earlyAcc.toFixed(1)}% to ${lateAcc.toFixed(1)}% late-run.`,
      metric: "accuracy",
      startSec: lateStart,
      endSec: lateEnd,
    });
  }

  if (thirds.length > 0) {
    const worstCorrectionWindow = [...thirds]
      .sort((a, b) => b.stats.shotsPerHit - a.stats.shotsPerHit)[0];

    if (worstCorrectionWindow.stats.shotsPerHit >= 1.6) {
      moments.push({
        id: "moment-correction-window",
        level: worstCorrectionWindow.stats.shotsPerHit >= 2.0 ? "warning" : "tip",
        title: "Correction-Heavy Window",
        detail: `${worstCorrectionWindow.label} needed ${worstCorrectionWindow.stats.shotsPerHit.toFixed(2)} shots per hit (${worstCorrectionWindow.stats.hitRatePct.toFixed(0)}% hit conversion).`,
        metric: "damage_eff",
        startSec: worstCorrectionWindow.startSec,
        endSec: worstCorrectionWindow.endSec,
      });
    }
  }

  if (peakSpmPoint?.score_per_minute != null) {
    moments.push({
      id: "moment-peak-spm",
      level: "good",
      title: "Peak Tempo Window",
      detail:
        peakSpmPoint.accuracy_pct != null
          ? `Best pace reached ${Math.round(peakSpmPoint.score_per_minute)} SPM at ${Math.round(peakSpmPoint.t_sec)}s with ${peakSpmPoint.accuracy_pct.toFixed(1)}% accuracy.`
          : `Best pace reached ${Math.round(peakSpmPoint.score_per_minute)} SPM at ${Math.round(peakSpmPoint.t_sec)}s.`,
      metric: "spm",
      startSec: Math.max(0, peakSpmPoint.t_sec - 4),
      endSec: Math.min(totalSecs, peakSpmPoint.t_sec + 4),
    });
  }

  if (minAccPoint?.accuracy_pct != null && minAccPoint.accuracy_pct < 78) {
    moments.push({
      id: "moment-low-accuracy",
      level: "tip",
      title: "Low Accuracy Pocket",
      detail: `Lowest point reached ${minAccPoint.accuracy_pct.toFixed(1)}% accuracy around ${Math.round(minAccPoint.t_sec)}s.`,
      metric: "accuracy",
      startSec: Math.max(0, minAccPoint.t_sec - 4),
      endSec: Math.min(totalSecs, minAccPoint.t_sec + 4),
    });
  }

  const levelRank: Record<RunMomentInsight["level"], number> = {
    warning: 0,
    tip: 1,
    good: 2,
  };

  return moments
    .sort((a, b) => levelRank[a.level] - levelRank[b.level])
    .slice(0, 4);
}

export function formatRunWindow(startSec: number, endSec: number): string {
  const start = Math.max(0, Math.round(startSec));
  const end = Math.max(start, Math.round(endSec));
  return `${start}s–${end}s`;
}

export function runMomentAction(moment: RunMomentInsight): string {
  switch (moment.id) {
    case "moment-late-spm-fade":
      return moment.level === "warning"
        ? "Do 3 reps where you hold your opening rhythm into the final third before trying to push speed."
        : "Keep this late-run control, then add pace back in 3–5% steps while preserving conversion.";
    case "moment-correction-window":
      return "Run this window at ~90% speed and focus on first-shot placement; only speed up after conversion stabilizes.";
    case "moment-low-accuracy":
      return "Pre-aim one target ahead in this window and delay the click slightly until the cursor is settled.";
    case "moment-accuracy-build":
      return "Use your opening pace for the first 5s, then apply this same settled timing earlier in the run.";
    case "moment-peak-spm":
      return "Anchor your rhythm to this window and repeat it across adjacent segments instead of short bursts.";
    default:
      return "Replay this window for 3 focused reps and change only one variable (speed, confirmation timing, or target switch plan).";
  }
}

// ─── Warmup detection ─────────────────────────────────────────────────────────

/** Sessions within a single continuous play block (gap < WARMUP_GAP_MS). */
interface PlayBlock<T extends SessionRecord = SessionRecord> {
  sessions: T[];
  /** ms gap from last session of previous block; null = first ever block. */
  gapBeforeMs: number | null;
}

const WARMUP_GAP_MS = 6 * 60 * 60 * 1000; // separates independent play blocks

function groupIntoPlayBlocks<T extends SessionRecord>(sorted: T[]): PlayBlock<T>[] {
  if (sorted.length === 0) return [];
  const blocks: PlayBlock<T>[] = [];
  let current: T[] = [sorted[0]];
  let blockGap: number | null = null;
  for (let i = 1; i < sorted.length; i++) {
    const prev = parseTimestamp(sorted[i - 1].timestamp)?.getTime() ?? 0;
    const cur  = parseTimestamp(sorted[i].timestamp)?.getTime() ?? 0;
    const gap  = cur - prev;
    if (gap > WARMUP_GAP_MS) {
      blocks.push({ sessions: current, gapBeforeMs: blockGap });
      blockGap = gap;
      current = [sorted[i]];
    } else {
      current.push(sorted[i]);
    }
  }
  blocks.push({ sessions: current, gapBeforeMs: blockGap });
  return blocks;
}

/**
 * Returns a Set of session IDs classified as warmup runs.
 * A session is warmup if it sits in the early under-baseline portion of a play
 * block and the block later recovers toward the player's normal score band.
 * This keeps the segmentation block-based while making classification depend on
 * scenario-relative performance, not a fixed number of early runs.
 */
function classifyWarmup(sorted: AnalyticsSessionRecord[]): Set<string> {
  const reliable = sorted.filter((record) => record.isReliableForAnalysis);
  if (reliable.length < 6) return new Set();

  const baselineMedian = median(reliable.map((record) => record.score));
  const baselineMad = medianAbsoluteDeviation(
    reliable.map((record) => record.score),
    baselineMedian,
  );
  const scoreScale = Math.max(baselineMad * 1.4826, baselineMedian * 0.06, 1);
  const warmupIds = new Set<string>();

  for (const block of groupIntoPlayBlocks(reliable)) {
    if (block.sessions.length < 3) continue;

    const zScores = block.sessions.map(
      (session) => (session.score - baselineMedian) / scoreScale,
    );
    const recoveredIndex = zScores.findIndex((zScore) => zScore >= -0.15);
    const blockPeak = Math.max(...zScores);

    if (recoveredIndex <= 0 || blockPeak < 0) continue;

    const recoveredZ = zScores[recoveredIndex];
    for (let i = 0; i < recoveredIndex; i++) {
      const zScore = zScores[i];
      if (zScore <= -0.25 && recoveredZ - zScore >= 0.35 && blockPeak - zScore >= 0.5) {
        warmupIds.add(block.sessions[i].id);
      }
    }
  }

  return warmupIds;
}

type SessionFilter = "all" | "warmup" | "warmedup";

// ─── Sub-components ────────────────────────────────────────────────────────────

function StatCard({
  label,
  value,
  sub,
  accent,
}: {
  label: string;
  value: string;
  sub?: string;
  accent?: string;
}) {
  return (
    <div style={CARD_STYLE}>
      <div
        style={{
          fontSize: 9,
          color: C.textFaint,
          textTransform: "uppercase",
          letterSpacing: "0.1em",
          marginBottom: 6,
          fontWeight: 600,
        }}
      >
        {label}
      </div>
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          width: "100%",
          minWidth: 0,
        }}
      >
        <span
          className="tabular-nums"
          style={{
            fontSize: "clamp(14px, 2.5vw, 20px)",
            fontWeight: 700,
            color: accent ?? C.text,
            lineHeight: 1,
            maxWidth: "100%",
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
        >
          {value}
        </span>
      </div>
      {sub && (
        <div style={{ fontSize: 10, color: C.textFaint, marginTop: 4 }}>
          {sub}
        </div>
      )}
    </div>
  );
}

function InfoTip({ text }: { text: string }) {
  return (
    <span
      title={text}
      aria-label={text}
      style={{
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
        width: 14,
        height: 14,
        borderRadius: "50%",
        border: "1px solid rgba(255,255,255,0.18)",
        background: "rgba(255,255,255,0.05)",
        color: "rgba(255,255,255,0.56)",
        fontSize: 9,
        fontWeight: 700,
        lineHeight: 1,
        cursor: "help",
        verticalAlign: "middle",
        marginLeft: 6,
      }}
    >
      i
    </span>
  );
}

function HoverInfoCard({
  title,
  summary,
  detail,
}: {
  title: string;
  summary: string;
  detail: string;
}) {
  const [open, setOpen] = useState(false);

  return (
    <div
      style={{ position: "relative", display: "inline-flex" }}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)}
      onBlur={() => setOpen(false)}
    >
      <span
        role="button"
        tabIndex={0}
        style={{
          fontSize: 11,
          color: "rgba(255,255,255,0.5)",
          cursor: "help",
          textDecoration: "underline dotted rgba(255,255,255,0.22)",
          textUnderlineOffset: 3,
          outline: "none",
        }}
      >
        {title}
      </span>
      {open && (
        <div
          style={{
            position: "absolute",
            left: 0,
            top: "calc(100% + 8px)",
            width: 240,
            zIndex: 30,
            background: "rgba(5,8,18,0.96)",
            border: "1px solid rgba(255,255,255,0.12)",
            borderRadius: 10,
            padding: "10px 12px",
            boxShadow: "0 16px 36px rgba(0,0,0,0.32)",
            backdropFilter: "blur(10px)",
            pointerEvents: "none",
          }}
        >
          <div style={{ fontSize: 11, fontWeight: 700, color: "rgba(255,255,255,0.9)", marginBottom: 4 }}>
            {title}
          </div>
          <div style={{ fontSize: 11, color: "rgba(255,255,255,0.62)", lineHeight: 1.55 }}>
            {summary}
          </div>
          <div style={{ fontSize: 10, color: "rgba(255,255,255,0.4)", lineHeight: 1.55, marginTop: 6 }}>
            {detail}
          </div>
        </div>
      )}
    </div>
  );
}

function SectionTitle({ children, info }: { children: React.ReactNode; info?: string }) {
  return (
    <SectionLabel className="mb-3">
      {children}
      {info ? <InfoTip text={info} /> : null}
    </SectionLabel>
  );
}

function MiniTooltip({ active, payload }: TooltipProps<number, string>) {
  if (!active || !payload?.length) return null;
  const d = payload[0]?.payload;
  return (
    <div style={{ ...TOOLTIP_STYLE, padding: "10px 14px", minWidth: 160 }}>
      <div
        style={{ color: "rgba(255,255,255,0.45)", marginBottom: 6, fontSize: 11 }}
      >
        {d?.dateLabel}
      </div>
      {payload.map((p) => (
        <div
          key={p.dataKey}
          style={{
            display: "flex",
            justifyContent: "space-between",
            gap: 16,
            marginBottom: 2,
          }}
        >
          <span style={{ color: "rgba(255,255,255,0.55)" }}>{p.name}</span>
          <span style={{ fontWeight: 700, color: p.color as string }}>
            {typeof p.value === "number"
              ? p.value >= 1000
                ? fmtScore(p.value)
                : p.value.toFixed(2)
              : p.value}
          </span>
        </div>
      ))}
    </div>
  );
}

// ─── Insight detection ────────────────────────────────────────────────────────

interface Insight {
  kind: "issue" | "positive";
  severity?: "high" | "medium" | "low";
  /** "mouse" = derived from smoothness metrics; "game" = derived from stats-panel data */
  category: "mouse" | "game";
  title: string;
  description: string;
}

function detectInsights(records: SessionRecord[]): Insight[] {
  const smoothRecords = records.filter((r) => r.smoothness !== null);
  const panelRecords  = records.filter((r) => r.stats_panel !== null);
  const shotTimingRecords = records.filter((r) => r.shot_timing != null);
  const insights: Insight[] = [];

  // Derive scenario type from the most recent panel record that has one
  const scenarioType =
    panelRecords.length > 0
      ? (panelRecords[panelRecords.length - 1].stats_panel!.scenario_type ?? "Unknown")
      : "Unknown";

  const isTracking = isTrackingScenario(scenarioType);
  const isOneShot  = isStaticClickingScenario(scenarioType);
  const isMultiHit = isTargetSwitchingScenario(scenarioType);
  const isReactive = isDynamicClickingScenario(scenarioType);
  const isAccuracy = isAccuracyScenario(scenarioType);

  // ── Mouse / smoothness insights ──────────────────────────────────────────────
  if (smoothRecords.length > 0) {
    const g = (fn: (s: SmoothnessSnapshot) => number) =>
      mean(smoothRecords.map((r) => fn(r.smoothness!)));

    const composite  = g((s) => s.composite);
    const jitter     = g((s) => s.jitter);
    const overshoot  = g((s) => s.overshoot_rate);
    const path       = g((s) => s.path_efficiency);
    const correction = g((s) => s.correction_ratio);
    const bias       = g((s) => s.directional_bias);
    const clickCV    = g((s) => s.click_timing_cv);
    const velStd     = g((s) => s.velocity_std);

    // Composite smoothness
    const smoothPositiveCtx = isTracking ? "tracking lines are clean and on-axis"
      : isOneShot   ? "flicks are landing accurately with minimal correction"
      : isMultiHit  ? "movement between targets is fluid"
      : isReactive  ? "flick quality is high — decisions are fast and clean"
      : isAccuracy  ? "consistent micro-aim is translating into reliable shot placement"
      :               "movement is consistently clean";

    const smoothHighIssueCtx = isTracking ? "Lower sensitivity and deliberate slow-tracking drills will build a steadier aim line."
      : isOneShot   ? "Jittery flicks hurt first-bullet accuracy on one-taps. Wrist stability drills will help."
      : isMultiHit  ? "Shaky movement wastes shots. Relax your grip and practise controlled tracking."
      : isReactive  ? "Clean decisive flicks lower TTK. Work on eliminating tension from arm and wrist."
      :               "Consider lowering your sensitivity or using a larger mousepad.";

    if (composite >= 75)
      insights.push({ kind: "positive", category: "mouse", title: "Great movement quality", description: `Overall smoothness ${composite.toFixed(1)}/100 — your ${smoothPositiveCtx}.` });
    else if (composite < 40)
      insights.push({ kind: "issue", severity: "high", category: "mouse", title: "Movement needs work", description: `Overall smoothness ${composite.toFixed(1)}/100. ${smoothHighIssueCtx}` });
    else if (composite < 60)
      insights.push({ kind: "issue", severity: "medium", category: "mouse", title: "Smoothness has room to grow", description: `Overall smoothness ${composite.toFixed(1)}/100. Use short 5–8 minute blocks at ~90% speed and prioritize cleaner ${isTracking ? "tracking lines" : "first-shot paths"}.` });

    // Jitter / wobble
    const jitterHighCtx = isTracking
      ? "Your cursor wobbles off the tracking line. Try relaxing your grip — mouse wobble usually comes from grip tension, not from moving too fast."
      : isAccuracy
      ? "Micro-tremor on small precision targets costs you hits. Try a lighter grip, more wrist support, or slightly lower sensitivity."
      : "Your cursor shakes between movements. Check if your mouse feet are worn, relax your grip, or try moving with your arm rather than your wrist.";

    if (jitter > 0.5)
      insights.push({ kind: "issue", severity: "high", category: "mouse", title: "A lot of aim wobble", description: jitterHighCtx });
    else if (jitter > 0.3)
      insights.push({ kind: "issue", severity: "medium", category: "mouse", title: "Some aim wobble", description: isTracking
        ? "Cursor drifts off the tracking line between movements. Relax your grip and let your elbow rest comfortably."
        : "Some wobble in your movements. Relax your grip and make sure your elbow is supported." });
    else if (jitter < 0.15)
      insights.push({ kind: "positive", category: "mouse", title: "Rock-steady aim", description: `Very low wobble${isTracking ? " — cursor stays glued to the target with almost no lateral drift" : " — clean, shake-free aim line"}.` });

    // Overshoot / correction: prefer shot-anchored metrics when available.
    if (!isTracking && shotTimingRecords.length >= 2) {
      const shotVals = shotTimingRecords
        .map((r) => r.shot_timing?.avg_shots_to_hit)
        .filter((v): v is number => v != null && Number.isFinite(v));
      const correctiveVals = shotTimingRecords
        .map((r) => r.shot_timing?.corrective_shot_ratio)
        .filter((v): v is number => v != null && Number.isFinite(v));
      const latencyVals = shotTimingRecords
        .map((r) => r.shot_timing?.avg_fire_to_hit_ms)
        .filter((v): v is number => v != null && Number.isFinite(v));

      const avgShotsToHit = shotVals.length > 0 ? mean(shotVals) : null;
      const avgCorrectiveRatio = correctiveVals.length > 0 ? mean(correctiveVals) : null;
      const avgFireToHitMs = latencyVals.length > 0 ? mean(latencyVals) : null;

      const hasShotsToHit = avgShotsToHit != null;
      const hasCorrective = avgCorrectiveRatio != null;
      const hasLatency = avgFireToHitMs != null;

      if (!hasShotsToHit && !hasCorrective && !hasLatency) {
        // No usable shot-timing values yet — fall back to movement heuristic below.
      } else {

        const severeCorrection = (hasShotsToHit && avgShotsToHit > 1.75)
          || (hasCorrective && avgCorrectiveRatio > 0.48)
          || (hasLatency && avgFireToHitMs > 320);
        const mildCorrection = (hasShotsToHit && avgShotsToHit > 1.35)
          || (hasCorrective && avgCorrectiveRatio > 0.28)
          || (hasLatency && avgFireToHitMs > 220);

        if (severeCorrection) {
          insights.push({
            kind: "issue",
            severity: "high",
            category: "mouse",
            title: "High post-shot correction",
            description: `Shot recovery is heavy (${avgShotsToHit?.toFixed(2) ?? "—"} shots/hit, ${avgCorrectiveRatio != null ? `${(avgCorrectiveRatio * 100).toFixed(0)}%` : "—"} corrective hits, ${avgFireToHitMs?.toFixed(0) ?? "—"}ms fired→hit). Prioritize first-shot placement, then add pace.`,
          });
        } else if (mildCorrection) {
          insights.push({
            kind: "issue",
            severity: "low",
            category: "mouse",
            title: "Moderate post-shot correction",
            description: `Some shots still need recovery (${avgShotsToHit?.toFixed(2) ?? "—"} shots/hit, ${avgCorrectiveRatio != null ? `${(avgCorrectiveRatio * 100).toFixed(0)}%` : "—"} corrective hits). Stabilize first-shot conversion before pushing speed.`,
          });
        } else {
          insights.push({
            kind: "positive",
            category: "mouse",
            title: "Clean first-shot conversion",
            description: `Shot timing is efficient (${avgShotsToHit?.toFixed(2) ?? "—"} shots/hit, ${avgCorrectiveRatio != null ? `${(avgCorrectiveRatio * 100).toFixed(0)}%` : "—"} corrective hits, ${avgFireToHitMs?.toFixed(0) ?? "—"}ms fired→hit).`,
          });
        }
        // Skip heuristic overshoot logic when direct shot timing is available.
        // Continue with remaining insights.
        
        
      }
      if (hasShotsToHit || hasCorrective || hasLatency) {
        // already handled via shot-anchored logic
      } else {
        const overshootHighCtx = isOneShot
          ? "You’re passing through targets on many flicks. Decelerate slightly in the last stretch so first shots land on target."
          : isReactive
          ? "Overshooting adds recovery time before you can fire. Brake into the target zone instead of snapping through it."
          : "You regularly overshoot targets after flicks. Use controlled deceleration reps and focus on clean stops.";

        const overshootLowCtx = isOneShot
          ? "Slight overshoot on some flicks. A little more braking right before the target will improve your first-shot accuracy."
          : "Slight overshoot on some movements. Practice stopping cleanly at the target instead of correcting back.";

        const overshootGoodCtx = isOneShot
          ? "flicks land on target on the first try — no wasted bullet"
          : "flicks land accurately without drifting past";

        if (overshoot > 0.4)
          insights.push({ kind: "issue", severity: "high", category: "mouse", title: "Overshooting often", description: overshootHighCtx });
        else if (overshoot > 0.2)
          insights.push({ kind: "issue", severity: "low", category: "mouse", title: "Slight overshoot", description: overshootLowCtx });
        else if (overshoot < 0.1)
          insights.push({ kind: "positive", category: "mouse", title: "Clean, precise flicks", description: `Very low overshoot — ${overshootGoodCtx}.` });
      }
    } else {
      const overshootHighCtx = isOneShot
        ? "You’re passing through targets on many flicks. Decelerate slightly in the last stretch so first shots land on target."
        : isReactive
        ? "Overshooting adds recovery time before you can fire. Brake into the target zone instead of snapping through it."
        : "You regularly overshoot targets after flicks. Use controlled deceleration reps and focus on clean stops.";

      const overshootLowCtx = isOneShot
        ? "Slight overshoot on some flicks. A little more braking right before the target will improve your first-shot accuracy."
        : "Slight overshoot on some movements. Practice stopping cleanly at the target instead of correcting back.";

      const overshootGoodCtx = isOneShot
        ? "flicks land on target on the first try — no wasted bullet"
        : "flicks land accurately without drifting past";

      if (overshoot > 0.4)
        insights.push({ kind: "issue", severity: "high", category: "mouse", title: "Overshooting often", description: overshootHighCtx });
      else if (overshoot > 0.2)
        insights.push({ kind: "issue", severity: "low", category: "mouse", title: "Slight overshoot", description: overshootLowCtx });
      else if (overshoot < 0.1)
        insights.push({ kind: "positive", category: "mouse", title: "Clean, precise flicks", description: `Very low overshoot — ${overshootGoodCtx}.` });
    }

    // Path quality
    const pathHighIssueCtx = isTracking
      ? "Your cursor weaves around the target instead of sticking to it. This usually means forearm tension or an overly tight grip — relax and try to follow the target in one smooth arc."
      : isAccuracy
      ? "Your cursor curves toward precision targets rather than going straight. Slow down and approach from a consistent angle each time to build reliable muscle memory."
      : "Your cursor takes a curved route to targets. This often comes from wrist tension or gripping too hard — relax and try to move with your whole arm.";

    const pathGoodCtx = isTracking
      ? "cursor stays locked on the tracking target with almost no drift"
      : "cursor travels in a nearly straight line to each target";

    if (path < 0.72)
      insights.push({ kind: "issue", severity: "high", category: "mouse", title: "Very curved aim paths", description: pathHighIssueCtx });
    else if (path < 0.82)
      insights.push({ kind: "issue", severity: "medium", category: "mouse", title: "Slightly curved paths", description: isTracking
        ? "Cursor drifts off the tracking line. Wrist or forearm tension is usually the cause — consciously relax between movements."
        : "Cursor curves a little on the way to targets. Wrist tension is usually the cause — try to move from the elbow." });
    else if (path > 0.92)
      insights.push({ kind: "positive", category: "mouse", title: "Straight, efficient paths", description: `Path quality ${(path * 100).toFixed(1)}% — ${pathGoodCtx}.` });

    // Over-aim / micro-corrections
    const corrHighCtx = isMultiHit
      ? "You're spending too long fine-tuning your aim on each target — on multi-hit scenarios, being decisive and committing earlier wins more damage. Slightly lower sensitivity can make small adjustments easier."
      : isReactive
      ? "Too much time adjusting after your initial flick adds to your kill time. Trust where your cursor lands and fire — hesitation costs more than a slight miss."
      : "A lot of small adjustments after each movement. This means you're not fully confident in your aim yet — try lowering sensitivity slightly so small corrections feel easier.";

    const corrGoodCtx = isReactive
      ? "reacting and committing to the kill in one clean motion"
      : "committing to each shot quickly and confidently";

    if (correction > 0.45)
      insights.push({ kind: "issue", severity: "medium", category: "mouse", title: "Too many micro-corrections", description: corrHighCtx });
    else if (correction < 0.2)
      insights.push({ kind: "positive", category: "mouse", title: "Confident, decisive aim", description: `Very few corrections needed — ${corrGoodCtx}.` });

    // Directional drift
    if (bias > 0.6)
      insights.push({ kind: "issue", severity: "medium", category: "mouse", title: "Drifting consistently one direction", description: "Your aim consistently overshoots to the same side. Check whether your mousepad is angled, or whether your elbow position is pulling your wrist in one direction." });
    else if (bias < 0.2)
      insights.push({ kind: "positive", category: "mouse", title: "Balanced in both directions", description: "No consistent left-right drift — your aim is well-calibrated in both directions." });

    // Click rhythm — only meaningful for clicking scenarios
    if (!isTracking) {
      const clickHighCtx = isMultiHit
        ? "Your clicks aren't evenly spaced on multi-hit targets — a steady rhythm maximises damage output. Click timing trainers can help lock in a consistent tempo."
        : isReactive
        ? "There's a gap between your cursor landing and clicking. Practice committing immediately — once your cursor is on the target, fire without hesitation."
        : "Click timing varies a lot between shots. Rhythm drills or click-timing trainers can help build a consistent firing tempo.";

      const clickGoodCtx = isMultiHit
        ? "rhythmic shots are maximising damage output on multi-hit targets"
        : "clicks land with a consistent, reliable rhythm";

      if (clickCV > 0.5)
        insights.push({ kind: "issue", severity: "low", category: "mouse", title: "Uneven click rhythm", description: clickHighCtx });
      else if (clickCV < 0.15)
        insights.push({ kind: "positive", category: "mouse", title: "Consistent click rhythm", description: `Very even click timing — ${clickGoodCtx}.` });
    }

    // Tracking-specific: speed consistency
    if (isTracking) {
      if (velStd > 0.6)
        insights.push({ kind: "issue", severity: "medium", category: "mouse", title: "Choppy tracking speed", description: `Speed varies ${(velStd * 100).toFixed(0)}% around your average — your cursor speeds up and brakes rather than smoothly following the target. Try anticipating where the target is going rather than reacting to where it is.` });
      else if (velStd < 0.3)
        insights.push({ kind: "positive", category: "mouse", title: "Smooth, even tracking speed", description: `Speed is very consistent (${(velStd * 100).toFixed(0)}% variation) — shows strong target prediction and relaxed arm control.` });
    }
  }

  // ── Game-stats insights ───────────────────────────────────────────────────
  if (panelRecords.length >= 3) {
    const withTrend = panelRecords.filter((r) => r.stats_panel!.accuracy_trend != null);
    if (withTrend.length >= 2) {
      const avgTrend = mean(withTrend.map((r) => r.stats_panel!.accuracy_trend!));
      if (avgTrend < -5) {
        const ctx = isTracking
          ? `On-target time drops ~${Math.abs(avgTrend).toFixed(1)}% from the first to the second half of each session. You're getting tired partway through — shorter sessions or a break between runs would help.`
          : `Accuracy drops ~${Math.abs(avgTrend).toFixed(1)}% from the first to the second half of each session. Mental fatigue sets in mid-session — try capping sessions at 30–45 minutes.`;
        insights.push({ kind: "issue", severity: "medium", category: "game", title: "Accuracy drops over time", description: ctx });
      } else if (avgTrend > 5) {
        const metric = isTracking ? "On-target time" : "Accuracy";
        insights.push({ kind: "positive", category: "game", title: "Gets better as you play", description: `${metric} improves ~${avgTrend.toFixed(1)}% as each session goes on — you warm up well. Consider a brief warm-up routine to get there faster.` });
      }
    }

    const withTtk = panelRecords.filter(
      (r) => r.stats_panel!.ttk_std_ms != null && r.stats_panel!.avg_ttk_ms != null,
    );
    if (withTtk.length >= 2) {
      const avgTtkStd  = mean(withTtk.map((r) => r.stats_panel!.ttk_std_ms!));
      const avgTtkMean = mean(withTtk.map((r) => r.stats_panel!.avg_ttk_ms!));
      const cv = avgTtkMean > 0 ? avgTtkStd / avgTtkMean : 0;
      const ttkGoodMs = isReactive ? 350 : 400;

      if (cv > 0.5) {
        const ctx = isReactive
          ? `Your reaction kill times vary a lot (${(cv * 100).toFixed(0)}% spread). Some targets you hit fast, others much slower — try pre-aiming spawn points so every reaction starts from the same position.`
          : isOneShot
          ? `Kill times are inconsistent (${(cv * 100).toFixed(0)}% spread) — some targets take much longer than others. Work on a consistent flick tempo regardless of target position.`
          : `Kill times vary a lot (${(cv * 100).toFixed(0)}% spread). Consistent pre-aiming and a steady engagement tempo would smooth this out.`;
        insights.push({ kind: "issue", severity: "medium", category: "game", title: "Kill speed varies a lot", description: ctx });
      } else if (cv < 0.2 && avgTtkMean < ttkGoodMs) {
        const ctx = isReactive
          ? `Avg kill time ${avgTtkMean.toFixed(0)}ms with very little variation — fast and consistent reactions.`
          : `Avg kill time ${avgTtkMean.toFixed(0)}ms with very little variation (${(cv * 100).toFixed(0)}% spread) — killing targets reliably every time.`;
        insights.push({ kind: "positive", category: "game", title: "Very consistent kill speed", description: ctx });
      }
    }

    // Accuracy benchmarks
    const withAcc = panelRecords.filter((r) => r.stats_panel!.accuracy_pct != null);
    if (withAcc.length >= 3) {
      const avgAcc        = mean(withAcc.map((r) => r.stats_panel!.accuracy_pct!));
      const goodThreshold = isTracking ? 65 : isMultiHit ? 50 : isAccuracy ? 75 : 58;
      const lowThreshold  = isTracking ? 40 : isMultiHit ? 30 : isAccuracy ? 50 : 38;

      if (avgAcc >= goodThreshold + 15) {
        const ctx = isTracking
          ? `${avgAcc.toFixed(1)}% on-target time — strong target lock throughout each session.`
          : isOneShot
          ? `${avgAcc.toFixed(1)}% on one-tap targets — nearly every flick is landing cleanly.`
          : `${avgAcc.toFixed(1)}% accuracy — very few wasted shots.`;
        insights.push({ kind: "positive", category: "game", title: "High accuracy", description: ctx });
      } else if (avgAcc < lowThreshold) {
        const ctx = isTracking
          ? `${avgAcc.toFixed(1)}% on-target time. Focus on staying with the target rather than chasing it. Lower sensitivity often helps with this.`
          : isMultiHit
          ? `${avgAcc.toFixed(1)}% accuracy — too many shots are missing. Focus on getting on target first, then shoot rather than shooting while still moving.`
          : isOneShot
          ? `${avgAcc.toFixed(1)}% on one-taps. Slow your flick down a little — getting placement right matters more than speed at this stage.`
          : `${avgAcc.toFixed(1)}% accuracy — slow down slightly and focus on hitting cleanly before worrying about speed.`;
        insights.push({ kind: "issue", severity: "medium", category: "game", title: "Low accuracy", description: ctx });
      }
    }
  }

  return insights;
}

function InsightCard({ ins }: { ins: Insight }) {
  const color = ins.kind === "positive" ? SEV_COLOR.good : SEV_COLOR[ins.severity!];

  return (
    <div
      style={{
        background: `${color}0e`,
        border: `1px solid ${color}28`,
        borderLeft: `3px solid ${color}`,
        borderRadius: 10,
        padding: "12px 16px",
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 5 }}>
        <Dot color={color} size={6} />
        <span style={{ fontWeight: 700, color, fontSize: 12 }}>{ins.title}</span>
        {ins.kind === "issue" && ins.severity && (
          <Badge color={color} size="xs">
            {ins.severity === "high" ? "priority" : ins.severity === "medium" ? "worth fixing" : "minor"}
          </Badge>
        )}
      </div>
      <p style={{ margin: 0, fontSize: 11, color: C.textSub, lineHeight: 1.6 }}>
        {ins.description}
      </p>
    </div>
  );
}

// ─── Overview tab ─────────────────────────────────────────────────────────────

function OverviewTab({
  records,
  sorted,
  best,
  warmupIds,
  onJumpToReplay,
  benchmarkRanks = [],
  thresholdLines = [],
}: {
  records: SessionRecord[];
  sorted: SessionRecord[];
  best: number;
  warmupIds: Set<string>;
  onJumpToReplay: (sessionId: string) => void;
  benchmarkRanks?: HubScenarioBenchmarkRank[];
  thresholdLines?: BenchmarkThresholdLine[];
}) {
  const avgScore = mean(records.map((r) => r.score));
  const accRecords = records.filter((r) => r.accuracy > 0);
  const avgAcc = accRecords.length ? mean(accRecords.map((r) => r.accuracy)) : null;
  const totalKills = records.reduce((s, r) => s + r.kills, 0);
  const killRecords = records.filter((r) => r.kills > 0);
  const avgKills = killRecords.length ? mean(killRecords.map((r) => r.kills)) : null;
  const latestRecord = sorted[sorted.length - 1];

  // ── Trend helpers (for enhanced score chart + half-delta cards) ──────────────
  const hasSmooth = records.some((r) => r.smoothness != null);
  const hasPanelAcc = records.some((r) => r.stats_panel?.accuracy_pct != null);
  const hasTtk = records.some((r) => r.stats_panel?.avg_ttk_ms != null);

  const trendScores = sorted.map((r) => r.score);
  const trendAvg = mean(trendScores);
  const trendSD = stddev(trendScores);
  const trendRolling = rollingMean(trendScores, 5);
  const trendXs = trendScores.map((_, i) => i + 1);
  const { slope: trendSlope, intercept: trendIntercept } = linearRegression(trendXs, trendScores);

  const chartData = sorted.map((r, i) => ({
    i: i + 1,
    score: r.score,
    rolling: trendRolling[i],
    trendLine: trendIntercept + trendSlope * (i + 1),
    composite: r.smoothness?.composite != null ? +r.smoothness.composite.toFixed(1) : null,
    acc: r.stats_panel?.accuracy_pct != null ? +r.stats_panel.accuracy_pct.toFixed(1) : null,
    ttk: r.stats_panel?.avg_ttk_ms != null ? +r.stats_panel.avg_ttk_ms.toFixed(0) : null,
    dateLabel: formatDateTime(r.timestamp),
  }));

  function halfDelta(key: keyof (typeof chartData)[0], invert = false): string {
    const vals = chartData.map((d) => d[key]).filter((v): v is number => v !== null);
    if (vals.length < 4) return "—";
    const half = Math.floor(vals.length / 2);
    const first = mean(vals.slice(0, half));
    const second = mean(vals.slice(half));
    const delta = second - first;
    const pct = first !== 0 ? (delta / Math.abs(first)) * 100 : 0;
    const improved = invert ? delta < 0 : delta > 0;
    return `${delta > 0 ? "+" : ""}${pct.toFixed(1)}% ${improved ? "↑" : "↓"}`;
  }

  const deltaCards = [
    { label: "Score", key: "score" as const, invert: false, color: "#00f5a0" },
    ...(hasSmooth ? [{ label: "Smoothness", key: "composite" as const, invert: false, color: "#00b4ff" }] : []),
    ...(hasPanelAcc ? [{ label: "Accuracy", key: "acc" as const, invert: false, color: "#a78bfa" }] : []),
    ...(hasTtk ? [{ label: "Avg TTK", key: "ttk" as const, invert: true, color: "#ffd700" }] : []),
  ];

  const recentRuns = [...sorted].reverse().slice(0, 30);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      {/* Summary cards */}
      <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
        <StatCard label="Best Score" value={fmtScore(best)} accent="#00f5a0" />
        <StatCard label="Avg Score" value={fmtScore(avgScore)} />
        <StatCard label="Sessions" value={records.length.toString()} />
        {avgAcc !== null && (
          <StatCard
            label="Avg Accuracy"
            value={avgAcc.toFixed(1) + "%"}
            accent="#a78bfa"
          />
        )}
        {totalKills > 0 && (
          <StatCard
            label="Total Kills"
            value={totalKills.toLocaleString()}
            sub={avgKills ? `~${avgKills.toFixed(0)}/session` : undefined}
            accent="#ffd700"
          />
        )}
        {latestRecord && (
          <StatCard
            label="Last Played"
            value={formatDateTime(latestRecord.timestamp)}
            sub={`Score: ${fmtScore(latestRecord.score)}`}
          />
        )}
      </div>

      {/* Benchmark rank chips */}
      {benchmarkRanks.length > 0 && (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {benchmarkRanks.map((rank) => {
            const rankColor = resolveBenchmarkColor(
              rank.scenarioRank?.color,
              rank.scenarioRank?.rankName,
              benchmarkPaletteIndex(rank.scenarioRank?.rankIndex),
            );
            return (
              <div
                key={rank.benchmarkId}
                title={`${rank.benchmarkName} · ${rank.categoryName} · Score: ${Math.round(rank.scenarioScore)}`}
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: 6,
                  background: `linear-gradient(180deg, ${rankColor}12, rgba(255,255,255,0.03))`,
                  border: `1px solid ${rankColor}55`,
                  borderRadius: 8,
                  padding: "5px 12px",
                }}
              >
                <span style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.06em" }}>
                  {rank.benchmarkName}
                </span>
                <span style={{ fontSize: 12, fontWeight: 700, color: rankColor }}>
                  {rank.scenarioRank?.rankName ?? "Unranked"}
                </span>
                {rank.leaderboardRank > 0 && (
                  <span style={{ fontSize: 10, color: C.textFaint }}>
                    #{rank.leaderboardRank}
                  </span>
                )}
              </div>
            );
          })}
        </div>
      )}

      {/* Progress delta cards: later sessions vs earlier sessions */}
      {deltaCards.length > 1 && sorted.length >= 4 && (
        <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
          {deltaCards.map((c) => {
            const delta = halfDelta(c.key, c.invert);
            const improved = delta.includes("↑");
            const neutral = delta === "—";
            return (
              <div key={c.label} style={{ ...CARD_STYLE, minWidth: 130 }}>
                <div
                  style={{
                    fontSize: 11,
                    color: "rgba(255,255,255,0.38)",
                    textTransform: "uppercase",
                    letterSpacing: 1,
                    marginBottom: 6,
                  }}
                >
                  {c.label}
                </div>
                <div
                  style={{
                    fontSize: 18,
                    fontWeight: 700,
                    color: neutral ? "rgba(255,255,255,0.3)" : improved ? "#00f5a0" : "#ff6b6b",
                  }}
                >
                  {delta}
                </div>
                <div style={{ fontSize: 10, color: "rgba(255,255,255,0.3)", marginTop: 3 }}>
                  later sessions vs earlier
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Enhanced score chart with rolling avg + trend line + ±1σ band */}
      <div style={CHART_STYLE}>
        <SectionTitle>Score progression</SectionTitle>
        <div style={{ display: "flex", gap: 16, marginBottom: 10, flexWrap: "wrap" }}>
          {[
            { color: "#00f5a0", label: "Score" },
            ...(trendScores.length >= 5 ? [{ color: "#ffd700", label: "5-session avg", dash: true }] : []),
            ...(trendScores.length >= 4 ? [{ color: "#ff9f43", label: "Trend", dash: true }] : []),
          ].map((l) => (
            <div key={l.label} style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <div
                style={{
                  width: 14,
                  height: 2,
                  borderRadius: 2,
                  background: l.color,
                  opacity: (l as { dash?: boolean }).dash ? 0.7 : 1,
                  backgroundImage: (l as { dash?: boolean }).dash
                    ? `repeating-linear-gradient(90deg,${l.color} 0,${l.color} 4px,transparent 4px,transparent 7px)`
                    : undefined,
                }}
              />
              <span style={{ fontSize: 11, color: "rgba(255,255,255,0.35)" }}>{l.label}</span>
            </div>
          ))}
          {trendSD > 0 && (
            <span style={{ fontSize: 11, color: "rgba(255,255,255,0.25)", marginLeft: 4 }}>
              shaded = ±1σ range
            </span>
          )}
        </div>
        <ResponsiveContainer width="100%" height={chartData.length > 40 ? 220 : 160}>
          <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
            <XAxis
              dataKey="i"
              tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
              tickLine={false}
              axisLine={false}
            />
            <YAxis
              tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              width={52}
              tickFormatter={(v: number) =>
                v >= 1000 ? `${(v / 1000).toFixed(1)}k` : String(v)
              }
            />
            <Tooltip content={<MiniTooltip />} />
            {trendSD > 0 && (
              <ReferenceArea
                y1={Math.round(trendAvg - trendSD)}
                y2={Math.round(trendAvg + trendSD)}
                fill="rgba(0,180,255,0.06)"
                stroke="none"
              />
            )}
            <Line
              type="monotone"
              dataKey="score"
              name="Score"
              stroke="#00f5a0"
              strokeWidth={2}
              dot={false}
              connectNulls
            />
            {trendScores.length >= 5 && (
              <Line
                type="monotone"
                dataKey="rolling"
                name="5-session avg"
                stroke="#ffd700"
                strokeWidth={2}
                strokeDasharray="5 3"
                dot={false}
                connectNulls
              />
            )}
            {trendScores.length >= 4 && (
              <Line
                type="monotone"
                dataKey="trendLine"
                name="Trend"
                stroke="#ff9f43"
                strokeWidth={1.5}
                strokeDasharray="8 4"
                dot={false}
                connectNulls
              />
            )}
            {chartData.length > 40 && (
              <Brush
                dataKey="i"
                height={24}
                stroke={C.accent}
                travellerWidth={8}
                fill="rgba(255,255,255,0.04)"
              />
            )}
            {thresholdLines.map((tl) => (
              <ReferenceLine
                key={`${tl.benchmarkName}-${tl.rankName}`}
                y={tl.score}
                stroke={tl.color || "#888888"}
                strokeDasharray="4 3"
                strokeOpacity={0.55}
                label={{ value: tl.rankName, fill: tl.color || "#888888", fontSize: 9, position: "insideTopRight" }}
              />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </div>

      {/* Recent runs table */}
      <div style={CHART_STYLE}>
        <SectionTitle>Recent runs</SectionTitle>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
          <thead>
            <tr style={{ color: "rgba(255,255,255,0.3)", textAlign: "left" }}>
              {["Date", "Score", "Acc", "Kills", "Duration", "Smooth", "Replay"].map((h) => (
                <th
                  key={h}
                  style={{
                    paddingBottom: 8,
                    fontWeight: 500,
                    borderBottom: "1px solid rgba(255,255,255,0.07)",
                  }}
                >
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {recentRuns.map((r, idx) => {
              const isBest = r.score === best;
              const isWarmup = warmupIds.has(r.id);
              const replayable = r.has_replay;
              return (
                <tr
                  key={r.id}
                  onClick={() => {
                    if (replayable) onJumpToReplay(r.id);
                  }}
                  style={{
                    borderBottom: "1px solid rgba(255,255,255,0.04)",
                    opacity: isWarmup ? 0.55 : 1,
                    background: isBest
                      ? "rgba(0,245,160,0.04)"
                      : idx % 2 === 0
                        ? "transparent"
                        : "rgba(255,255,255,0.01)",
                    cursor: replayable ? "pointer" : "default",
                  }}
                >
                  <td style={{ padding: "8px 4px 8px 0", color: "rgba(255,255,255,0.5)" }}>
                    {formatDateTime(r.timestamp)}
                    {isWarmup && (
                      <span
                        style={{
                          fontSize: 9,
                          background: "rgba(255,180,0,0.18)",
                          color: "#ffb400",
                          borderRadius: 3,
                          padding: "1px 4px",
                          marginLeft: 5,
                          verticalAlign: "middle",
                        }}
                      >
                        warm-up
                      </span>
                    )}
                  </td>
                  <td
                    style={{
                      padding: "8px 4px",
                      fontWeight: 700,
                      color: isBest ? "#00f5a0" : "#fff",
                    }}
                  >
                    {fmtScore(r.score)}
                    {isBest && (
                      <span style={{ fontSize: 10, color: "#00f5a0", marginLeft: 6 }}>PB</span>
                    )}
                  </td>
                  <td style={{ padding: "8px 4px", color: "rgba(255,255,255,0.6)" }}>
                    {r.accuracy > 0 ? r.accuracy.toFixed(1) + "%" : "—"}
                  </td>
                  <td style={{ padding: "8px 4px", color: "rgba(255,255,255,0.55)" }}>
                    {r.kills > 0 ? r.kills : "—"}
                  </td>
                  <td style={{ padding: "8px 4px", color: "rgba(255,255,255,0.5)" }}>
                    {fmtDuration(r.duration_secs)}
                  </td>
                  <td
                    style={{
                      padding: "8px 4px",
                      color: r.smoothness ? "#00b4ff" : "rgba(255,255,255,0.2)",
                    }}
                  >
                    {r.smoothness ? r.smoothness.composite.toFixed(1) : "—"}
                  </td>
                  <td style={{ padding: "8px 4px", color: replayable ? C.accent : "rgba(255,255,255,0.25)", fontSize: 11, fontWeight: replayable ? 700 : 400 }}>
                    {replayable ? "Open" : "—"}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function ScenarioComparisonCard({
  leftLabel,
  left,
  rightLabel,
  right,
}: {
  leftLabel: string;
  left: ScenarioSummary;
  rightLabel: string;
  right: ScenarioSummary;
}) {
  const rows = [
    {
      label: "Best score",
      leftValue: fmtScore(left.best),
      rightValue: fmtScore(right.best),
      leftRaw: left.best,
      rightRaw: right.best,
    },
    {
      label: "Average score",
      leftValue: fmtScore(left.avgScore),
      rightValue: fmtScore(right.avgScore),
      leftRaw: left.avgScore,
      rightRaw: right.avgScore,
    },
    {
      label: "Recent avg",
      leftValue: fmtScore(left.recentAvg),
      rightValue: fmtScore(right.recentAvg),
      leftRaw: left.recentAvg,
      rightRaw: right.recentAvg,
    },
    {
      label: "Accuracy",
      leftValue: left.avgAccuracy != null ? `${left.avgAccuracy.toFixed(1)}%` : "—",
      rightValue: right.avgAccuracy != null ? `${right.avgAccuracy.toFixed(1)}%` : "—",
      leftRaw: left.avgAccuracy ?? -1,
      rightRaw: right.avgAccuracy ?? -1,
    },
    {
      label: "Play volume",
      leftValue: `${left.sessions} runs`,
      rightValue: `${right.sessions} runs`,
      leftRaw: left.sessions,
      rightRaw: right.sessions,
    },
  ];

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10, marginTop: 14 }}>
      <div style={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) 120px minmax(0, 1fr)", gap: 10, alignItems: "center" }}>
        <div style={{ fontSize: 13, color: C.text, fontWeight: 700 }}>{leftLabel}</div>
        <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", textAlign: "center", fontWeight: 700 }}>
          Metric
        </div>
        <div style={{ fontSize: 13, color: C.text, fontWeight: 700, textAlign: "right" }}>{rightLabel}</div>
      </div>
      {rows.map((row) => {
        const leftBetter = row.leftRaw > row.rightRaw;
        const rightBetter = row.rightRaw > row.leftRaw;
        return (
          <div
            key={row.label}
            style={{
              display: "grid",
              gridTemplateColumns: "minmax(0, 1fr) 120px minmax(0, 1fr)",
              gap: 10,
              alignItems: "center",
              background: "rgba(255,255,255,0.025)",
              border: `1px solid ${C.borderSub}`,
              borderRadius: 10,
              padding: "10px 12px",
            }}
          >
            <div style={{ color: leftBetter ? C.accent : C.textSub, fontWeight: leftBetter ? 700 : 500 }}>
              {row.leftValue}
            </div>
            <div style={{ color: C.textFaint, textAlign: "center", fontSize: 11 }}>{row.label}</div>
            <div style={{ color: rightBetter ? C.accent : C.textSub, textAlign: "right", fontWeight: rightBetter ? 700 : 500 }}>
              {row.rightValue}
            </div>
          </div>
        );
      })}
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, color: C.textFaint, fontSize: 11, flexWrap: "wrap" }}>
        <span>{left.latestTimestamp ? `Last played ${relativeTime(left.latestTimestamp)}` : "No recent timestamp"}</span>
        <span>{right.latestTimestamp ? `Last played ${relativeTime(right.latestTimestamp)}` : "No recent timestamp"}</span>
      </div>
    </div>
  );
}

// ─── Movement tab ─────────────────────────────────────────────────────────────

function MovementTab({
  records,
  sorted,
}: {
  records: SessionRecord[];
  sorted: SessionRecord[];
}) {

  const smoothRecords = records.filter((r) => r.smoothness !== null);

  if (smoothRecords.length === 0) {
    return (
      <div style={{ color: "rgba(255,255,255,0.3)", padding: 20, lineHeight: 1.7 }}>
        No smoothness data recorded for this scenario.
        <br />
        Make sure mouse tracking is turned on while you play.
      </div>
    );
  }

  const g = (fn: (s: SmoothnessSnapshot) => number) =>
    mean(smoothRecords.map((r) => fn(r.smoothness!)));

  const avgComposite = g((s) => s.composite);
  const avgJitter = g((s) => s.jitter);
  const avgOvershoot = g((s) => s.overshoot_rate);
  const avgPath = g((s) => s.path_efficiency);
  const avgVelStd = g((s) => s.velocity_std);
  const avgCorrection = g((s) => s.correction_ratio);
  const avgBias = g((s) => s.directional_bias);
  const avgClickCV = g((s) => s.click_timing_cv);

  const smoothSorted = sorted.filter((r) => r.smoothness !== null);
  const chartData = smoothSorted.map((r, i) => ({
    i: i + 1,
    composite: +r.smoothness!.composite.toFixed(1),
    jitter: +(r.smoothness!.jitter * 100).toFixed(2),
    overshoot: +(r.smoothness!.overshoot_rate * 100).toFixed(1),
    path_eff: +(r.smoothness!.path_efficiency * 100).toFixed(1),
    vel_std: +(r.smoothness!.velocity_std * 100).toFixed(1),
    correction: +(r.smoothness!.correction_ratio * 100).toFixed(1),
    bias: +(r.smoothness!.directional_bias * 100).toFixed(1),
    dateLabel: formatDateTime(r.timestamp),
  }));

  const metrics = [
    {
      label: "Overall Smoothness",
      value: avgComposite.toFixed(1),
      unit: "/100",
      note: "higher = smoother movement",
      accent:
        avgComposite >= 70 ? "#00f5a0" : avgComposite >= 50 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Wobble (Jitter)",
      value: avgJitter.toFixed(3),
      note: "lower = steadier aim",
      accent: avgJitter < 0.2 ? "#00f5a0" : avgJitter < 0.35 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Overshoot",
      value: (avgOvershoot * 100).toFixed(1),
      unit: "%",
      note: "lower = fewer overshoots",
      accent:
        avgOvershoot < 0.15 ? "#00f5a0" : avgOvershoot < 0.3 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Path Quality",
      value: (avgPath * 100).toFixed(1),
      unit: "%",
      note: "higher = straighter aim paths",
      accent: avgPath > 0.87 ? "#00f5a0" : avgPath > 0.75 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Speed Consistency",
      value: (avgVelStd * 100).toFixed(1),
      unit: "%",
      note: "lower = more even mouse speed",
      accent: avgVelStd < 0.4 ? "#00f5a0" : avgVelStd < 0.6 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Over-aim",
      value: (avgCorrection * 100).toFixed(1),
      unit: "%",
      note: "lower = fewer micro-corrections",
      accent:
        avgCorrection < 0.25 ? "#00f5a0" : avgCorrection < 0.4 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Side Drift",
      value: (avgBias * 100).toFixed(1),
      unit: "%",
      note: "lower = aim drifts neither way",
      accent: avgBias < 0.25 ? "#00f5a0" : avgBias < 0.5 ? "#ffd700" : "#ff6b6b",
    },
    {
      label: "Click Rhythm",
      value: avgClickCV.toFixed(3),
      note: "lower = more consistent clicks",
      accent:
        avgClickCV < 0.2 ? "#00f5a0" : avgClickCV < 0.4 ? "#ffd700" : "#ff6b6b",
    },
  ];

  const insights = detectInsights(records);
  const mouseInsights = insights.filter((ins) => ins.category === "mouse");

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      {/* Metrics grid */}
      <div
        style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 10 }}
      >
        {metrics.map((m) => (
          <div
            key={m.label}
            style={{
              ...CARD_STYLE,
              minWidth: 0,
              flex: "none",
              padding: "12px 14px",
            }}
          >
            <div
              style={{
                fontSize: 10,
                color: "rgba(255,255,255,0.35)",
                textTransform: "uppercase",
                letterSpacing: 1,
                marginBottom: 4,
              }}
            >
              {m.label}
            </div>
            <div
              style={{ fontSize: 19, fontWeight: 700, color: m.accent, lineHeight: 1 }}
            >
              {m.value}
              <span style={{ fontSize: 12, fontWeight: 400 }}>{m.unit}</span>
            </div>
            <div
              style={{ fontSize: 10, color: "rgba(255,255,255,0.28)", marginTop: 3 }}
            >
              {m.note}
            </div>
          </div>
        ))}
      </div>

      {/* Composite + path + speed CV trend */}
      <div style={CHART_STYLE}>
        <SectionTitle>Smoothness trend</SectionTitle>
        <div style={{ display: "flex", gap: 16, marginBottom: 10, flexWrap: "wrap" }}>
          {[
            { color: "#00b4ff", label: "Smoothness score" },
            { color: "#00f5a0", label: "Path quality %" },
            { color: "#ffd700", label: "Speed consistency %" },
          ].map((l) => (
            <div key={l.label} style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <div
                style={{
                  width: 12,
                  height: 2,
                  borderRadius: 2,
                  background: l.color,
                }}
              />
              <span style={{ fontSize: 11, color: "rgba(255,255,255,0.35)" }}>
                {l.label}
              </span>
            </div>
          ))}
        </div>
        <ResponsiveContainer width="100%" height={150}>
          <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
            <XAxis
              dataKey="i"
              tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
              tickLine={false}
              axisLine={false}
            />
            <YAxis
              tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              width={38}
            />
            <Tooltip content={<MiniTooltip />} />
            <Line
              type="monotone"
              dataKey="composite"
              name="Smoothness score"
              stroke="#00b4ff"
              strokeWidth={2}
              dot={false}
              connectNulls
            />
            <Line
              type="monotone"
              dataKey="path_eff"
              name="Path quality %"
              stroke="#00f5a0"
              strokeWidth={1.5}
              dot={false}
              connectNulls
            />
            <Line
              type="monotone"
              dataKey="vel_std"
              name="Speed consistency %"
              stroke="#ffd700"
              strokeWidth={1.5}
              dot={false}
              connectNulls
            />
          </LineChart>
        </ResponsiveContainer>
      </div>

      {/* Error metrics */}
      <div style={CHART_STYLE}>
        <SectionTitle>Aim errors — all lower is better</SectionTitle>
        <div style={{ display: "flex", gap: 16, marginBottom: 10, flexWrap: "wrap" }}>
          {[
            { color: "#ff6b6b", label: "Wobble ×100" },
            { color: "#ff9f43", label: "Overshoot %" },
            { color: "#a78bfa", label: "Over-aim %" },
            { color: "#e056fd", label: "Side drift %" },
          ].map((l) => (
            <div key={l.label} style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <div
                style={{
                  width: 12,
                  height: 2,
                  borderRadius: 2,
                  background: l.color,
                }}
              />
              <span style={{ fontSize: 11, color: "rgba(255,255,255,0.35)" }}>
                {l.label}
              </span>
            </div>
          ))}
        </div>
        <ResponsiveContainer width="100%" height={130}>
          <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
            <XAxis
              dataKey="i"
              tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
              tickLine={false}
              axisLine={false}
            />
            <YAxis
              tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              width={38}
            />
            <Tooltip content={<MiniTooltip />} />
            <Line
              type="monotone"
              dataKey="jitter"
              name="Wobble ×100"
              stroke="#ff6b6b"
              strokeWidth={1.5}
              dot={false}
              connectNulls
            />
            <Line
              type="monotone"
              dataKey="overshoot"
              name="Overshoot %"
              stroke="#ff9f43"
              strokeWidth={1.5}
              dot={false}
              connectNulls
            />
            <Line
              type="monotone"
              dataKey="correction"
              name="Over-aim %"
              stroke="#a78bfa"
              strokeWidth={1.5}
              dot={false}
              connectNulls
            />
            <Line
              type="monotone"
              dataKey="bias"
              name="Side drift %"
              stroke="#e056fd"
              strokeWidth={1.5}
              dot={false}
              connectNulls
            />
          </LineChart>
        </ResponsiveContainer>
      </div>

      {/* Insights */}
      {mouseInsights.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <SectionTitle>Insights</SectionTitle>
          {mouseInsights.map((ins, i) => (
            <InsightCard key={i} ins={ins} />
          ))}
        </div>
      )}
    </div>
  );
}

// ─── Performance tab ──────────────────────────────────────────────────────────

function PerformanceTab({
  records,
  sorted,
}: {
  records: SessionRecord[];
  sorted: SessionRecord[];
}) {
  const perfRecords = records
    .map((r) => {
      const fallbackKps =
        r.duration_secs > 0 && Number.isFinite(r.duration_secs) ? r.kills / r.duration_secs : null;
      const fallbackAcc = Number.isFinite(r.accuracy) ? r.accuracy : null;
      return {
        ...r,
        perf_kps: r.stats_panel?.avg_kps ?? fallbackKps,
        perf_acc: r.stats_panel?.accuracy_pct ?? fallbackAcc,
        perf_ttk: r.stats_panel?.avg_ttk_ms ?? null,
        perf_ttk_std: r.stats_panel?.ttk_std_ms ?? null,
        perf_trend: r.stats_panel?.accuracy_trend ?? null,
        perf_scenario_type: r.stats_panel?.scenario_type ?? null,
      };
    })
    .filter((r) => r.perf_kps != null || r.perf_acc != null || r.perf_ttk != null);

  if (perfRecords.length === 0) {
    return (
      <div style={{ color: "rgba(255,255,255,0.3)", padding: 20, lineHeight: 1.7 }}>
        No performance data recorded for this scenario.
        <br />
        Run a scenario with AimMod active to capture detailed
        kill-speed, accuracy, and TTK metrics.
      </div>
    );
  }

  const perfSorted = sorted
    .map((r) => {
      const fallbackKps =
        r.duration_secs > 0 && Number.isFinite(r.duration_secs) ? r.kills / r.duration_secs : null;
      const fallbackAcc = Number.isFinite(r.accuracy) ? r.accuracy : null;
      return {
        ...r,
        perf_kps: r.stats_panel?.avg_kps ?? fallbackKps,
        perf_acc: r.stats_panel?.accuracy_pct ?? fallbackAcc,
        perf_ttk: r.stats_panel?.avg_ttk_ms ?? null,
        perf_ttk_std: r.stats_panel?.ttk_std_ms ?? null,
        perf_trend: r.stats_panel?.accuracy_trend ?? null,
        perf_scenario_type: r.stats_panel?.scenario_type ?? null,
      };
    })
    .filter((r) => r.perf_kps != null || r.perf_acc != null || r.perf_ttk != null);

  const withKps = perfRecords.filter((r) => r.perf_kps != null);
  const withAcc = perfRecords.filter((r) => r.perf_acc != null);
  const withTtk = perfRecords.filter((r) => r.perf_ttk != null);
  const withBestTtk = perfRecords.filter((r) => r.stats_panel?.best_ttk_ms != null);
  const withTrend = perfRecords.filter((r) => r.perf_trend != null);

  const avgKps = withKps.length ? mean(withKps.map((r) => r.perf_kps!)) : null;
  const avgAccPct = withAcc.length ? mean(withAcc.map((r) => r.perf_acc!)) : null;
  const avgTtk = withTtk.length ? mean(withTtk.map((r) => r.perf_ttk!)) : null;
  const bestTtk = withBestTtk.length
    ? Math.min(...withBestTtk.map((r) => r.stats_panel!.best_ttk_ms!))
    : null;
  const avgTrend = withTrend.length ? mean(withTrend.map((r) => r.perf_trend!)) : null;

  const scenarioType =
    perfRecords[perfRecords.length - 1]?.perf_scenario_type ?? "Unknown";
  const scenarioSubtype =
    perfRecords[perfRecords.length - 1]?.stats_panel?.scenario_subtype ?? null;

  const chartData = perfSorted.map((r, i) => ({
    i: i + 1,
    kps: r.perf_kps != null ? +r.perf_kps.toFixed(2) : null,
    acc: r.perf_acc != null ? +r.perf_acc.toFixed(1) : null,
    ttk: r.perf_ttk != null ? +r.perf_ttk.toFixed(0) : null,
    ttk_std: r.perf_ttk_std != null ? +r.perf_ttk_std.toFixed(0) : null,
    trend: r.perf_trend != null ? +r.perf_trend.toFixed(1) : null,
    dateLabel: formatDateTime(r.timestamp),
  }));

  const panelInsights = detectInsights(records).filter((ins) => ins.category === "game");

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      {/* Summary cards */}
      <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
        <StatCard label="Scenario Type" value={scenarioType} />
        {scenarioSubtype && <StatCard label="Scenario Subtype" value={scenarioSubtype} />}
        {avgKps != null && (
          <StatCard
            label="Avg KPS"
            value={avgKps.toFixed(2)}
            sub="kills per second"
            accent="#00f5a0"
          />
        )}
        {avgAccPct != null && (
          <StatCard
            label="Avg Accuracy"
            value={avgAccPct.toFixed(1) + "%"}
            accent="#00b4ff"
          />
        )}
        {avgTtk != null && (
          <StatCard label="Avg TTK" value={avgTtk.toFixed(0) + "ms"} accent="#ffd700" />
        )}
        {bestTtk != null && (
          <StatCard
            label="Best TTK"
            value={bestTtk.toFixed(0) + "ms"}
            sub="fastest kill"
            accent="#00f5a0"
          />
        )}
        {avgTrend != null && (
          <StatCard
            label="Acc. Trend"
            value={(avgTrend > 0 ? "+" : "") + avgTrend.toFixed(1) + "%"}
            sub="later half of session vs earlier half"
            accent={avgTrend > 2 ? "#00f5a0" : avgTrend < -2 ? "#ff6b6b" : "rgba(255,255,255,0.6)"}
          />
        )}
      </div>

      {/* TTK trend */}
      {withTtk.length > 1 && (
        <div style={CHART_STYLE}>
          <SectionTitle>Time to Kill in milliseconds — lower is faster</SectionTitle>
          <div style={{ display: "flex", gap: 16, marginBottom: 10 }}>
            {[
              { color: "#ffd700", label: "Avg kill speed" },
              { color: "#ff9f43", label: "Kill speed spread" },
            ].map((l) => (
              <div key={l.label} style={{ display: "flex", alignItems: "center", gap: 6 }}>
                <div
                  style={{ width: 12, height: 2, borderRadius: 2, background: l.color }}
                />
                <span style={{ fontSize: 11, color: "rgba(255,255,255,0.35)" }}>
                  {l.label}
                </span>
              </div>
            ))}
          </div>
          <ResponsiveContainer width="100%" height={140}>
            <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
              <XAxis
                dataKey="i"
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
                width={46}
              />
              <Tooltip content={<MiniTooltip />} />
              <Line
                type="monotone"
                dataKey="ttk"
                name="Avg TTK"
                stroke="#ffd700"
                strokeWidth={2}
                dot={false}
                connectNulls
              />
              <Line
                type="monotone"
                dataKey="ttk_std"
                name="Kill speed spread"
                stroke="#ff9f43"
                strokeWidth={1.5}
                strokeDasharray="4 3"
                dot={false}
                connectNulls
              />
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* Accuracy trend */}
      {withAcc.length > 1 && (
        <div style={CHART_STYLE}>
          <SectionTitle>Accuracy (%)</SectionTitle>
          <ResponsiveContainer width="100%" height={130}>
            <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
              <XAxis
                dataKey="i"
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
                width={38}
                domain={["auto", "auto"]}
              />
              <Tooltip content={<MiniTooltip />} />
              <Line
                type="monotone"
                dataKey="acc"
                name="Accuracy %"
                stroke="#00b4ff"
                strokeWidth={2}
                dot={false}
                connectNulls
              />
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* Within-session accuracy trend (fatigue) */}
      {withTrend.length > 1 && (
        <div style={CHART_STYLE}>
          <SectionTitle>
            Accuracy within each session — above zero means you get better as you play
          </SectionTitle>
          <ResponsiveContainer width="100%" height={120}>
            <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
              <XAxis
                dataKey="i"
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
                width={38}
              />
              <Tooltip content={<MiniTooltip />} />
              <ReferenceLine y={0} stroke="rgba(255,255,255,0.15)" />
              <Line
                type="monotone"
                dataKey="trend"
                name="Acc. trend"
                stroke="#a78bfa"
                strokeWidth={2}
                dot={false}
                connectNulls
              />
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* KPS trend */}
      {withKps.length > 1 && (
        <div style={CHART_STYLE}>
          <SectionTitle>Kills per second</SectionTitle>
          <ResponsiveContainer width="100%" height={120}>
            <LineChart data={chartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
              <XAxis
                dataKey="i"
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
              />
              <YAxis
                tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                tickLine={false}
                axisLine={false}
                width={38}
              />
              <Tooltip content={<MiniTooltip />} />
              <Line
                type="monotone"
                dataKey="kps"
                name="KPS"
                stroke="#00f5a0"
                strokeWidth={2}
                dot={false}
                connectNulls
              />
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}

      {panelInsights.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <SectionTitle>Insights</SectionTitle>
          {panelInsights.map((ins, i) => (
            <InsightCard key={i} ins={ins} />
          ))}
        </div>
      )}
    </div>
  );
}

function MechanicsTab({
  records,
  sorted,
}: {
  records: SessionRecord[];
  sorted: SessionRecord[];
}) {
  const hasSmooth = records.some((record) => record.smoothness != null);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 24 }}>
      <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <div>
          <div style={{ fontSize: 11, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.1em", fontWeight: 700 }}>
            Game Performance
          </div>
          <div style={{ marginTop: 4, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
            Score pace, accuracy, kill speed, and how this scenario performs across runs.
          </div>
        </div>
        <PerformanceTab records={records} sorted={sorted} />
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
        <div>
          <div style={{ fontSize: 11, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.1em", fontWeight: 700 }}>
            Mouse Control
          </div>
          <div style={{ marginTop: 4, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
            Smoothness, wobble, overshoot, path quality, and how your mouse movement holds up over time.
          </div>
        </div>
        {hasSmooth ? (
          <MovementTab records={records} sorted={sorted} />
        ) : (
          <div style={{ ...CHART_STYLE, color: "rgba(255,255,255,0.45)", lineHeight: 1.7 }}>
            Mouse movement detail is not available for this scenario yet. Play a run with mouse tracking enabled to unlock it.
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Replay tab ───────────────────────────────────────────────────────────────

function ReplayTab({
  records,
  sorted,
  warmupIds,
  requestedSelectedId,
  onRequestedSelectedIdHandled,
  hubMode = false,
  onReplayMetadataChanged,
}: {
  records: SessionRecord[];
  sorted: SessionRecord[];
  warmupIds: Set<string>;
  requestedSelectedId?: string | null;
  onRequestedSelectedIdHandled?: () => void;
  hubMode?: boolean;
  onReplayMetadataChanged?: () => void;
}) {
  const replayRecords = useMemo(
    () => [...sorted].reverse().filter((r) => r.has_replay),
    [sorted],
  );
  const [sortBy, setSortBy] = useState<"date" | "score" | "duration">("date");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("desc");
  const [selectedId, setSelectedId] = useState<string | null>(
    replayRecords.length > 0 ? replayRecords[0].id : null,
  );
  const [replayPayload, setReplayPayload] = useState<ReplayPayloadData | null>(null);
  const [runSummary, setRunSummary] = useState<BridgeRunSnapshot | null>(null);
  const [runTimeline, setRunTimeline] = useState<BridgeRunTimelinePoint[]>([]);
  const [runCoachingAnalysis, setRunCoachingAnalysis] = useState<SessionRunCoachingAnalysis | null>(null);
  const [targetResponseAnalysis, setTargetResponseAnalysis] = useState<TargetResponseAnalysis | null>(null);
  const [shotTelemetry, setShotTelemetry] = useState<BridgeShotTelemetryEvent[]>([]);
  const [replayContextWindows, setReplayContextWindows] = useState<BridgeReplayContextWindow[]>([]);
  const [selectedContextKey, setSelectedContextKey] = useState<string | null>(null);
  const [shotTelemetryDisplayMode, setShotTelemetryDisplayMode] = useState<ShotTelemetryDisplayMode>("context");
  const [loading, setLoading] = useState(false);
  const [actionBusy, setActionBusy] = useState<"favorite" | "export" | "delete" | null>(null);
  const [actionStatus, setActionStatus] = useState<string | null>(null);
  // const [inGameReplayBusy, setInGameReplayBusy] = useState(false);
  // const [inGameReplayStatus, setInGameReplayStatus] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    if (!selectedId) {
      setReplayPayload(null);
      setRunSummary(null);
      setRunTimeline([]);
      setRunCoachingAnalysis(null);
      setTargetResponseAnalysis(null);
      setShotTelemetry([]);
      setReplayContextWindows([]);
      return () => {
        active = false;
      };
    }
    setSelectedContextKey(null);
    setLoading(true);
    Promise.all([
      invoke<ReplayPayloadData | null>("get_session_replay_payload", { sessionId: selectedId }).catch(() => null),
      invoke<BridgeRunSnapshot | null>("get_session_run_summary", { sessionId: selectedId }).catch(() => null),
      invoke<BridgeRunTimelinePoint[]>("get_session_run_timeline", { sessionId: selectedId }).catch(() => []),
      invoke<SessionRunCoachingAnalysis | null>("get_session_run_coaching_analysis", { sessionId: selectedId }).catch(() => null),
      invoke<TargetResponseAnalysis | null>("get_session_target_response_analysis", { sessionId: selectedId }).catch(() => null),
      invoke<BridgeShotTelemetryEvent[]>("get_session_shot_telemetry", { sessionId: selectedId }).catch(() => []),
      invoke<BridgeReplayContextWindow[]>("get_session_replay_context_windows", { sessionId: selectedId }).catch(() => []),
    ]).then(([payload, summary, timeline, coachingAnalysis, responseAnalysis, telemetry, contextWindows]) => {
      if (!active) return;
      setReplayPayload(payload);
      setRunSummary(summary);
      setRunTimeline(timeline);
      setRunCoachingAnalysis(coachingAnalysis);
      setTargetResponseAnalysis(responseAnalysis);
      setShotTelemetry(telemetry);
      setReplayContextWindows(contextWindows);
      setLoading(false);
    });

    return () => {
      active = false;
    };
  }, [selectedId]);

  // When new sessions arrive, auto-select the newest if nothing is selected
  useEffect(() => {
    if (!selectedId && replayRecords.length > 0) {
      setSelectedId(replayRecords[0].id);
    }
  }, [replayRecords]);

  useEffect(() => {
    if (!requestedSelectedId) return;
    if (replayRecords.some((record) => record.id === requestedSelectedId)) {
      setSelectedId(requestedSelectedId);
    }
    onRequestedSelectedIdHandled?.();
  }, [onRequestedSelectedIdHandled, replayRecords, requestedSelectedId]);

  const sortedReplayRecords = useMemo(() => {
    const next = [...replayRecords];
    next.sort((a, b) => {
      const direction = sortDir === "asc" ? 1 : -1;
      if (sortBy === "score") return (a.score - b.score) * direction;
      if (sortBy === "duration") return ((a.duration_secs ?? 0) - (b.duration_secs ?? 0)) * direction;
      const aTs = parseTimestamp(a.timestamp)?.getTime() ?? 0;
      const bTs = parseTimestamp(b.timestamp)?.getTime() ?? 0;
      return (aTs - bTs) * direction;
    });
    return next;
  }, [replayRecords, sortBy, sortDir]);

  const toggleReplaySort = (column: "date" | "score" | "duration") => {
    if (sortBy === column) {
      setSortDir((prev) => (prev === "desc" ? "asc" : "desc"));
      return;
    }
    setSortBy(column);
    setSortDir(column === "date" ? "desc" : "desc");
  };

  const selectedRecord = records.find((r) => r.id === selectedId) ?? null;
  const handleToggleFavorite = async () => {
    if (!selectedRecord) return;
    setActionBusy("favorite");
    setActionStatus(null);
    try {
      await invoke("set_session_replay_favorite", {
        sessionId: selectedRecord.id,
        isFavorite: !selectedRecord.replay_is_favorite,
      });
      onReplayMetadataChanged?.();
      setActionStatus(
        !selectedRecord.replay_is_favorite
          ? "Replay pinned. It will be kept forever."
          : "Replay unpinned.",
      );
    } catch (error) {
      setActionStatus(String(error));
    } finally {
      setActionBusy(null);
    }
  };
  const handleDeleteReplay = async () => {
    if (!selectedRecord) return;
    setActionBusy("delete");
    setActionStatus(null);
    try {
      await invoke("delete_session_replay", { sessionId: selectedRecord.id });
      const remaining = sortedReplayRecords.filter((record) => record.id !== selectedRecord.id);
      setSelectedId(remaining[0]?.id ?? null);
      onReplayMetadataChanged?.();
      setActionStatus("Replay removed from local storage.");
    } catch (error) {
      setActionStatus(String(error));
    } finally {
      setActionBusy(null);
    }
  };
  const handleExportReplay = async () => {
    if (!selectedRecord) return;
    setActionBusy("export");
    setActionStatus(null);
    try {
      const outputPath = await invoke<string>("export_session_replay_video", {
        sessionId: selectedRecord.id,
      });
      setActionStatus(`Video exported to ${outputPath}`);
    } catch (error) {
      setActionStatus(String(error));
    } finally {
      setActionBusy(null);
    }
  };
  const runSnapshot: BridgeRunSnapshot | null = useMemo(
    () => (runSummary ? { ...runSummary, timeline: runTimeline } : null),
    [runSummary, runTimeline],
  );
  const normalizedRunTimeline = useMemo(
    () => normalizeBridgeRunTimeline(runTimeline),
    [runTimeline],
  );
  const selectedShotTiming = selectedRecord?.shot_timing ?? null;
  // const hasInGameTickStream =
  //   (runSnapshot?.tick_stream_v1?.keyframes?.length ?? 0) > 0
  //   || (runSnapshot?.tick_stream_v1?.deltas?.length ?? 0) > 0;
  const hasRunTimelineSignal = useMemo(
    () => normalizedRunTimeline.some((point) =>
      point.scorePerMinute != null
      || point.killsPerSecond != null
      || point.accuracyPct != null
      || point.damageEfficiency != null,
    ),
    [normalizedRunTimeline],
  );
  const runMoments = runCoachingAnalysis?.keyMoments ?? [];
  const runChartData = useMemo(
    () => normalizedRunTimeline.map((point) => ({
      tSec: point.tSec,
      spm: point.scorePerMinute,
      kps: point.killsPerSecond,
      acc: point.accuracyPct,
      dmgEff: point.damageEfficiency,
    })),
    [normalizedRunTimeline],
  );
  const runAccuracy = normalizeAccuracyPct(
    runSnapshot?.accuracy_pct,
    runSnapshot?.shots_hit,
    runSnapshot?.shots_fired,
  )
    ?? (selectedRecord && selectedRecord.accuracy > 0 ? selectedRecord.accuracy : null);
  const runDurationSecs = runSnapshot?.duration_secs ?? selectedRecord?.duration_secs ?? null;
  const runShotsToHit = runSnapshot?.avg_shots_to_hit ?? selectedShotTiming?.avg_shots_to_hit ?? null;
  const runCorrectiveRatio = runSnapshot?.corrective_shot_ratio ?? selectedShotTiming?.corrective_shot_ratio ?? null;
  const runFireToHitMs = runSnapshot?.avg_fire_to_hit_ms ?? selectedShotTiming?.avg_fire_to_hit_ms ?? null;
  const runFireToHitP90Ms = runSnapshot?.p90_fire_to_hit_ms ?? selectedShotTiming?.p90_fire_to_hit_ms ?? null;
  const runDamageEff =
    runSnapshot?.damage_efficiency
    ?? (
      runSnapshot?.damage_possible != null
      && runSnapshot.damage_possible > 0
      && runSnapshot.damage_done != null
      ? (runSnapshot.damage_done / runSnapshot.damage_possible) * 100
      : null
    );
  const bridgeRunStatCards = useMemo(() => {
    const cards: Array<{ label: string; value: string; sub?: string; accent: string }> = [];

    if (runDurationSecs != null) {
      cards.push({
        label: "Duration",
        value: fmtDuration(runDurationSecs),
        accent: "#00b4ff",
      });
    }
    const scorePerMinute = runSnapshot?.score_per_minute;
    const peakScorePerMinute = runSnapshot?.peak_score_per_minute;

    if (scorePerMinute != null) {
      cards.push({
        label: "SPM",
        value: scorePerMinute.toFixed(0),
        sub: peakScorePerMinute != null ? `peak ${peakScorePerMinute.toFixed(0)}` : undefined,
        accent: "#00f5a0",
      });
    }
    if (runAccuracy != null) {
      cards.push({
        label: "Accuracy",
        value: `${runAccuracy.toFixed(1)}%`,
        accent: "#ffd700",
      });
    }
    if (runDamageEff != null) {
      cards.push({
        label: "Damage Eff",
        value: `${runDamageEff.toFixed(1)}%`,
        accent: "#a78bfa",
      });
    }
    if (runShotsToHit != null) {
      cards.push({
        label: "Shots / Hit",
        value: runShotsToHit.toFixed(2),
        sub: runCorrectiveRatio != null ? `${(runCorrectiveRatio * 100).toFixed(0)}% corrective` : undefined,
        accent: "#00b4ff",
      });
    }
    if (runFireToHitMs != null) {
      cards.push({
        label: "Fire→Hit",
        value: `${runFireToHitMs.toFixed(0)}ms`,
        sub: runFireToHitP90Ms != null ? `p90 ${runFireToHitP90Ms.toFixed(0)}ms` : undefined,
        accent: "#ff9f43",
      });
    }

    return cards;
  }, [
    runAccuracy,
    runCorrectiveRatio,
    runDamageEff,
    runDurationSecs,
    runFireToHitMs,
    runFireToHitP90Ms,
    runShotsToHit,
    runSnapshot?.peak_score_per_minute,
    runSnapshot?.score_per_minute,
  ]);
  const nearestTargetProfileEntityCounts = useMemo(() => {
    const counts = new Map<string, Set<string>>();
    for (const event of shotTelemetry) {
      const botTargets = event.targets.filter((target) => target.is_bot);
      const nearest =
        botTargets.find((target) => target.is_nearest)
        ?? botTargets[0]
        ?? event.targets.find((target) => target.is_nearest)
        ?? event.targets[0];
      if (!nearest) continue;
      const profile = nearest.profile?.trim() || nearest.entity_id;
      const entityIds = counts.get(profile) ?? new Set<string>();
      entityIds.add(nearest.entity_id);
      counts.set(profile, entityIds);
    }
    return counts;
  }, [shotTelemetry]);
  const shotTelemetryBaseTs = shotTelemetry[0]?.ts_ms ?? 0;
  const shotTelemetryContext = useMemo(() => {
    if (replayContextWindows.length > 0) {
      return {
        source: "sql" as const,
        rows: [...replayContextWindows]
          .sort((a, b) => b.start_ms - a.start_ms)
          .map((window): ShotTelemetryContextRow => ({
            key: `window-${window.window_idx}`,
            label: window.label,
            startMs: window.start_ms,
            endMs: window.end_ms,
            firedCount: window.fired_count,
            hitCount: window.hit_count,
            accuracyPct: window.accuracy_pct,
            avgBotCount: window.avg_bot_count,
            nearestLabel: window.primary_target_label ?? "—",
            nearestDistance: window.avg_nearest_distance,
            yawError: window.avg_nearest_yaw_error_deg,
            pitchError: window.avg_nearest_pitch_error_deg,
            source: "sql",
            contextKind: window.context_kind,
            phase: window.phase,
            primaryTargetShare: window.primary_target_share,
            avgScorePerMinute: window.avg_score_per_minute,
            avgKillsPerSecond: window.avg_kills_per_second,
            avgTimelineAccuracyPct: window.avg_timeline_accuracy_pct,
            avgDamageEfficiency: window.avg_damage_efficiency,
          })),
      };
    }
    if (shotTelemetry.length === 0) return { source: "empty" as const, rows: [] as ShotTelemetryContextRow[] };
    const firstTs = shotTelemetry[0]?.ts_ms ?? 0;
    const windowMs = selectShotTelemetryWindowMs(runDurationSecs, shotTelemetry.length);
    const windows = new Map<number, {
      startMs: number;
      endMs: number;
      firedCount: number;
      hitCount: number;
      weightedBotCount: number;
      weightedEventCount: number;
      weightedDistanceSum: number;
      weightedDistanceCount: number;
      weightedYawSum: number;
      weightedYawCount: number;
      weightedPitchSum: number;
      weightedPitchCount: number;
      nearestCounts: Map<string, number>;
    }>();

    for (const event of shotTelemetry) {
      const offsetMs = Math.max(0, event.ts_ms - firstTs);
      const bucketIndex = Math.floor(offsetMs / windowMs);
      const bucketStartMs = bucketIndex * windowMs;
      const bucketEndMs = Math.min(bucketStartMs + windowMs - 1, Math.max(bucketStartMs, offsetMs));
      const current = windows.get(bucketIndex) ?? {
        startMs: bucketStartMs,
        endMs: bucketEndMs,
        firedCount: 0,
        hitCount: 0,
        weightedBotCount: 0,
        weightedEventCount: 0,
        weightedDistanceSum: 0,
        weightedDistanceCount: 0,
        weightedYawSum: 0,
        weightedYawCount: 0,
        weightedPitchSum: 0,
        weightedPitchCount: 0,
        nearestCounts: new Map<string, number>(),
      };

      current.endMs = Math.max(current.endMs, offsetMs);
      const weight = Math.max(1, event.count ?? 1);
      if (event.event === "shot_fired") current.firedCount += weight;
      if (event.event === "shot_hit") current.hitCount += weight;

      const botTargets = event.targets.filter((target) => target.is_bot);
      current.weightedBotCount += botTargets.length * weight;
      current.weightedEventCount += weight;

      const nearest =
        botTargets.find((target) => target.is_nearest)
        ?? botTargets[0]
        ?? event.targets.find((target) => target.is_nearest)
        ?? event.targets[0]
        ?? null;
      if (nearest) {
        const profileKey = nearest.profile?.trim() || nearest.entity_id;
        const duplicateProfile = (nearestTargetProfileEntityCounts.get(profileKey)?.size ?? 0) > 1;
        const label = formatShotTelemetryTargetLabel(nearest.profile, nearest.entity_id, duplicateProfile);
        current.nearestCounts.set(label, (current.nearestCounts.get(label) ?? 0) + weight);

        const distance = nearest.distance_3d ?? nearest.distance_2d;
        if (distance != null) {
          current.weightedDistanceSum += distance * weight;
          current.weightedDistanceCount += weight;
        }
        if (nearest.yaw_error_deg != null) {
          current.weightedYawSum += Math.abs(nearest.yaw_error_deg) * weight;
          current.weightedYawCount += weight;
        }
        if (nearest.pitch_error_deg != null) {
          current.weightedPitchSum += Math.abs(nearest.pitch_error_deg) * weight;
          current.weightedPitchCount += weight;
        }
      }

      windows.set(bucketIndex, current);
    }

    return {
      source: "derived" as const,
      rows: [...windows.entries()]
        .sort((a, b) => b[0] - a[0])
        .map(([bucketIndex, bucket]): ShotTelemetryContextRow => {
          const nearestLabel =
            [...bucket.nearestCounts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? "—";
          return {
            key: `window-${bucketIndex}`,
            label: nearestLabel,
            startMs: bucket.startMs,
            endMs: bucket.endMs,
            firedCount: bucket.firedCount,
            hitCount: bucket.hitCount,
            accuracyPct: bucket.firedCount > 0 ? (bucket.hitCount / bucket.firedCount) * 100 : null,
            avgBotCount: bucket.weightedEventCount > 0 ? bucket.weightedBotCount / bucket.weightedEventCount : null,
            nearestLabel,
            nearestDistance: bucket.weightedDistanceCount > 0 ? bucket.weightedDistanceSum / bucket.weightedDistanceCount : null,
            yawError: bucket.weightedYawCount > 0 ? bucket.weightedYawSum / bucket.weightedYawCount : null,
            pitchError: bucket.weightedPitchCount > 0 ? bucket.weightedPitchSum / bucket.weightedPitchCount : null,
            source: "derived",
            contextKind: null,
            phase: null,
            primaryTargetShare: null,
            avgScorePerMinute: null,
            avgKillsPerSecond: null,
            avgTimelineAccuracyPct: null,
            avgDamageEfficiency: null,
          };
        }),
    };
  }, [nearestTargetProfileEntityCounts, replayContextWindows, runDurationSecs, shotTelemetry]);
  const replayScenarioType = selectedRecord?.stats_panel?.scenario_type ?? "Unknown";
  const replayContextCoaching = useMemo(
    () => buildReplayContextCoaching(shotTelemetryContext.rows, replayScenarioType),
    [replayScenarioType, shotTelemetryContext.rows],
  );
  const selectedContextRow = useMemo(
    () => shotTelemetryContext.rows.find((row) => row.key === selectedContextKey) ?? null,
    [selectedContextKey, shotTelemetryContext.rows],
  );
  useEffect(() => {
    if (selectedContextKey && !shotTelemetryContext.rows.some((row) => row.key === selectedContextKey)) {
      setSelectedContextKey(null);
    }
  }, [selectedContextKey, shotTelemetryContext.rows]);
  const visibleShotTelemetry = useMemo(() => {
    if (!selectedContextRow) return shotTelemetry;
    return shotTelemetry.filter((event) => {
      const offsetMs = Math.max(0, event.ts_ms - shotTelemetryBaseTs);
      return offsetMs >= selectedContextRow.startMs && offsetMs <= selectedContextRow.endMs;
    });
  }, [selectedContextRow, shotTelemetry, shotTelemetryBaseTs]);
  const shotTelemetrySummary = useMemo(() => {
    if (visibleShotTelemetry.length === 0) return null;
    const nearestDistances: number[] = [];
    const nearestYawErrors: number[] = [];
    const nearestPitchErrors: number[] = [];
    const profileCounts = new Map<string, { label: string; count: number }>();
    let firedEvents = 0;
    let hitEvents = 0;
    let totalEvents = 0;
    let weightedBotTargetTotal = 0;

    for (const event of visibleShotTelemetry) {
      const weight = Math.max(1, event.count ?? 1);
      totalEvents += weight;
      if (event.event === "shot_fired") firedEvents += weight;
      if (event.event === "shot_hit") hitEvents += weight;

      const botTargets = event.targets.filter((target) => target.is_bot);
      weightedBotTargetTotal += botTargets.length * weight;

      const nearest =
        botTargets.find((target) => target.is_nearest)
        ?? botTargets[0]
        ?? event.targets.find((target) => target.is_nearest)
        ?? event.targets[0];
      if (!nearest) continue;

      const baseProfile = nearest.profile?.trim() || nearest.entity_id;
      const duplicateProfile = (nearestTargetProfileEntityCounts.get(baseProfile)?.size ?? 0) > 1;
      const identityKey = `${baseProfile}::${nearest.entity_id}`;
      const current = profileCounts.get(identityKey);
      profileCounts.set(identityKey, {
        label: formatShotTelemetryTargetLabel(nearest.profile, nearest.entity_id, duplicateProfile),
        count: (current?.count ?? 0) + weight,
      });

      const distance = nearest.distance_3d ?? nearest.distance_2d;
      if (distance != null) {
        for (let i = 0; i < weight; i += 1) nearestDistances.push(distance);
      }
      if (nearest.yaw_error_deg != null) {
        for (let i = 0; i < weight; i += 1) nearestYawErrors.push(Math.abs(nearest.yaw_error_deg));
      }
      if (nearest.pitch_error_deg != null) {
        for (let i = 0; i < weight; i += 1) nearestPitchErrors.push(Math.abs(nearest.pitch_error_deg));
      }
    }

    return {
      totalEvents,
      firedEvents,
      hitEvents,
      avgBotCount: weightedBotTargetTotal > 0 && totalEvents > 0
        ? weightedBotTargetTotal / totalEvents
        : null,
      avgNearestDistance: nearestDistances.length > 0 ? mean(nearestDistances) : null,
      avgNearestYawError: nearestYawErrors.length > 0 ? mean(nearestYawErrors) : null,
      avgNearestPitchError: nearestPitchErrors.length > 0 ? mean(nearestPitchErrors) : null,
      topProfiles: [...profileCounts.values()]
        .sort((a, b) => b.count - a.count)
        .slice(0, 4)
        .map((entry) => [entry.label, entry.count] as const),
    };
  }, [nearestTargetProfileEntityCounts, visibleShotTelemetry]);
  const shotTelemetrySummaryCards = useMemo(() => {
    if (!shotTelemetrySummary) return [] as Array<{ label: string; value: string; sub?: string; accent: string }>;

    const cards: Array<{ label: string; value: string; sub?: string; accent: string }> = [
      {
        label: "Events",
        value: shotTelemetrySummary.totalEvents.toLocaleString(),
        sub: `${shotTelemetrySummary.firedEvents} fired · ${shotTelemetrySummary.hitEvents} hit`,
        accent: "#00f5a0",
      },
    ];

    if (shotTelemetrySummary.avgBotCount != null) {
      cards.push({
        label: "Avg bots seen",
        value: shotTelemetrySummary.avgBotCount.toFixed(1),
        accent: "#00b4ff",
      });
    }
    if (shotTelemetrySummary.avgNearestDistance != null) {
      cards.push({
        label: "Nearest range",
        value: shotTelemetrySummary.avgNearestDistance.toFixed(0),
        accent: "#ffd700",
      });
    }
    if (shotTelemetrySummary.avgNearestYawError != null) {
      cards.push({
        label: "Yaw error",
        value: `${shotTelemetrySummary.avgNearestYawError.toFixed(1)}°`,
        accent: "#ff9f43",
      });
    }
    if (shotTelemetrySummary.avgNearestPitchError != null) {
      cards.push({
        label: "Pitch error",
        value: `${shotTelemetrySummary.avgNearestPitchError.toFixed(1)}°`,
        accent: "#a78bfa",
      });
    }

    return cards;
  }, [shotTelemetrySummary]);
  const shotTelemetrySummaryLabel = useMemo(() => {
    if (shotTelemetryDisplayMode === "context") {
      return `Showing ${shotTelemetryContext.rows.length.toLocaleString()} moments`;
    }
    if (selectedContextRow) {
      return `Showing ${visibleShotTelemetry.length.toLocaleString()} shot records in this moment`;
    }
    return `Showing ${shotTelemetry.length.toLocaleString()} shot records`;
  }, [selectedContextRow, shotTelemetry.length, shotTelemetryContext.rows.length, shotTelemetryContext.source, shotTelemetryDisplayMode, visibleShotTelemetry.length]);
  const shotTelemetrySummaryInfo = useMemo(() => {
    if (shotTelemetryDisplayMode === "context") {
      if (shotTelemetryContext.source === "sql") {
        return `${shotTelemetryContext.rows.length.toLocaleString()} saved moments were grouped from this run.`;
      }
      return `${shotTelemetryContext.rows.length.toLocaleString()} moments were rebuilt from ${shotTelemetry.length.toLocaleString()} saved shot records.`;
    }
    if (selectedContextRow) {
      return `${visibleShotTelemetry.length.toLocaleString()} shot records fall inside the selected moment.`;
    }
    return `All ${shotTelemetry.length.toLocaleString()} saved shot records from this run.`;
  }, [selectedContextRow, shotTelemetry.length, shotTelemetryContext.rows.length, shotTelemetryContext.source, shotTelemetryDisplayMode, visibleShotTelemetry.length]);
  const shotTelemetrySampleRows = useMemo(() => {
    if (visibleShotTelemetry.length === 0) return [];
    return [...visibleShotTelemetry].reverse().map((event, index) => {
      const botTargets = event.targets.filter((target) => target.is_bot);
      const nearest =
        botTargets.find((target) => target.is_nearest)
        ?? botTargets[0]
        ?? event.targets.find((target) => target.is_nearest)
        ?? event.targets[0]
        ?? null;

      return {
        key: `${event.ts_ms}-${event.sample_seq ?? index}-${event.event}`,
        offsetMs: Math.max(0, event.ts_ms - shotTelemetryBaseTs),
        eventLabel: event.event === "shot_hit" ? "Hit" : "Fired",
        count: Math.max(1, event.count ?? 1),
        total: event.total,
        botCount: botTargets.length,
        nearestLabel: nearest
          ? formatShotTelemetryTargetLabel(
              nearest.profile,
              nearest.entity_id,
              (nearestTargetProfileEntityCounts.get(nearest.profile?.trim() || nearest.entity_id)?.size ?? 0) > 1,
            )
          : "—",
        nearestDistance: nearest?.distance_3d ?? nearest?.distance_2d ?? null,
        yawError: nearest?.yaw_error_deg != null ? Math.abs(nearest.yaw_error_deg) : null,
        pitchError: nearest?.pitch_error_deg != null ? Math.abs(nearest.pitch_error_deg) : null,
      };
    });
  }, [nearestTargetProfileEntityCounts, shotTelemetryBaseTs, visibleShotTelemetry]);
  const filteredRunMoments = useMemo(() => {
    if (!selectedContextRow) return runMoments;
    return runMoments.filter((moment) =>
      rangesOverlap(moment.startSec * 1000, moment.endSec * 1000, selectedContextRow.startMs, selectedContextRow.endMs),
    );
  }, [runMoments, selectedContextRow]);
  const focusMomentRows = shotTelemetryContext.rows;
  const selectedContextIndex = selectedContextRow
    ? focusMomentRows.findIndex((row) => row.key === selectedContextRow.key)
    : -1;
  const canFocusPrev = selectedContextIndex > 0;
  const canFocusNext = selectedContextIndex >= 0 && selectedContextIndex < focusMomentRows.length - 1;
  const activeFocusTitle = selectedContextRow ? selectedContextRow.label : "Full run";
  const activeFocusDetail = selectedContextRow
    ? [
        formatTelemetryWindowLabel(selectedContextRow.startMs, selectedContextRow.endMs),
        formatReplayMomentPhaseLabel(selectedContextRow.phase),
        formatReplayMomentContextLabel(selectedContextRow.contextKind),
      ].filter(Boolean).join(" · ")
    : "Showing the full replay with all saved moments.";
  const jumpFocusedMoment = (direction: -1 | 1) => {
    if (selectedContextIndex < 0) return;
    const nextRow = focusMomentRows[selectedContextIndex + direction];
    if (!nextRow) return;
    setSelectedContextKey(nextRow.key);
  };
  const visibleReplayContextCoaching = useMemo(() => {
    if (!selectedContextRow) return replayContextCoaching;
    return replayContextCoaching.filter((signal) => signal.contextKey === selectedContextRow.key);
  }, [replayContextCoaching, selectedContextRow]);
  const replayBaseTs = useMemo(() => {
    if (runSnapshot?.started_at_bridge_ts_ms != null) {
      return runSnapshot.started_at_bridge_ts_ms;
    }
    return estimateReplayBridgeBaseTs(shotTelemetry, runTimeline);
  }, [runSnapshot?.started_at_bridge_ts_ms, runTimeline, shotTelemetry]);
  const selectedContextRunWindow = useMemo(() => {
    if (!selectedContextRow) return null;
    // Context row times are shot-relative (offset from shotTelemetryBaseTs = first shot's ts_ms).
    // Target-response episode times and replay position timestamps are run-relative
    // (offset from replayBaseTs = started_at_bridge_ts_ms).
    // Add the first-shot offset to convert shot-relative → run-relative.
    const firstShotOffsetMs = Math.max(0, shotTelemetryBaseTs - replayBaseTs);
    return {
      startMs: selectedContextRow.startMs + firstShotOffsetMs,
      endMs: selectedContextRow.endMs + firstShotOffsetMs,
    };
  }, [selectedContextRow, shotTelemetryBaseTs, replayBaseTs]);
  const replaySelectionRange = useMemo(() => {
    if (!selectedContextRunWindow) return null;
    return {
      startMs: Math.max(0, selectedContextRunWindow.startMs - 750),
      endMs: selectedContextRunWindow.endMs + 750,
    };
  }, [selectedContextRunWindow]);
  const replayPayloadView = useMemo(() => {
    if (!replayPayload || !replaySelectionRange) return replayPayload;
    const positions = sliceRowsToRange(replayPayload.positions, replaySelectionRange.startMs, replaySelectionRange.endMs);
    const metrics = sliceRowsToRange(replayPayload.metrics, replaySelectionRange.startMs, replaySelectionRange.endMs);
    const frames = sliceRowsToRange(replayPayload.frames ?? [], replaySelectionRange.startMs, replaySelectionRange.endMs);
    if (positions.length < 2 && metrics.length === 0 && frames.length === 0) {
      return replayPayload;
    }
    return {
      positions,
      metrics,
      frames,
    };
  }, [replayPayload, replaySelectionRange]);
  const replayTimelineMarkers = useMemo(() => {
    if (visibleShotTelemetry.length === 0) return [] as Array<{ id: string; timestamp_ms: number; color: string; label: string }>;
    const hitEvents = visibleShotTelemetry.filter((event) => event.event === "shot_hit");
    const firedEvents = visibleShotTelemetry.filter((event) => event.event === "shot_fired");
    const markerSource = hitEvents.length > 0 ? hitEvents : firedEvents;
    const step = Math.max(1, Math.ceil(markerSource.length / 24));
    return markerSource.filter((_, index) => index % step === 0).map((event, index) => {
      const nearest = event.targets.find((target) => target.is_nearest) ?? event.targets[0] ?? null;
      return {
        id: `${event.event}-${event.ts_ms}-${index}`,
        timestamp_ms: Math.max(0, event.ts_ms - replayBaseTs),
        color: event.event === "shot_hit" ? "#00f5a0" : "#ffd166",
        label: `${event.event === "shot_hit" ? "Hit" : "Shot"}${nearest ? ` · ${nearest.profile || nearest.entity_id}` : ""}`,
      };
    });
  }, [replayBaseTs, visibleShotTelemetry]);
  const replayHitTimestamps = useMemo(
    () =>
      visibleShotTelemetry
        .filter((event) => event.event === "shot_hit")
        .map((event) => Math.max(0, event.ts_ms - replayBaseTs)),
    [replayBaseTs, visibleShotTelemetry],
  );
  const replayTimelineWindows = useMemo(
    () => visibleReplayContextCoaching.map((signal) => ({
      id: signal.id,
      start_ms: signal.startMs,
      end_ms: signal.endMs,
      color: signal.badgeColor,
      label: signal.title,
    })),
    [visibleReplayContextCoaching],
  );
  const visibleTargetResponseEpisodes = useMemo(() => {
    const episodes = targetResponseAnalysis?.episodes ?? [];
    if (!selectedContextRunWindow) return episodes;
    return episodes.filter((episode) =>
      rangesOverlap(episode.startMs, episode.endMs, selectedContextRunWindow.startMs, selectedContextRunWindow.endMs),
    );
  }, [selectedContextRunWindow, targetResponseAnalysis?.episodes]);
  const targetResponseSummaryCards = useMemo(() => {
    const summary = targetResponseAnalysis?.summary;
    if (!summary) return [] as Array<{ label: string; value: string; sub?: string; accent: string }>;

    const cards: Array<{ label: string; value: string; sub?: string; accent: string }> = [
      {
        label: "Episodes",
        value: summary.episode_count.toLocaleString(),
        sub: `${summary.path_change_count} path breaks · ${summary.target_switch_count} switches`,
        accent: "#00b4ff",
      },
    ];

    if (summary.avg_reaction_time_ms != null) {
      cards.push({
        label: "Reaction",
        value: fmtLatencyMs(summary.avg_reaction_time_ms),
        sub:
          summary.avg_path_change_reaction_ms != null || summary.avg_target_switch_reaction_ms != null
            ? `path ${fmtLatencyMs(summary.avg_path_change_reaction_ms)} · switch ${fmtLatencyMs(summary.avg_target_switch_reaction_ms)}`
            : undefined,
        accent: "#00f5a0",
      });
    }
    if (summary.avg_pre_slowdown_reaction_ms != null) {
      cards.push({
        label: "Pre-slowdown",
        value: fmtLatencyMs(summary.avg_pre_slowdown_reaction_ms),
        accent: "#ffd166",
      });
    }
    if (summary.avg_recovery_time_ms != null) {
      cards.push({
        label: "Recovery",
        value: fmtLatencyMs(summary.avg_recovery_time_ms),
        sub: summary.p90_recovery_time_ms != null ? `p90 ${fmtLatencyMs(summary.p90_recovery_time_ms)}` : undefined,
        accent: "#ff9f43",
      });
    }
    if (summary.stable_response_ratio != null) {
      cards.push({
        label: "Stable responses",
        value: `${(summary.stable_response_ratio * 100).toFixed(0)}%`,
        accent: "#a78bfa",
      });
    }
    if (summary.avg_trigger_magnitude_deg != null) {
      cards.push({
        label: "Trigger size",
        value: `${summary.avg_trigger_magnitude_deg.toFixed(1)}°`,
        sub: summary.avg_peak_yaw_error_deg != null ? `peak err ${summary.avg_peak_yaw_error_deg.toFixed(1)}°` : undefined,
        accent: "#00b4ff",
      });
    }

    return cards;
  }, [targetResponseAnalysis?.summary]);

  // In-game replay logic disabled for development. Logic is preserved but not executed.
  // const handlePlayInGameReplay = async () => {
  //   if (!selectedId) return;
  //   setInGameReplayBusy(true);
  //   setInGameReplayStatus(null);
  //   try {
  //     await invoke("replay_play_in_game", { sessionId: selectedId, speed: 1.0 });
  //     setInGameReplayStatus("Streaming replay to game...");
  //   } catch (err) {
  //     setInGameReplayStatus(String(err));
  //   } finally {
  //     setInGameReplayBusy(false);
  //   }
  // };

  // const handleStopInGameReplay = async () => {
  //   setInGameReplayBusy(true);
  //   setInGameReplayStatus(null);
  //   try {
  //     await invoke("replay_stop_in_game");
  //     setInGameReplayStatus("Stopped in-game replay.");
  //   } catch (err) {
  //     setInGameReplayStatus(String(err));
  //   } finally {
  //     setInGameReplayBusy(false);
  //   }
  // };

  if (replayRecords.length === 0) {
    return (
      <div style={{ color: "rgba(255,255,255,0.3)", padding: 20, lineHeight: 1.7 }}>
        No replays saved yet.
        <br />
        Replays are recorded automatically during sessions when mouse tracking is active.
      </div>
    );
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      {/* Session selector */}
      <div style={CHART_STYLE}>
        <SectionTitle info="Pick any run to inspect the mouse replay, shot detail, and how the run changed over time.">Select session</SectionTitle>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, marginBottom: 10, flexWrap: "wrap" }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
            {selectedRecord ? (
              <>
                <Btn
                  size="sm"
                  variant={selectedRecord.replay_is_favorite ? "accent" : "ghost"}
                  onClick={handleToggleFavorite}
                  disabled={actionBusy != null}
                >
                  {actionBusy === "favorite"
                    ? "Saving…"
                    : selectedRecord.replay_is_favorite
                      ? "★ Favorited"
                      : "☆ Favorite"}
                </Btn>
                <Btn size="sm" variant="ghost" onClick={handleExportReplay} disabled={actionBusy != null}>
                  {actionBusy === "export" ? "Exporting…" : "Export video"}
                </Btn>
                <Btn size="sm" variant="ghost" onClick={handleDeleteReplay} disabled={actionBusy != null}>
                  {actionBusy === "delete" ? "Removing…" : "Remove replay"}
                </Btn>
              </>
            ) : null}
          </div>
          <div style={{ display: "flex", gap: 6, alignItems: "center" }}>
            <span style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 700 }}>
              Sort
            </span>
            {(["date", "score", "duration"] as const).map((column) => {
              const active = sortBy === column;
              return (
                <button
                  key={column}
                  type="button"
                  onClick={() => toggleReplaySort(column)}
                  style={{
                    background: active ? accentAlpha("16") : "rgba(255,255,255,0.04)",
                    border: `1px solid ${active ? C.accentBorder : C.border}`,
                    borderRadius: 999,
                    color: active ? C.accent : C.textMuted,
                    cursor: "pointer",
                    fontSize: 10,
                    padding: "5px 8px",
                    fontFamily: "inherit",
                    textTransform: "uppercase",
                    letterSpacing: "0.06em",
                    fontWeight: 700,
                  }}
                >
                  {column === "date" ? "Date" : column === "score" ? "Score" : "Duration"} {active ? (sortDir === "desc" ? "↓" : "↑") : ""}
                </button>
              );
            })}
          </div>
        </div>
        {actionStatus ? (
          <div style={{ marginBottom: 10, fontSize: 11, color: C.textFaint, lineHeight: 1.6 }}>
            {actionStatus}
          </div>
        ) : null}
        <div style={{ maxHeight: 180, overflowY: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
            <thead>
              <tr style={{ color: "rgba(255,255,255,0.3)" }}>
                {[
                  "Date",
                  ...(hubMode ? ["Scenario"] : []),
                  "Score",
                  "Duration",
                  "Acc",
                  "Smooth",
                ].map((h) => (
                  <th
                    key={h}
                    style={{
                      paddingBottom: 6,
                      fontWeight: 500,
                      textAlign: "left",
                      borderBottom: "1px solid rgba(255,255,255,0.07)",
                    }}
                  >
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {sortedReplayRecords.map((r) => {
                const active = r.id === selectedId;
                const isWarmup = warmupIds.has(r.id);
                return (
                  <tr
                    key={r.id}
                    onClick={() => setSelectedId(r.id)}
                    style={{
                      cursor: "pointer",
                      opacity: isWarmup ? 0.65 : 1,
                      background: active ? "rgba(0,245,160,0.07)" : "transparent",
                      borderBottom: "1px solid rgba(255,255,255,0.04)",
                    }}
                  >
                    <td style={{ padding: "7px 4px 7px 0", color: active ? "#00f5a0" : "rgba(255,255,255,0.5)" }}>
                      {r.replay_is_favorite && <span style={{ color: "#ffd700", marginRight: 6 }}>★</span>}
                      {formatDateTime(r.timestamp)}
                      {isWarmup && (
                        <span
                          style={{
                            fontSize: 9,
                            background: "rgba(255,180,0,0.18)",
                            color: "#ffb400",
                            borderRadius: 3,
                            padding: "1px 4px",
                            marginLeft: 5,
                            verticalAlign: "middle",
                          }}
                        >
                          warm-up
                        </span>
                      )}
                    </td>
                    {hubMode && (
                      <td style={{ padding: "7px 4px", color: "rgba(255,255,255,0.6)" }}>
                        {r.scenario}
                      </td>
                    )}
                    <td style={{ padding: "7px 4px", fontWeight: active ? 700 : 400, color: active ? "#fff" : "rgba(255,255,255,0.7)" }}>
                      {fmtScore(r.score)}
                    </td>
                    <td style={{ padding: "7px 4px", color: "rgba(255,255,255,0.5)" }}>
                      {fmtDuration(r.duration_secs)}
                    </td>
                    <td style={{ padding: "7px 4px", color: "rgba(255,255,255,0.5)" }}>
                      {r.accuracy > 0 ? r.accuracy.toFixed(1) + "%" : "—"}
                    </td>
                    <td style={{ padding: "7px 4px", color: r.smoothness ? "#00b4ff" : "rgba(255,255,255,0.2)" }}>
                      {r.smoothness ? r.smoothness.composite.toFixed(1) : "—"}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>

      {loading && (
        <div style={{ color: "rgba(255,255,255,0.3)", fontSize: 12 }}>Loading replay…</div>
      )}
      {!loading && replayPayload && selectedRecord && (
        runSnapshot ? (
          <div
            style={{
              display: "grid",
              gridTemplateColumns: "minmax(280px, 340px) minmax(0, 1fr)",
              gap: 20,
              alignItems: "start",
            }}
          >
            <div
              style={{
                display: "flex",
                flexDirection: "column",
                gap: 14,
                position: "sticky",
                top: 0,
                alignSelf: "start",
                maxHeight: "calc(100vh - 72px)",
                overflowY: "auto",
                paddingRight: 4,
              }}
            >
              <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 12 }}>
                <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "flex-start" }}>
                  <div>
                    <div style={{ fontSize: 11, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.1em", fontWeight: 700 }}>
                      Replay Focus
                    </div>
                    <div style={{ marginTop: 6, fontSize: 16, color: C.text, fontWeight: 700 }}>
                      {activeFocusTitle}
                    </div>
                    <div style={{ marginTop: 6, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
                      {activeFocusDetail}
                    </div>
                  </div>
                  {selectedContextRow && (
                    <button
                      type="button"
                      onClick={() => setSelectedContextKey(null)}
                      style={{
                        background: "rgba(255,255,255,0.05)",
                        border: "1px solid rgba(255,255,255,0.14)",
                        borderRadius: 999,
                        color: "rgba(255,255,255,0.74)",
                        cursor: "pointer",
                        fontSize: 10,
                        padding: "5px 10px",
                        textTransform: "uppercase",
                        letterSpacing: "0.06em",
                        fontWeight: 700,
                        whiteSpace: "nowrap",
                      }}
                    >
                      Show Full Run
                    </button>
                  )}
                </div>

                <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                  <button
                    type="button"
                    onClick={() => jumpFocusedMoment(-1)}
                    disabled={!canFocusPrev}
                    style={{
                      background: canFocusPrev ? "rgba(255,255,255,0.05)" : "rgba(255,255,255,0.02)",
                      border: `1px solid ${canFocusPrev ? "rgba(255,255,255,0.14)" : "rgba(255,255,255,0.08)"}`,
                      borderRadius: 999,
                      color: canFocusPrev ? C.textSub : C.textFaint,
                      cursor: canFocusPrev ? "pointer" : "not-allowed",
                      fontSize: 10,
                      padding: "5px 10px",
                      textTransform: "uppercase",
                      letterSpacing: "0.06em",
                      fontWeight: 700,
                    }}
                  >
                    Previous
                  </button>
                  <button
                    type="button"
                    onClick={() => jumpFocusedMoment(1)}
                    disabled={!canFocusNext}
                    style={{
                      background: canFocusNext ? "rgba(255,255,255,0.05)" : "rgba(255,255,255,0.02)",
                      border: `1px solid ${canFocusNext ? "rgba(255,255,255,0.14)" : "rgba(255,255,255,0.08)"}`,
                      borderRadius: 999,
                      color: canFocusNext ? C.textSub : C.textFaint,
                      cursor: canFocusNext ? "pointer" : "not-allowed",
                      fontSize: 10,
                      padding: "5px 10px",
                      textTransform: "uppercase",
                      letterSpacing: "0.06em",
                      fontWeight: 700,
                    }}
                  >
                    Next
                  </button>
                  <div style={{ display: "flex", gap: 6, marginLeft: "auto" }}>
                    {([
                      { key: "context", label: "Moments" },
                      { key: "samples", label: "Shots" },
                    ] as const).map((option) => {
                      const active = shotTelemetryDisplayMode === option.key;
                      return (
                        <button
                          key={option.key}
                          type="button"
                          onClick={() => setShotTelemetryDisplayMode(option.key)}
                          style={{
                            background: active ? accentAlpha("16") : "rgba(255,255,255,0.04)",
                            border: `1px solid ${active ? C.accentBorder : C.border}`,
                            borderRadius: 999,
                            color: active ? C.accent : C.textMuted,
                            cursor: "pointer",
                            fontSize: 10,
                            padding: "5px 8px",
                            fontFamily: "inherit",
                            textTransform: "uppercase",
                            letterSpacing: "0.06em",
                            fontWeight: 700,
                          }}
                        >
                          {option.label}
                        </button>
                      );
                    })}
                  </div>
                </div>

                <div style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 11, color: "rgba(255,255,255,0.42)", lineHeight: 1.5 }}>
                  <span>{shotTelemetrySummaryLabel}</span>
                  <InfoTip text={shotTelemetrySummaryInfo} />
                </div>
              </div>

              <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 12 }}>
                <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "center" }}>
                  <SectionTitle>Focus moments</SectionTitle>
                  <span style={{ fontSize: 11, color: C.textFaint }}>
                    {focusMomentRows.length.toLocaleString()}
                  </span>
                </div>
                {focusMomentRows.length > 0 ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 8, maxHeight: 420, overflowY: "auto", paddingRight: 4 }}>
                    {focusMomentRows.map((row) => {
                      const active = row.key === selectedContextKey;
                      return (
                        <button
                          key={row.key}
                          type="button"
                          onClick={() => setSelectedContextKey((current) => current === row.key ? null : row.key)}
                          style={{
                            textAlign: "left",
                            background: active ? "rgba(0,245,160,0.08)" : "rgba(255,255,255,0.03)",
                            border: `1px solid ${active ? "rgba(0,245,160,0.28)" : "rgba(255,255,255,0.08)"}`,
                            borderLeft: `3px solid ${active ? "#00f5a0" : "rgba(255,255,255,0.16)"}`,
                            borderRadius: 10,
                            padding: "10px 12px",
                            cursor: "pointer",
                            display: "flex",
                            flexDirection: "column",
                            gap: 6,
                          }}
                        >
                          <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "center" }}>
                            <span style={{ fontSize: 12, fontWeight: 700, color: active ? C.text : C.textSub }}>
                              {row.label}
                            </span>
                            <span style={{ fontSize: 10, color: C.textFaint }}>
                              {formatTelemetryWindowLabel(row.startMs, row.endMs)}
                            </span>
                          </div>
                          <div style={{ fontSize: 11, color: C.textFaint, lineHeight: 1.5 }}>
                            {[formatReplayMomentPhaseLabel(row.phase), formatReplayMomentContextLabel(row.contextKind)].filter(Boolean).join(" · ") || "Saved moment"}
                          </div>
                          <div style={{ display: "flex", gap: 10, flexWrap: "wrap", fontSize: 11, color: "rgba(255,255,255,0.62)" }}>
                            <span style={{ color: "#ffd166" }}>{row.firedCount.toLocaleString()} fired</span>
                            <span style={{ color: "#00f5a0" }}>{row.hitCount.toLocaleString()} hit</span>
                            <span>{row.accuracyPct != null ? `${row.accuracyPct.toFixed(1)}% acc` : "—"}</span>
                          </div>
                        </button>
                      );
                    })}
                  </div>
                ) : (
                  <div style={{ color: "rgba(255,255,255,0.45)", fontSize: 12, lineHeight: 1.6 }}>
                    No saved moments are available for this replay yet.
                  </div>
                )}
              </div>

              {visibleReplayContextCoaching.length > 0 && (
                <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 10 }}>
                  <SectionTitle>Quick notes</SectionTitle>
                  {visibleReplayContextCoaching.slice(0, selectedContextRow ? 4 : 3).map((signal) => (
                    <button
                      key={signal.id}
                      type="button"
                      onClick={() => setSelectedContextKey((current) => current === signal.contextKey ? null : signal.contextKey)}
                      style={{
                        textAlign: "left",
                        background: selectedContextKey === signal.contextKey ? `${signal.badgeColor}12` : "rgba(255,255,255,0.03)",
                        border: `1px solid ${selectedContextKey === signal.contextKey ? `${signal.badgeColor}45` : "rgba(255,255,255,0.08)"}`,
                        borderLeft: `3px solid ${signal.badgeColor}`,
                        borderRadius: 10,
                        padding: "10px 12px",
                        cursor: "pointer",
                        display: "flex",
                        flexDirection: "column",
                        gap: 4,
                      }}
                    >
                      <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "center" }}>
                        <span style={{ fontSize: 12, fontWeight: 700, color: "rgba(255,255,255,0.84)" }}>{signal.title}</span>
                        <span style={{ fontSize: 10, color: C.textFaint }}>{formatTelemetryWindowLabel(signal.startMs, signal.endMs)}</span>
                      </div>
                      <div style={{ fontSize: 11, color: C.textSub, lineHeight: 1.5 }}>{signal.detail}</div>
                    </button>
                  ))}
                </div>
              )}
            </div>

            <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
              <div style={CHART_STYLE}>
                <SectionTitle>
                  Mouse path —{" "}
                  <span style={{ color: "#00f5a0", fontWeight: 700 }}>{fmtScore(selectedRecord.score)}</span>{" "}
                  pts · {formatDateTime(selectedRecord.timestamp)}
                </SectionTitle>
                <MousePathViewer
                  rawPositions={replayPayloadView?.positions ?? replayPayload.positions}
                  metricPoints={replayPayloadView?.metrics ?? replayPayload.metrics}
                  screenFrames={replayPayloadView?.frames ?? replayPayload.frames ?? []}
                  hitTimestampsMs={replayHitTimestamps}
                  segmentLabel={selectedContextRow?.label ?? null}
                  segmentWindowLabel={selectedContextRow ? formatTelemetryWindowLabel(selectedContextRow.startMs, selectedContextRow.endMs) : null}
                  timelineMarkers={replayTimelineMarkers}
                  timelineWindows={replayTimelineWindows}
                />
              </div>

              {runChartData.length > 1 && hasRunTimelineSignal && (
                <div style={CHART_STYLE}>
                  <SectionTitle>Run timeline by second</SectionTitle>
                  <ResponsiveContainer width="100%" height={200}>
                    <LineChart data={runChartData} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
                      <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
                      <XAxis
                        dataKey="tSec"
                        tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                        tickLine={false}
                        axisLine={false}
                        label={{ value: "seconds", position: "insideBottomRight", offset: -5, fill: "rgba(255,255,255,0.35)", fontSize: 10 }}
                      />
                      <YAxis
                        yAxisId="pace"
                        tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                        tickLine={false}
                        axisLine={false}
                        width={42}
                      />
                      <YAxis
                        yAxisId="pct"
                        orientation="right"
                        tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
                        tickLine={false}
                        axisLine={false}
                        width={42}
                        domain={[0, 100]}
                      />
                      <Tooltip content={<MiniTooltip />} />
                      {runMoments.map((moment) => (
                        <ReferenceArea
                          key={moment.id}
                          x1={moment.startSec}
                          x2={moment.endSec}
                          fill={moment.level === "warning" ? "#ff6b6b" : moment.level === "good" ? "#00f5a0" : "#ffd166"}
                          fillOpacity={0.08}
                          strokeOpacity={0}
                        />
                      ))}
                      {selectedContextRow && (
                        <ReferenceArea
                          x1={selectedContextRow.startMs / 1000}
                          x2={selectedContextRow.endMs / 1000}
                          fill={selectedContextRow.source === "sql" ? "#00f5a0" : "#ffd166"}
                          fillOpacity={0.14}
                          strokeOpacity={0}
                        />
                      )}
                      <Line yAxisId="pace" type="monotone" dataKey="spm" name="SPM" stroke="#00f5a0" strokeWidth={2} dot={false} connectNulls />
                      <Line yAxisId="pace" type="monotone" dataKey="kps" name="KPS" stroke="#00b4ff" strokeWidth={1.7} strokeDasharray="4 3" dot={false} connectNulls />
                      <Line yAxisId="pct" type="monotone" dataKey="acc" name="Accuracy %" stroke="#ffd700" strokeWidth={1.8} dot={false} connectNulls />
                    </LineChart>
                  </ResponsiveContainer>
                </div>
              )}

              <div style={CHART_STYLE}>
                <SectionTitle>{selectedContextRow ? "Focused moment detail" : "Run stats"}</SectionTitle>
                {bridgeRunStatCards.length > 0 && (
                  <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
                    {bridgeRunStatCards.map((card) => (
                      <StatCard
                        key={card.label}
                        label={card.label}
                        value={card.value}
                        sub={card.sub}
                        accent={card.accent}
                      />
                    ))}
                  </div>
                )}
                {(runChartData.length <= 1 || !hasRunTimelineSignal) && (
                  <div style={{ marginTop: 10, fontSize: 11, color: "rgba(255,255,255,0.42)", lineHeight: 1.5 }}>
                    This run has limited second-by-second detail, so the summary above uses the best saved data available.
                  </div>
                )}
              </div>

              <div style={CHART_STYLE}>
                <SectionTitle
                  info={
                    selectedContextRow
                      ? `Showing extracted response episodes that overlap ${formatTelemetryWindowLabel(selectedContextRow.startMs, selectedContextRow.endMs)}.`
                      : "Response episodes are extracted from replay tick data and mouse-path motion in Rust."
                  }
                >
                  {selectedContextRow ? "Target response in this moment" : "Target response across the run"}
                </SectionTitle>
                {targetResponseAnalysis ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    {targetResponseSummaryCards.length > 0 && (
                      <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
                        {targetResponseSummaryCards.map((card) => (
                          <StatCard
                            key={card.label}
                            label={card.label}
                            value={card.value}
                            sub={card.sub}
                            accent={card.accent}
                          />
                        ))}
                      </div>
                    )}
                    <div style={{ fontSize: 11, color: "rgba(255,255,255,0.46)", lineHeight: 1.55 }}>
                      Extracted {targetResponseAnalysis.episodeCount.toLocaleString()} response episodes with{" "}
                      {targetResponseAnalysis.responseCoveragePct != null
                        ? `${targetResponseAnalysis.responseCoveragePct.toFixed(0)}%`
                        : "unknown"}{" "}
                      coverage.
                      {selectedContextRow
                        ? ` ${visibleTargetResponseEpisodes.length.toLocaleString()} overlap the selected moment.`
                        : ""}
                    </div>
                    {visibleTargetResponseEpisodes.length > 0 ? (
                      <div style={{ overflowX: "auto" }}>
                        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
                          <thead>
                            <tr style={{ color: "rgba(255,255,255,0.35)" }}>
                              {["T", "Type", "Target", "Trigger", "Reaction", "Pre-slow", "Recovery", "Peak yaw", "Stable"].map((heading) => (
                                <th
                                  key={heading}
                                  style={{
                                    padding: "0 0 6px",
                                    textAlign: "left",
                                    fontWeight: 500,
                                    borderBottom: "1px solid rgba(255,255,255,0.07)",
                                  }}
                                >
                                  {heading}
                                </th>
                              ))}
                            </tr>
                          </thead>
                          <tbody>
                            {visibleTargetResponseEpisodes.map((episode) => (
                              <tr key={episode.id} style={{ borderBottom: "1px solid rgba(255,255,255,0.04)" }}>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>
                                  {formatTelemetryOffset(episode.startMs)}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: episode.kind === "path_change" ? "#ff9f43" : "#00b4ff" }}>
                                  {formatTargetResponseKind(episode.kind)}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>
                                  {episode.targetLabel}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.6)" }}>
                                  {episode.triggerMagnitudeDeg != null ? `${episode.triggerMagnitudeDeg.toFixed(1)}°` : "—"}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "#00f5a0" }}>
                                  {fmtLatencyMs(episode.reactionTimeMs)}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "#ffd166" }}>
                                  {fmtLatencyMs(episode.preSlowdownReactionMs)}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "#ff9f43" }}>
                                  {fmtLatencyMs(episode.recoveryTimeMs)}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.6)" }}>
                                  {episode.peakYawErrorDeg != null ? `${episode.peakYawErrorDeg.toFixed(1)}°` : "—"}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: episode.stableResponse ? "#00f5a0" : "#ff6b6b" }}>
                                  {episode.stableResponse ? "Stable" : "Unsettled"}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    ) : (
                      <div style={{ color: "rgba(255,255,255,0.45)", fontSize: 12, lineHeight: 1.6 }}>
                        No extracted target-response episodes overlap this moment.
                      </div>
                    )}
                  </div>
                ) : (
                  <div style={{ color: "rgba(255,255,255,0.45)", fontSize: 12, lineHeight: 1.6 }}>
                    This replay does not have enough saved bot/tick telemetry to extract reaction and recovery episodes yet.
                  </div>
                )}
              </div>

              <div style={CHART_STYLE}>
                <SectionTitle>{selectedContextRow ? "Shot detail in this moment" : "Shot detail across the run"}</SectionTitle>
                {shotTelemetrySummary ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                    {shotTelemetrySummaryCards.length > 0 && (
                      <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
                        {shotTelemetrySummaryCards.map((card) => (
                          <StatCard
                            key={card.label}
                            label={card.label}
                            value={card.value}
                            sub={card.sub}
                            accent={card.accent}
                          />
                        ))}
                      </div>
                    )}
                    {shotTelemetrySummary.topProfiles.length > 0 && (
                      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                        <span style={{ fontSize: 11, color: "rgba(255,255,255,0.42)" }}>Nearest target mix</span>
                        {shotTelemetrySummary.topProfiles.map(([profile, count]) => (
                          <span
                            key={profile}
                            style={{
                              fontSize: 11,
                              padding: "4px 8px",
                              borderRadius: 999,
                              background: "rgba(255,255,255,0.05)",
                              border: "1px solid rgba(255,255,255,0.08)",
                              color: "rgba(255,255,255,0.72)",
                            }}
                          >
                            {profile} · {count}
                          </span>
                        ))}
                      </div>
                    )}
                    <div style={{ overflowX: "auto" }}>
                      {shotTelemetryDisplayMode === "context" ? (
                        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
                          <thead>
                            <tr style={{ color: "rgba(255,255,255,0.35)" }}>
                              {["Moment", "Context", "Fired", "Hit", "Acc", "Bots", "Nearest", "Range", "Yaw", "Pitch"].map((heading) => (
                                <th
                                  key={heading}
                                  style={{
                                    padding: "0 0 6px",
                                    textAlign: "left",
                                    fontWeight: 500,
                                    borderBottom: "1px solid rgba(255,255,255,0.07)",
                                  }}
                                >
                                  {heading}
                                </th>
                              ))}
                            </tr>
                          </thead>
                          <tbody>
                            {shotTelemetryContext.rows.map((row) => (
                              <tr
                                key={row.key}
                                onClick={() => setSelectedContextKey((current) => current === row.key ? null : row.key)}
                                style={{
                                  borderBottom: "1px solid rgba(255,255,255,0.04)",
                                  cursor: "pointer",
                                  background: selectedContextKey === row.key ? "rgba(0,245,160,0.08)" : "transparent",
                                }}
                              >
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>
                                  {formatTelemetryWindowLabel(row.startMs, row.endMs)}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>
                                  <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
                                    <span>{row.label}</span>
                                    <span style={{ fontSize: 10, color: "rgba(255,255,255,0.42)" }}>
                                      {formatReplayMomentSourceLabel(row.source)}
                                      {formatReplayMomentPhaseLabel(row.phase) ? ` · ${formatReplayMomentPhaseLabel(row.phase)}` : ""}
                                      {formatReplayMomentContextLabel(row.contextKind) ? ` · ${formatReplayMomentContextLabel(row.contextKind)}` : ""}
                                    </span>
                                  </div>
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "#ffd166" }}>{row.firedCount.toLocaleString()}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "#00f5a0" }}>{row.hitCount.toLocaleString()}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>{row.accuracyPct != null ? `${row.accuracyPct.toFixed(1)}%` : "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>{row.avgBotCount != null ? row.avgBotCount.toFixed(1) : "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>{row.nearestLabel}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{row.nearestDistance != null ? row.nearestDistance.toFixed(0) : "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{row.yawError != null ? `${row.yawError.toFixed(1)}°` : "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{row.pitchError != null ? `${row.pitchError.toFixed(1)}°` : "—"}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      ) : (
                        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
                          <thead>
                            <tr style={{ color: "rgba(255,255,255,0.35)" }}>
                              {["T", "Event", "Total", "Bots", "Nearest", "Range", "Yaw", "Pitch"].map((heading) => (
                                <th
                                  key={heading}
                                  style={{
                                    padding: "0 0 6px",
                                    textAlign: "left",
                                    fontWeight: 500,
                                    borderBottom: "1px solid rgba(255,255,255,0.07)",
                                  }}
                                >
                                  {heading}
                                </th>
                              ))}
                            </tr>
                          </thead>
                          <tbody>
                            {shotTelemetrySampleRows.map((row) => (
                              <tr key={row.key} style={{ borderBottom: "1px solid rgba(255,255,255,0.04)" }}>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{formatTelemetryOffset(row.offsetMs)}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: row.eventLabel === "Hit" ? "#00f5a0" : "#ffd166" }}>
                                  {row.count > 1 ? `${row.eventLabel} ×${row.count}` : row.eventLabel}
                                </td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>{row.total ?? "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>{row.botCount || "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.72)" }}>{row.nearestLabel}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{row.nearestDistance != null ? row.nearestDistance.toFixed(0) : "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{row.yawError != null ? `${row.yawError.toFixed(1)}°` : "—"}</td>
                                <td style={{ padding: "7px 8px 7px 0", color: "rgba(255,255,255,0.52)" }}>{row.pitchError != null ? `${row.pitchError.toFixed(1)}°` : "—"}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      )}
                    </div>
                  </div>
                ) : (
                  <div style={{ color: "rgba(255,255,255,0.45)", fontSize: 12, lineHeight: 1.6 }}>
                    No shot detail was saved for this replay.
                  </div>
                )}
              </div>

              {filteredRunMoments.length > 0 && (
                <div style={CHART_STYLE}>
                  <SectionTitle
                    info={selectedContextRow ? `Showing only coaching notes that overlap ${formatTelemetryWindowLabel(selectedContextRow.startMs, selectedContextRow.endMs)}.` : undefined}
                  >
                    Moment coaching
                  </SectionTitle>
                  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    {filteredRunMoments.map((moment) => (
                      <div
                        key={moment.id}
                        style={{
                          background: "rgba(255,255,255,0.03)",
                          border: "1px solid rgba(255,255,255,0.08)",
                          borderRadius: 8,
                          padding: "10px 12px",
                          display: "flex",
                          flexDirection: "column",
                          gap: 6,
                        }}
                      >
                        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8 }}>
                          <div style={{ fontWeight: 700, color: moment.level === "warning" ? "#ff6b6b" : moment.level === "good" ? "#00f5a0" : "#ffd166" }}>
                            {moment.title}
                          </div>
                          <div style={{ fontSize: 11, color: "rgba(255,255,255,0.45)" }}>
                            {formatUnifiedRunWindow(moment.startSec, moment.endSec)}
                          </div>
                        </div>
                        <div style={{ fontSize: 12, color: "rgba(255,255,255,0.62)", lineHeight: 1.55 }}>
                          {moment.detail}
                        </div>
                        <div style={{ fontSize: 12, color: "rgba(255,255,255,0.78)", lineHeight: 1.55 }}>
                          <span style={{ color: "rgba(255,255,255,0.42)" }}>Action: </span>
                          {buildUnifiedRunMomentAction(moment)}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {(replayPayload?.frames?.length ?? 0) === 0 && (
                <div style={{ ...CHART_STYLE, color: "rgba(255,255,255,0.52)", fontSize: 12, lineHeight: 1.6 }}>
                  No video frames were saved for this replay.
                </div>
              )}
            </div>
          </div>
        ) : (
          <>
            <div style={{ ...CHART_STYLE, color: "rgba(255,255,255,0.42)", fontSize: 12, lineHeight: 1.6 }}>
              This replay was saved before full timing detail was available, so only the mouse path can be shown.
            </div>
            <div style={CHART_STYLE}>
              <SectionTitle>
                Mouse path —{" "}
                <span style={{ color: "#00f5a0", fontWeight: 700 }}>{fmtScore(selectedRecord.score)}</span>{" "}
                pts · {formatDateTime(selectedRecord.timestamp)}
              </SectionTitle>
              <MousePathViewer
                rawPositions={replayPayloadView?.positions ?? replayPayload.positions}
                metricPoints={replayPayloadView?.metrics ?? replayPayload.metrics}
                screenFrames={replayPayloadView?.frames ?? replayPayload.frames ?? []}
                hitTimestampsMs={replayHitTimestamps}
                segmentLabel={selectedContextRow?.label ?? null}
                segmentWindowLabel={selectedContextRow ? formatTelemetryWindowLabel(selectedContextRow.startMs, selectedContextRow.endMs) : null}
                timelineMarkers={replayTimelineMarkers}
                timelineWindows={replayTimelineWindows}
              />
            </div>
          </>
        )
      )}
    </div>
  );
}

// ─── Aim Fingerprint ──────────────────────────────────────────────────────────

interface AimFingerprint {
  precision: number;    // 0-100 (path efficiency + low jitter)
  speed: number;        // 0-100 (avg_speed normalised)
  control: number;      // 0-100 (1 - overshoot)
  consistency: number;  // 0-100 (1 - velocity_std)
  decisiveness: number; // 0-100 (1 - correction_ratio)
  rhythm: number;       // 0-100 (1 - click_timing_cv)
  sessionCount: number;
  basisLabel: string;
  axes: AimAxisProfile[];
}

interface AimAxisDefinition {
  label: string;
  what: string;
  how: (tracking: boolean) => string;
}

const AIM_AXIS_DEFINITIONS: Record<AimAxisKey, AimAxisDefinition> = {
  precision: {
    label: "Precision",
    what: "How cleanly and directly you move onto the target.",
    how: () => "Higher scores come from straighter mouse paths and less small shake near the target.",
  },
  speed: {
    label: "Speed",
    what: "How quickly you move when a run is live.",
    how: (tracking) =>
      tracking
        ? "This reflects your typical movement speed across recent tracking runs."
        : "This reflects your typical movement speed across recent clicking runs.",
  },
  control: {
    label: "Control",
    what: "How well you stop cleanly and stay on line without wasting motion.",
    how: () => "Higher scores come from fewer overshoots, fewer extra corrections, and cleaner paths into the target.",
  },
  consistency: {
    label: "Consistency",
    what: "How steady your movement pace stays from moment to moment.",
    how: () => "Higher scores mean your movement speed stays more even instead of speeding up and braking all the time.",
  },
  decisiveness: {
    label: "Decisiveness",
    what: "How quickly you commit once you are on target.",
    how: () => "Higher scores mean you spend less time in small follow-up corrections before finishing the shot or track.",
  },
  rhythm: {
    label: "Rhythm",
    what: "How stable your timing feels during a run.",
    how: (tracking) =>
      tracking
        ? "For tracking, this becomes Flow and rewards smooth, even speed while staying connected to the target."
        : "For clicking, this rewards even shot timing instead of rushed bursts and hesitant pauses.",
  },
};

function dominantScenarioType(records: SessionRecord[]): string {
  const panelRecs = records.filter(
    (r) => r.stats_panel?.scenario_type && r.stats_panel.scenario_type !== "Unknown",
  );
  if (!panelRecs.length) return "Unknown";
  const counts = new Map<string, number>();
  for (const r of panelRecs) {
    const t = r.stats_panel!.scenario_type;
    counts.set(t, (counts.get(t) ?? 0) + 1);
  }
  let best = "Unknown", bestCount = 0;
  for (const [t, c] of counts) if (c > bestCount) { best = t; bestCount = c; }
  return best;
}

function inverseRangeScore(value: number, good: number, bad: number): number {
  return 100 - scaleToScore(value, good, bad);
}

function weightedAxisScore(parts: Array<[score: number, weight: number]>): number {
  const totalWeight = parts.reduce((sum, [, weight]) => sum + weight, 0);
  if (totalWeight <= 0) return 0;
  const weighted = parts.reduce((sum, [score, weight]) => sum + score * weight, 0) / totalWeight;
  return Math.round(clampNumber(weighted, 0, 100));
}

function buildAimFingerprint(smoothRecords: SessionRecord[], scenarioType: string): AimFingerprint {
  const dist = (fn: (s: SmoothnessSnapshot) => number) =>
    metricDistribution(smoothRecords.map((r) => fn(r.smoothness!))) ?? { median: 0, p25: 0, p75: 0 };

  const jitter = dist((s) => s.jitter);
  const overshoot = dist((s) => s.overshoot_rate);
  const velStd = dist((s) => s.velocity_std);
  const avgSpeed = dist((s) => s.avg_speed);
  const pathEff = dist((s) => s.path_efficiency);
  const correction = dist((s) => s.correction_ratio);
  const clickCV = dist((s) => s.click_timing_cv);
  const directionalBias = dist((s) => s.directional_bias);

  const tracking = isTrackingScenario(scenarioType);

  const precision = weightedAxisScore([
    [scaleToScore(pathEff.median, 0.86, 0.985), 0.65],
    [inverseRangeScore(jitter.p75, 0.14, 0.45), 0.35],
  ]);
  const speed = Math.round(scaleToScore(avgSpeed.median, tracking ? 650 : 450, tracking ? 2600 : 2300));
  const control = weightedAxisScore([
    [inverseRangeScore(Math.max(overshoot.median, overshoot.p75 * 0.75), 0.00005, 0.0045), 0.4],
    [inverseRangeScore(Math.max(correction.median, correction.p75 * 0.85), 0.10, 0.42), 0.4],
    [scaleToScore(pathEff.median, 0.88, 0.98), 0.15],
    [inverseRangeScore(Math.max(directionalBias.median, directionalBias.p75 * 0.8), 0.0, 0.08), 0.05],
  ]);
  const consistency = weightedAxisScore([
    [inverseRangeScore(Math.max(velStd.median, velStd.p75 * 0.85), 0.18, 0.9), 0.8],
    [inverseRangeScore(jitter.median, 0.12, 0.42), 0.2],
  ]);
  const decisiveness = weightedAxisScore([
    [inverseRangeScore(Math.max(correction.median, correction.p75 * 0.8), 0.08, 0.38), 0.85],
    [inverseRangeScore(Math.max(directionalBias.median, directionalBias.p75), 0.0, 0.08), 0.15],
  ]);
  const rhythm = tracking
    ? weightedAxisScore([
        [inverseRangeScore(Math.max(velStd.median, velStd.p75), 0.18, 0.95), 0.7],
        [inverseRangeScore(jitter.median, 0.12, 0.42), 0.3],
      ])
    : weightedAxisScore([
        [inverseRangeScore(Math.max(clickCV.median, clickCV.p75 * 0.9), 0.03, 0.28), 0.8],
        [inverseRangeScore(Math.max(correction.median, correction.p75 * 0.85), 0.08, 0.4), 0.2],
      ]);

  const axes: AimAxisProfile[] = [
    {
      key: "precision",
      label: "Precision",
      volatility: Math.round((scaleToVolatility(jitter.p75 - jitter.p25, 0.18) + scaleToVolatility(pathEff.p75 - pathEff.p25, 0.2)) / 2),
    },
    {
      key: "speed",
      label: "Speed",
      volatility: Math.round(scaleToVolatility(avgSpeed.p75 - avgSpeed.p25, 420)),
    },
    {
      key: "control",
      label: "Control",
      volatility: Math.round(
        (
          scaleToVolatility(overshoot.p75 - overshoot.p25, 0.0045)
          + scaleToVolatility(correction.p75 - correction.p25, 0.22)
          + scaleToVolatility(directionalBias.p75 - directionalBias.p25, 0.08)
        ) / 3,
      ),
    },
    {
      key: "consistency",
      label: "Consistency",
      volatility: Math.round(scaleToVolatility(velStd.p75 - velStd.p25, 0.24)),
    },
    {
      key: "decisiveness",
      label: "Decisiveness",
      volatility: Math.round(
        (
          scaleToVolatility(correction.p75 - correction.p25, 0.22)
          + scaleToVolatility(directionalBias.p75 - directionalBias.p25, 0.08)
        ) / 2,
      ),
    },
    {
      key: "rhythm",
      label: tracking ? "Flow" : "Rhythm",
      volatility: Math.round(scaleToVolatility((tracking ? velStd.p75 - velStd.p25 : clickCV.p75 - clickCV.p25), tracking ? 0.24 : 0.3)),
    },
  ];

  return {
    precision,
    speed,
    control,
    consistency,
    decisiveness,
    rhythm,
    sessionCount: smoothRecords.length,
    basisLabel: "Recent saved movement stats",
    axes,
  };
}

interface AimStyle {
  name: string;
  tagline: string;
  color: string;
  description: string;
  focus: string;
}

function classifyAimStyle(fp: AimFingerprint, scenarioType: string): AimStyle {
  const { precision, speed, control, consistency, decisiveness, rhythm } = fp;
  const isTracking = scenarioType === "PureTracking" || scenarioType.includes("Tracking");

  if (isTracking) {
    // rhythm axis = tracking flow (speed evenness) for tracking scenarios
    if (precision > 70 && consistency > 70 && rhythm > 70)
      return {
        name: "The Rail",
        tagline: "Locked on and flowing",
        color: "#00f5a0",
        description:
          "Your tracking is smooth, consistent, and precise — you stay on target with minimal wobble and even speed. You're already a strong tracker; push into harder, faster-moving targets to keep growing.",
        focus: "Faster target variants, smaller hitbox scenarios, long-session endurance",
      };
    if (speed > 65 && consistency < 50)
      return {
        name: "The Sprinter",
        tagline: "Fast but choppy",
        color: "#ff6b6b",
        description:
          "You can keep up with fast targets but your speed is uneven — you accelerate and decelerate in bursts instead of flowing continuously. This choppiness breaks your aim and loses score in longer tracking windows.",
        focus: "Smooth-tracking drills, large target slow-tracking, constant-speed follow scenarios",
      };
    if (control > 70 && precision > 65 && rhythm > 60)
      return {
        name: "The Orbiter",
        tagline: "Smooth and controlled",
        color: "#00b4ff",
        description:
          "You maintain clean, controlled contact with targets and rarely overshoot. Your movement flows well. Speed is the next unlock — you're leaving points on the table by playing too conservatively on faster targets.",
        focus: "Speed-ramp drills, reactive tracking, target-leading practice",
      };
    if (speed > 60 && control > 55 && decisiveness > 60)
      return {
        name: "The Overtaker",
        tagline: "Aggressive and reactive",
        color: "#ffd700",
        description:
          "You chase targets hard and react fast — your instincts are sharp. The gap to close is refining that speed into smoother, sustained contact rather than aggressive reacquisitions.",
        focus: "Strafing target scenarios, smooth acceleration drills, reduce overcorrections",
      };
    if (consistency > 65 && speed < 40)
      return {
        name: "The Anchor",
        tagline: "Steady but slow",
        color: "#a78bfa",
        description:
          "Your tracking is mechanically consistent and clean, but you struggle when targets accelerate or change direction. Your foundation is solid — it's time to push your speed ceiling.",
        focus: "Dynamic tracking scenarios, speed-increasing variants, reaction-based targets",
      };
    return {
      name: "The Foundation Builder",
      tagline: "Building tracking fundamentals",
      color: "#ffd700",
      description:
        "Your tracking mechanics are still developing. Focus on staying on target continuously, matching target speed evenly, and reducing jitter before worrying about score.",
      focus: "Beginner tracking scenarios, large slow targets, smooth-follow drills",
    };
  }

  // Clicking/flicking archetypes
  if (speed > 65 && control < 40)
    return {
      name: "The Aggressor",
      tagline: "Raw speed, needs refinement",
      color: "#ff6b6b",
      description:
        "You move fast and commit hard, but overshoot often. Your instincts are strong — channel that aggression into deliberate deceleration near the target.",
      focus: "Deceleration drills, close-range flick scenarios, overshooting correction",
    };
  if (precision > 70 && control > 65 && speed < 50)
    return {
      name: "The Surgeon",
      tagline: "Clean and controlled",
      color: "#00f5a0",
      description:
        "Your mouse movement is exceptionally clean. You rarely miss, but you're playing conservatively. Match that precision at higher speed and your scores will jump.",
      focus: "Reactive scenarios, tempo drills, increasing flick distance",
    };
  if (consistency > 70 && rhythm > 70)
    return {
      name: "The Metronome",
      tagline: "Mechanically reliable",
      color: "#00b4ff",
      description:
        "Extremely consistent mechanics with a reliable click rhythm. This repeatability is your foundation. Target harder scenarios that force you outside your comfort zone.",
      focus: "Difficulty escalation, novel scenario types to raise your ceiling",
    };
  if (decisiveness > 70 && precision < 55)
    return {
      name: "The Gambler",
      tagline: "Confident but imprecise",
      color: "#ffd700",
      description:
        "You commit fast and trust your instincts — great for reaction time. But shots sometimes fire before fully acquiring the target. Slowing down 10% could dramatically improve accuracy.",
      focus: "Micro-adjustment training, precision clicking, accuracy-first drills",
    };
  if (precision > 65 && consistency > 65)
    return {
      name: "The Technician",
      tagline: "Solid all-around mechanics",
      color: "#a78bfa",
      description:
        "A well-rounded, technically sound aimer with strong precision and consistency. Speed and reactive decision-making are your main remaining growth levers.",
      focus: "Reactive flick scenarios, head-tracking, increasing pace",
    };
  return {
    name: "The Foundation Builder",
    tagline: "Developing core mechanics",
    color: "#ffd700",
    description:
      "Your aim style is still taking shape. Focus on fundamentals: reduce jitter, clean up movement paths, and build consistent click timing before chasing scores.",
    focus: "Tracking basics, precision clicking, click timing trainers",
  };
}

// ─── Coaching Cards ────────────────────────────────────────────────────────────

interface CoachingCardData {
  id?: string;
  source?: "global" | "scenario";
  title: string;
  badge: string;
  badgeColor: string;
  body: string;
  tip: string;
  drills?: DrillRecommendation[];
  confidence?: number;
  signals?: string[];
}

function coachingCardFeedbackId(card: Pick<CoachingCardData, "id" | "title" | "badge">): string {
  const explicitId = card.id?.trim();
  if (explicitId) return explicitId;
  return `legacy:${slugifyScenarioName(card.badge)}:${slugifyScenarioName(card.title)}`;
}

function coachingCardSignalKeys(
  card: Pick<CoachingCardData, "signals" | "badge" | "title">,
): string[] {
  const explicitSignals = (card.signals ?? [])
    .map((signal) => signal.trim())
    .filter((signal) => signal.length > 0);
  if (explicitSignals.length > 0) return explicitSignals;

  const fallback = slugifyScenarioName(card.badge || card.title);
  return fallback ? [fallback] : [];
}

interface ScenarioScoreBaseline {
  medianScore: number;
  sessionCount: number;
  scenarioType: string;
}

interface NormalizedSessionSignal {
  record: AnalyticsSessionRecord;
  normalizedScore: number;
  baseline: ScenarioScoreBaseline;
}

interface FamilyContrastPlan {
  label: string;
  drills: DrillRecommendation[];
}

function buildScenarioScoreBaselines(
  records: AnalyticsSessionRecord[],
): Map<string, ScenarioScoreBaseline> {
  const grouped = new Map<string, {
    scores: number[];
    scenarioTypeCounts: Map<string, number>;
  }>();

  for (const record of records) {
    if (!record.isReliableForAnalysis || !Number.isFinite(record.score) || record.score <= 0) continue;
    const entry = grouped.get(record.normalizedScenario) ?? {
      scores: [],
      scenarioTypeCounts: new Map<string, number>(),
    };
    entry.scores.push(record.score);
    const scenarioType = record.stats_panel?.scenario_type?.trim();
    if (scenarioType && scenarioType !== "Unknown") {
      entry.scenarioTypeCounts.set(
        scenarioType,
        (entry.scenarioTypeCounts.get(scenarioType) ?? 0) + 1,
      );
    }
    grouped.set(record.normalizedScenario, entry);
  }

  const baselines = new Map<string, ScenarioScoreBaseline>();
  for (const [scenario, entry] of grouped.entries()) {
    if (entry.scores.length < 3) continue;
    const sortedScores = [...entry.scores].sort((a, b) => a - b);
    const scenarioType =
      [...entry.scenarioTypeCounts.entries()]
        .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))[0]?.[0]
      ?? "Unknown";
    baselines.set(scenario, {
      medianScore: percentileOf(sortedScores, 50),
      sessionCount: entry.scores.length,
      scenarioType,
    });
  }

  return baselines;
}

function buildNormalizedSessionSignals(records: AnalyticsSessionRecord[]): NormalizedSessionSignal[] {
  const baselines = buildScenarioScoreBaselines(records);
  return records
    .filter((record) => record.isReliableForAnalysis)
    .map((record) => {
      const baseline = baselines.get(record.normalizedScenario);
      if (!baseline || baseline.medianScore <= 0) return null;
      const normalizedScore = record.score / baseline.medianScore;
      if (!Number.isFinite(normalizedScore) || normalizedScore <= 0) return null;
      return {
        record,
        normalizedScore: clampNumber(normalizedScore, 0.2, 3),
        baseline,
      };
    })
    .filter((entry): entry is NormalizedSessionSignal => entry != null);
}

function contrastPlanForScenarioFamily(scenarioType: string): FamilyContrastPlan {
  if (isTrackingScenario(scenarioType)) {
    return {
      label: "dynamic clicking or target switching",
      drills: [
        { label: "Pasu", query: "Pasu" },
        { label: "1w4ts", query: "1w4ts" },
        { label: "Floating Heads", query: "Floating Heads" },
      ],
    };
  }
  if (
    isStaticClickingScenario(scenarioType)
    || isDynamicClickingScenario(scenarioType)
    || isTargetSwitchingScenario(scenarioType)
  ) {
    return {
      label: "smooth tracking",
      drills: [
        { label: "Smoothbot", query: "Smoothbot" },
        { label: "Air", query: "Air" },
        { label: "Centering", query: "Centering" },
      ],
    };
  }
  if (isAccuracyScenario(scenarioType)) {
    return {
      label: "reactive clicking",
      drills: [
        { label: "Microshot", query: "Microshot" },
        { label: "Tile Frenzy", query: "Tile Frenzy" },
        { label: "1wall 6targets", query: "1wall 6targets" },
      ],
    };
  }
  return {
    label: "a contrast family",
    drills: [
      { label: "Smoothbot", query: "Smoothbot" },
      { label: "Pasu", query: "Pasu" },
      { label: "Microshot", query: "Microshot" },
    ],
  };
}

export function generateGlobalCoachingCards(
  records: AnalyticsSessionRecord[],
  practiceProfile: PracticeProfile | null,
  warmupIds: Set<string>,
): CoachingCardData[] {
  const reliableSorted = [...records]
    .filter((record) => record.isReliableForAnalysis)
    .sort((a, b) => a.timestampMs - b.timestampMs);
  if (reliableSorted.length < 6) return [];

  const pushUnique = (cards: CoachingCardData[], card: CoachingCardData) => {
    if (cards.some((existing) => existing.title === card.title)) return;
    cards.push(card);
  };

  const cards: CoachingCardData[] = [];
  const normalizedSignals = buildNormalizedSessionSignals(reliableSorted);
  const settledSignals = normalizedSignals.filter((entry) => !warmupIds.has(entry.record.id));

  if (normalizedSignals.length >= 8 && warmupIds.size >= 2) {
    const warmupNorm = normalizedSignals
      .filter((entry) => warmupIds.has(entry.record.id))
      .map((entry) => entry.normalizedScore);
    const settledNorm = normalizedSignals
      .filter((entry) => !warmupIds.has(entry.record.id))
      .map((entry) => entry.normalizedScore);
    if (warmupNorm.length >= 3 && settledNorm.length >= 5) {
      const warmupAvg = mean(warmupNorm);
      const settledAvg = mean(settledNorm);
      const dropPct = settledAvg > 0 ? ((settledAvg - warmupAvg) / settledAvg) * 100 : 0;
      if (dropPct >= 6) {
        pushUnique(cards, {
          title: "Warm-up Tax Across Scenarios",
          badge: "Readiness",
          badgeColor: "#ffb400",
          body: `Across recent scenarios, your opening runs land about ${dropPct.toFixed(0)}% below your own settled-in level once each run is normalized against that scenario's usual score band. The issue is not one bad scenario; it is global readiness.`,
          tip: "Protect score attempts with a short 2-3 run ramp: easy tracking or wide targets, then medium-speed confirms, then serious attempts once the cursor feels settled.",
        });
      } else if (dropPct <= 1.5) {
        pushUnique(cards, {
          title: "You Ramp Quickly",
          badge: "Readiness",
          badgeColor: "#00f5a0",
          body: "Your opening runs are already close to your own settled standard across different scenarios. That is a strong sign that your setup and pre-run routine are doing their job.",
          tip: "Keep the routine stable. If you want more progress, spend the saved warm-up time on one focused mechanic block instead of adding random extra attempts.",
        });
      }
    }
  }

  if (practiceProfile && practiceProfile.sessionCount >= 5) {
    const daysPerWeek = practiceProfile.daysPerWeek;
    const avgBlockMinutes = practiceProfile.avgBlockMinutes;
    const massedPattern = avgBlockMinutes >= 45 || (daysPerWeek < 2.5 && practiceProfile.avgBlockRuns >= 6);
    const distributedPattern = daysPerWeek >= 3.5 && avgBlockMinutes >= 12 && avgBlockMinutes <= 35;

    if (massedPattern) {
      pushUnique(cards, {
        title: "Practice Density Is Hiding Progress",
        badge: "Spacing",
        badgeColor: "#00b4ff",
        body: `Recent work is concentrated into ${avgBlockMinutes.toFixed(0)}-minute blocks across about ${daysPerWeek.toFixed(1)} active day${daysPerWeek >= 1.5 ? "s" : ""}/week. That mixes warm-up gains and fatigue into the same block, which makes true improvement harder to read.`,
        tip: "Keep the volume, split the block. Two shorter 20-35 minute sessions usually preserve effort better than one long grind and make your next-day quality easier to judge.",
      });
    } else if (distributedPattern) {
      pushUnique(cards, {
        title: "Your Practice Cadence Is Healthy",
        badge: "Spacing",
        badgeColor: "#00f5a0",
        body: `You are practicing across about ${daysPerWeek.toFixed(1)} active days/week with blocks averaging ${avgBlockMinutes.toFixed(0)} minutes. That is a strong range for retaining skill without paying too much fatigue tax.`,
        tip: "Use this structure as your base and adjust one variable at a time: either slightly more difficulty, slightly more contrast work, or slightly more deliberate warm-up, not all three at once.",
      });
    }
  }

  if (normalizedSignals.length >= 10) {
    const recentSignals = normalizedSignals
      .slice(-Math.min(normalizedSignals.length, 30))
      .filter((entry) => entry.baseline.scenarioType !== "Unknown");
    const familyCounts = new Map<string, number>();
    for (const entry of recentSignals) {
      familyCounts.set(
        entry.baseline.scenarioType,
        (familyCounts.get(entry.baseline.scenarioType) ?? 0) + 1,
      );
    }
    const familyEntries = [...familyCounts.entries()].sort(
      (a, b) => b[1] - a[1] || a[0].localeCompare(b[0]),
    );
    const totalFamilyRuns = familyEntries.reduce((sum, [, count]) => sum + count, 0);
    const dominantFamily = familyEntries[0];
    if (dominantFamily && totalFamilyRuns >= 10) {
      const dominantShare = dominantFamily[1] / totalFamilyRuns;
      if (dominantShare >= 0.58) {
        const contrast = contrastPlanForScenarioFamily(dominantFamily[0]);
        pushUnique(cards, {
          title: "Practice Mix Is Too Narrow",
          badge: "Transfer",
          badgeColor: "#a78bfa",
          body: `${Math.round(dominantShare * 100)}% of your recent reliable runs sit in ${SCENARIO_LABELS[dominantFamily[0]] ?? dominantFamily[0]}. That is enough to sharpen scenario familiarity, but it usually undertrains broader transfer.`,
          tip: `Keep your main family, but insert one ${contrast.label} set after every 2-3 serious runs. The goal is not comfort inside the block; it is better retention and carryover when you come back.`,
          drills: contrast.drills,
        });
      } else if (familyEntries.length >= 4 && dominantShare <= 0.4) {
        pushUnique(cards, {
          title: "Family Coverage Looks Balanced",
          badge: "Transfer",
          badgeColor: "#00f5a0",
          body: `Recent practice is spread across ${familyEntries.length} scenario families without one family swallowing the block. That gives you useful interference without turning the session into noise.`,
          tip: "Keep one primary focus for the day, but preserve this level of contrast work. Variety is helping your overall carryover, not just your score on one favorite scenario.",
        });
      }
    }
  }

  if (settledSignals.length >= 10) {
    type NormalizedBlockRecord = AnalyticsSessionRecord & { normalizedScore: number };
    const normalizedBlockRecords: NormalizedBlockRecord[] = settledSignals.map((entry) => ({
      ...entry.record,
      normalizedScore: entry.normalizedScore,
    }));
    const blocks = groupIntoPlayBlocks(normalizedBlockRecords);

    const blockFadePcts: number[] = [];
    const switchedScores: number[] = [];
    const repeatedScores: number[] = [];

    for (const block of blocks) {
      const sessions = block.sessions;
      const blockMinutes = sessions.reduce((sum, session) => sum + Math.max(0, session.duration_secs), 0) / 60;
      if (sessions.length >= 4 && blockMinutes >= 12) {
        const earlyAvg = mean(sessions.slice(0, Math.min(2, sessions.length)).map((session) => session.normalizedScore));
        const lateAvg = mean(sessions.slice(-Math.min(2, sessions.length)).map((session) => session.normalizedScore));
        if (earlyAvg > 0) {
          blockFadePcts.push(((earlyAvg - lateAvg) / earlyAvg) * 100);
        }
      }

      for (let index = 1; index < sessions.length; index += 1) {
        const current = sessions[index];
        const previous = sessions[index - 1];
        if (current.normalizedScenario !== previous.normalizedScenario) {
          switchedScores.push(current.normalizedScore);
        } else {
          repeatedScores.push(current.normalizedScore);
        }
      }
    }

    if (blockFadePcts.length >= 2) {
      const avgFadePct = mean(blockFadePcts);
      if (avgFadePct >= 5) {
        pushUnique(cards, {
          title: "Long Blocks Fade Late",
          badge: "Endurance",
          badgeColor: "#ff9f43",
          body: `Across your longer practice blocks, settled runs finish about ${avgFadePct.toFixed(0)}% below the level you hit near the start of the block. That points to endurance or attention decay, not a lack of raw skill.`,
          tip: "Treat the middle of the block as the scoring window. Once quality fades, swap into a lower-stakes drill or stop the block instead of grinding more serious attempts.",
        });
      } else if (avgFadePct <= 1.5 && practiceProfile?.avgBlockMinutes && practiceProfile.avgBlockMinutes >= 15) {
        pushUnique(cards, {
          title: "You Hold Quality Deep Into Blocks",
          badge: "Endurance",
          badgeColor: "#00f5a0",
          body: "Your later settled runs stay close to your early-block standard even in longer sessions. That is a real endurance strength and gives you more freedom to train with volume.",
          tip: "Use that endurance advantage on deliberate quality reps, not on mindless extra volume. Staying stable is most valuable when the later reps still have a purpose.",
        });
      }
    }

    if (switchedScores.length >= 6 && repeatedScores.length >= 6) {
      const switchedAvg = mean(switchedScores);
      const repeatedAvg = mean(repeatedScores);
      const switchPenaltyPct = repeatedAvg > 0 ? ((repeatedAvg - switchedAvg) / repeatedAvg) * 100 : 0;
      if (switchPenaltyPct >= 5) {
        pushUnique(cards, {
          title: "Scenario Switches Still Cost You",
          badge: "Context",
          badgeColor: "#ffd700",
          body: `Runs that follow a scenario change land about ${switchPenaltyPct.toFixed(0)}% below runs that stay on the same task, even after normalizing for each scenario's own score range. Right now the switch itself is expensive.`,
          tip: "Use mini-blocks instead of single-run hopping: 2-3 reps on one task, then switch. That keeps some interleaving benefit without paying a reset cost every run.",
        });
      } else if (switchPenaltyPct <= 1.5 && (practiceProfile?.avgUniqueScenariosPerBlock ?? 0) >= 2) {
        pushUnique(cards, {
          title: "You Re-center Quickly After Switches",
          badge: "Context",
          badgeColor: "#00b4ff",
          body: "Your run quality stays stable even when the block changes task. That is a strong sign that your fundamentals transfer cleanly instead of depending on scenario-specific rhythm.",
          tip: "You can afford more contrast work than most players. Keep using controlled interleaving and judge it by next-day quality, not only by instant same-block peaks.",
        });
      }
    }
  }

  if (settledSignals.length >= 12) {
    const settledNorm = settledSignals.map((entry) => entry.normalizedScore);
    const normMean = mean(settledNorm);
    const normCv = normMean > 0 ? (stddev(settledNorm) / normMean) * 100 : 0;
    const window = Math.min(8, Math.max(4, Math.floor(settledSignals.length / 3)));
    const recent = settledSignals.slice(-window).map((entry) => entry.normalizedScore);
    const older = settledSignals.slice(-window * 2, -window).map((entry) => entry.normalizedScore);
    if (older.length === window && recent.length === window) {
      const olderAvg = mean(older);
      const recentAvg = mean(recent);
      const recentDeltaPct = olderAvg > 0 ? ((recentAvg - olderAvg) / olderAvg) * 100 : 0;
      if (recentDeltaPct >= 4) {
        pushUnique(cards, {
          title: "Cross-Scenario Form Is Rising",
          badge: "Momentum",
          badgeColor: "#00f5a0",
          body: `Your last ${window} settled runs are about ${recentDeltaPct.toFixed(0)}% stronger than the ${window} before them after normalizing for each scenario. That means the improvement is carrying across tasks, not staying trapped inside one score line.`,
          tip: "This is the moment to raise difficulty slightly or tighten one technical focus. The carryover is real, so you can challenge it without losing the trend.",
        });
      } else if (recentDeltaPct <= -4) {
        pushUnique(cards, {
          title: "Global Form Has Cooled Off",
          badge: "Momentum",
          badgeColor: "#ff6b6b",
          body: `Your last ${window} settled runs are about ${Math.abs(recentDeltaPct).toFixed(0)}% weaker than the ${window} before them across mixed scenarios. That usually points to fatigue, inconsistency, or too much change at once.`,
          tip: "Run a reset week: keep volume steady, simplify the scenario rotation, and lock in one mechanic focus. The goal is to get your normal level back before adding more difficulty.",
        });
      } else if (normCv >= 12) {
        pushUnique(cards, {
          title: "Execution Is Swingy Across Scenarios",
          badge: "Consistency",
          badgeColor: "#ff9f43",
          body: `Even after normalizing per scenario, your settled runs still swing by about ${normCv.toFixed(0)}%. That means the inconsistency is coming from your overall execution, not just from which scenario you queued.`,
          tip: "Tighten the repeatables first: same warm-up, same seating and grip, same first-block structure, and one focus cue for the whole session.",
        });
      }
    }
  }

  if (cards.length === 0) {
    cards.push({
      title: "Global Practice Looks Stable",
      badge: "Baseline",
      badgeColor: "#00f5a0",
      body: "Recent practice does not show a single major cross-scenario leak. Your training structure, readiness, and carryover look reasonably healthy from the data we have.",
      tip: "Keep the routine steady and push one lever at a time: slightly harder scenarios, slightly cleaner execution, or slightly better spacing between blocks.",
    });
  }

  return cards.slice(0, 5);
}

function PracticeProfilePanel({ practiceProfile }: { practiceProfile: PracticeProfile }) {
  return (
    <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 12 }}>
      <SectionTitle>Practice Profile</SectionTitle>
      <div
        style={{
          display: "grid",
          gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
          gap: 12,
          alignItems: "start",
        }}
      >
        <div
          style={{
            ...CARD_STYLE,
            minWidth: 0,
            minHeight: 112,
            padding: "12px 16px",
            display: "flex",
            flexDirection: "column",
            justifyContent: "flex-start",
            gap: 4,
          }}
        >
          <div style={{ fontSize: 11, color: "rgba(255,255,255,0.38)", textTransform: "uppercase", letterSpacing: 1, marginBottom: 6 }}>
            Active cadence
          </div>
          <div style={{ fontSize: 20, fontWeight: 700, color: "#00b4ff" }}>
            {practiceProfile.daysPerWeek.toFixed(1)} days/wk
          </div>
          <div style={{ fontSize: 10, color: "rgba(255,255,255,0.3)", marginTop: 3 }}>
            {practiceProfile.activeDays} active day{practiceProfile.activeDays !== 1 ? "s" : ""} in the last {practiceProfile.spanDays} day{practiceProfile.spanDays !== 1 ? "s" : ""}
          </div>
        </div>
        <div
          style={{
            ...CARD_STYLE,
            minWidth: 0,
            minHeight: 112,
            padding: "12px 16px",
            display: "flex",
            flexDirection: "column",
            justifyContent: "flex-start",
            gap: 4,
          }}
        >
          <div style={{ fontSize: 11, color: "rgba(255,255,255,0.38)", textTransform: "uppercase", letterSpacing: 1, marginBottom: 6 }}>
            Block size
          </div>
          <div style={{ fontSize: 20, fontWeight: 700, color: "#ffd700" }}>
            {practiceProfile.avgBlockMinutes.toFixed(0)} min
          </div>
          <div style={{ fontSize: 10, color: "rgba(255,255,255,0.3)", marginTop: 3 }}>
            avg {practiceProfile.avgBlockRuns.toFixed(1)} runs/block, peak {practiceProfile.maxBlockMinutes.toFixed(0)} min
          </div>
        </div>
        <div
          style={{
            ...CARD_STYLE,
            minWidth: 0,
            minHeight: 112,
            padding: "12px 16px",
            display: "flex",
            flexDirection: "column",
            justifyContent: "flex-start",
            gap: 4,
          }}
        >
          <div style={{ fontSize: 11, color: "rgba(255,255,255,0.38)", textTransform: "uppercase", letterSpacing: 1, marginBottom: 6 }}>
            Scenario mix
          </div>
          <div style={{ fontSize: 20, fontWeight: 700, color: "#a78bfa" }}>
            {Math.round(practiceProfile.dominantScenarioShare * 100)}% main
          </div>
          <div style={{ fontSize: 10, color: "rgba(255,255,255,0.3)", marginTop: 3 }}>
            {practiceProfile.scenarioDiversity} scenarios total, {practiceProfile.avgUniqueScenariosPerBlock.toFixed(1)} per block
          </div>
        </div>
      </div>
      {practiceProfile.topScenarios.length > 0 && (
        <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap", marginTop: -2 }}>
          <span style={{ fontSize: 10, color: "rgba(255,255,255,0.34)", textTransform: "uppercase", letterSpacing: 0.8 }}>
            Mix
          </span>
          {practiceProfile.topScenarios.map((entry) => (
            <span
              key={entry.scenario}
              title={entry.scenario}
              style={{
                fontSize: 10,
                padding: "3px 8px",
                borderRadius: 999,
                background: "rgba(167,139,250,0.10)",
                border: "1px solid rgba(167,139,250,0.20)",
                color: "rgba(255,255,255,0.72)",
                maxWidth: 220,
                overflow: "hidden",
                textOverflow: "ellipsis",
                whiteSpace: "nowrap",
              }}
            >
              {Math.round(entry.share * 100)}% {entry.scenario}
            </span>
          ))}
        </div>
      )}
      <p style={{ margin: "2px 0 0", fontSize: 12, color: "rgba(255,255,255,0.58)", lineHeight: 1.7 }}>
        Recent overall practice is centered on {practiceProfile.dominantScenario} and averages {practiceProfile.sessionsPerActiveDay.toFixed(1)} run{practiceProfile.sessionsPerActiveDay >= 1.5 ? "s" : ""} per active day, with scenario changes on about {Math.round(practiceProfile.switchRate * 100)}% of runs inside each practice block.
      </p>
    </div>
  );
}

function CoachingCard({
  card,
  onExploreDrill,
  feedback,
  onFeedback,
  snapshotKind,
}: {
  card: CoachingCardData;
  onExploreDrill: (query: string) => void;
  feedback?: CoachingCardFeedback | null;
  onFeedback?: ((snapshotKind: string, card: CoachingCardData, feedback: CoachingCardFeedback) => void) | null;
  snapshotKind?: string;
}) {
  const [expanded, setExpanded] = useState(false);
  const feedbackOptions: Array<{ value: CoachingCardFeedback; label: string; color: string }> = [
    { value: "helpful", label: "Helpful", color: "#00f5a0" },
    { value: "trying", label: "Trying It", color: "#00b4ff" },
    { value: "not_now", label: "Not Now", color: "#ffd700" },
    { value: "not_for_me", label: "Not For Me", color: "#ff9f43" },
  ];
  return (
    <div
      style={{
        background: "rgba(255,255,255,0.04)",
        border: "1px solid rgba(255,255,255,0.08)",
        borderRadius: 10,
        padding: "14px 16px",
        cursor: "pointer",
        userSelect: "none",
      }}
      onClick={() => setExpanded((x) => !x)}
    >
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          gap: 12,
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 10, minWidth: 0 }}>
          <span
            style={{
              background: `${card.badgeColor}20`,
              border: `1px solid ${card.badgeColor}40`,
              color: card.badgeColor,
              borderRadius: 4,
              fontSize: 10,
              padding: "2px 7px",
              textTransform: "uppercase",
              letterSpacing: 0.8,
              flexShrink: 0,
              whiteSpace: "nowrap",
            }}
          >
            {card.badge}
          </span>
          <span
            style={{
              fontWeight: 700,
              fontSize: 13,
              overflow: "hidden",
              textOverflow: "ellipsis",
              whiteSpace: "nowrap",
            }}
          >
            {card.title}
          </span>
        </div>
        <span style={{ color: "rgba(255,255,255,0.3)", fontSize: 11, flexShrink: 0 }}>
          {expanded ? "▲" : "▼"}
        </span>
      </div>
      {expanded && (
        <div style={{ marginTop: 12, display: "flex", flexDirection: "column", gap: 10 }}>
          <p
            style={{
              margin: 0,
              fontSize: 12,
              color: "rgba(255,255,255,0.55)",
              lineHeight: 1.7,
            }}
          >
            {card.body}
          </p>
          <div
            style={{
              background: `${card.badgeColor}12`,
              borderLeft: `3px solid ${card.badgeColor}`,
              padding: "8px 12px",
              borderRadius: "0 6px 6px 0",
            }}
          >
            <div
              style={{
                fontSize: 10,
                color: card.badgeColor,
                textTransform: "uppercase",
                letterSpacing: 0.8,
                marginBottom: 5,
              }}
            >
              Action
            </div>
            <p
              style={{
                margin: 0,
                fontSize: 12,
                color: "rgba(255,255,255,0.7)",
                lineHeight: 1.65,
              }}
            >
              {card.tip}
            </p>
          </div>
          {(card.drills?.length ?? 0) > 0 && (
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 700 }}>
                Recommended drill searches
              </div>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                {(card.drills ?? []).map((drill) => (
                  <button
                    key={`${card.title}:${drill.query}`}
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      onExploreDrill(drill.query);
                    }}
                    style={{
                      background: "rgba(255,255,255,0.05)",
                      border: `1px solid ${C.border}`,
                      borderRadius: 999,
                      color: C.textSub,
                      cursor: "pointer",
                      fontSize: 11,
                      padding: "6px 10px",
                      fontFamily: "inherit",
                    }}
                  >
                    {drill.label} ↗
                  </button>
                ))}
              </div>
            </div>
          )}
          {onFeedback && snapshotKind && (
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 700 }}>
                Tailor this coaching
              </div>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                {feedbackOptions.map((option) => {
                  const active = feedback === option.value;
                  return (
                    <button
                      key={`${coachingCardFeedbackId(card)}:${option.value}`}
                      type="button"
                      onClick={(event) => {
                        event.stopPropagation();
                        onFeedback(snapshotKind, card, option.value);
                      }}
                      style={{
                        background: active ? `${option.color}18` : "rgba(255,255,255,0.05)",
                        border: `1px solid ${active ? `${option.color}66` : C.border}`,
                        borderRadius: 999,
                        color: active ? option.color : C.textSub,
                        cursor: "pointer",
                        fontSize: 11,
                        padding: "6px 10px",
                        fontFamily: "inherit",
                      }}
                    >
                      {option.label}
                    </button>
                  );
                })}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function ScoreDistributionChart({
  scores,
  p10,
  p50,
  p90,
}: {
  scores: number[];
  p10: number;
  p50: number;
  p90: number;
}) {
  if (scores.length < 4) return null;
  const min = scores[0];
  const max = scores[scores.length - 1];
  const range = max - min;
  if (range === 0) return null;
  const BINS = 10;
  const binSize = range / BINS;
  const bins = Array.from({ length: BINS }, (_, i) => {
    const lo = min + i * binSize;
    const hi = lo + binSize;
    const count = scores.filter((s) =>
      i === BINS - 1 ? s >= lo && s <= hi : s >= lo && s < hi,
    ).length;
    return { label: fmtScore(Math.round(lo)), count, lo, hi };
  });
  return (
    <div style={CHART_STYLE}>
      <SectionTitle>Score distribution</SectionTitle>
      <div style={{ display: "flex", gap: 16, marginBottom: 10, flexWrap: "wrap" }}>
        {[
          { color: "#a78bfa", label: `Floor (bottom 10%): ${fmtScore(p10)}` },
          { color: "#ffd700", label: `Median: ${fmtScore(p50)}` },
          { color: "#00f5a0", label: `Peak (top 10%): ${fmtScore(p90)}` },
        ].map((l) => (
          <div key={l.label} style={{ display: "flex", alignItems: "center", gap: 6 }}>
            <div
              style={{ width: 8, height: 8, borderRadius: "50%", background: l.color }}
            />
            <span style={{ fontSize: 11, color: "rgba(255,255,255,0.5)" }}>{l.label}</span>
          </div>
        ))}
      </div>
      <ResponsiveContainer width="100%" height={140}>
        <BarChart data={bins} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
          <CartesianGrid
            strokeDasharray="3 3"
            stroke="rgba(255,255,255,0.06)"
            vertical={false}
          />
          <XAxis
            dataKey="label"
            tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 10 }}
            tickLine={false}
            axisLine={false}
          />
          <YAxis
            tick={{ fill: "rgba(255,255,255,0.3)", fontSize: 11 }}
            tickLine={false}
            axisLine={false}
            width={28}
            allowDecimals={false}
          />
          <Tooltip
            content={({ active, payload }) => {
              if (!active || !payload?.length) return null;
              const d = payload[0]?.payload as (typeof bins)[0];
              return (
                <div style={{ ...TOOLTIP_STYLE, padding: "8px 12px" }}>
                  <div
                    style={{
                      color: "rgba(255,255,255,0.5)",
                      fontSize: 11,
                      marginBottom: 4,
                    }}
                  >
                    {fmtScore(Math.round(d.lo))} – {fmtScore(Math.round(d.hi))}
                  </div>
                  <div style={{ fontWeight: 700 }}>{d.count} sessions</div>
                </div>
              );
            }}
          />
          <Bar dataKey="count" radius={[3, 3, 0, 0]}>
            {bins.map((bin, i) => (
              <Cell
                key={i}
                fill={bin.lo >= p90 ? "#00f5a0" : bin.hi <= p10 ? "#a78bfa80" : "#00b4ff50"}
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

// ─── Coaching tab ─────────────────────────────────────────────────────────────

function CoachingTab({
  records,
  sorted,
  warmupIds,
  scenarioName,
  dateRange,
  sessionFilter,
  onExploreDrill,
  feedbackRows,
  onFeedback,
  appSettings,
  globalSummary,
}: {
  records: AnalyticsSessionRecord[];
  sorted: AnalyticsSessionRecord[];
  warmupIds: Set<string>;
  scenarioName: string;
  dateRange: DateRangePreset;
  sessionFilter: SessionFilter;
  onExploreDrill: (query: string) => void;
  feedbackRows: CoachingUserFeedbackRecord[];
  onFeedback: (snapshotKind: string, card: CoachingCardData, feedback: CoachingCardFeedback) => void;
  appSettings: AppSettings | null;
  globalSummary: string;
}) {
  const [scenarioCoachingOverview, setScenarioCoachingOverview] = useState<ScenarioCoachingOverview | null>(null);
  const [localCoachQuestion, setLocalCoachQuestion] = useState("");
  const [localCoachGeneral, setLocalCoachGeneral] = useState(false);
  const [localCoachHistory, setLocalCoachHistory] = useState<LocalCoachTurn[]>([]);
  const [localCoachRuntime, setLocalCoachRuntime] = useState<LocalLlmRuntimeStatus | null>(null);
  const [localCoachReply, setLocalCoachReply] = useState<LocalCoachChatResponse | null>(null);
  const [localCoachDraftMessage, setLocalCoachDraftMessage] = useState("");
  const [localCoachProgress, setLocalCoachProgress] = useState<string[]>([]);
  const [localCoachBusy, setLocalCoachBusy] = useState(false);
  const [localCoachError, setLocalCoachError] = useState<string | null>(null);
  const localCoachActiveStreamIdRef = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    void invoke<ScenarioCoachingOverview>("get_scenario_coaching_overview", {
      scenarioName,
      dateRange: dateRange === "all" ? null : dateRange,
    })
      .then((overview) => {
        if (!cancelled) {
          setScenarioCoachingOverview(overview);
        }
      })
      .catch((error) => {
        console.warn("Could not load Rust scenario coaching overview", error);
        if (!cancelled) {
          setScenarioCoachingOverview(null);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [dateRange, scenarioName]);

  useEffect(() => {
    setLocalCoachHistory([]);
    setLocalCoachReply(null);
    setLocalCoachDraftMessage("");
  }, [scenarioName]);

  useEffect(() => {
    let cancelled = false;
    invoke<LocalLlmRuntimeStatus>("get_local_llm_runtime_status")
      .then((status) => {
        if (!cancelled) {
          setLocalCoachRuntime(status);
        }
      })
      .catch((error) => {
        if (!cancelled) {
          setLocalCoachRuntime(null);
          setLocalCoachError(String(error));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [scenarioName]);

  useEffect(() => {
    let disposed = false;
    const unlistenPromise = listen<LocalCoachStreamEvent>(LOCAL_COACH_STREAM_EVENT, (event) => {
      if (disposed) return;
      if (event.payload.streamId !== localCoachActiveStreamIdRef.current) return;
      if (event.payload.kind === "error") {
        setLocalCoachError(event.payload.error?.trim() || "Local coach failed.");
        setLocalCoachBusy(false);
        return;
      }
      if (event.payload.kind === "status") {
        const next = event.payload.delta.trim();
        if (!next) return;
        setLocalCoachProgress((prev) => {
          if (prev[prev.length - 1] === next) return prev;
          return [...prev, next];
        });
        return;
      }
      setLocalCoachDraftMessage(event.payload.content);
    });
    return () => {
      disposed = true;
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, []);

  // CoachingTab always works on the full dataset and handles splits internally.
  const warmupSorted  = sorted.filter((r) => warmupIds.has(r.id));
  const peakSorted    = sorted.filter((r) => !warmupIds.has(r.id));
  const peakRecords   = records.filter((r) => !warmupIds.has(r.id));
  const hasWarmupData = warmupSorted.length > 0;

  // Use peak-only data for the main coaching analysis so warmup sessions don't skew it.
  // Fall back to all records if there are no peak records yet.
  const coachRecords = peakRecords.length >= 3 ? peakRecords : records;
  const coachSorted  = peakRecords.length >= 3 ? peakSorted  : sorted;

  const smoothRecords = coachRecords.filter((r) => r.smoothness !== null);
  const scores = coachSorted.map((r) => r.score);

  const showWarmupSection = sessionFilter !== "warmedup" && hasWarmupData;
  const showPeakSection   = sessionFilter !== "warmup";

  if (scores.length < 3 && !showWarmupSection) {
    return (
      <div style={{ color: "rgba(255,255,255,0.3)", padding: 20, lineHeight: 1.7 }}>
        Play at least 3 sessions to unlock coaching analysis.
      </div>
    );
  }

  // ── Peak performance stats ────────────────────────────────────────────────
  const avgScoreVal = scenarioCoachingOverview?.avgScore ?? (scores.length > 0 ? mean(scores) : 0);
  const localScoreStdDev = stddev(scores);
  const localScoreCV = avgScoreVal > 0 ? (localScoreStdDev / avgScoreVal) * 100 : 0;
  const xs = scores.map((_, i) => i + 1);
  const { slope: localSlope } = linearRegression(xs, scores);
  const scenarioType = scenarioCoachingOverview?.scenarioType ?? dominantScenarioType(coachRecords);
  const isTracking   = scenarioType === "PureTracking" || scenarioType.includes("Tracking");

  const fingerprint  = smoothRecords.length > 0 ? buildAimFingerprint(smoothRecords, scenarioType) : null;
  const aimStyle     = fingerprint ? classifyAimStyle(fingerprint, scenarioType) : null;

  const sixthAxisLabel = isTracking ? "Flow" : "Rhythm";
  const stableAxes = fingerprint
    ? [...fingerprint.axes].sort((a, b) => a.volatility - b.volatility).slice(0, 2)
    : [];
  const volatileAxes = fingerprint
    ? [...fingerprint.axes].sort((a, b) => b.volatility - a.volatility).slice(0, 2)
    : [];

  const radarData = fingerprint
    ? [
        { metric: "Precision",    value: fingerprint.precision },
        { metric: "Speed",        value: fingerprint.speed },
        { metric: "Control",      value: fingerprint.control },
        { metric: "Consistency",  value: fingerprint.consistency },
        { metric: "Decisiveness", value: fingerprint.decisiveness },
        { metric: sixthAxisLabel, value: fingerprint.rhythm },
      ]
    : [];

  const scoreCV = scenarioCoachingOverview?.scoreCvPct ?? localScoreCV;
  const slope = scenarioCoachingOverview?.slopePtsPerRun ?? localSlope;
  const isPlateau = scenarioCoachingOverview?.isPlateau ?? false;
  const coachingCards = scenarioCoachingOverview?.coachingCards ?? [];

  const sortedScores = [...scores].sort((a, b) => a - b);
  const p10 = scenarioCoachingOverview?.p10Score ?? (sortedScores.length > 0 ? percentileOf(sortedScores, 10) : 0);
  const p50 = scenarioCoachingOverview?.p50Score ?? (sortedScores.length > 0 ? percentileOf(sortedScores, 50) : 0);
  const p90 = scenarioCoachingOverview?.p90Score ?? (sortedScores.length > 0 ? percentileOf(sortedScores, 90) : 0);
  const warmupStats = scenarioCoachingOverview?.warmupStats ?? null;
  const localCoachSignals = Array.from(
    new Set(
      coachingCards
        .flatMap((card) => coachingCardSignalKeys(card))
        .filter((signal) => signal.trim().length > 0),
    ),
  ).slice(0, 8);
  const localCoachContextTags = [
    isPlateau ? "plateau" : null,
    warmupStats ? "warmup" : null,
    scoreCV >= 12 ? "variance" : null,
    slope > 0 ? "improving" : slope < 0 ? "downtrend" : null,
  ].filter((tag): tag is string => tag != null);
  // ── Rich context for the local coach prompt ──────────────────────────────
  const allScoresSorted = [...sorted].map((r) => r.score); // chronological, all records
  const allTimeTotal = records.length;
  const allTimeBest = allScoresSorted.length > 0 ? Math.max(...allScoresSorted) : 0;
  const allTimeAvgScore = allScoresSorted.length > 0 ? mean(allScoresSorted) : 0;
  const recentN = Math.min(7, allScoresSorted.length);
  const recentScoreValues = allScoresSorted.slice(-recentN);
  const recentAvgScore = recentScoreValues.length > 0 ? mean(recentScoreValues) : 0;
  const earlyN = Math.min(10, Math.floor(allScoresSorted.length * 0.25));
  const earlyAvgScore = earlyN > 0 ? mean(allScoresSorted.slice(0, earlyN)) : 0;
  const firstTimestampMs = sorted.length > 0 ? sorted[0].timestampMs : 0;
  const daysPracticing = firstTimestampMs > 0 ? Math.round((Date.now() - firstTimestampMs) / 86_400_000) : 0;
  const totalPracticeHours = records.reduce((s, r) => s + (r.duration_secs ?? 0), 0) / 3600;
  const localCoachFacts: LocalCoachFact[] = [
    {
      key: "score_cv_pct",
      label: "Score consistency spread",
      valueText: `${scoreCV.toFixed(1)}%`,
      numericValue: scoreCV,
      direction: "lower_is_better",
      confidence: "high",
    },
    {
      key: "score_slope_pts_per_run",
      label: "Learning slope",
      valueText: `${slope > 0 ? "+" : ""}${Math.round(slope)} pts/run`,
      numericValue: slope,
      direction: "higher_is_better",
      confidence: "high",
    },
    {
      key: "plateau_detected",
      label: "Plateau detected",
      valueText: isPlateau ? "yes" : "no",
      boolValue: isPlateau,
      direction: "context",
      confidence: "medium",
    },
    ...(warmupStats
      ? [{
          key: "warmup_drop_pct",
          label: "Opening vs settled gap",
          valueText: `${warmupStats.dropPct.toFixed(0)}% below settled runs`,
          numericValue: warmupStats.dropPct,
          direction: "lower_is_better",
          confidence: "medium",
        } satisfies LocalCoachFact]
      : []),
    ...(allTimeBest > 0
      ? [{
          key: "all_time_best_score",
          label: "All-time best",
          valueText: `${Math.round(allTimeBest)} pts`,
          numericValue: allTimeBest,
          direction: "higher_is_better",
          confidence: "high",
        } satisfies LocalCoachFact]
      : []),
    ...(recentAvgScore > 0
      ? [{
          key: "recent_avg_score",
          label: "Recent average",
          valueText: `${Math.round(recentAvgScore)} pts`,
          numericValue: recentAvgScore,
          direction: "higher_is_better",
          confidence: "high",
        } satisfies LocalCoachFact]
      : []),
    ...(allTimeAvgScore > 0
      ? [{
          key: "all_time_avg_score",
          label: "Lifetime average",
          valueText: `${Math.round(allTimeAvgScore)} pts`,
          numericValue: allTimeAvgScore,
          direction: "higher_is_better",
          confidence: "medium",
        } satisfies LocalCoachFact]
      : []),
    ...(daysPracticing > 0
      ? [{
          key: "days_practicing",
          label: "Days practicing",
          valueText: `${daysPracticing} days`,
          numericValue: daysPracticing,
          direction: "higher_is_better",
          confidence: "medium",
        } satisfies LocalCoachFact]
      : []),
    ...(totalPracticeHours > 0
      ? [{
          key: "total_practice_hours",
          label: "Practice time",
          valueText: totalPracticeHours < 1 ? `${Math.round(totalPracticeHours * 60)} min` : `${totalPracticeHours.toFixed(1)} hrs`,
          numericValue: totalPracticeHours,
          direction: "higher_is_better",
          confidence: "medium",
        } satisfies LocalCoachFact]
      : []),
    ...(recentScoreValues.length >= 3
      ? [{
          key: "recent_score_window",
          label: "Recent score window",
          valueText: recentScoreValues.map((s) => Math.round(s)).join(", "),
          direction: "context",
          confidence: "high",
        } satisfies LocalCoachFact]
      : []),
  ];

  const localCoachScenarioSummaryParts = [
    // Long-term context (the most-missing piece)
    allTimeTotal > 0
      ? `Total sessions: ${allTimeTotal}${daysPracticing > 0 ? ` over ${daysPracticing} days` : ""}.`
      : null,
    allTimeBest > 0
      ? `All-time best: ${Math.round(allTimeBest)} pts.`
      : null,
    earlyAvgScore > 0 && recentAvgScore > 0 && earlyN >= 3
      ? `Improved from ~${Math.round(earlyAvgScore)} pts (first ${earlyN} runs) to ~${Math.round(recentAvgScore)} pts recently.`
      : null,
    allTimeAvgScore > 0 && recentAvgScore > 0 && allTimeTotal >= 15
      ? `Lifetime avg: ${Math.round(allTimeAvgScore)} pts; recent avg (last ${recentN}): ${Math.round(recentAvgScore)} pts.`
      : null,
    totalPracticeHours > 0
      ? `Total practice time: ${totalPracticeHours < 1 ? `${Math.round(totalPracticeHours * 60)} min` : `${totalPracticeHours.toFixed(1)} hrs`}.`
      : null,
    // Recent actual score values so the LLM can see the real numbers
    recentScoreValues.length >= 3
      ? `Recent ${recentN} scores (oldest→newest): ${recentScoreValues.map((s) => Math.round(s)).join(", ")}.`
      : null,
    // Current-window derived stats
    scoreCV > 0 ? `Score consistency spread: ${scoreCV.toFixed(1)}%.` : null,
    `Learning slope: ${(slope > 0 ? "+" : "") + Math.round(slope)} pts/run.`,
    isPlateau ? "A plateau is currently detected." : null,
    warmupStats
      ? `Warm-up effect: opening runs land about ${warmupStats.dropPct.toFixed(0)}% below settled-in runs.`
      : null,
  ].filter((part): part is string => part != null);

  const askLocalCoach = async () => {
    const question = localCoachQuestion.trim();
    if (!question) return;
    const streamId = `local-coach-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
    localCoachActiveStreamIdRef.current = streamId;
    setLocalCoachBusy(true);
    setLocalCoachError(null);
    setLocalCoachReply(null);
    setLocalCoachDraftMessage("");
    setLocalCoachProgress([]);
    try {
      const response = await invoke<LocalCoachChatResponse>("local_llm_stream_coaching_reply", {
        request: {
          scenarioName: localCoachGeneral ? "" : scenarioName,
          scenarioType: localCoachGeneral ? "" : scenarioType,
          question,
          signalKeys: localCoachSignals,
          contextTags: localCoachContextTags,
          focusArea: appSettings?.coaching_focus_area ?? "balanced",
          challengePreference: appSettings?.coaching_challenge_preference ?? "balanced",
          timePreference: appSettings?.coaching_time_preference ?? "this_week",
          scenarioSummary: localCoachGeneral ? "" : localCoachScenarioSummaryParts.join(" "),
          globalSummary,
          general: localCoachGeneral,
          conversationHistory: localCoachHistory.slice(-4),
          coachFacts: localCoachFacts,
          coachingCards: coachingCards.slice(0, 5).map((card): LocalCoachInputCard => ({
            title: card.title,
            badge: card.badge,
            body: card.body,
            tip: card.tip,
            signals: coachingCardSignalKeys(card),
          })),
        },
        streamId,
      });
      if (localCoachActiveStreamIdRef.current !== streamId) return;
      setLocalCoachReply(response);
      setLocalCoachDraftMessage(response.message);
      setLocalCoachRuntime(response.runtimeStatus);
      if (response.message) {
        setLocalCoachHistory((prev) => [...prev, { question, answer: response.message }]);
      }
    } catch (error) {
      if (localCoachActiveStreamIdRef.current !== streamId) return;
      setLocalCoachError(String(error));
    } finally {
      if (localCoachActiveStreamIdRef.current === streamId) {
        localCoachActiveStreamIdRef.current = null;
        setLocalCoachBusy(false);
      }
    }
  };

  const refreshLocalCoachStatus = () => {
    setLocalCoachError(null);
    void invoke<LocalLlmRuntimeStatus>("get_local_llm_runtime_status")
      .then((status) => setLocalCoachRuntime(status))
      .catch((error) => setLocalCoachError(String(error)));
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 24 }}>

      {/* ── Peak performance section ──────────────────────────────────────── */}
      {showPeakSection && scores.length < 3 && (
        <div style={{ color: "rgba(255,255,255,0.3)", padding: "10px 0", lineHeight: 1.7 }}>
          Play at least 3 settled-in runs to unlock peak performance coaching.
        </div>
      )}
      {showPeakSection && scores.length >= 3 && (
      <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      <div
        style={{
          ...CHART_STYLE,
          display: "flex",
          flexDirection: "column",
          gap: 12,
          borderColor:
            localCoachRuntime?.state === "ready"
              ? accentAlpha("33")
              : localCoachRuntime?.state === "missing_assets"
                ? "rgba(255,215,0,0.2)"
                : C.border,
        }}
      >
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
          <div style={{ display: "flex", alignItems: "flex-start", gap: 8 }}>
            <div>
              <SectionTitle>Local Coach</SectionTitle>
              <div style={{ marginTop: 4, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
                Ask anything — your trends, what to focus on, how to break a plateau. Runs on your PC.
              </div>
            </div>
            <button
              type="button"
              title="AI Coach settings"
              onClick={() => {
                void invoke("toggle_settings");
                void emit("navigate-settings", { section: "ai" });
              }}
              style={{
                background: "transparent",
                border: "none",
                cursor: "pointer",
                color: C.textFaint,
                padding: "2px 4px",
                fontSize: 14,
                lineHeight: 1,
                flexShrink: 0,
                marginTop: 2,
                borderRadius: 4,
              }}
            >
              ⚙
            </button>
          </div>
          <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
            <span
              style={{
                fontSize: 11,
                padding: "5px 9px",
                borderRadius: 999,
                background:
                  localCoachRuntime?.state === "ready"
                    ? "rgba(0,245,160,0.12)"
                    : localCoachRuntime?.state === "missing_assets"
                      ? "rgba(255,215,0,0.12)"
                      : "rgba(255,255,255,0.06)",
                border:
                  localCoachRuntime?.state === "ready"
                    ? "1px solid rgba(0,245,160,0.24)"
                    : localCoachRuntime?.state === "missing_assets"
                      ? "1px solid rgba(255,215,0,0.24)"
                      : `1px solid ${C.border}`,
                color:
                  localCoachRuntime?.state === "ready"
                    ? "#00f5a0"
                    : localCoachRuntime?.state === "missing_assets"
                      ? C.warn
                      : C.textSub,
              }}
            >
              {localCoachRuntime?.state === "ready"
                ? "Active"
                : localCoachRuntime?.state === "missing_assets"
                  ? "Setup needed"
                  : localCoachRuntime?.state === "error"
                    ? "Error"
                    : localCoachRuntime?.state === "stopped"
                      ? "Available"
                      : "Checking…"}
            </span>
            <button
              type="button"
              onClick={refreshLocalCoachStatus}
              className="am-btn"
              style={{ padding: "6px 10px", minHeight: 0, fontSize: 11 }}
            >
              Refresh
            </button>
          </div>
        </div>

        {/* ── Conversation thread (prior turns) ── */}
        {localCoachHistory.length > 0 && (
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            {localCoachHistory.map((turn, i) => (
              <div
                key={i}
                style={{
                  borderRadius: 10,
                  background: "rgba(255,255,255,0.025)",
                  border: `1px solid ${C.borderSub}`,
                  overflow: "hidden",
                }}
              >
                <div style={{ padding: "7px 12px 4px", fontSize: 11, fontWeight: 600, color: C.textFaint }}>
                  {turn.question}
                </div>
                <div style={{ padding: "0 12px 8px", fontSize: 11, color: C.textSub, lineHeight: 1.5, maxHeight: 72, overflow: "hidden", maskImage: "linear-gradient(to bottom, black 60%, transparent 100%)" }}>
                  {turn.answer.slice(0, 200)}{turn.answer.length > 200 ? "…" : ""}
                </div>
              </div>
            ))}
            <button
              type="button"
              onClick={() => {
                setLocalCoachHistory([]);
                setLocalCoachReply(null);
                setLocalCoachDraftMessage("");
              }}
              style={{ alignSelf: "flex-start", fontSize: 10, color: C.textFaint, background: "transparent", border: "none", cursor: "pointer", padding: "2px 0", textDecoration: "underline" }}
            >
              Clear conversation
            </button>
          </div>
        )}

        {/* ── Input ── */}
        <textarea
          value={localCoachQuestion}
          onChange={(event) => setLocalCoachQuestion(event.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && !localCoachBusy && localCoachQuestion.trim().length > 0) {
              e.preventDefault();
              void askLocalCoach();
            }
          }}
          placeholder={
            localCoachHistory.length > 0
              ? "Ask a follow-up…"
              : "Why do I keep losing control late in this scenario, and what should I change next session?"
          }
          className="am-input"
          style={{
            minHeight: localCoachHistory.length > 0 ? 56 : 88,
            resize: "vertical",
            width: "100%",
            boxSizing: "border-box",
            lineHeight: 1.6,
            padding: "10px 12px",
          }}
        />

        {/* ── Scope toggle + actions row ── */}
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
          {/* Scope toggle */}
          <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
            <div style={{ display: "flex", alignItems: "center", gap: 0, background: "rgba(255,255,255,0.04)", border: `1px solid ${C.border}`, borderRadius: 8, overflow: "hidden" }}>
              <button
                type="button"
                onClick={() => setLocalCoachGeneral(false)}
                style={{ padding: "4px 10px", fontSize: 11, background: !localCoachGeneral ? accentAlpha("14") : "transparent", color: !localCoachGeneral ? C.accent : C.textFaint, border: "none", cursor: "pointer", borderRight: `1px solid ${C.border}` }}
              >
                This scenario
              </button>
              <button
                type="button"
                onClick={() => setLocalCoachGeneral(true)}
                style={{ padding: "4px 10px", fontSize: 11, background: localCoachGeneral ? accentAlpha("14") : "transparent", color: localCoachGeneral ? C.accent : C.textFaint, border: "none", cursor: "pointer" }}
              >
                General
              </button>
            </div>
            <span style={{ fontSize: 11, color: C.textFaint }}>
              {localCoachGeneral ? "All aim training knowledge" : scenarioName || "This scenario"}
            </span>
          </div>

          {/* Action buttons */}
          <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
            {(localCoachRuntime?.state === "missing_assets" || localCoachRuntime?.state === "error") && (
              <button
                type="button"
                className="am-btn"
                style={{ padding: "6px 10px", minHeight: 0, fontSize: 11 }}
                onClick={() => {
                  setLocalCoachBusy(true);
                  setLocalCoachError(null);
                  void invoke<LocalLlmRuntimeStatus>("install_local_llm_assets")
                    .then((status) => setLocalCoachRuntime(status))
                    .catch((error) => setLocalCoachError(String(error)))
                    .finally(() => setLocalCoachBusy(false));
                }}
                disabled={localCoachBusy}
              >
                {localCoachBusy ? "Setting up…" : "Set up coach"}
              </button>
            )}
            {localCoachRuntime?.state === "ready" && (
              <button
                type="button"
                className="am-btn"
                style={{ padding: "6px 10px", minHeight: 0, fontSize: 11 }}
                onClick={() => {
                  setLocalCoachBusy(true);
                  setLocalCoachError(null);
                  void invoke<LocalLlmRuntimeStatus>("stop_local_llm_runtime")
                    .then((status) => setLocalCoachRuntime(status))
                    .catch((error) => setLocalCoachError(String(error)))
                    .finally(() => setLocalCoachBusy(false));
                }}
              >
                Stop
              </button>
            )}
            <button
              type="button"
              onClick={() => void askLocalCoach()}
              className="am-btn"
              style={{
                padding: "6px 14px",
                minHeight: 0,
                fontSize: 11,
                background: accentAlpha("16"),
                border: `1px solid ${C.accentBorder}`,
                color: C.accent,
              }}
              disabled={localCoachBusy || localCoachQuestion.trim().length === 0}
            >
              {localCoachBusy ? "Thinking…" : localCoachHistory.length > 0 ? "Ask follow-up" : "Ask coach"}
            </button>
          </div>
        </div>

        {(localCoachError || (localCoachRuntime?.state === "missing_assets") || (localCoachRuntime?.state === "error")) && !localCoachBusy && (
          <div
            style={{
              fontSize: 12,
              color: localCoachError || localCoachRuntime?.state === "error" ? C.warn : C.textSub,
              lineHeight: 1.7,
              background: "rgba(255,255,255,0.03)",
              border: `1px solid ${C.borderSub}`,
              borderRadius: 12,
              padding: "10px 12px",
            }}
          >
            {localCoachRuntime?.state === "missing_assets" && !localCoachError
              ? "The local coach needs to be set up before you can use it. Click \"Set up coach\" to download the required files."
              : localCoachRuntime?.state === "error" && !localCoachError
                ? "The local coach ran into a problem. Try clicking \"Set up coach\" to reinstall, or ask a question to restart automatically."
                : localCoachError
                  ? (
                    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                      <div>Local coach error:</div>
                      <div
                        style={{
                          fontFamily: "monospace",
                          fontSize: 11,
                          whiteSpace: "pre-wrap",
                          wordBreak: "break-word",
                          color: C.textSub,
                          background: "rgba(0,0,0,0.18)",
                          border: `1px solid ${C.borderSub}`,
                          borderRadius: 8,
                          padding: "8px 10px",
                        }}
                      >
                        {localCoachError}
                      </div>
                    </div>
                  )
                  : null}
          </div>
        )}

        {/* ── Live progress — visible from the first click, before any content arrives ── */}
        {localCoachBusy && (
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              gap: 6,
              padding: "10px 12px",
              borderRadius: 10,
              background: "rgba(255,255,255,0.025)",
              border: `1px solid ${C.borderSub}`,
            }}
          >
            {/* Completed steps */}
            {localCoachProgress.slice(0, -1).map((step, index) => (
              <div
                key={`done-${index}-${step}`}
                style={{ display: "flex", alignItems: "baseline", gap: 6, fontSize: 11, color: C.textFaint, lineHeight: 1.5 }}
              >
                <span style={{ color: accentAlpha("60"), flexShrink: 0 }}>✓</span>
                <span>{step}</span>
              </div>
            ))}
            {/* Current step — highlighted */}
            <div
              style={{ display: "flex", alignItems: "baseline", gap: 7, fontSize: 12, color: C.textSub, lineHeight: 1.5 }}
            >
              <span
                style={{
                  display: "inline-block",
                  width: 7,
                  height: 7,
                  borderRadius: "50%",
                  background: C.accent,
                  flexShrink: 0,
                  marginTop: 2,
                  animation: "glow-pulse 1.4s ease-in-out infinite",
                }}
              />
              <span>
                {localCoachProgress.length > 0
                  ? localCoachProgress[localCoachProgress.length - 1]
                  : "Starting up…"}
              </span>
            </div>
          </div>
        )}

        {/* ── Streaming / completed answer ── */}
        {(localCoachDraftMessage || localCoachReply?.message) && (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <LocalCoachResponseBody
              content={localCoachDraftMessage || localCoachReply?.message || ""}
              visuals={localCoachReply?.visuals ?? []}
            />
            {/* Knowledge items used */}
            {(localCoachReply?.knowledgeItems.length ?? 0) > 0 && (
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 700 }}>
                  Hub knowledge used
                </div>
                <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                  {localCoachReply?.knowledgeItems.map((item) => (
                    <div
                      key={item.id}
                      style={{
                        minWidth: 180,
                        flex: "1 1 180px",
                        padding: "10px 12px",
                        borderRadius: 12,
                        border: `1px solid ${C.border}`,
                        background: "rgba(255,255,255,0.02)",
                      }}
                    >
                      <div style={{ fontSize: 12, fontWeight: 700, color: C.text }}>{item.title}</div>
                      <div style={{ marginTop: 5, fontSize: 11, color: C.textSub, lineHeight: 1.6 }}>{item.summary}</div>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* ── Aim Fingerprint ── */}
      {fingerprint && aimStyle && (
        <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
          <div style={{ ...CHART_STYLE, flex: "1 1 280px", minWidth: 220 }}>
            <SectionTitle
              info={`Built from ${fingerprint.sessionCount} recent runs using ${fingerprint.basisLabel.toLowerCase()}, so one unusual session does not rewrite the whole profile.`}
            >
              Aim Fingerprint
            </SectionTitle>
            <ResponsiveContainer width="100%" height={220}>
              <RadarChart data={radarData} cx="50%" cy="50%">
                <PolarGrid stroke="rgba(255,255,255,0.1)" />
                <PolarAngleAxis
                  dataKey="metric"
                  tick={{ fill: "rgba(255,255,255,0.5)", fontSize: 11 }}
                />
                <PolarRadiusAxis
                  angle={90}
                  domain={[0, 100]}
                  tick={false}
                  axisLine={false}
                />
                <Radar
                  dataKey="value"
                  stroke={aimStyle.color}
                  fill={aimStyle.color}
                  fillOpacity={0.18}
                  strokeWidth={2}
                />
              </RadarChart>
            </ResponsiveContainer>
            <div style={{ display: "flex", flexDirection: "column", gap: 8, marginTop: 8 }}>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                {stableAxes.map((axis) => (
                  <span
                    key={`stable-${axis.key}`}
                    style={{
                      fontSize: 11,
                      padding: "4px 8px",
                      borderRadius: 999,
                      background: "rgba(0,245,160,0.10)",
                      border: "1px solid rgba(0,245,160,0.18)",
                      color: "rgba(255,255,255,0.74)",
                    }}
                  >
                    Stable: {axis.label}
                  </span>
                ))}
                {volatileAxes.map((axis) => (
                  <span
                    key={`volatile-${axis.key}`}
                    style={{
                      fontSize: 11,
                      padding: "4px 8px",
                      borderRadius: 999,
                      background: "rgba(255,159,67,0.10)",
                      border: "1px solid rgba(255,159,67,0.18)",
                      color: "rgba(255,255,255,0.74)",
                    }}
                  >
                    Swingy: {axis.label}
                  </span>
                ))}
              </div>
              <div style={{ display: "flex", gap: 10, flexWrap: "wrap", alignItems: "center" }}>
                {(Object.keys(AIM_AXIS_DEFINITIONS) as AimAxisKey[]).map((axisKey) => {
                  const definition = AIM_AXIS_DEFINITIONS[axisKey];
                  return (
                    <HoverInfoCard
                      key={`axis-definition-${axisKey}`}
                      title={definition.label}
                      summary={definition.what}
                      detail={definition.how(isTracking)}
                    />
                  );
                })}
              </div>
            </div>
          </div>
          <div
            style={{
              ...CHART_STYLE,
              flex: "1 1 200px",
              minWidth: 180,
              display: "flex",
              flexDirection: "column",
              justifyContent: "center",
              gap: 10,
            }}
          >
            <div
              style={{
                fontSize: 10,
                color: "rgba(255,255,255,0.35)",
                textTransform: "uppercase",
                letterSpacing: 1,
              }}
            >
              Aim Style
            </div>
            <div
              style={{ fontSize: 20, fontWeight: 800, color: aimStyle.color, lineHeight: 1.1 }}
            >
              {aimStyle.name}
            </div>
            <div style={{ fontSize: 12, color: "rgba(255,255,255,0.45)", fontStyle: "italic" }}>
              {aimStyle.tagline}
            </div>
            <p
              style={{
                margin: 0,
                fontSize: 12,
                color: "rgba(255,255,255,0.55)",
                lineHeight: 1.65,
              }}
            >
              {aimStyle.description}
            </p>
            <div
              style={{
                fontSize: 11,
                color: aimStyle.color,
                borderTop: `1px solid ${aimStyle.color}30`,
                paddingTop: 10,
                lineHeight: 1.5,
              }}
            >
              <span style={{ opacity: 0.6 }}>Focus: </span>
              {aimStyle.focus}
            </div>
          </div>
        </div>
      )}

      {/* ── Score analytics cards ── */}
      <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
        <StatCard
          label="Consistency"
          value={scoreCV.toFixed(1) + "% spread"}
          sub={scoreCV < 5 ? "Very consistent" : scoreCV < 12 ? "Moderate variance" : "High variance"}
          accent={scoreCV < 5 ? "#00f5a0" : scoreCV < 12 ? "#ffd700" : "#ff6b6b"}
        />
        <StatCard
          label="Learning Rate"
          value={(slope > 0 ? "+" : "") + Math.round(slope) + " pts/run"}
          sub={isPlateau ? "Plateau detected" : slope > avgScoreVal * 0.005 ? "Trending up" : Math.abs(slope) < avgScoreVal * 0.005 ? "Stable" : "Trending down"}
          accent={slope > avgScoreVal * 0.005 ? "#00f5a0" : slope < -avgScoreVal * 0.01 ? "#ff6b6b" : "#ffd700"}
        />
        <StatCard label="Score Floor"  value={fmtScore(p10)} sub="your bottom 10% of runs" accent="#a78bfa" />
        <StatCard label="Typical Score" value={fmtScore(p50)} sub="your most common result" />
        <StatCard label="Peak Zone"    value={fmtScore(p90)} sub="your top 10% of runs" accent="#00f5a0" />
      </div>

      {/* ── Distribution chart ── */}
      <ScoreDistributionChart scores={sortedScores} p10={p10} p50={p50} p90={p90} />

      {/* ── Coaching cards ── */}
      {coachingCards.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <SectionTitle>Coaching Insights — click any card to expand</SectionTitle>
          {coachingCards.map((card, i) => (
            <CoachingCard
              key={i}
              card={card}
              onExploreDrill={onExploreDrill}
              feedback={
                feedbackRows.find((row) => row.snapshotKind === "scenario_coaching" && row.recommendationId === coachingCardFeedbackId(card))?.feedback ?? null
              }
              onFeedback={onFeedback}
              snapshotKind="scenario_coaching"
            />
          ))}
        </div>
      )}
      {showWarmupSection && warmupStats && (
        <div
          style={{
            ...CHART_STYLE,
            display: "flex",
            flexDirection: "column",
            gap: 12,
            borderColor: "rgba(255,180,0,0.18)",
            background: "linear-gradient(180deg, rgba(255,180,0,0.05), rgba(255,180,0,0.02))",
          }}
        >
          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
            <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span
                style={{
                  background: "rgba(255,180,0,0.16)",
                  border: "1px solid rgba(255,180,0,0.26)",
                  color: "#ffb400",
                  borderRadius: 999,
                  fontSize: 10,
                  padding: "3px 9px",
                  textTransform: "uppercase",
                  letterSpacing: 0.8,
                  fontWeight: 700,
                }}
              >
                Warm-up Effect
              </span>
              <span style={{ fontSize: 13, fontWeight: 700, color: "rgba(255,255,255,0.9)" }}>
                You usually settle in by {warmupStats.settleInLabel}
              </span>
            </div>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              <span style={{ fontSize: 11, color: "#ffb400" }}>
                −{warmupStats.dropPct.toFixed(0)}% opening dip
              </span>
              <span style={{ fontSize: 11, color: "rgba(255,255,255,0.45)" }}>
                {warmupStats.blockCount} block{warmupStats.blockCount !== 1 ? "s" : ""} observed
              </span>
            </div>
          </div>
          <p style={{ margin: 0, fontSize: 12, color: "rgba(255,255,255,0.58)", lineHeight: 1.7 }}>
            At the start of a new practice block your opening runs land about {warmupStats.dropPct.toFixed(0)}% below your settled-in level. That is normal, not lost skill. Your timing and feel just need a few runs to click back into place after a break. Those early runs are excluded from the peak-performance view above.
          </p>
          <div
            style={{
              background: "rgba(255,180,0,0.08)",
              border: "1px solid rgba(255,180,0,0.14)",
              borderRadius: 10,
              padding: "10px 12px",
              fontSize: 12,
              color: "rgba(255,255,255,0.74)",
              lineHeight: 1.65,
            }}
          >
            <span style={{ color: "#ffb400", fontWeight: 700, textTransform: "uppercase", fontSize: 10, letterSpacing: 0.8 }}>
              Action
            </span>
            <div style={{ marginTop: 6 }}>
              {warmupStats.action}
            </div>
          </div>
        </div>
      )}
      </div>  /* end peak performance section */
      )}
    </div>
  );
}

function SessionsOverviewPanel({
  records,
  scenarioGroups,
  practiceProfile,
  globalLearningState,
  globalCoachingCards,
  compareScenario,
  selectedScenario,
  compareSummary,
  selectedSummary,
  onCompareScenarioChange,
  onExploreDrill,
  playerLearningProfile,
  coachingPersistenceStatus,
  feedbackRows,
  onFeedback,
}: {
  records: AnalyticsSessionRecord[];
  scenarioGroups: Array<{
    name: string;
    bestReliable: number | null;
    bestAny: number;
    count: number;
    reliableCount: number;
    flaggedCount: number;
    lastTs: string;
    trend: "up" | "down" | "flat" | null;
    scenarioType: string;
  }>;
  practiceProfile: PracticeProfile | null;
  globalLearningState: GlobalCoachingLearningState | null;
  globalCoachingCards: CoachingCardData[];
  compareScenario: string | null;
  selectedScenario: string | null;
  compareSummary: ScenarioSummary | null;
  selectedSummary: ScenarioSummary | null;
  onCompareScenarioChange: (value: string | null) => void;
  onExploreDrill: (query: string) => void;
  playerLearningProfile?: PlayerLearningProfile | null;
  coachingPersistenceStatus?: CoachingPersistenceStatus | null;
  feedbackRows: CoachingUserFeedbackRecord[];
  onFeedback: (snapshotKind: string, card: CoachingCardData, feedback: CoachingCardFeedback) => void;
}) {
  const reliable = useMemo(
    () => records.filter((record) => record.isReliableForAnalysis),
    [records],
  );
  const totalSessions = records.length;
  const totalScenarios = scenarioGroups.length;
  const totalPlaySeconds = records.reduce((sum, record) => sum + (record.duration_secs ?? 0), 0);
  const reliableScores = reliable.map((record) => record.score);
  const medianScore = reliableScores.length > 0
    ? percentileOf([...reliableScores].sort((a, b) => a - b), 50)
    : 0;
  const scenarioTypeCounts = scenarioGroups.reduce((acc, group) => {
    acc.set(group.scenarioType, (acc.get(group.scenarioType) ?? 0) + group.count);
    return acc;
  }, new Map<string, number>());
  const topScenarioTypes = [...scenarioTypeCounts.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .slice(0, 4);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 16 }}>
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
          <div>
            <div style={{ fontSize: 11, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.1em", fontWeight: 700 }}>
              Compare scenarios
            </div>
            <div style={{ marginTop: 4, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
              Put any two scenarios next to each other to compare score ceiling, accuracy, and play volume.
            </div>
          </div>
          <select
            value={compareScenario ?? ""}
            onChange={(event) => onCompareScenarioChange(event.target.value || null)}
            className="am-input"
            style={{ minWidth: 260, boxSizing: "border-box", padding: "7px 10px" }}
          >
            <option value="">No comparison</option>
            {scenarioGroups
              .filter((group) => group.name !== selectedScenario)
              .map((group) => (
                <option key={group.name} value={group.name}>
                  {group.name}
                </option>
              ))}
          </select>
        </div>
        {selectedSummary && compareSummary && compareScenario && selectedScenario && (
          <ScenarioComparisonCard
            leftLabel={selectedScenario}
            left={selectedSummary}
            rightLabel={compareScenario}
            right={compareSummary}
          />
        )}
      </div>
      <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 12 }}>
        <SectionTitle>Overview</SectionTitle>
        <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
          <StatCard label="Runs" value={totalSessions.toString()} />
          <StatCard label="Scenarios" value={totalScenarios.toString()} accent="#a78bfa" />
          <StatCard label="Play Time" value={formatPlayTime(totalPlaySeconds)} accent="#00b4ff" />
          <StatCard label="Median Score" value={fmtScore(medianScore)} accent="#00f5a0" />
        </div>
        {topScenarioTypes.length > 0 && (
          <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
            <span style={{ fontSize: 10, color: "rgba(255,255,255,0.34)", textTransform: "uppercase", letterSpacing: 0.8 }}>
              Scenario families
            </span>
            {topScenarioTypes.map(([scenarioType, count]) => (
              <span
                key={scenarioType}
                style={{
                  fontSize: 10,
                  padding: "3px 8px",
                  borderRadius: 999,
                  background: `${scenarioColor(scenarioType)}12`,
                  border: `1px solid ${scenarioColor(scenarioType)}25`,
                  color: "rgba(255,255,255,0.72)",
                }}
              >
                {SCENARIO_LABELS[scenarioType] ?? scenarioType} · {count}
              </span>
            ))}
          </div>
        )}
      </div>
      {globalCoachingCards.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <SectionTitle>Global Coaching</SectionTitle>
          {globalLearningState && (
            <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
              {[
                globalLearningState.warmupTaxPct != null
                  ? {
                      label: "Warm-up Tax",
                      value: `${globalLearningState.warmupTaxPct >= 0 ? "" : "+"}${globalLearningState.warmupTaxPct.toFixed(0)}%`,
                      accent: globalLearningState.warmupTaxPct >= 6 ? "#ffb400" : "#00f5a0",
                    }
                  : null,
                globalLearningState.switchPenaltyPct != null
                  ? {
                      label: "Switch Cost",
                      value: `${globalLearningState.switchPenaltyPct.toFixed(0)}%`,
                      accent: globalLearningState.switchPenaltyPct >= 5 ? "#ffd700" : "#00b4ff",
                    }
                  : null,
                globalLearningState.avgBlockFadePct != null
                  ? {
                      label: "Late Block Fade",
                      value: `${globalLearningState.avgBlockFadePct.toFixed(0)}%`,
                      accent: globalLearningState.avgBlockFadePct >= 5 ? "#ff9f43" : "#00f5a0",
                    }
                  : null,
                globalLearningState.retentionAfterGapPct != null
                  ? {
                      label: "Retention After Breaks",
                      value: `${globalLearningState.retentionAfterGapPct.toFixed(0)}%`,
                      accent: globalLearningState.retentionAfterGapPct >= 98 ? "#00f5a0" : globalLearningState.retentionAfterGapPct < 92 ? "#ff6b6b" : "#00b4ff",
                    }
                  : null,
                globalLearningState.momentumDeltaPct != null
                  ? {
                      label: "Momentum",
                      value: `${globalLearningState.momentumDeltaPct >= 0 ? "+" : ""}${globalLearningState.momentumDeltaPct.toFixed(0)}%`,
                      accent: globalLearningState.momentumDeltaPct >= 4 ? "#00f5a0" : globalLearningState.momentumDeltaPct <= -4 ? "#ff6b6b" : "#ffd700",
                    }
                  : null,
              ]
                .filter((entry): entry is { label: string; value: string; accent: string } => entry != null)
                .map((entry) => (
                  <StatCard
                    key={entry.label}
                    label={entry.label}
                    value={entry.value}
                    accent={entry.accent}
                  />
                ))}
            </div>
          )}
          {globalCoachingCards.map((card, index) => (
            <CoachingCard
              key={`global-${index}`}
              card={card}
              onExploreDrill={onExploreDrill}
                feedback={
                  feedbackRows.find((row) => row.snapshotKind === "player_learning_profile" && row.recommendationId === coachingCardFeedbackId(card))?.feedback ?? null
                }
              onFeedback={onFeedback}
              snapshotKind="player_learning_profile"
            />
          ))}
        </div>
      )}
      {playerLearningProfile && <PlayerLearningProfilePanel profile={playerLearningProfile} />}
      {coachingPersistenceStatus && <CoachingEvaluationLoopPanel status={coachingPersistenceStatus} />}
      {practiceProfile && <PracticeProfilePanel practiceProfile={practiceProfile} />}
    </div>
  );
}

function PlayerLearningProfilePanel({ profile }: { profile: PlayerLearningProfile }) {
  return (
    <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 14 }}>
      <SectionTitle>Unified Learning Model</SectionTitle>
      <div style={{ fontSize: 13, color: C.textSub, lineHeight: 1.7 }}>
        {profile.summary}
      </div>
      <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
        {profile.strengths.map((signal) => (
          <div
            key={`strength-${signal.key}`}
            style={{
              padding: "8px 10px",
              borderRadius: 12,
              border: "1px solid rgba(0,245,160,0.22)",
              background: "rgba(0,245,160,0.08)",
              minWidth: 180,
              flex: "1 1 180px",
            }}
          >
            <div style={{ fontSize: 10, color: "rgba(255,255,255,0.5)", textTransform: "uppercase", letterSpacing: 0.8 }}>
              Strength
            </div>
            <div style={{ marginTop: 4, fontSize: 13, fontWeight: 700, color: C.text }}>
              {signal.label}
            </div>
            <div style={{ marginTop: 4, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
              {signal.detail}
            </div>
          </div>
        ))}
        {profile.constraints.map((signal) => (
          <div
            key={`constraint-${signal.key}`}
            style={{
              padding: "8px 10px",
              borderRadius: 12,
              border: "1px solid rgba(255,159,67,0.22)",
              background: "rgba(255,159,67,0.08)",
              minWidth: 180,
              flex: "1 1 180px",
            }}
          >
            <div style={{ fontSize: 10, color: "rgba(255,255,255,0.5)", textTransform: "uppercase", letterSpacing: 0.8 }}>
              Bottleneck
            </div>
            <div style={{ marginTop: 4, fontSize: 13, fontWeight: 700, color: C.text }}>
              {signal.label}
            </div>
            <div style={{ marginTop: 4, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
              {signal.detail}
            </div>
          </div>
        ))}
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 10 }}>
        {profile.axes.map((axis) => (
          <div
            key={axis.key}
            style={{
              padding: "10px 12px",
              borderRadius: 12,
              border: `1px solid ${C.border}`,
              background: "rgba(255,255,255,0.02)",
            }}
          >
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8 }}>
              <span style={{ fontSize: 12, fontWeight: 600, color: C.text }}>{axis.label}</span>
              <span style={{ fontSize: 12, color: C.textSub }}>{Math.round(axis.valuePct)}%</span>
            </div>
            <div style={{ marginTop: 8, height: 6, borderRadius: 999, background: "rgba(255,255,255,0.08)", overflow: "hidden" }}>
              <div
                style={{
                  width: `${axis.valuePct}%`,
                  height: "100%",
                  borderRadius: 999,
                  background: axis.valuePct >= 60 ? "#00f5a0" : axis.valuePct <= 45 ? "#ff9f43" : "#00b4ff",
                }}
              />
            </div>
            <div style={{ marginTop: 8, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
              {axis.detail}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function CoachingEvaluationLoopPanel({ status }: { status: CoachingPersistenceStatus }) {
  return (
    <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 12 }}>
      <SectionTitle>Coaching Evaluation Loop</SectionTitle>
      <div style={{ fontSize: 13, color: C.textSub, lineHeight: 1.7 }}>
        AimMod is now persisting the player-learning snapshot and tracking whether recommendation-linked metrics later improve, stall, or regress.
      </div>
      <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
        <StatCard label="Pending Follow-up" value={status.pendingCount.toString()} accent="#ffd700" />
        <StatCard label="Improved" value={status.improvedCount.toString()} accent="#00f5a0" />
        <StatCard label="Flat" value={status.flatCount.toString()} accent="#00b4ff" />
        <StatCard label="Regressed" value={status.regressedCount.toString()} accent="#ff6b6b" />
      </div>
      {status.snapshotUpdatedAtMs != null && (
        <div style={{ fontSize: 11, color: C.textFaint }}>
          Last learning snapshot: {new Date(status.snapshotUpdatedAtMs).toLocaleString()}
        </div>
      )}
    </div>
  );
}

function formatHubStatusTime(unixMs: number | null | undefined): string {
  if (!unixMs || !Number.isFinite(unixMs)) return "Never";
  return new Date(unixMs).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function HubOverviewPanel({
  settings,
  overview,
  replayRecords,
  onOpenSettings,
  onOpenReplayHub,
  onForceResync,
  resyncBusy,
}: {
  settings: AppSettings | null;
  overview: StatsHubSyncOverview | null;
  replayRecords: AnalyticsSessionRecord[];
  onOpenSettings: () => void;
  onOpenReplayHub: () => void;
  onForceResync: () => void;
  resyncBusy: boolean;
}) {
  const linkedAccount = settings?.hub_account_label?.trim() || overview?.accountLabel?.trim() || "";
  const linked = linkedAccount.length > 0 && Boolean(settings?.hub_upload_token?.trim());
  const replayUploadMode = settings?.replay_media_upload_mode ?? "favorites_and_pb";
  const replayUploadQuality = settings?.replay_media_upload_quality ?? "standard";
  const shareableReplays = replayRecords
    .filter((record) => record.has_replay)
    .sort((a, b) => b.timestampMs - a.timestampMs)
    .slice(0, 6);

  const replayUploadLabel =
    replayUploadMode === "all"
      ? "All replays"
      : replayUploadMode === "favorites"
        ? "Favorites"
        : replayUploadMode === "favorites_and_pb"
          ? "Favorites + PBs"
          : "Off";

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
      <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 14 }}>
        <div style={{ display: "flex", alignItems: "flex-start", justifyContent: "space-between", gap: 16, flexWrap: "wrap" }}>
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: 11, color: C.accent, textTransform: "uppercase", letterSpacing: "0.1em", fontWeight: 700 }}>
              AimMod Hub
            </div>
            <div style={{ marginTop: 6, fontSize: 22, fontWeight: 700, color: C.text, letterSpacing: "-0.02em" }}>
              Your runs, replays, and profile in one place.
            </div>
            <div style={{ marginTop: 8, maxWidth: 760, fontSize: 12, color: C.textSub, lineHeight: 1.7 }}>
              Stay linked to keep your latest runs, replay uploads, and profile up to date automatically.
            </div>
          </div>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <Btn size="sm" variant={linked ? "ghost" : "primary"} onClick={onOpenSettings}>
              {linked ? "Manage Hub settings" : "Connect account"}
            </Btn>
            <Btn size="sm" variant="ghost" onClick={onOpenReplayHub}>
              Open Replay Hub
            </Btn>
            {linked && (
              <Btn size="sm" variant="ghost" onClick={onForceResync} disabled={resyncBusy}>
                {resyncBusy ? "Queueing resync…" : "Resync all runs"}
              </Btn>
            )}
          </div>
        </div>

        <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
          <StatCard
            label="Account"
            value={linked ? linkedAccount : "Not linked"}
            accent={linked ? C.accent : C.warn}
            sub={linked ? "Connected" : "Link in settings"}
          />
            <StatCard
            label="Waiting"
            value={String(overview?.status.pendingCount ?? 0)}
            accent="#00b4ff"
            sub={overview?.status.syncInProgress ? "Uploading now" : "Ready to upload"}
          />
          <StatCard
            label="Replay uploads"
            value={replayUploadLabel}
            accent="#a78bfa"
            sub={`${replayUploadQuality} quality`}
          />
        </div>

        <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
          <Badge color={linked ? "#00f5a0" : "#ffb400"}>
            {linked ? "Connected" : "Needs linking"}
          </Badge>
          {overview?.status.lastSuccessAtUnixMs ? (
            <span style={{ fontSize: 11, color: C.textFaint }}>
              Last run sync {formatHubStatusTime(overview.status.lastSuccessAtUnixMs)}
            </span>
          ) : null}
          {overview?.status.lastReplayMediaUploadAtUnixMs ? (
            <span style={{ fontSize: 11, color: C.textFaint }}>
              Last replay upload {formatHubStatusTime(overview.status.lastReplayMediaUploadAtUnixMs)}
            </span>
          ) : null}
        </div>

        {formatHubUserError(overview?.status.lastError) ? (
          <div style={{ padding: "10px 12px", borderRadius: 10, border: `1px solid ${C.warn}35`, background: `${C.warn}12`, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
              Latest sync issue: {formatHubUserError(overview?.status.lastError)}
            </div>
          ) : null}
        {formatHubUserError(overview?.status.lastReplayMediaError) ? (
          <div style={{ padding: "10px 12px", borderRadius: 10, border: `1px solid ${C.warn}35`, background: `${C.warn}12`, fontSize: 12, color: C.textSub, lineHeight: 1.6 }}>
              Latest replay upload issue: {formatHubUserError(overview?.status.lastReplayMediaError)}
            </div>
          ) : null}
      </div>

      <div style={{ ...CHART_STYLE, display: "flex", flexDirection: "column", gap: 12 }}>
        <SectionTitle>Replay-ready runs</SectionTitle>
        <div style={{ fontSize: 12, color: C.textSub, lineHeight: 1.65 }}>
          These recent runs already have replay data and are ready to share.
        </div>
        {shareableReplays.length > 0 ? (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: 10 }}>
            {shareableReplays.map((record) => (
              <div
                key={record.id}
                style={{
                  padding: "12px 13px",
                  borderRadius: 12,
                  border: `1px solid ${C.borderSub}`,
                  background: "rgba(255,255,255,0.02)",
                  display: "flex",
                  flexDirection: "column",
                  gap: 6,
                  minWidth: 0,
                }}
              >
                <div style={{ fontSize: 12, fontWeight: 700, color: C.text, lineHeight: 1.45, wordBreak: "break-word" }}>
                  {record.scenario}
                </div>
                <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap", fontSize: 10, color: C.textFaint }}>
                  <span>{relativeTime(record.timestamp)}</span>
                  <span>{fmtScore(record.score)}</span>
                  <span>{record.duration_secs}s</span>
                  {record.replay_is_favorite && (
                    <span style={{ color: "#ffd700", fontWeight: 700 }}>★ Favorite</span>
                  )}
                </div>
                <div style={{ fontSize: 10, color: C.textFaint }}>
                  {record.replay_frames_count} frames · {record.replay_positions_count} mouse samples
                </div>
              </div>
            ))}
          </div>
        ) : (
          <div style={{ fontSize: 12, color: C.textFaint }}>
            No replay-ready runs yet. Finish a run with replay capture enabled and it will show up here.
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Benchmarks tab ───────────────────────────────────────────────────────────

function BenchmarksTab({
  scenarioName,
  sorted,
  best,
  benchmarkRanks,
  benchmarkPages,
  loading,
  hubHandle,
}: {
  scenarioName: string;
  sorted: SessionRecord[];
  best: number;
  benchmarkRanks: HubScenarioBenchmarkRank[];
  benchmarkPages: HubBenchmarkPageResponse[];
  loading: boolean;
  hubHandle: string | null;
}) {
  const [favoriteBenchmarkIds, setFavoriteBenchmarkIds] = useState<number[]>(
    () => readStoredNumberArray(STATS_WINDOW_STORAGE_KEYS.favoriteBenchmarks),
  );

  useEffect(() => {
    writeStoredNumberArray(STATS_WINDOW_STORAGE_KEYS.favoriteBenchmarks, favoriteBenchmarkIds);
  }, [favoriteBenchmarkIds]);

  const benchmarkScenarioEntries = useMemo(() => {
    const map = new Map<number, HubBenchmarkScenarioEntry>();
    for (const page of benchmarkPages) {
      for (const cat of page.categories) {
        const entry = cat.scenarios.find(
          (s) => s.scenarioName.toLowerCase() === scenarioName.toLowerCase(),
        );
        if (entry) {
          map.set(page.benchmarkId, entry);
          break;
        }
      }
    }
    return map;
  }, [benchmarkPages, scenarioName]);

  const favoriteSet = useMemo(() => new Set(favoriteBenchmarkIds), [favoriteBenchmarkIds]);

  const orderedBenchmarkRanks = useMemo(() => {
    return [...benchmarkRanks].sort((a, b) => {
      const aFavorite = favoriteSet.has(a.benchmarkId) ? 1 : 0;
      const bFavorite = favoriteSet.has(b.benchmarkId) ? 1 : 0;
      if (aFavorite !== bFavorite) return bFavorite - aFavorite;
      const aRankIndex = a.scenarioRank?.rankIndex ?? -1;
      const bRankIndex = b.scenarioRank?.rankIndex ?? -1;
      if (aRankIndex !== bRankIndex) return bRankIndex - aRankIndex;
      return a.benchmarkName.localeCompare(b.benchmarkName);
    });
  }, [benchmarkRanks, favoriteSet]);

  function getRankForScore(score: number, thresholds: HubBenchmarkThreshold[]): HubBenchmarkThreshold | null {
    const desc = [...thresholds].sort((a, b) => b.score - a.score);
    return desc.find((t) => score >= t.score) ?? null;
  }

  function imageUrl(value?: string | null): string {
    return value?.trim() ? value : "";
  }

  function toggleFavorite(benchmarkId: number) {
    setFavoriteBenchmarkIds((current) =>
      current.includes(benchmarkId)
        ? current.filter((id) => id !== benchmarkId)
        : [...current, benchmarkId],
    );
  }

  if (!hubHandle) {
    return (
      <div style={{ ...CHART_STYLE, color: C.textFaint, fontSize: 12, textAlign: "center", padding: "32px 20px" }}>
        Link your AimMod Hub account in Settings to see benchmark ranks for this scenario.
      </div>
    );
  }

  if (loading) {
    return (
      <div style={{ ...CHART_STYLE, color: C.textFaint, fontSize: 12, textAlign: "center", padding: "32px 20px" }}>
        Loading benchmark data…
      </div>
    );
  }

  if (orderedBenchmarkRanks.length === 0) {
    return (
      <div style={{ ...CHART_STYLE, color: C.textFaint, fontSize: 12, textAlign: "center", padding: "32px 20px" }}>
        This scenario is part of a benchmark, but no benchmark ranks are showing yet. Play it and sync to AimMod Hub to load your benchmark progress.
      </div>
    );
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      {orderedBenchmarkRanks.map((rank) => {
        const entry = benchmarkScenarioEntries.get(rank.benchmarkId);
        const thresholds = entry?.thresholds ?? [];
        const sortedThresholds = [...thresholds].sort((a, b) => a.score - b.score);
        const bestThreshold = getRankForScore(best, thresholds);
        const favorited = favoriteSet.has(rank.benchmarkId);
        const thresholdColors = new Map(
          sortedThresholds.map((threshold, index) => [
            threshold.rankIndex,
            resolveBenchmarkColor(threshold.color, threshold.rankName, index),
          ]),
        );
        const rankThreshold = rank.scenarioRank
          ? sortedThresholds.find((threshold) => threshold.rankIndex === rank.scenarioRank?.rankIndex) ?? null
          : null;
        const rankColor = resolveBenchmarkColor(
          rank.scenarioRank?.color ?? rankThreshold?.color ?? bestThreshold?.color,
          rank.scenarioRank?.rankName ?? rankThreshold?.rankName ?? bestThreshold?.rankName,
          rankThreshold
            ? sortedThresholds.findIndex((threshold) => threshold.rankIndex === rankThreshold.rankIndex)
            : benchmarkPaletteIndex(rank.scenarioRank?.rankIndex),
        );
        const bestThresholdColor = bestThreshold
          ? thresholdColors.get(bestThreshold.rankIndex)
            ?? resolveBenchmarkColor(bestThreshold.color, bestThreshold.rankName, benchmarkPaletteIndex(bestThreshold.rankIndex))
          : null;

        return (
          <div
            key={rank.benchmarkId}
            style={{
              ...CHART_STYLE,
              display: "flex",
              flexDirection: "column",
              gap: 14,
              border: `1px solid ${favorited ? `${rankColor}55` : C.border}`,
              boxShadow: favorited ? `0 0 0 1px ${rankColor}22 inset` : undefined,
              background: favorited
                ? `linear-gradient(180deg, ${rankColor}14, rgba(255,255,255,0.02))`
                : CHART_STYLE.background,
            }}
          >
            <div style={{ display: "flex", alignItems: "flex-start", justifyContent: "space-between", gap: 12 }}>
              <div style={{ minWidth: 0, display: "flex", alignItems: "flex-start", gap: 12 }}>
                {imageUrl(rank.benchmarkIconUrl) ? (
                  <img
                    src={rank.benchmarkIconUrl}
                    alt=""
                    style={{
                      width: 42,
                      height: 42,
                      borderRadius: 12,
                      objectFit: "cover",
                      border: `1px solid ${C.borderSub}`,
                      background: "rgba(0,0,0,0.22)",
                      flexShrink: 0,
                    }}
                  />
                ) : null}
                <div style={{ minWidth: 0 }}>
                  <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap", marginBottom: 3 }}>
                    <div style={{ fontSize: 11, color: rankColor, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 700 }}>
                      {rank.categoryName}
                    </div>
                    {favorited ? (
                      <div style={{ fontSize: 10, color: "#ffd76a", fontWeight: 700, letterSpacing: "0.06em", textTransform: "uppercase" }}>
                        Favorite
                      </div>
                    ) : null}
                  </div>
                  <div style={{ fontSize: 16, fontWeight: 700, color: C.text, lineHeight: 1.25 }}>{rank.benchmarkName}</div>
                  {rank.leaderboardRank > 0 ? (
                    <div style={{ marginTop: 5, fontSize: 11, color: C.textFaint }}>
                      Leaderboard rank <strong style={{ color: C.text }}>#{rank.leaderboardRank.toLocaleString()}</strong>
                    </div>
                  ) : null}
                </div>
              </div>
              <div style={{ display: "flex", alignItems: "center", gap: 10, flexShrink: 0 }}>
                <button
                  type="button"
                  onClick={() => toggleFavorite(rank.benchmarkId)}
                  title={favorited ? "Remove favorite" : "Favorite benchmark"}
                  style={{
                    border: `1px solid ${favorited ? "#ffd76a80" : C.border}`,
                    background: favorited ? "rgba(255,215,106,0.14)" : "rgba(255,255,255,0.03)",
                    color: favorited ? "#ffd76a" : C.textFaint,
                    borderRadius: 10,
                    width: 34,
                    height: 34,
                    cursor: "pointer",
                    fontSize: 16,
                    fontWeight: 700,
                    lineHeight: 1,
                  }}
                >
                  ★
                </button>
                <div
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 10,
                    padding: imageUrl(rank.scenarioRank?.iconUrl) ? "7px 12px 7px 8px" : "7px 12px",
                    borderRadius: 12,
                    background: `${rankColor}1a`,
                    border: `1px solid ${rankColor}66`,
                    minWidth: 0,
                  }}
                >
                  {imageUrl(rank.scenarioRank?.iconUrl) ? (
                    <img
                      src={rank.scenarioRank?.iconUrl}
                      alt={rank.scenarioRank?.rankName ?? ""}
                      style={{
                        width: 30,
                        height: 30,
                        borderRadius: 9,
                        objectFit: "cover",
                        border: `1px solid ${rankColor}44`,
                        background: "rgba(0,0,0,0.18)",
                        flexShrink: 0,
                      }}
                    />
                  ) : null}
                  <div style={{ minWidth: 0 }}>
                    <div style={{ fontSize: 13, fontWeight: 700, color: rankColor, whiteSpace: "nowrap" }}>
                      {rank.scenarioRank?.rankName ?? "Unranked"}
                    </div>
                    <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em" }}>
                      Current rank
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 10 }}>
              <div style={{ padding: "10px 12px", borderRadius: 12, border: `1px solid ${C.borderSub}`, background: "rgba(0,0,0,0.14)" }}>
                <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", marginBottom: 4 }}>
                  Benchmark PB
                </div>
                <div style={{ fontSize: 18, fontWeight: 700, color: rankColor }}>
                  {Math.round(rank.scenarioScore).toLocaleString()}
                </div>
                <div style={{ marginTop: 4, fontSize: 10, color: C.textFaint }}>
                  Synced to AimMod Hub
                </div>
              </div>
              <div style={{ padding: "10px 12px", borderRadius: 12, border: `1px solid ${C.borderSub}`, background: "rgba(0,0,0,0.14)" }}>
                <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", marginBottom: 4 }}>
                  Local Best
                </div>
                <div style={{ fontSize: 18, fontWeight: 700, color: bestThresholdColor || C.text }}>
                  {Math.round(best).toLocaleString()}
                </div>
                <div style={{ marginTop: 4, fontSize: 10, color: C.textFaint }}>
                  From runs in this view
                </div>
              </div>
            </div>

            {sortedThresholds.length > 0 && (
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 600 }}>
                  Rank thresholds
                </div>
                {[...sortedThresholds].reverse().map((threshold) => {
                  const thresholdColor = thresholdColors.get(threshold.rankIndex)
                    ?? resolveBenchmarkColor(threshold.color, threshold.rankName, benchmarkPaletteIndex(threshold.rankIndex));
                  const progress = threshold.score > 0
                    ? Math.max(0, Math.min(100, (best / threshold.score) * 100))
                    : 0;
                  const achieved = best >= threshold.score;
                  return (
                    <div
                      key={threshold.rankIndex}
                      style={{ display: "grid", gridTemplateColumns: "156px minmax(0,1fr) 80px", gap: 10, alignItems: "center" }}
                    >
                      <div style={{ display: "flex", alignItems: "center", gap: 8, minWidth: 0 }}>
                        {imageUrl(threshold.iconUrl) ? (
                          <img
                            src={threshold.iconUrl}
                            alt={threshold.rankName}
                            style={{
                              width: 22,
                              height: 22,
                              borderRadius: 7,
                              objectFit: "cover",
                              border: `1px solid ${thresholdColor}44`,
                              background: "rgba(0,0,0,0.18)",
                              flexShrink: 0,
                            }}
                          />
                        ) : null}
                        <div style={{ fontSize: 11, color: achieved ? thresholdColor : C.textFaint, fontWeight: achieved ? 700 : 400, minWidth: 0 }}>
                          {threshold.rankName}
                        </div>
                      </div>
                      <div style={{ height: 7, borderRadius: 999, background: "rgba(255,255,255,0.08)", overflow: "hidden" }}>
                        <div
                          style={{
                            width: `${progress}%`,
                            height: "100%",
                            borderRadius: 999,
                            background: achieved ? thresholdColor : `${thresholdColor}70`,
                            transition: "width 0.4s ease",
                          }}
                        />
                      </div>
                      <div style={{ fontSize: 10, color: C.textFaint, textAlign: "right" }}>
                        {threshold.score.toLocaleString()}
                      </div>
                    </div>
                  );
                })}
              </div>
            )}

            {sorted.length > 0 && sortedThresholds.length > 0 && (
              <div>
                <div style={{ fontSize: 10, color: C.textFaint, textTransform: "uppercase", letterSpacing: "0.08em", fontWeight: 600, marginBottom: 8 }}>
                  Score history — {rank.benchmarkName}
                </div>
                <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 11 }}>
                  <thead>
                    <tr style={{ color: C.textFaint }}>
                      <th style={{ textAlign: "left", paddingBottom: 6, fontWeight: 500, borderBottom: `1px solid ${C.border}` }}>Date</th>
                      <th style={{ textAlign: "right", paddingBottom: 6, fontWeight: 500, borderBottom: `1px solid ${C.border}` }}>Score</th>
                      <th style={{ textAlign: "right", paddingBottom: 6, fontWeight: 500, paddingLeft: 12, borderBottom: `1px solid ${C.border}` }}>Rank</th>
                    </tr>
                  </thead>
                  <tbody>
                    {[...sorted].reverse().slice(0, 20).map((r) => {
                      const rankHit = getRankForScore(r.score, thresholds);
                      const rankHitColor = rankHit
                        ? thresholdColors.get(rankHit.rankIndex)
                          ?? resolveBenchmarkColor(rankHit.color, rankHit.rankName, benchmarkPaletteIndex(rankHit.rankIndex))
                        : null;
                      const isBest = r.score === best;
                      return (
                        <tr key={r.id}>
                          <td style={{ padding: "5px 0", color: C.textSub, borderBottom: `1px solid ${C.border}20` }}>
                            {formatDateTime(r.timestamp)}
                          </td>
                          <td style={{ padding: "5px 0", textAlign: "right", fontWeight: isBest ? 700 : 400, color: isBest ? "#00f5a0" : C.text, borderBottom: `1px solid ${C.border}20` }}>
                            {fmtScore(r.score)}
                          </td>
                          <td style={{ padding: "5px 0", textAlign: "right", paddingLeft: 12, borderBottom: `1px solid ${C.border}20` }}>
                            {rankHit ? (
                              <span style={{ display: "inline-flex", alignItems: "center", gap: 6, color: rankHitColor || C.text, fontWeight: 700 }}>
                                {imageUrl(rankHit.iconUrl) ? (
                                  <img
                                    src={rankHit.iconUrl}
                                    alt={rankHit.rankName}
                                    style={{
                                      width: 18,
                                      height: 18,
                                      borderRadius: 6,
                                      objectFit: "cover",
                                      border: `1px solid ${(rankHitColor || C.text)}44`,
                                      background: "rgba(0,0,0,0.18)",
                                    }}
                                  />
                                ) : null}
                                <span>{rankHit.rankName}</span>
                              </span>
                            ) : (
                              <span style={{ color: C.textFaint }}>—</span>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}

            {bestThreshold === null && sortedThresholds.length > 0 && (
              <div style={{ fontSize: 11, color: C.textFaint }}>
                Need{" "}
                <strong style={{ color: C.text }}>{sortedThresholds[0].score.toLocaleString()}</strong>{" "}
                to reach{" "}
                <strong style={{ color: thresholdColors.get(sortedThresholds[0].rankIndex) || rankColor }}>{sortedThresholds[0].rankName}</strong>
                {" "}
                ({(sortedThresholds[0].score - best).toLocaleString()} more points)
              </div>
            )}
            {bestThreshold !== null && (() => {
              const nextIdx = sortedThresholds.findIndex((t) => t.rankIndex === bestThreshold.rankIndex) + 1;
              const next = sortedThresholds[nextIdx];
              if (!next) return null;
              const nextColor = thresholdColors.get(next.rankIndex)
                ?? resolveBenchmarkColor(next.color, next.rankName, benchmarkPaletteIndex(next.rankIndex));
              return (
                <div style={{ fontSize: 11, color: C.textFaint }}>
                  Next rank:{" "}
                  <strong style={{ color: nextColor }}>{next.rankName}</strong> at{" "}
                  <strong style={{ color: C.text }}>{next.score.toLocaleString()}</strong>
                  {" "}— {(next.score - best).toLocaleString()} more points needed
                </div>
              );
            })()}
          </div>
        );
      })}
    </div>
  );
}

// ─── Scenario details (tabbed) ────────────────────────────────────────────────

function ScenarioDetails({
  records,
  scenarioName,
  dateRange,
  onExploreDrill,
  onReplayMetadataChanged,
  hubHandle,
  feedbackRows,
  onFeedback,
  appSettings,
  globalSummary,
}: {
  records: AnalyticsSessionRecord[];
  scenarioName: string;
  dateRange: DateRangePreset;
  onExploreDrill: (query: string) => void;
  onReplayMetadataChanged?: () => void;
  hubHandle: string | null;
  feedbackRows: CoachingUserFeedbackRecord[];
  onFeedback: (snapshotKind: string, card: CoachingCardData, feedback: CoachingCardFeedback) => void;
  appSettings: AppSettings | null;
  globalSummary: string;
}) {
  const [activeTab, setActiveTab] = useState<Tab>(() => {
    const stored = readStoredValue(STATS_WINDOW_STORAGE_KEYS.scenarioTab);
    return stored === "mechanics"
      || stored === "coaching"
      || stored === "replay"
      || stored === "leaderboard"
      || stored === "benchmarks"
      ? stored
      : "summary";
  });
  const [sessionFilter, setSessionFilter] = useState<SessionFilter>(() => {
    const stored = readStoredValue(STATS_WINDOW_STORAGE_KEYS.sessionFilter);
    return stored === "warmup" || stored === "warmedup" ? stored : "all";
  });
  const [replayJumpId, setReplayJumpId] = useState<string | null>(null);
  const [hubBenchmarkRanks, setHubBenchmarkRanks] = useState<HubScenarioBenchmarkRank[]>([]);
  const [hubBenchmarkPages, setHubBenchmarkPages] = useState<HubBenchmarkPageResponse[]>([]);
  const [hubBenchmarkLoading, setHubBenchmarkLoading] = useState(false);
  const scenarioSlug = useMemo(() => slugifyScenarioName(scenarioName), [scenarioName]);

  useEffect(() => {
    if (!hubHandle || !scenarioSlug) {
      setHubBenchmarkRanks([]);
      setHubBenchmarkPages([]);
      return;
    }
    let cancelled = false;
    setHubBenchmarkLoading(true);
    void (async () => {
      try {
        type ScenarioHistoryResp = { benchmarkRanks: HubScenarioBenchmarkRank[] };
        const history = await invoke<ScenarioHistoryResp>("hub_get_player_scenario_history", {
          handle: hubHandle,
          scenarioSlug,
        });
        if (cancelled) return;
        const ranks = history.benchmarkRanks ?? [];
        setHubBenchmarkRanks(ranks);
        if (ranks.length > 0) {
          const pages = await Promise.all(
            ranks.map((rank) =>
              invoke<HubBenchmarkPageResponse>("hub_get_benchmark_page", {
                handle: hubHandle,
                benchmarkId: rank.benchmarkId,
              }).catch(() => null),
            ),
          );
          if (!cancelled) {
            setHubBenchmarkPages(pages.filter((p): p is HubBenchmarkPageResponse => p !== null));
          }
        }
      } catch {
        if (!cancelled) {
          setHubBenchmarkRanks([]);
          setHubBenchmarkPages([]);
        }
      } finally {
        if (!cancelled) setHubBenchmarkLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [hubHandle, scenarioSlug]);
  const analysisRecords = useMemo(
    () => records.filter((record) => record.isReliableForAnalysis),
    [records],
  );

  const sorted = useMemo(
    () =>
      [...analysisRecords].sort((a, b) => {
        const da = parseTimestamp(a.timestamp)?.getTime() ?? 0;
        const db = parseTimestamp(b.timestamp)?.getTime() ?? 0;
        return da - db;
      }),
    [analysisRecords],
  );

  const rawSorted = useMemo(
    () =>
      [...records].sort((a, b) => {
        const da = parseTimestamp(a.timestamp)?.getTime() ?? 0;
        const db = parseTimestamp(b.timestamp)?.getTime() ?? 0;
        return da - db;
      }),
    [records],
  );

  const warmupIds = useMemo(() => classifyWarmup(sorted), [sorted]);
  const hasWarmup = warmupIds.size > 0;

  const filteredRecords = useMemo(() => {
    if (sessionFilter === "warmup")   return analysisRecords.filter((r) => warmupIds.has(r.id));
    if (sessionFilter === "warmedup") return analysisRecords.filter((r) => !warmupIds.has(r.id));
    return analysisRecords;
  }, [analysisRecords, warmupIds, sessionFilter]);

  const filteredSorted = useMemo(() => {
    if (sessionFilter === "warmup")   return sorted.filter((r) => warmupIds.has(r.id));
    if (sessionFilter === "warmedup") return sorted.filter((r) => !warmupIds.has(r.id));
    return sorted;
  }, [sorted, warmupIds, sessionFilter]);

  const replayFilteredRecords = useMemo(() => {
    if (sessionFilter === "warmup") return records.filter((r) => warmupIds.has(r.id));
    if (sessionFilter === "warmedup") return records.filter((r) => !warmupIds.has(r.id));
    return records;
  }, [records, warmupIds, sessionFilter]);

  const replayFilteredSorted = useMemo(() => {
    if (sessionFilter === "warmup") return rawSorted.filter((r) => warmupIds.has(r.id));
    if (sessionFilter === "warmedup") return rawSorted.filter((r) => !warmupIds.has(r.id));
    return rawSorted;
  }, [rawSorted, warmupIds, sessionFilter]);

  const best = Math.max(...filteredRecords.map((r) => r.score), 0);

  const benchmarkThresholdLines = useMemo<BenchmarkThresholdLine[]>(() => {
    const lines: BenchmarkThresholdLine[] = [];
    const seen = new Set<number>();
    for (const page of hubBenchmarkPages) {
      for (const cat of page.categories) {
        const entry = cat.scenarios.find(
          (s) => s.scenarioName.toLowerCase() === scenarioName.toLowerCase(),
        );
        if (entry) {
          for (const [index, t] of entry.thresholds.entries()) {
            if (!seen.has(t.score)) {
              seen.add(t.score);
              lines.push({
                score: t.score,
                rankName: t.rankName,
                color: resolveBenchmarkColor(t.color, t.rankName, index),
                benchmarkName: page.benchmarkName,
              });
            }
          }
        }
      }
    }
    return lines.sort((a, b) => a.score - b.score);
  }, [hubBenchmarkPages, scenarioName]);

  const tabs: { id: Tab; label: string; hidden?: boolean }[] = [
    { id: "summary", label: "Summary" },
    { id: "mechanics", label: "Mechanics" },
    { id: "coaching", label: "Coaching" },
    { id: "replay", label: "Replay" },
    { id: "leaderboard", label: "Leaderboard" },
    { id: "benchmarks", label: "Benchmarks", hidden: !hubHandle },
  ];

  useEffect(() => {
    if (tabs.some((tab) => !tab.hidden && tab.id === activeTab)) {
      return;
    }
    setActiveTab("summary");
  }, [activeTab, tabs]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.scenarioTab, activeTab);
  }, [activeTab, scenarioName]);

  useEffect(() => {
    if (!hasWarmup && sessionFilter !== "all") {
      setSessionFilter("all");
    }
  }, [hasWarmup, sessionFilter]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.sessionFilter, sessionFilter);
  }, [sessionFilter, scenarioName]);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      {analysisRecords.length === 0 ? (
        <div style={{ ...CHART_STYLE, color: "rgba(255,255,255,0.45)", lineHeight: 1.7 }}>
          All recorded runs for this scenario look incomplete or malformed, so trend and coaching views are hidden until a clean run is recorded.
        </div>
      ) : (
        <>
      {/* Tab bar + session filter toggle */}
      <div
        style={{
          display: "flex",
          alignItems: "flex-end",
          gap: 2,
          borderBottom: `1px solid ${C.border}`,
        }}
      >
        {tabs.map((t) => {
          if (t.hidden) return null;
          const active = activeTab === t.id;
          return (
            <button
              key={t.id}
              onClick={() => setActiveTab(t.id)}
              style={{
                background: "none",
                border: "none",
                borderBottom: active ? `2px solid ${C.accent}` : "2px solid transparent",
                padding: "8px 14px",
                marginBottom: -1,
                cursor: "pointer",
                color: active ? C.text : C.textMuted,
                fontFamily: "inherit",
                fontSize: 12,
                fontWeight: active ? 700 : 500,
                transition: "color 0.12s",
                letterSpacing: "0.01em",
              }}
            >
              {t.label}
            </button>
          );
        })}

        {/* Session filter — only shown when warmup sessions have been detected */}
        {hasWarmup && (
          <div
            style={{
              display: "flex",
              gap: 4,
              marginLeft: "auto",
              marginBottom: 6,
              alignItems: "center",
            }}
          >
            <span style={{ fontSize: 9, color: C.textFaint, marginRight: 2, textTransform: "uppercase", letterSpacing: "0.1em", fontWeight: 600 }}>
              View
            </span>
            {(["all", "warmup", "warmedup"] as SessionFilter[]).map((f) => {
              const active = sessionFilter === f;
              const baseColor = f === "warmup" ? C.warn : C.accent;
              return (
                <button
                  key={f}
                  onClick={() => setSessionFilter(f)}
                  style={{
                    background: active ? `${baseColor}15` : "transparent",
                    border: active ? `1px solid ${baseColor}40` : `1px solid ${C.border}`,
                    borderRadius: 6,
                    padding: "3px 9px",
                    fontSize: 10,
                    color: active ? baseColor : C.textMuted,
                    cursor: "pointer",
                    fontFamily: "inherit",
                    fontWeight: active ? 700 : 500,
                    transition: "all 0.1s",
                    letterSpacing: "0.03em",
                  }}
                >
                  {f === "all" ? "All" : f === "warmup" ? "Warm-up" : "Warmed-up"}
                </button>
              );
            })}
          </div>
        )}
      </div>

      {activeTab === "summary" && (
        <OverviewTab
          records={filteredRecords}
          sorted={filteredSorted}
          best={best}
          warmupIds={warmupIds}
          benchmarkRanks={hubBenchmarkRanks}
          thresholdLines={benchmarkThresholdLines}
          onJumpToReplay={(sessionId) => {
            setReplayJumpId(sessionId);
            setActiveTab("replay");
          }}
        />
      )}
      {activeTab === "mechanics" && (
        <MechanicsTab records={filteredRecords} sorted={filteredSorted} />
      )}
      {activeTab === "coaching" && (
        <CoachingTab
          records={records}
          sorted={sorted}
          warmupIds={warmupIds}
          scenarioName={scenarioName}
          dateRange={dateRange}
          sessionFilter={sessionFilter}
          onExploreDrill={onExploreDrill}
          feedbackRows={feedbackRows}
          onFeedback={onFeedback}
          appSettings={appSettings}
          globalSummary={globalSummary}
        />
      )}
      {activeTab === "replay" && (
        <ReplayTab
          records={replayFilteredRecords}
          sorted={replayFilteredSorted}
          warmupIds={warmupIds}
          requestedSelectedId={replayJumpId}
          onRequestedSelectedIdHandled={() => setReplayJumpId(null)}
          onReplayMetadataChanged={onReplayMetadataChanged}
        />
      )}
      {activeTab === "leaderboard" && <ScenarioLeaderboardPanel scenarioName={scenarioName} />}
      {activeTab === "benchmarks" && (
        <BenchmarksTab
          scenarioName={scenarioName}
          sorted={filteredSorted}
          best={best}
          benchmarkRanks={hubBenchmarkRanks}
          benchmarkPages={hubBenchmarkPages}
          loading={hubBenchmarkLoading}
          hubHandle={hubHandle}
        />
      )}
        </>
      )}
    </div>
  );
}

// ─── Root ─────────────────────────────────────────────────────────────────────

type RootMode = "sessions" | "hub" | "replays" | "leaderboards" | "settings" | "debug";
type ActiveGame = "kovaaks" | "osu";
type ScenarioSortMode = "recent" | "plays" | "type";
type SessionsPaneMode = "overview" | "scenario";

export function StatsWindow({ embedded }: { embedded?: boolean } = {}) {
  useAppTheme();
  const { status: updateStatus, checkForUpdate, installUpdate } = useUpdater();
  const [activeGame, setActiveGame] = useState<ActiveGame>(() =>
    window.localStorage.getItem("aimmod.activeGame") === "osu" ? "osu" : "kovaaks",
  );
  const [records, setRecords] = useState<SessionRecord[]>([]);
  const [search, setSearch] = useState(() => readStoredValue(STATS_WINDOW_STORAGE_KEYS.search) ?? "");
  const [selectedScenario, setSelectedScenario] = useState<string | null>(
    () => readStoredValue(STATS_WINDOW_STORAGE_KEYS.selectedScenario),
  );
  const [loading, setLoading] = useState(true);
  const [loadingMessage, setLoadingMessage] = useState<string>("Loading history…");
  const [confirmClear, setConfirmClear] = useState(false);
  const [importingHistory, setImportingHistory] = useState(false);
  const [importStatus, setImportStatus] = useState<string | null>(null);
  const [rootMode, setRootMode] = useState<RootMode>(() => {
    const stored = readStoredValue(STATS_WINDOW_STORAGE_KEYS.rootMode);
    return stored === "hub" || stored === "replays" || stored === "leaderboards" || stored === "settings" || stored === "debug"
      ? stored
      : "sessions";
  });
  const [isDebugBuild, setIsDebugBuild] = useState(false);
  const [scenarioSort, setScenarioSort] = useState<ScenarioSortMode>(() => {
    const stored = readStoredValue(STATS_WINDOW_STORAGE_KEYS.scenarioSort);
    return stored === "plays" || stored === "type" ? stored : "recent";
  });
  const [dateRange, setDateRange] = useState<DateRangePreset>(() => {
    const stored = readStoredValue(STATS_WINDOW_STORAGE_KEYS.dateRange);
    return stored === "30d" || stored === "90d" || stored === "365d" ? stored : "all";
  });
  const [compareScenario, setCompareScenario] = useState<string | null>(
    () => readStoredValue(STATS_WINDOW_STORAGE_KEYS.compareScenario),
  );
  const [sessionsPane, setSessionsPane] = useState<SessionsPaneMode>(() => {
    const stored = readStoredValue(STATS_WINDOW_STORAGE_KEYS.sessionsPane);
    return stored === "scenario" ? "scenario" : "overview";
  });
  const [helpOpen, setHelpOpen] = useState(false);
  const [pendingClear, setPendingClear] = useState<{ records: SessionRecord[]; selectedScenario: string | null } | null>(null);
  const [leaderboardSeedQuery, setLeaderboardSeedQuery] = useState<string | null>(null);
  const [liveBridgeStats, setLiveBridgeStats] = useState<Record<string, number>>({});
  const [liveBridgeEventCounts, setLiveBridgeEventCounts] = useState<Record<string, number>>({});
  const [hubSyncOverview, setHubSyncOverview] = useState<StatsHubSyncOverview | null>(null);
  const [appSettings, setAppSettings] = useState<AppSettings | null>(null);
  const [globalCoachingOverview, setGlobalCoachingOverview] = useState<GlobalCoachingOverview | null>(null);
  const [coachingUserFeedback, setCoachingUserFeedback] = useState<CoachingUserFeedbackRecord[]>([]);
  const [coachingOverviewVersion, setCoachingOverviewVersion] = useState(0);
  const [hubNoticeDismissed, setHubNoticeDismissed] = useState<boolean>(
    () => readStoredValue(STATS_WINDOW_STORAGE_KEYS.hubNoticeDismissed) === "1",
  );
  const [dismissedUpdateVersion, setDismissedUpdateVersion] = useState<string | null>(
    () => readStoredValue(STATS_WINDOW_STORAGE_KEYS.updateNoticeDismissedVersion),
  );
  const [availableUpdate, setAvailableUpdate] = useState<Update | null>(null);
  const [hubResyncBusy, setHubResyncBusy] = useState(false);
  const updateCheckStartedRef = useRef(false);

  useEffect(() => {
    window.localStorage.setItem("aimmod.activeGame", activeGame);
  }, [activeGame]);

  // Always-current ref prevents stale closure in event listener
  const selectedRef = useRef<string | null>(null);
  selectedRef.current = selectedScenario;
  const searchInputRef = useRef<HTMLInputElement>(null);
  const clearTimerRef = useRef<number | null>(null);
  const visibleRecords = useMemo(
    () => records.filter((record) => withinDateRange(record.timestamp, dateRange)),
    [dateRange, records],
  );
  const analyticsRecords = useMemo(() => buildAnalyticsRecords(visibleRecords), [visibleRecords]);
  const replayHubRecords = useMemo(
    () => [...analyticsRecords].sort((a, b) => a.timestampMs - b.timestampMs),
    [analyticsRecords],
  );
  const flaggedRecordCount = useMemo(
    () => analyticsRecords.filter((record) => !record.isReliableForAnalysis).length,
    [analyticsRecords],
  );
  const totalPlaySeconds = useMemo(
    () => visibleRecords.reduce((sum, r) => sum + (r.duration_secs ?? 0), 0),
    [visibleRecords],
  );

  // "/" shortcut to focus search
  useEffect(() => {
    if (activeGame !== "kovaaks") return;

    const handler = (e: KeyboardEvent) => {
      const tagName = (e.target as HTMLElement)?.tagName ?? "";
      const inField = ["INPUT", "TEXTAREA", "SELECT"].includes(tagName);
      if (!inField && e.shiftKey && e.key === "?") {
        e.preventDefault();
        setHelpOpen(true);
        return;
      }
      if (e.key === "Escape") {
        setHelpOpen(false);
        return;
      }
      if (e.key === "/" && !e.shiftKey && !inField) {
        e.preventDefault();
        searchInputRef.current?.focus();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [activeGame]);

  async function loadHistory(preserveSelection: boolean) {
    setLoading(true);
    setLoadingMessage("Loading history…");
    try {
      const data: SessionRecord[] = [];
      let offset = 0;
      let total = 0;

      while (true) {
        const page = await invoke<SessionHistoryPage>("get_session_history_page", {
          offset,
          limit: HISTORY_PAGE_SIZE,
        });
        if (offset === 0) {
          total = page.total;
        }

        data.push(...page.records);

        if (!page.has_more || page.records.length === 0) {
          break;
        }

        offset += page.records.length;
        setLoadingMessage(`Loading history… ${Math.min(offset, total)} / ${total}`);
      }

      setRecords(data);
      const currentSelection = selectedRef.current;
      if (currentSelection) {
        const exists = data.some((record) => normalizeScenario(record.scenario) === currentSelection);
        if (exists) {
          setSelectedScenario(currentSelection);
          return;
        }
      }

      if (data.length > 0) {
        const latest = data.reduce((a, b) =>
          (parseTimestamp(b.timestamp)?.getTime() ?? 0) >
          (parseTimestamp(a.timestamp)?.getTime() ?? 0)
            ? b
            : a,
        );
        setSelectedScenario(normalizeScenario(latest.scenario));
      } else if (!preserveSelection) {
        setSelectedScenario(null);
      }
    } catch (e) {
      console.error(e);
    } finally {
      setLoadingMessage("Loading history…");
      setLoading(false);
    }
  }

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.search, search.trim() ? search : null);
  }, [search]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.selectedScenario, selectedScenario);
  }, [selectedScenario]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.rootMode, rootMode);
  }, [rootMode]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.scenarioSort, scenarioSort);
  }, [scenarioSort]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.dateRange, dateRange === "all" ? null : dateRange);
  }, [dateRange]);

  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.compareScenario, compareScenario);
  }, [compareScenario]);
  useEffect(() => {
    writeStoredValue(STATS_WINDOW_STORAGE_KEYS.sessionsPane, sessionsPane);
  }, [sessionsPane]);
  useEffect(() => {
    writeStoredValue(
      STATS_WINDOW_STORAGE_KEYS.hubNoticeDismissed,
      hubNoticeDismissed ? "1" : null,
    );
  }, [hubNoticeDismissed]);
  useEffect(() => {
    writeStoredValue(
      STATS_WINDOW_STORAGE_KEYS.updateNoticeDismissedVersion,
      dismissedUpdateVersion,
    );
  }, [dismissedUpdateVersion]);

  useEffect(() => {
    if (updateCheckStartedRef.current) return;
    updateCheckStartedRef.current = true;
    void checkForUpdate();
  }, [checkForUpdate]);

  useEffect(() => {
    if (updateStatus.state === "available") {
      setAvailableUpdate(updateStatus.update);
      return;
    }
    if (updateStatus.state === "up-to-date") {
      setAvailableUpdate(null);
    }
  }, [updateStatus]);

  useEffect(() => {
    loadHistory(false);
    invoke<boolean>("get_is_debug_build")
      .then(setIsDebugBuild)
      .catch(() => setIsDebugBuild(false));
    invoke<CoachingUserFeedbackRecord[]>("get_coaching_user_feedback", {})
      .then(setCoachingUserFeedback)
      .catch(() => setCoachingUserFeedback([]));
    const refreshAppSettings = () => {
      invoke<AppSettings>("get_settings")
        .then(setAppSettings)
        .catch(() => setAppSettings(null));
    };
    refreshAppSettings();
    let lastBridgeRefresh = 0;
    const maybeRefreshHistory = (force = false) => {
      const now = Date.now();
      if (!force && now - lastBridgeRefresh < 1500) return;
      lastBridgeRefresh = now;
      loadHistory(true);
    };

    // Refresh when a session is finalized and persisted.
    const unlistenComplete = listen("session-complete", () => {
      maybeRefreshHistory(true);
    });

    // Fallback when bridge signals completion but file-watcher timing varies.
    const unlistenBridgeParsed = listen<BridgeParsedEvent>("bridge-metric", (event) => {
      const ev = String(event.payload?.ev ?? "");
      if (
        ev === "challenge_complete" ||
        ev === "post_challenge_complete" ||
        ev === "challenge_quit" ||
        ev === "challenge_canceled"
      ) {
        maybeRefreshHistory(true);
      }
    });

    // Keep a tiny live snapshot so Session Stats isn't empty while no run is persisted yet.
    const unlistenStatsPanel = listen<StatsPanelReading>("stats-panel-update", (event) => {
      const payload = event.payload;
      setLiveBridgeStats((prev) => {
        const next = {
          ...prev,
          pull_shots_fired_total: payload.accuracy_shots ?? prev.pull_shots_fired_total,
          pull_shots_hit_total: payload.accuracy_hits ?? prev.pull_shots_hit_total,
          pull_kills_total: payload.kills ?? prev.pull_kills_total,
          pull_score_per_minute: payload.spm ?? prev.pull_score_per_minute,
          pull_score_total_derived: payload.score_total_derived ?? prev.pull_score_total_derived,
          pull_score_total: payload.score_total ?? prev.pull_score_total,
          pull_damage_done: payload.damage_dealt ?? prev.pull_damage_done,
          pull_damage_possible: payload.damage_total ?? prev.pull_damage_possible,
          pull_damage_efficiency:
            payload.damage_dealt != null
            && payload.damage_total != null
            && payload.damage_total > 0
              ? payload.damage_dealt / payload.damage_total
              : prev.pull_damage_efficiency,
          pull_kills_per_second: payload.kps ?? prev.pull_kills_per_second,
          pull_seconds_total: payload.session_time_secs ?? prev.pull_seconds_total,
        };
        return next;
      });
    });

    const unlistenBridgeMetric = listen<BridgeParsedEvent>("bridge-metric", (event) => {
      const ev = String(event.payload?.ev ?? "");
      const delta = event.payload?.delta;
      if (!ev) return;

      if (
        ev === "shot_fired" ||
        ev === "shot_hit" ||
        ev === "kill" ||
        ev === "challenge_queued" ||
        ev === "challenge_start" ||
        ev === "challenge_end" ||
        ev === "challenge_complete" ||
        ev === "challenge_completed" ||
        ev === "challenge_canceled" ||
        ev === "scenario_start" ||
        ev === "scenario_end"
      ) {
        const inc =
          typeof delta === "number" && Number.isFinite(delta)
            ? Math.max(1, Math.round(delta))
            : 1;
        setLiveBridgeEventCounts((prev) => ({ ...prev, [ev]: (prev[ev] ?? 0) + inc }));
      }
    });

    const refreshHubSyncOverview = () => {
      invoke<StatsHubSyncOverview>("get_hub_sync_status")
        .then(setHubSyncOverview)
        .catch(() => setHubSyncOverview(null));
    };
    refreshHubSyncOverview();
    const hubStatusInterval = window.setInterval(refreshHubSyncOverview, 5_000);
    const unlistenHubSyncStatus = listen<StatsHubSyncOverview>("hub-sync-status", (event) => {
      setHubSyncOverview(event.payload);
    });
    const unlistenSettingsChanged = listen("settings-changed", () => {
      refreshAppSettings();
      refreshHubSyncOverview();
    });

    return () => {
      unlistenComplete.then((fn) => fn());
      unlistenBridgeParsed.then((fn) => fn());
      unlistenStatsPanel.then((fn) => fn());
      unlistenBridgeMetric.then((fn) => fn());
      unlistenHubSyncStatus.then((fn) => fn());
      unlistenSettingsChanged.then((fn) => fn());
      window.clearInterval(hubStatusInterval);
    };
  }, []);

  useEffect(() => {
    if (!isDebugBuild && rootMode === "debug") {
      setRootMode("sessions");
    }
  }, [isDebugBuild, rootMode]);

  async function handleClear() {
    if (pendingClear) return;
    if (!confirmClear) {
      setConfirmClear(true);
      return;
    }
    setConfirmClear(false);
    setPendingClear({ records, selectedScenario });
    setRecords([]);
    setSelectedScenario(null);
    if (clearTimerRef.current != null) window.clearTimeout(clearTimerRef.current);
    clearTimerRef.current = window.setTimeout(() => {
      void invoke("clear_session_history")
        .then(() => setImportStatus("Session history cleared."))
        .catch((error) => {
          setImportStatus(String(error));
          setPendingClear((snapshot) => {
            if (snapshot) {
              setRecords(snapshot.records);
              setSelectedScenario(snapshot.selectedScenario);
            }
            return null;
          });
        })
        .finally(() => {
          clearTimerRef.current = null;
          setPendingClear(null);
        });
    }, 5000);
  }

  function handleUndoClear() {
    if (!pendingClear) return;
    if (clearTimerRef.current != null) {
      window.clearTimeout(clearTimerRef.current);
      clearTimerRef.current = null;
    }
    setRecords(pendingClear.records);
    setSelectedScenario(pendingClear.selectedScenario);
    setPendingClear(null);
    setImportStatus("Clear canceled.");
  }

  async function handleImportHistory() {
    setImportingHistory(true);
    setImportStatus(null);
    try {
      const result = await invoke<SessionCsvImportSummary>("import_session_csv_history");
      const summary = [
        result.imported > 0
          ? `Imported ${result.imported} run${result.imported !== 1 ? "s" : ""}`
          : "No new runs imported",
        result.skipped_existing > 0
          ? `${result.skipped_existing} already present`
          : null,
        result.failed > 0 ? `${result.failed} failed to parse` : null,
      ]
        .filter(Boolean)
        .join(" • ");
      setImportStatus(summary || `Scanned ${result.scanned} CSV files.`);
      await loadHistory(true);
    } catch (error) {
      setImportStatus(String(error));
    } finally {
      setImportingHistory(false);
    }
  }

  async function handleForceHubResync() {
    setHubResyncBusy(true);
    try {
      await invoke("hub_force_full_resync");
      const refreshed = await invoke<StatsHubSyncOverview>("get_hub_sync_status");
      setHubSyncOverview(refreshed);
      setRootMode("hub");
    } catch (error) {
      console.error(error);
    } finally {
      setHubResyncBusy(false);
    }
  }

  async function handleCoachingFeedback(
    snapshotKind: string,
    card: CoachingCardData,
    feedback: CoachingCardFeedback,
  ) {
    const now = Date.now();
    const recommendationId = coachingCardFeedbackId(card);
    const signalKeys = coachingCardSignalKeys(card);
    const existing = coachingUserFeedback.find(
      (row) => row.snapshotKind === snapshotKind && row.recommendationId === recommendationId,
    );
    const nextRow: CoachingUserFeedbackRecord = {
      snapshotKind,
      recommendationId,
      signalKey: signalKeys[0] ?? null,
      feedback,
      notes: existing?.notes ?? null,
      createdAtUnixMs: existing?.createdAtUnixMs ?? now,
      updatedAtUnixMs: now,
      contextJson: {
        title: card.title,
        badge: card.badge,
        signals: signalKeys,
      },
    };

    setCoachingUserFeedback((prev) => {
      const remaining = prev.filter(
        (row) => !(row.snapshotKind === snapshotKind && row.recommendationId === recommendationId),
      );
      return [nextRow, ...remaining].sort((a, b) => b.updatedAtUnixMs - a.updatedAtUnixMs);
    });

    try {
      await invoke("save_coaching_user_feedback", { feedback: nextRow });
      setCoachingOverviewVersion((value) => value + 1);
    } catch (error) {
      console.warn("Could not save coaching feedback", error);
      setCoachingUserFeedback((prev) => {
        const restored = existing
          ? [existing, ...prev.filter(
            (row) => !(row.snapshotKind === snapshotKind && row.recommendationId === recommendationId),
          )]
          : prev.filter(
            (row) => !(row.snapshotKind === snapshotKind && row.recommendationId === recommendationId),
          );
        return restored.sort((a, b) => b.updatedAtUnixMs - a.updatedAtUnixMs);
      });
    }
  }

  useEffect(() => () => {
    if (clearTimerRef.current != null) {
      window.clearTimeout(clearTimerRef.current);
    }
  }, []);

  const scenarioGroups = useMemo(() => {
    const q = search.toLowerCase();
    const map = new Map<string, {
      bestReliable: number | null;
      bestAny: number;
      count: number;
      reliableCount: number;
      flaggedCount: number;
      lastTs: string;
      scenarioType: string | null;
      scenarioTypeCounts: Map<string, number>;
      // {score, tsMs} pairs for reliable sessions — used for trend
      reliableSessions: Array<{ score: number; tsMs: number }>;
    }>();
    for (const r of analyticsRecords) {
      const name = r.normalizedScenario;
      if (q && !name.toLowerCase().includes(q)) continue;
      const cur = map.get(name);
      const curTs = cur?.lastTs ?? "";
      const isNewer =
        (parseTimestamp(r.timestamp)?.getTime() ?? 0) >
        (parseTimestamp(curTs)?.getTime() ?? 0);
      const rawScenarioType = r.stats_panel?.scenario_type?.trim() || null;
      const scenarioType = rawScenarioType && rawScenarioType !== "Unknown" ? rawScenarioType : null;
      const scenarioTypeCounts = new Map(cur?.scenarioTypeCounts ?? []);
      if (scenarioType) {
        scenarioTypeCounts.set(scenarioType, (scenarioTypeCounts.get(scenarioType) ?? 0) + 1);
      }
      const dominantScenarioType =
        [...scenarioTypeCounts.entries()]
          .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))[0]?.[0]
        ?? null;
      const reliableSessions = cur?.reliableSessions ?? [];
      if (r.isReliableForAnalysis) {
        reliableSessions.push({ score: r.score, tsMs: parseTimestamp(r.timestamp)?.getTime() ?? 0 });
      }
      map.set(name, {
        bestReliable: r.isReliableForAnalysis
          ? Math.max(cur?.bestReliable ?? 0, r.score)
          : (cur?.bestReliable ?? null),
        bestAny: Math.max(cur?.bestAny ?? 0, r.score),
        count: (cur?.count ?? 0) + 1,
        reliableCount: (cur?.reliableCount ?? 0) + (r.isReliableForAnalysis ? 1 : 0),
        flaggedCount: (cur?.flaggedCount ?? 0) + (r.isReliableForAnalysis ? 0 : 1),
        lastTs: isNewer ? r.timestamp : curTs,
        scenarioType: (isNewer && scenarioType) ? scenarioType : (cur?.scenarioType ?? dominantScenarioType),
        scenarioTypeCounts,
        reliableSessions,
      });
    }
    return [...map.entries()]
      .map(([name, s]) => {
        // Compute trend: compare avg of last 3 reliable sessions vs sessions 4–6
        const sorted = [...s.reliableSessions].sort((a, b) => a.tsMs - b.tsMs);
        let trend: "up" | "down" | "flat" | null = null;
        if (sorted.length >= 6) {
          const recent = sorted.slice(-3).map((x) => x.score);
          const older  = sorted.slice(-6, -3).map((x) => x.score);
          const recentAvg = mean(recent);
          const olderAvg  = mean(older);
          const delta = (recentAvg - olderAvg) / (olderAvg || 1);
          trend = delta > 0.02 ? "up" : delta < -0.02 ? "down" : "flat";
        }
        return {
          name,
          bestReliable: s.bestReliable,
          bestAny: s.bestAny,
          count: s.count,
          reliableCount: s.reliableCount,
          flaggedCount: s.flaggedCount,
          lastTs: s.lastTs,
          trend,
          scenarioType:
            [...s.scenarioTypeCounts.entries()]
              .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))[0]?.[0]
            ?? s.scenarioType
            ?? "Unknown",
        };
      })
      .sort((a, b) => {
        if (scenarioSort === "plays") {
          return b.count - a.count
            || (parseTimestamp(b.lastTs)?.getTime() ?? 0) - (parseTimestamp(a.lastTs)?.getTime() ?? 0)
            || a.name.localeCompare(b.name);
        }
        if (scenarioSort === "type") {
          return scenarioTypeSortRank(a.scenarioType).localeCompare(scenarioTypeSortRank(b.scenarioType))
            || b.count - a.count
            || a.name.localeCompare(b.name);
        }
        return (parseTimestamp(b.lastTs)?.getTime() ?? 0) -
          (parseTimestamp(a.lastTs)?.getTime() ?? 0)
          || b.count - a.count
          || a.name.localeCompare(b.name);
      });
  }, [analyticsRecords, search, scenarioSort]);

  const selectedRecords = useMemo(
    () => analyticsRecords.filter((r) => r.normalizedScenario === selectedScenario),
    [analyticsRecords, selectedScenario],
  );

  const selectedGroup = useMemo(
    () => scenarioGroups.find((g) => g.name === selectedScenario) ?? null,
    [scenarioGroups, selectedScenario],
  );
  const compareRecords = useMemo(
    () => analyticsRecords.filter((record) => record.normalizedScenario === compareScenario),
    [analyticsRecords, compareScenario],
  );
  const compareSummary = useMemo(
    () => summarizeScenario(compareRecords),
    [compareRecords],
  );
  const selectedSummary = useMemo(
    () => summarizeScenario(selectedRecords),
    [selectedRecords],
  );
  useEffect(() => {
    let cancelled = false;

    void invoke<GlobalCoachingOverview>("get_global_coaching_overview", {
      dateRange: dateRange === "all" ? null : dateRange,
    })
      .then((overview) => {
        if (!cancelled) {
          setGlobalCoachingOverview(overview);
        }
      })
      .catch((error) => {
        console.warn("Could not load Rust coaching overview", error);
        if (!cancelled) {
          setGlobalCoachingOverview(null);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [dateRange, records, appSettings, coachingOverviewVersion]);

  const globalPracticeProfile = globalCoachingOverview?.practiceProfile ?? null;
  const globalLearningState = globalCoachingOverview?.learningState ?? null;
  const globalCoachingCards = globalCoachingOverview?.globalCards ?? [];
  const playerLearningProfile = globalCoachingOverview?.playerLearningProfile ?? null;
  const coachingPersistenceStatus = globalCoachingOverview?.coachingPersistenceStatus ?? null;

  useEffect(() => {
    if (selectedScenario && scenarioGroups.some((group) => group.name === selectedScenario)) return;
    if (scenarioGroups.length > 0) {
      setSelectedScenario(scenarioGroups[0].name);
      return;
    }
    setSelectedScenario(null);
  }, [scenarioGroups, selectedScenario]);

  useEffect(() => {
    if (!compareScenario) return;
    if (compareScenario === selectedScenario || !scenarioGroups.some((group) => group.name === compareScenario)) {
      setCompareScenario(null);
    }
  }, [compareScenario, scenarioGroups, selectedScenario]);

  const showHubLinkNotice = useMemo(() => {
    if (hubNoticeDismissed) return false;
    if (!hubSyncOverview) return false;
    const accountLabel = hubSyncOverview.accountLabel?.trim() ?? "";
    const linked =
      accountLabel.length > 0
      || hubSyncOverview.configured
      || hubSyncOverview.enabled;
    return !linked;
  }, [hubNoticeDismissed, hubSyncOverview]);
  const updateNoticeVersion = availableUpdate?.version ?? null;
  const showUpdateNotice = useMemo(() => {
    if (!updateNoticeVersion) return false;
    if (dismissedUpdateVersion === updateNoticeVersion) return false;
    return updateStatus.state === "available"
      || updateStatus.state === "downloading"
      || updateStatus.state === "ready"
      || updateStatus.state === "error";
  }, [dismissedUpdateVersion, updateNoticeVersion, updateStatus.state]);

  return (
    <div
      style={{
        position: "relative",
        display: "flex",
        flexDirection: "column",
        height: embedded ? "100%" : "100vh",
        background: C.bg,
        color: C.text,
        fontFamily: "'JetBrains Mono', monospace",
        fontSize: 13,
        overflow: "hidden",
      }}
    >
      {/* ── Mode tab bar ── */}
      <div
        style={{
          display: "flex",
          gap: 0,
          borderBottom: `1px solid ${C.border}`,
          padding: "0 20px",
          background: "rgba(255,255,255,0.018)",
          flexShrink: 0,
        }}
      >
        {(["kovaaks", "osu"] as ActiveGame[]).map((game) => {
          const active = activeGame === game;
          return (
            <button
              key={game}
              type="button"
              aria-pressed={active}
              onClick={() => setActiveGame(game)}
              style={{
                alignItems: "center",
                alignSelf: "center",
                background: active
                  ? game === "osu" ? "rgba(255,102,170,0.13)" : "rgba(var(--am-accent-rgb), 0.13)"
                  : "transparent",
                border: `1px solid ${active ? game === "osu" ? "rgba(255,102,170,0.45)" : C.accentBorder : C.border}`,
                borderRadius: 7,
                color: active ? C.text : C.textMuted,
                cursor: "pointer",
                display: "inline-flex",
                fontFamily: "inherit",
                fontSize: 10,
                fontWeight: active ? 750 : 550,
                marginRight: 7,
                padding: "6px 10px",
              }}
            >
              {game === "kovaaks" ? "Kovaak's" : "osu!lazer"}
            </button>
          );
        })}
        <div style={{ alignSelf: "stretch", background: C.border, margin: "8px 10px 8px 5px", width: 1 }} />
        {activeGame === "kovaaks" && ([
          "sessions",
          "hub",
          "replays",
          "leaderboards",
          "settings",
          ...(isDebugBuild ? ["debug"] : []),
        ] as RootMode[]).map((m) => {
          const active = rootMode === m;
          return (
            <button
              key={m}
              onClick={() => setRootMode(m)}
              style={{
                background: "none",
                border: "none",
                borderBottom: active ? `2px solid ${C.accent}` : "2px solid transparent",
                padding: "12px 18px",
                marginBottom: -1,
                cursor: "pointer",
                color: active ? C.text : C.textMuted,
                fontFamily: "inherit",
                fontSize: 12,
                fontWeight: active ? 700 : 500,
                letterSpacing: "0.02em",
                transition: "color 0.15s",
              }}
            >
              {m === "sessions"
                ? "Session Stats"
                : m === "hub"
                  ? "AimMod Hub"
                : m === "replays"
                  ? "Replay Hub"
                  : m === "leaderboards"
                  ? "Leaderboards"
                  : m === "settings"
                    ? "Settings"
                    : "Debug"}
            </button>
          );
        })}
        {activeGame === "osu" && (
          <div style={{ alignItems: "center", color: C.textMuted, display: "flex", fontSize: 10, padding: "0 8px" }}>
            osu!lazer workspace
          </div>
        )}
        {activeGame === "kovaaks" && (
          <button
            type="button"
            onClick={() => setHelpOpen(true)}
            style={{
              marginLeft: "auto",
              background: "none",
              border: "none",
              color: C.textFaint,
              cursor: "pointer",
              fontFamily: "inherit",
              fontSize: 12,
              padding: "12px 0 12px 18px",
            }}
            title="Shortcuts (?)"
          >
            ? Shortcuts
          </button>
        )}
      </div>

      {activeGame === "osu" && <OsuWorkspace />}

      {/* ── Sessions content ── */}
      {activeGame === "kovaaks" && rootMode === "sessions" && (
        <div style={{ display: "flex", flex: 1, flexDirection: "column", overflow: "hidden" }}>
          {showUpdateNotice && (
            <div
              style={{
                padding: "12px 16px",
                borderBottom: `1px solid ${C.border}`,
                background: "rgba(255,255,255,0.018)",
                flexShrink: 0,
              }}
            >
              <div
                style={{
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "space-between",
                  gap: 12,
                  padding: "12px 14px",
                  borderRadius: 12,
                  border: "1px solid rgba(255,159,67,0.24)",
                  background: "linear-gradient(135deg, rgba(255,159,67,0.12), rgba(255,159,67,0.05))",
                  boxShadow: "0 8px 24px rgba(0,0,0,0.22)",
                }}
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                  <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
                    <Badge color="#ff9f43">Update Available</Badge>
                    <span style={{ fontSize: 13, fontWeight: 700, color: C.text }}>
                      {updateNoticeVersion ? `AimMod ${updateNoticeVersion} is ready to install.` : "A new AimMod update is available."}
                    </span>
                  </div>
                  <span style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.5 }}>
                    {updateStatus.state === "available" && "Install from Session Stats and AimMod will relaunch when the update finishes."}
                    {updateStatus.state === "downloading" && `Downloading update… ${updateStatus.progress}%`}
                    {updateStatus.state === "ready" && "Update installed. Restarting AimMod…"}
                    {updateStatus.state === "error" && "Update install failed. You can retry from here or use Settings."}
                  </span>
                </div>

                <div style={{ display: "flex", gap: 8, alignItems: "center", flexShrink: 0 }}>
                  <Btn
                    variant="primary"
                    size="sm"
                    disabled={
                      !availableUpdate
                      || updateStatus.state === "checking"
                      || updateStatus.state === "downloading"
                      || updateStatus.state === "ready"
                    }
                    onClick={() => {
                      if (availableUpdate) void installUpdate(availableUpdate);
                    }}
                  >
                    {updateStatus.state === "downloading"
                      ? `Downloading ${updateStatus.progress}%`
                      : updateStatus.state === "ready"
                        ? "Restarting…"
                        : updateStatus.state === "error"
                          ? "Retry Install"
                          : `Install ${updateNoticeVersion ?? "Update"}`}
                  </Btn>
                  <Btn
                    variant="ghost"
                    size="sm"
                    disabled={!updateNoticeVersion}
                    onClick={() => {
                      if (updateNoticeVersion) {
                        setDismissedUpdateVersion(updateNoticeVersion);
                      }
                    }}
                  >
                    Dismiss
                  </Btn>
                </div>
              </div>
            </div>
          )}

          <div style={{ display: "flex", flex: 1, overflow: "hidden" }}>
      {/* ── Sidebar ── */}
      <div
        style={{
          width: 260,
          minWidth: 260,
          borderRight: `1px solid ${C.border}`,
          display: "flex",
          flexDirection: "column",
          background: "rgba(255,255,255,0.018)",
        }}
      >
        <div style={{ padding: "16px 14px 10px" }}>
          <div style={{ fontSize: 11, fontWeight: 700, letterSpacing: "0.1em", textTransform: "uppercase", color: C.textFaint, marginBottom: 10 }}>
            Scenarios
          </div>
          <div style={{ position: "relative" }}>
            <input
              ref={searchInputRef}
              type="text"
              placeholder="Search… (/)"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="am-input"
              style={{ width: "100%", boxSizing: "border-box", paddingRight: search ? 24 : undefined }}
            />
            {search && (
              <button
                onClick={() => setSearch("")}
                style={{
                  position: "absolute",
                  right: 7,
                  top: "50%",
                  transform: "translateY(-50%)",
                  background: "none",
                  border: "none",
                  color: C.textMuted,
                  cursor: "pointer",
                  fontSize: 14,
                  lineHeight: 1,
                  padding: 0,
                }}
                title="Clear search"
              >
                ×
              </button>
            )}
          </div>
          <div style={{ display: "flex", alignItems: "center", gap: 8, marginTop: 8 }}>
            <span
              style={{
                fontSize: 9,
                color: C.textFaint,
                textTransform: "uppercase",
                letterSpacing: "0.1em",
                fontWeight: 600,
                whiteSpace: "nowrap",
              }}
            >
              Sort
            </span>
            <select
              value={scenarioSort}
              onChange={(e) => setScenarioSort(e.target.value as ScenarioSortMode)}
              className="am-input"
              style={{
                flex: 1,
                boxSizing: "border-box",
                padding: "5px 8px",
              }}
            >
              <option value="recent">Recent</option>
              <option value="plays">Most Played</option>
              <option value="type">Scenario Type</option>
            </select>
          </div>
          <div style={{ display: "flex", alignItems: "center", gap: 8, marginTop: 8 }}>
            <span
              style={{
                fontSize: 9,
                color: C.textFaint,
                textTransform: "uppercase",
                letterSpacing: "0.1em",
                fontWeight: 600,
                whiteSpace: "nowrap",
              }}
            >
              Range
            </span>
            <select
              value={dateRange}
              onChange={(e) => setDateRange(e.target.value as DateRangePreset)}
              className="am-input"
              style={{
                flex: 1,
                boxSizing: "border-box",
                padding: "5px 8px",
              }}
            >
              <option value="all">All time</option>
              <option value="30d">Last 30 days</option>
              <option value="90d">Last 90 days</option>
              <option value="365d">Last year</option>
            </select>
          </div>
        </div>

        <div style={{ flex: 1, overflowY: "auto" }}>
          {loading ? (
            <div
              style={{ padding: "20px 16px", color: "rgba(255,255,255,0.3)", fontSize: 12 }}
            >
              {loadingMessage}
            </div>
          ) : scenarioGroups.length === 0 ? (
            <div
              style={{
                padding: "20px 16px",
                color: "rgba(255,255,255,0.25)",
                fontSize: 12,
                lineHeight: 1.6,
              }}
            >
              {records.length === 0
                ? "No sessions recorded yet. Sessions are saved automatically when you finish a run."
                : visibleRecords.length === 0
                  ? "No sessions in this date range yet."
                  : "No matches."}
            </div>
          ) : (
            scenarioGroups.map((g) => {
              const active = g.name === selectedScenario;
              const typeColor = scenarioColor(g.scenarioType);
              return (
                <button
                  key={g.name}
                  onClick={() => {
                    setSelectedScenario(g.name);
                    setSessionsPane("scenario");
                  }}
                  style={{
                    display: "block",
                    width: "100%",
                    textAlign: "left",
                    background: active ? `${typeColor}0c` : "transparent",
                    border: "none",
                    borderLeft: active
                      ? `3px solid ${typeColor}`
                      : `3px solid ${typeColor}30`,
                    padding: "9px 12px 9px 11px",
                    cursor: "pointer",
                    color: active ? C.text : C.textSub,
                    fontFamily: "inherit",
                    fontSize: 12,
                    transition: "background 0.1s",
                  }}
                >
                  <div
                    style={{
                      fontWeight: active ? 700 : 400,
                      marginBottom: 3,
                      lineHeight: 1.3,
                      wordBreak: "break-word",
                    }}
                  >
                    {g.name}
                  </div>
                  <div
                    style={{
                      fontSize: 10,
                      color: active ? C.textMuted : C.textFaint,
                      display: "flex",
                      gap: 6,
                      alignItems: "center",
                      flexWrap: "nowrap",
                      overflow: "hidden",
                    }}
                  >
                    <span style={{ color: typeColor, fontWeight: 600, flexShrink: 0 }}>
                      {SCENARIO_LABELS[g.scenarioType] ?? g.scenarioType}
                    </span>
                    <span style={{ flexShrink: 0 }}>{g.count}×</span>
                    <span style={{ color: C.textFaint, flexShrink: 0 }}>
                      {relativeTime(g.lastTs)}
                    </span>
                    {g.flaggedCount > 0 && (
                      <span style={{ color: C.warn, flexShrink: 0 }}>
                        ⚑ {g.flaggedCount}
                      </span>
                    )}
                    <span
                      className="tabular-nums"
                      style={{ marginLeft: "auto", color: active ? C.accent : C.textFaint, flexShrink: 0, display: "flex", alignItems: "center", gap: 3 }}
                    >
                      {g.trend === "up" && <span style={{ color: "#4ade80", fontSize: 9, fontWeight: 700 }}>▲</span>}
                      {g.trend === "down" && <span style={{ color: C.danger, fontSize: 9, fontWeight: 700 }}>▼</span>}
                      {fmtScore(g.bestReliable ?? g.bestAny)}
                    </span>
                  </div>
                </button>
              );
            })
          )}
        </div>

        <div
          style={{
            padding: "10px 14px",
            borderTop: `1px solid ${C.border}`,
            display: "flex",
            flexDirection: "column",
            gap: 8,
          }}
        >
          <div
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "space-between",
              gap: 8,
            }}
          >
            <span style={{ fontSize: 10, color: C.textFaint }}>
              {visibleRecords.length} session{visibleRecords.length === 1 ? "" : "s"}
              {dateRange !== "all" && records.length !== visibleRecords.length ? ` of ${records.length}` : ""}
              {totalPlaySeconds > 0 && ` · ${formatPlayTime(totalPlaySeconds)}`}
              {flaggedRecordCount > 0 && ` · ${flaggedRecordCount} flagged`}
            </span>
            <div style={{ display: "flex", gap: 5 }}>
              <Btn
                size="xs"
                variant="ghost"
                onClick={handleImportHistory}
                disabled={importingHistory}
              >
                {importingHistory ? "Importing…" : "Import CSVs"}
              </Btn>
              <Btn
                size="xs"
                variant={confirmClear || pendingClear ? "danger" : "ghost"}
                onClick={handleClear}
                onBlur={() => {
                  if (!pendingClear) setConfirmClear(false);
                }}
                disabled={pendingClear != null}
              >
                {pendingClear ? "Pending clear…" : confirmClear ? "Confirm clear" : "Clear"}
              </Btn>
            </div>
          </div>
          {pendingClear && (
            <div
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "space-between",
                gap: 8,
                padding: "8px 10px",
                borderRadius: 10,
                background: `${C.warn}14`,
                border: `1px solid ${C.warn}40`,
                color: C.textSub,
                fontSize: 10,
              }}
            >
              <span>Session history will be cleared in a few seconds.</span>
              <Btn size="xs" variant="ghost" onClick={handleUndoClear}>
                Undo
              </Btn>
            </div>
          )}
          {importStatus && (
            <div style={{ fontSize: 10, color: C.textFaint, lineHeight: 1.5 }}>
              {importStatus}
            </div>
          )}
        </div>
      </div>

      {/* ── Main panel ── */}
        <div style={{ flex: 1, overflowY: "auto", padding: "24px 28px" }}>
          {showHubLinkNotice && (
            <div
              style={{
                marginBottom: 18,
                borderRadius: 12,
                border: `1px solid ${C.accentBorder}`,
                background: "linear-gradient(135deg, rgba(0,245,160,0.10), rgba(0,180,255,0.05))",
                padding: "14px 16px",
                display: "flex",
                alignItems: "flex-start",
                justifyContent: "space-between",
                gap: 16,
              }}
            >
              <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                <div
                  style={{
                    fontSize: 10,
                    color: C.accent,
                    textTransform: "uppercase",
                    letterSpacing: "0.1em",
                    fontWeight: 700,
                  }}
                >
                  AimMod Hub
                </div>
                <div style={{ fontSize: 15, fontWeight: 700, color: C.text }}>
                  Link your AimMod Hub account
                </div>
                <div style={{ fontSize: 12, color: C.textSub, lineHeight: 1.65, maxWidth: 780 }}>
                  Sync your runs to unlock your web profile, scenario pages, search, and shared practice history across devices.
                </div>
              </div>
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexShrink: 0 }}>
                <Btn size="sm" variant="primary" onClick={() => setRootMode("settings")}>
                  Connect
                </Btn>
                <Btn size="sm" variant="ghost" onClick={() => setHubNoticeDismissed(true)}>
                  Dismiss
                </Btn>
              </div>
            </div>
          )}
          <div
            style={{
              display: "flex",
              alignItems: "center",
              gap: 2,
              borderBottom: `1px solid ${C.border}`,
              marginBottom: 20,
            }}
          >
            {([
              { id: "overview", label: "Overview" },
              { id: "scenario", label: "Scenario" },
            ] as Array<{ id: SessionsPaneMode; label: string }>).map((tab) => {
              const active = sessionsPane === tab.id;
              return (
                <button
                  key={tab.id}
                  type="button"
                  onClick={() => setSessionsPane(tab.id)}
                  style={{
                    background: "none",
                    border: "none",
                    borderBottom: active ? `2px solid ${C.accent}` : "2px solid transparent",
                    padding: "8px 14px",
                    marginBottom: -1,
                    cursor: "pointer",
                    color: active ? C.text : C.textMuted,
                    fontFamily: "inherit",
                    fontSize: 12,
                    fontWeight: active ? 700 : 500,
                    letterSpacing: "0.01em",
                  }}
                >
                  {tab.label}
                </button>
              );
            })}
          </div>
          {sessionsPane === "overview" ? (
            <SessionsOverviewPanel
              records={analyticsRecords}
              scenarioGroups={scenarioGroups}
              practiceProfile={globalPracticeProfile}
              globalLearningState={globalLearningState}
              globalCoachingCards={globalCoachingCards}
              compareScenario={compareScenario}
              selectedScenario={selectedScenario}
              compareSummary={compareSummary}
              selectedSummary={selectedSummary}
              onCompareScenarioChange={setCompareScenario}
              playerLearningProfile={playerLearningProfile}
              coachingPersistenceStatus={coachingPersistenceStatus}
              feedbackRows={coachingUserFeedback}
              onFeedback={handleCoachingFeedback}
              onExploreDrill={(query) => {
                setLeaderboardSeedQuery(query);
                setRootMode("leaderboards");
              }}
            />
          ) : selectedScenario && selectedRecords.length > 0 ? (
            <>
              <div style={{ marginBottom: 20 }}>
                <h2
                style={{ margin: "0 0 6px", fontSize: 15, fontWeight: 700, color: C.text, letterSpacing: "-0.01em" }}
              >
                {selectedScenario}
              </h2>
              {selectedGroup && (
                <div style={{ display: "flex", alignItems: "center", gap: 10, fontSize: 11, flexWrap: "wrap" }}>
                  <span style={{ color: scenarioColor(selectedGroup.scenarioType), fontWeight: 600 }}>
                    {SCENARIO_LABELS[selectedGroup.scenarioType] ?? selectedGroup.scenarioType}
                  </span>
                  <span style={{ color: C.textFaint }}>{selectedGroup.count} sessions</span>
                  <span style={{ color: C.textFaint }}>PB {fmtScore(selectedGroup.bestReliable ?? selectedGroup.bestAny)}</span>
                  <span style={{ color: C.textFaint }}>{relativeTime(selectedGroup.lastTs)}</span>
                  {selectedGroup.trend === "up" && (
                    <span style={{ color: "#4ade80", fontWeight: 700, fontSize: 11 }}>▲ Improving</span>
                  )}
                  {selectedGroup.trend === "down" && (
                    <span style={{ color: C.danger, fontWeight: 700, fontSize: 11 }}>▼ Declining</span>
                  )}
                </div>
              )}
            </div>
            <ScenarioDetails
              records={selectedRecords}
              scenarioName={selectedScenario!}
              dateRange={dateRange}
              hubHandle={appSettings?.hub_account_label?.trim() || null}
              feedbackRows={coachingUserFeedback}
              onFeedback={handleCoachingFeedback}
              appSettings={appSettings}
              globalSummary={globalCoachingOverview?.playerLearningProfile?.summary ?? ""}
              onReplayMetadataChanged={() => {
                void loadHistory(true);
              }}
              onExploreDrill={(query) => {
                setLeaderboardSeedQuery(query);
                setRootMode("leaderboards");
              }}
            />
          </>
          ) : (
            <div
              style={{
                height: "calc(100% - 46px)",
                display: "flex",
                flexDirection: "column",
                alignItems: "center",
              justifyContent: "center",
              color: C.textFaint,
              gap: 12,
            }}
          >
            {records.length === 0 ? (
              <div
                style={{
                  width: "min(720px, 100%)",
                  background: "rgba(255,255,255,0.03)",
                  border: `1px solid ${C.border}`,
                  borderRadius: 16,
                  padding: "22px 24px",
                  display: "flex",
                  flexDirection: "column",
                  gap: 18,
                }}
              >
                <div>
                  <div style={{ fontSize: 11, color: C.accent, textTransform: "uppercase", letterSpacing: "0.12em", fontWeight: 700 }}>
                    First run setup
                  </div>
                  <div style={{ marginTop: 6, fontSize: 22, color: C.text, fontWeight: 700 }}>
                    Session Stats comes alive after your first run.
                  </div>
                  <div style={{ marginTop: 8, fontSize: 13, color: C.textSub, lineHeight: 1.7 }}>
                    Once a session is recorded, this view will group scenarios, chart progress, surface coaching patterns, and let you inspect saved replays.
                  </div>
                </div>
                <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12 }}>
                  {[
                    { title: "1. Play a run", detail: "Finish any KovaaK's scenario once. AimMod saves the result automatically." },
                    { title: "2. Or import history", detail: "Pull existing CSV session files from your stats folder if you already have prior runs." },
                    { title: "3. Use shortcuts", detail: "Press `?` any time for keyboard help, or `/` to jump into search once history exists." },
                  ].map((step) => (
                    <div
                      key={step.title}
                      style={{
                        background: "rgba(255,255,255,0.025)",
                        border: `1px solid ${C.borderSub}`,
                        borderRadius: 12,
                        padding: "14px 14px 12px",
                      }}
                    >
                      <div style={{ color: C.text, fontSize: 13, fontWeight: 700, marginBottom: 6 }}>{step.title}</div>
                      <div style={{ color: C.textFaint, fontSize: 12, lineHeight: 1.6 }}>{step.detail}</div>
                    </div>
                  ))}
                </div>
                <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
                  <Btn size="sm" variant="primary" onClick={handleImportHistory} disabled={importingHistory}>
                    {importingHistory ? "Importing…" : "Import CSV History"}
                  </Btn>
                  <Btn size="sm" variant="ghost" onClick={() => setHelpOpen(true)}>
                    Open Shortcuts
                  </Btn>
                </div>
              </div>
            ) : (
              <div>
                {visibleRecords.length === 0
                  ? "No sessions in this date range. Try widening the range."
                  : "Select a scenario from the sidebar."}
              </div>
            )}
            {records.length === 0 &&
              (Object.keys(liveBridgeStats).length > 0 || Object.keys(liveBridgeEventCounts).length > 0) && (
              <div
                style={{
                  marginTop: 4,
                  border: `1px solid ${C.accentBorder}`,
                  background: C.accentDim,
                  borderRadius: 8,
                  padding: "10px 12px",
                  minWidth: 320,
                  color: C.textSub,
                }}
              >
                <div style={{ fontSize: 11, marginBottom: 6, color: C.accent, fontWeight: 700, letterSpacing: "0.06em", textTransform: "uppercase" }}>
                  Live AimMod metrics (current run only)
                </div>
                <div style={{ display: "grid", gridTemplateColumns: "1fr auto", gap: "4px 10px", fontSize: 11 }}>
                  {[
                    "pull_shots_fired_total",
                    "pull_shots_hit_total",
                    "pull_kills_total",
                    "pull_score_per_minute",
                    "pull_score_total_derived",
                    "pull_score_total",
                    "pull_damage_done",
                    "pull_damage_possible",
                    "pull_damage_efficiency",
                    "pull_kills_per_second",
                    "pull_seconds_total",
                  ].map((k) => (
                    <div key={k} style={{ display: "contents" }}>
                      <span style={{ color: "rgba(255,255,255,0.6)" }}>{k}</span>
                      <span style={{ fontFamily: "'JetBrains Mono', monospace" }}>
                        {liveBridgeStats[k] !== undefined ? String(liveBridgeStats[k]) : "-"}
                      </span>
                    </div>
                  ))}
                </div>
                {Object.keys(liveBridgeEventCounts).length > 0 && (
                  <div style={{ marginTop: 8, display: "grid", gridTemplateColumns: "1fr auto", gap: "4px 10px", fontSize: 11 }}>
                    {[
                      "challenge_queued",
                      "challenge_start",
                      "scenario_start",
                      "shot_fired",
                      "shot_hit",
                      "kill",
                      "challenge_complete",
                      "challenge_canceled",
                    ].map((k) => (
                      <div key={k} style={{ display: "contents" }}>
                        <span style={{ color: "rgba(255,255,255,0.6)" }}>{k}</span>
                        <span style={{ fontFamily: "'JetBrains Mono', monospace" }}>
                          {liveBridgeEventCounts[k] !== undefined ? String(liveBridgeEventCounts[k]) : "-"}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>
        )}
          </div>
        </div>
      </div>
      )}

      {activeGame === "kovaaks" && rootMode === "replays" && (
        <div style={{ flex: 1, overflowY: "auto", padding: "24px 28px" }}>
          <div style={{ marginBottom: 20 }}>
            <h2
              style={{ margin: "0 0 6px", fontSize: 15, fontWeight: 700, color: C.text, letterSpacing: "-0.01em" }}
            >
              Replay Hub
            </h2>
            <div style={{ fontSize: 12, color: C.textSub, lineHeight: 1.7, maxWidth: 860 }}>
              Browse every saved replay in one place, pin the ones you want to keep forever, and export runs to video.
            </div>
          </div>
          <ReplayTab
            records={replayHubRecords}
            sorted={replayHubRecords}
            warmupIds={new Set()}
            hubMode
            onReplayMetadataChanged={() => {
              void loadHistory(true);
            }}
          />
        </div>
      )}

      {activeGame === "kovaaks" && rootMode === "hub" && (
        <div style={{ flex: 1, overflowY: "auto", padding: "24px 28px" }}>
          <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
            <HubOverviewPanel
              settings={appSettings}
              overview={hubSyncOverview}
              replayRecords={replayHubRecords}
              onOpenSettings={() => setRootMode("settings")}
              onOpenReplayHub={() => setRootMode("replays")}
              onForceResync={() => {
                void handleForceHubResync();
              }}
              resyncBusy={hubResyncBusy}
            />
            <HubBrowserPanel />
          </div>
        </div>
      )}

      {/* ── Leaderboards content ── */}
      {activeGame === "kovaaks" && rootMode === "leaderboards" && (
        <div style={{ flex: 1, overflow: "hidden" }}>
          <LeaderboardBrowser seedQuery={leaderboardSeedQuery} />
        </div>
      )}

      {activeGame === "kovaaks" && rootMode === "settings" && (
        <div style={{ flex: 1, overflow: "hidden" }}>
          <Suspense
            fallback={
              <div style={{ padding: "28px 32px", color: C.textFaint, fontFamily: "'JetBrains Mono', monospace" }}>
                Loading settings…
              </div>
            }
          >
            <SettingsTab
              embeddedInStats
              hideStatsTab
              onLayoutHUDs={() => {
                void invoke("toggle_layout_huds").catch(console.error);
              }}
            />
          </Suspense>
        </div>
      )}

      {/* ── Memory debug ── */}
      {activeGame === "kovaaks" && isDebugBuild && rootMode === "debug" && (
        <div style={{ flex: 1, overflowY: "auto", padding: "24px 28px" }}>
          <DebugTab />
        </div>
      )}

      <ShortcutHelpModal
        open={activeGame === "kovaaks" && helpOpen}
        onClose={() => setHelpOpen(false)}
        title="Session Stats Shortcuts"
        note="Use `?` from anywhere in this window. Replay rows and recent-run rows open the selected replay directly when replay data exists."
        groups={[
          {
            title: "Navigation",
            items: [
              { keys: "?", action: "Open this shortcuts panel" },
              { keys: "/", action: "Focus scenario search" },
              { keys: "Esc", action: "Close the shortcuts panel" },
            ],
          },
          {
            title: "History",
            items: [
              { keys: "30d", action: "Use the new date-range filter to focus on recent progress", detail: "Switch between All time, 30 days, 90 days, and 1 year from the sidebar." },
              { keys: "Replay", action: "Click a replayable recent-run row to jump straight into Replay", detail: "Rows with saved replay data show an Open action." },
            ],
          },
          {
            title: "Coaching",
            items: [
              { keys: "Compare", action: "Compare the selected scenario against another one side-by-side" },
              { keys: "Drills", action: "Use coaching drill search buttons to jump into related drill lookups" },
            ],
          },
        ]}
      />
    </div>
  );
}
