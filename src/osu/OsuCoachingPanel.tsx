import { useEffect, useMemo, useState } from "react";
import { invoke, isTauri } from "@tauri-apps/api/core";
import {
  ArrowClockwise,
  ArrowLeft,
  ArrowRight,
  Brain,
  CaretDown,
  ChartLine,
  CheckCircle,
  Info,
  MagnifyingGlass,
  Play,
  Target,
  WarningCircle,
} from "@phosphor-icons/react";
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import type { WorkspaceReplay } from "./models";
import "./OsuCoachingPanel.css";

type Confidence = "high" | "medium" | "low" | "unavailable";
type RunFilter = "all" | "replay" | "misses" | "clean";

interface LocalScore {
  id: string;
  beatmapHash: string;
  beatmapId: number | null;
  mode: string;
  playerName: string;
  playerId: number;
  totalScore: number;
  totalScoreWithoutMods: number;
  maxCombo: number;
  accuracyPercent: number;
  pp: number | null;
  playedAt: string;
  onlineId: number;
  legacyOnlineId: number;
  clientVersion: string;
  scoreHash: string;
  modsJson: string;
  statisticsJson: string;
  maximumStatisticsJson: string;
  replayPath: string | null;
}

interface LocalScoreLibrary {
  items: LocalScore[];
  error: string | null;
}

interface CoachingMetric {
  id: string;
  label: string;
  value: number;
  unit: string;
  confidence: Confidence;
  evidence: string;
  limitation: string | null;
}

interface SegmentMetrics {
  index: number;
  label: string;
  startMs: number;
  endMs: number;
  cursorDistance: number;
  cursorTravelRate: number;
  pressCount: number;
  pressRate: number;
  medianPressIntervalMs: number | null;
}

interface CoachingInsight {
  id: string;
  category: string;
  title: string;
  summary: string;
  confidence: Confidence;
  metricIds: string[];
  startMs: number | null;
  endMs: number | null;
  nextStep: string;
}

interface UnavailableMetric {
  id: string;
  label: string;
  confidence: "unavailable";
  reason: string;
  requiredData: string;
}

interface ReplayCoachingAnalysis {
  schemaVersion: number;
  source: {
    path: string;
    gameVersion: number;
    beatmapHash: string;
    playerName: string;
    replayHash: string;
    modBitmask: number;
    mods: string[];
    frameCount: number;
    excludedPositionFrameCount: number;
    hasLazerScoreInfo: boolean;
    officialJudgementEngine: string | null;
  };
  score: {
    count300: number;
    count100: number;
    count50: number;
    countMiss: number;
    maxCombo: number;
    perfect: boolean;
    largeTickMissCount: number | null;
    sliderTailHitCount: number | null;
    maximumSliderTailCount: number | null;
    pauseCount: number | null;
  };
  metrics: CoachingMetric[];
  segments: SegmentMetrics[];
  insights: CoachingInsight[];
  unavailableMetrics: UnavailableMetric[];
  limitations: string[];
}

interface RunView {
  score: LocalScore;
  replay: WorkspaceReplay | null;
  title: string;
  difficulty: string | null;
  mods: string[];
  missCount: number | null;
}

interface ImprovementFocus {
  insightId: string | null;
  tone: "attention" | "slider" | "review" | "clear";
  title: string;
  detail: string;
  action: string;
}

interface GlobalFocus {
  tone: "attention" | "progress" | "baseline";
  title: string;
  detail: string;
  action: string;
  run: RunView | null;
}

function messageOf(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason);
}

function average(values: number[]) {
  return values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : null;
}

function median(values: number[]) {
  if (!values.length) return null;
  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

function formatTime(milliseconds: number) {
  const seconds = Math.max(0, Math.round(milliseconds / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

function formatDate(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }).format(date);
}

function formatMetric(metric: CoachingMetric) {
  if (metric.unit === "seconds") return `${metric.value.toFixed(1)} s`;
  if (metric.unit === "%") return `${metric.value.toFixed(0)}%`;
  if (metric.unit === "ms") return `${metric.value.toFixed(1)} ms`;
  if (metric.unit === "playfield units/s") return `${metric.value.toFixed(1)} u/s`;
  if (metric.unit === "playfield units") return `${Math.round(metric.value).toLocaleString()} u`;
  if (metric.unit.startsWith("of ")) return `${Math.round(metric.value)} ${metric.unit}`;
  return `${Math.round(metric.value).toLocaleString()} ${metric.unit}`;
}

function accuracyOf(score: ReplayCoachingAnalysis["score"]) {
  const total = score.count300 + score.count100 + score.count50 + score.countMiss;
  if (!total) return null;
  return ((score.count300 * 300 + score.count100 * 100 + score.count50 * 50) / (total * 300)) * 100;
}

function signedPercentChange(early: number, late: number) {
  if (early <= 0) return null;
  return ((late - early) / early) * 100;
}

function changeLabel(change: number | null) {
  if (change === null) return "No comparison";
  if (Math.abs(change) < 1) return "About the same";
  return `${change > 0 ? "+" : ""}${change.toFixed(0)}%`;
}

function readJson(value: string): unknown {
  try { return JSON.parse(value); } catch { return null; }
}

function statisticValue(json: string, name: string) {
  const parsed = readJson(json);
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;
  for (const [key, value] of Object.entries(parsed)) {
    if (key.replace(/[^a-z0-9]/gi, "").toLowerCase() === name.toLowerCase() && typeof value === "number" && Number.isFinite(value)) return value;
  }
  return null;
}

function modNames(json: string) {
  const parsed = readJson(json);
  if (!Array.isArray(parsed)) return [];
  return parsed.flatMap((item) => {
    if (typeof item === "string") return [item];
    if (item && typeof item === "object" && "acronym" in item && typeof item.acronym === "string") return [item.acronym];
    return [];
  });
}

function normalisedModKey(json: string) {
  return modNames(json).sort().join("+") || "NM";
}

function modSetupKey(json: string) {
  const parsed = readJson(json);
  if (Array.isArray(parsed) && parsed.length === 0) return "NM";
  return parsed === null ? json.trim() || "NM" : JSON.stringify(parsed);
}

function primaryFocus(analysis: ReplayCoachingAnalysis): ImprovementFocus {
  const nonPlayer = analysis.insights.find((insight) => insight.id === "nonPlayerReplay");
  if (nonPlayer) return { insightId: nonPlayer.id, tone: "review", title: "Choose a replay you played yourself", detail: nonPlayer.summary, action: "Select a standard-mode replay without Autoplay or Cinema." };
  const relax = analysis.insights.find((insight) => insight.id === "relaxContext");
  if (relax) return { insightId: relax.id, tone: "review", title: "Use a no-Relax replay for tapping feedback", detail: relax.summary, action: "Play the map without Relax, then analyze that replay to include your key presses." };
  const autopilot = analysis.insights.find((insight) => insight.id === "autopilotContext");
  if (autopilot) return { insightId: autopilot.id, tone: "review", title: "Use a no-Autopilot replay for cursor feedback", detail: autopilot.summary, action: "Play the map without Autopilot, then analyze that replay to include your cursor movement." };
  const exactMiss = analysis.insights.find((insight) => insight.id === "exactMiss");
  if (exactMiss) return { insightId: exactMiss.id, tone: "review", title: exactMiss.title, detail: exactMiss.summary, action: exactMiss.startMs !== null ? `Replay around ${formatTime(exactMiss.startMs + 750)} and check the cursor approach before choosing what to practice.` : exactMiss.nextStep };
  const exactSliderBreak = analysis.insights.find((insight) => insight.id === "exactSliderBreak");
  if (exactSliderBreak) return { insightId: exactSliderBreak.id, tone: "slider", title: exactSliderBreak.title, detail: exactSliderBreak.summary, action: exactSliderBreak.nextStep };
  if ((analysis.score.largeTickMissCount ?? 0) > 0) {
    const count = analysis.score.largeTickMissCount ?? 0;
    return { insightId: "largeTickMisses", tone: "slider", title: `Clean up ${count} missed slider ${count === 1 ? "tick" : "ticks"}`, detail: "Lazer recorded these slider judgements in the score. Their exact positions are not stored in the replay summary.", action: "Retry the map and keep your cursor and hold through each slider. Aim to lower the tick-miss count." };
  }
  const tailsHit = analysis.score.sliderTailHitCount;
  const tailsMaximum = analysis.score.maximumSliderTailCount;
  if (tailsHit !== null && tailsMaximum !== null && tailsHit < tailsMaximum) {
    const dropped = tailsMaximum - tailsHit;
    return { insightId: null, tone: "slider", title: `${dropped} slider ${dropped === 1 ? "tail was" : "tails were"} not completed`, detail: `The score records ${tailsHit} of ${tailsMaximum} slider tails hit. It does not identify which sliders were affected.`, action: "On the next play, hold and follow each slider through its end before moving to the next object." };
  }
  const lateChange = analysis.insights.find((insight) => insight.id === "lateInputChange");
  if (lateChange) return { insightId: lateChange.id, tone: "review", title: "Review what changed in the final quarter", detail: lateChange.summary, action: lateChange.startMs !== null && lateChange.endMs !== null ? `Watch ${formatTime(lateChange.startMs)} to ${formatTime(lateChange.endMs)} and check whether the map pattern explains the change before adjusting your technique.` : lateChange.nextStep };
  const timingBalance = analysis.insights.find((insight) => insight.id === "timingBalance");
  if (timingBalance) return { insightId: timingBalance.id, tone: "review", title: timingBalance.title, detail: timingBalance.summary, action: timingBalance.nextStep };
  if (analysis.score.countMiss > 0) {
    return { insightId: "aggregateMisses", tone: "review", title: "Miss locations need a ruleset judgement pass", detail: `The score stores ${analysis.score.countMiss} ${analysis.score.countMiss === 1 ? "miss" : "misses"}, but it does not store their object IDs or times. AimMod will not present an inferred input window as the miss location.`, action: "Use the replay viewer to inspect the real cursor trace. Exact miss advice remains unavailable until AimMod runs the matching beatmap and replay through lazer's ruleset processor." };
  }
  return { insightId: null, tone: "clear", title: "No clear issue appears in the supported data", detail: "This play has no confirmed miss or slider issue that AimMod can turn into a specific correction.", action: "Analyze another play of the same map. Repeated plays are needed before calling any change consistent." };
}

function globalFocus(runs: RunView[]): GlobalFocus {
  const groups = new Map<string, RunView[]>();
  for (const run of runs) {
    const mapKey = run.score.beatmapHash.trim()
      ? `hash:${run.score.beatmapHash}`
      : run.score.beatmapId !== null
        ? `id:${run.score.beatmapId}`
        : `score:${run.score.id}`;
    const key = `${mapKey}:${modSetupKey(run.score.modsJson)}`;
    const group = groups.get(key) ?? [];
    group.push(run);
    groups.set(key, group);
  }
  const repeatComparisons = [...groups.values()].filter((group) => group.length >= 10).map((group) => {
    const ordered = [...group].sort((left, right) => right.score.playedAt.localeCompare(left.score.playedAt));
    const recent = ordered.slice(0, 5);
    const prior = ordered.slice(5, 10);
    const recentMedian = median(recent.map((run) => run.score.accuracyPercent)) ?? recent[0].score.accuracyPercent;
    const priorMedian = median(prior.map((run) => run.score.accuracyPercent)) ?? recentMedian;
    return { latest: recent[0], recentMedian, priorMedian, delta: recentMedian - priorMedian, recentSamples: recent.length, priorSamples: prior.length };
  }).sort((left, right) => left.delta - right.delta);
  const revisit = repeatComparisons.find((comparison) => comparison.delta <= -0.75);
  if (revisit) return { tone: "attention", title: `Revisit ${revisit.latest.title}`, detail: `The median of ${revisit.recentSamples} recent ${normalisedModKey(revisit.latest.score.modsJson)} plays was ${Math.abs(revisit.delta).toFixed(2)} accuracy points below the median of ${revisit.priorSamples} earlier plays on the same map and full mod setup.`, action: `Play the same setup again. Use ${revisit.priorMedian.toFixed(2)}% as a reference, not a guarantee.`, run: revisit.latest };
  const improving = [...repeatComparisons].sort((left, right) => right.delta - left.delta).find((comparison) => comparison.delta >= 0.75);
  if (improving) return { tone: "progress", title: `${improving.latest.title} moved forward`, detail: `The median of ${improving.recentSamples} recent ${normalisedModKey(improving.latest.score.modsJson)} plays was ${improving.delta.toFixed(2)} accuracy points above the median of ${improving.priorSamples} earlier plays on the same map and full mod setup.`, action: "Repeat the same setup once more. Another matching result will show whether the improvement continues.", run: improving.latest };

  const comparableGroups = [...groups.values()]
    .filter((group) => group.length >= 3)
    .map((group) => [...group].sort((left, right) => right.score.playedAt.localeCompare(left.score.playedAt)).slice(0, 10))
    .sort((left, right) => right[0].score.playedAt.localeCompare(left[0].score.playedAt));
  const missGroup = comparableGroups.find((group) => group.some((run) => run.missCount !== null && run.missCount > 0));
  if (missGroup) {
    const knownMisses = missGroup.filter((run) => run.missCount !== null);
    const missedRuns = knownMisses.filter((run) => (run.missCount ?? 0) > 0);
    const reviewRun = missedRuns[0] ?? missGroup[0];
    return { tone: "attention", title: `Clean up ${reviewRun.title}`, detail: `${missedRuns.length} of ${knownMisses.length} recent matching plays with stored judgement counts contain at least one miss. All use the same map and full mod setup.`, action: "Retry the same setup with one measurable goal: finish with zero misses.", run: reviewRun };
  }

  const baseline = comparableGroups[0];
  if (baseline) {
    const remaining = Math.max(1, 10 - baseline.length);
    return { tone: "baseline", title: `Keep building a baseline on ${baseline[0].title}`, detail: `${baseline.length} matching plays are useful context, but not enough to call an accuracy trend.`, action: `Play the same full mod setup ${remaining} more ${remaining === 1 ? "time" : "times"} to reach a five-versus-five comparison.`, run: baseline[0] };
  }
  return { tone: "baseline", title: "Build a same-map baseline", detail: "Your local history does not yet contain three plays on the same map and full mod setup.", action: "Play the same map and mod setup three times. AimMod will label that first comparison as provisional.", run: runs[0] ?? null };
}

function ConfidenceBadge({ value }: { value: Confidence }) {
  return <span className={`osu-coaching-confidence ${value}`}>{value}</span>;
}

interface AccuracyHistoryPoint {
  accuracy: number;
  difficulty: string | null;
  misses: number | null;
  playedAt: string;
  title: string;
}

function AccuracyHistoryTooltip({ active, payload }: { active?: boolean; payload?: Array<{ payload: AccuracyHistoryPoint }> }) {
  const point = payload?.[0]?.payload;
  if (!active || !point) return null;
  return <div className="osu-coaching-history-tooltip">
    <strong>{point.title}</strong>
    {point.difficulty && <span>{point.difficulty}</span>}
    <small>{formatDate(point.playedAt)}</small>
    <div><b>{point.accuracy.toFixed(2)}%</b><span>{point.misses === null ? "Misses unavailable" : `${point.misses} miss${point.misses === 1 ? "" : "es"}`}</span></div>
  </div>;
}

function AccuracyHistory({ runs }: { runs: RunView[] }) {
  const chronological = [...runs].slice(0, 40).reverse();
  if (chronological.length < 2) return null;
  const data: AccuracyHistoryPoint[] = chronological.map((run) => ({
    accuracy: run.score.accuracyPercent,
    difficulty: run.difficulty,
    misses: run.missCount,
    playedAt: run.score.playedAt,
    title: run.title,
  }));
  const values = data.map((point) => point.accuracy);
  const low = Math.min(...values);
  const high = Math.max(...values);
  const padding = Math.max((high - low) * 0.15, 0.2);
  return <div className="osu-coaching-history-chart" role="img" aria-label={`Accuracy across ${values.length} recent local plays`}>
    <ResponsiveContainer width="100%" height={210}>
      <AreaChart data={data} margin={{ top: 8, right: 8, bottom: 3, left: 0 }}>
        <defs><linearGradient id="coaching-accuracy-fill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#ff4f9a" stopOpacity={0.28} /><stop offset="1" stopColor="#ff4f9a" stopOpacity={0.01} /></linearGradient></defs>
        <CartesianGrid stroke="rgba(255,255,255,.065)" strokeDasharray="3 4" vertical={false} />
        <XAxis dataKey="playedAt" minTickGap={42} tickFormatter={(value) => new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" }).format(new Date(String(value)))} tick={{ fill: "#777886", fontSize: 10 }} tickLine={false} axisLine={false} />
        <YAxis domain={[Math.max(0, low - padding), Math.min(100, high + padding)]} tickFormatter={(value) => `${Number(value).toFixed(1)}%`} width={48} tick={{ fill: "#777886", fontSize: 10 }} tickLine={false} axisLine={false} />
        <Tooltip content={<AccuracyHistoryTooltip />} cursor={{ stroke: "rgba(255,255,255,.22)", strokeDasharray: "3 3" }} />
        <Area type="monotone" dataKey="accuracy" stroke="#ff4f9a" strokeWidth={2.5} fill="url(#coaching-accuracy-fill)" activeDot={{ r: 5, stroke: "#090a11", strokeWidth: 2 }} />
      </AreaChart>
    </ResponsiveContainer>
    <div className="chart-footer"><span>Older</span><strong>{values.length} recent plays across mixed maps</strong><span>Latest</span></div>
  </div>;
}

function ProgressMetric({ label, early, late, format, quarters }: { label: string; early: number; late: number; format: (value: number) => string; quarters: number[] }) {
  const change = signedPercentChange(early, late);
  const maximum = Math.max(...quarters, 0.0001);
  return <article className="osu-coaching-progress-row"><div className="osu-coaching-progress-label"><strong>{label}</strong><span>{changeLabel(change)} in the final quarter</span></div><div className="osu-coaching-progress-value"><small>Opening</small><strong>{format(early)}</strong></div><div className="osu-coaching-quarter-bars" aria-label={`${label} across four replay quarters`}>{quarters.map((value, index) => <span key={index} title={`Quarter ${index + 1}: ${format(value)}`}><i style={{ height: `${Math.max(8, (value / maximum) * 100)}%` }} /></span>)}</div><ArrowRight size={20} /><div className="osu-coaching-progress-value late"><small>Final</small><strong>{format(late)}</strong></div></article>;
}

export function OsuCoachingPanel({ replays }: { replays: WorkspaceReplay[] }) {
  const [localScores, setLocalScores] = useState<LocalScore[]>([]);
  const [scoreLoading, setScoreLoading] = useState(true);
  const [scoreError, setScoreError] = useState<string | null>(null);
  const [historyRequest, setHistoryRequest] = useState(0);
  const [query, setQuery] = useState("");
  const [runFilter, setRunFilter] = useState<RunFilter>("all");
  const [selectedPath, setSelectedPath] = useState("");
  const [analysis, setAnalysis] = useState<ReplayCoachingAnalysis | null>(null);
  const [requestNumber, setRequestNumber] = useState(0);
  const [analysisLoading, setAnalysisLoading] = useState(false);
  const [analysisError, setAnalysisError] = useState<string | null>(null);

  useEffect(() => {
    if (!isTauri()) { setScoreLoading(false); return; }
    let active = true;
    setScoreLoading(true);
    setScoreError(null);
    void invoke<LocalScoreLibrary>("list_osu_local_scores")
      .then((response) => { if (active) { setLocalScores(response.items); setScoreError(response.error); } })
      .catch((reason) => { if (active) setScoreError(messageOf(reason)); })
      .finally(() => { if (active) setScoreLoading(false); });
    return () => { active = false; };
  }, [historyRequest]);

  useEffect(() => {
    if (!selectedPath || !isTauri()) return;
    let active = true;
    setAnalysisLoading(true);
    setAnalysisError(null);
    void invoke<ReplayCoachingAnalysis>("analyze_osu_replay", { path: selectedPath })
      .then((result) => { if (active) setAnalysis(result); })
      .catch((reason) => { if (active) { setAnalysis(null); setAnalysisError(messageOf(reason)); } })
      .finally(() => { if (active) setAnalysisLoading(false); });
    return () => { active = false; };
  }, [requestNumber, selectedPath]);

  const replayByPath = useMemo(() => new Map(replays.map((replay) => [replay.path, replay])), [replays]);
  const runs = useMemo<RunView[]>(() => {
    const standard = localScores.filter((score) => score.mode === "osu" && Number.isFinite(score.accuracyPercent) && Boolean(score.playedAt)).sort((left, right) => right.playedAt.localeCompare(left.playedAt));
    const player = standard.find((score) => score.playerId > 0 && score.playerName.trim())?.playerName ?? standard[0]?.playerName;
    return standard.filter((score) => !player || score.playerName === player).map((score) => {
      const replay = score.replayPath ? replayByPath.get(score.replayPath) ?? null : null;
      return { score, replay, title: replay?.beatmapTitle || (score.beatmapId ? `Beatmap ${score.beatmapId}` : `Map ${score.beatmapHash.slice(0, 8)}`), difficulty: replay?.difficultyName ?? null, mods: replay?.mods.length ? replay.mods : modNames(score.modsJson), missCount: statisticValue(score.statisticsJson, "Miss") };
    });
  }, [localScores, replayByPath]);

  const visibleRuns = useMemo(() => {
    const search = query.trim().toLowerCase();
    return runs.filter((run) => {
      if (runFilter === "replay" && !run.score.replayPath) return false;
      if (runFilter === "misses" && !(run.missCount !== null && run.missCount > 0)) return false;
      if (runFilter === "clean" && run.missCount !== 0) return false;
      return !search || [run.title, run.difficulty ?? "", run.mods.join(" "), run.score.playerName].some((value) => value.toLowerCase().includes(search));
    });
  }, [query, runFilter, runs]);

  const currentRun = runs.find((run) => run.score.replayPath === selectedPath) ?? null;
  const availableReplayPaths = useMemo(() => [...new Set(runs.flatMap((run) => run.score.replayPath ? [run.score.replayPath] : []))], [runs]);

  if (!isTauri()) return <div className="osu-single-page osu-empty"><Brain size={42} /><strong>Coaching runs in AimMod desktop</strong><span>AimMod builds the overview from your local osu!lazer scores.</span></div>;

  function openRun(run: RunView) {
    if (!run.score.replayPath) return;
    setAnalysis(null);
    setAnalysisError(null);
    setSelectedPath(run.score.replayPath);
  }

  function closeRun() {
    setSelectedPath("");
    setAnalysis(null);
    setAnalysisError(null);
  }

  if (!selectedPath) {
    const recent = runs.slice(0, 20);
    const previous = runs.slice(20, 40);
    const recentAccuracy = average(recent.map((run) => run.score.accuracyPercent));
    const previousAccuracy = average(previous.map((run) => run.score.accuracyPercent));
    const accuracyChange = recentAccuracy !== null && previousAccuracy !== null ? recentAccuracy - previousAccuracy : null;
    const knownMisses = recent.filter((run) => run.missCount !== null);
    const cleanRuns = knownMisses.filter((run) => run.missCount === 0).length;
    const ppValues = runs.flatMap((run) => run.score.pp !== null && Number.isFinite(run.score.pp) ? [run.score.pp] : []);
    const focus = globalFocus(runs);
    return <div className="osu-coaching-page osu-coaching-overview">
      <header className="osu-coaching-overview-header"><div><span>Your coach</span><h1>{runs[0]?.score.playerName || "Local player"}</h1><p>{runs.length.toLocaleString()} standard-mode plays in local history</p></div><button type="button" className="osu-secondary-action" onClick={() => setHistoryRequest((value) => value + 1)} disabled={scoreLoading}><ArrowClockwise size={18} className={scoreLoading ? "spin" : ""} />Refresh history</button></header>
      {scoreError && <div className="osu-coaching-error" role="status"><WarningCircle size={18} /><span>{scoreError}</span></div>}
      {scoreLoading && !runs.length ? <section className="osu-coaching-loading"><ArrowClockwise size={30} className="spin" /><strong>Reading your play history</strong><span>Building trends from local osu!lazer scores.</span></section> : !runs.length ? <section className="osu-coaching-loading"><Brain size={36} /><strong>No standard-mode scores found</strong><span>Play a map in osu!lazer, then return here.</span></section> : <>
        <section className={`osu-coaching-global-focus ${focus.tone}`}><div className="osu-coaching-focus-icon">{focus.tone === "progress" ? <CheckCircle size={31} /> : <Target size={31} />}</div><div><span>Best next step</span><h2>{focus.title}</h2><p>{focus.detail}</p></div><aside><span>Try this</span><strong>{focus.action}</strong>{focus.run?.score.replayPath && <button type="button" onClick={() => openRun(focus.run!)}>Review this run<ArrowRight size={17} /></button>}</aside></section>
        <section className="osu-coaching-overview-grid">
          <article><span>Recent accuracy</span><strong>{recentAccuracy === null ? "Not available" : `${recentAccuracy.toFixed(2)}%`}</strong><small>Average of {recent.length} recent plays</small></article>
          <article className={accuracyChange !== null && accuracyChange < 0 ? "needs-work" : "positive"}><span>Compared with prior plays</span><strong>{accuracyChange === null ? "Need more plays" : `${accuracyChange >= 0 ? "+" : ""}${accuracyChange.toFixed(2)} pts`}</strong><small>Mixed maps, so treat this as context</small></article>
          <article><span>Clean recent finishes</span><strong>{knownMisses.length ? `${cleanRuns} / ${knownMisses.length}` : "Not available"}</strong><small>Runs with zero stored misses</small></article>
          <article><span>Best stored PP</span><strong>{ppValues.length ? `${Math.max(...ppValues).toFixed(1)}pp` : "Not available"}</strong><small>Highest PP in local history</small></article>
        </section>
        <section className="osu-coaching-history-section"><div className="osu-coaching-section-heading"><div><strong>Recent accuracy</strong><span>Each point is one local play</span></div><small>Map choice and mods vary</small></div><AccuracyHistory runs={runs} /></section>
        <section className="osu-coaching-runs-section"><div className="osu-coaching-section-heading"><div><strong>Recent runs</strong><span>Search a beatmap and open its replay</span></div><small>{visibleRuns.length} matching plays</small></div><div className="osu-coaching-run-tools"><label><MagnifyingGlass size={18} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search beatmaps, difficulties, or mods" /></label><div>{(["all", "replay", "misses", "clean"] as RunFilter[]).map((filter) => <button type="button" key={filter} className={runFilter === filter ? "active" : ""} onClick={() => setRunFilter(filter)}>{filter === "all" ? "All runs" : filter === "replay" ? "Has replay" : filter === "misses" ? "With misses" : "Miss-free"}</button>)}</div></div><div className="osu-coaching-run-list">{visibleRuns.slice(0, 100).map((run) => <button type="button" key={run.score.id} disabled={!run.score.replayPath} onClick={() => openRun(run)}><span className="run-status">{run.score.replayPath ? <Play size={18} /> : <Info size={18} />}</span><span className="run-name"><strong>{run.title}</strong><small>{run.difficulty || "Difficulty not linked"} · {run.mods.join(" + ") || "No Mod"}</small></span><span className="run-date">{formatDate(run.score.playedAt)}</span><span className="run-accuracy">{run.score.accuracyPercent.toFixed(2)}%</span><span className={run.missCount && run.missCount > 0 ? "run-misses has-misses" : "run-misses"}>{run.missCount === null ? "Misses N/A" : `${run.missCount} miss${run.missCount === 1 ? "" : "es"}`}</span><span className="run-pp">{run.score.pp === null ? "PP N/A" : `${run.score.pp.toFixed(1)}pp`}</span><ArrowRight size={18} /></button>)}</div>{visibleRuns.length === 0 && <div className="osu-coaching-no-runs"><MagnifyingGlass size={26} /><strong>No matching runs</strong><span>Change the search or filter.</span></div>}</section>
      </>}
    </div>;
  }

  const focus = analysis ? primaryFocus(analysis) : null;
  const accuracy = analysis ? accuracyOf(analysis.score) : null;
  const early = analysis?.segments[0] ?? null;
  const late = analysis && analysis.segments.length > 0 ? analysis.segments[analysis.segments.length - 1] : null;
  const secondaryInsights = analysis?.insights.filter((insight) => insight.id !== focus?.insightId && !["nonPlayerReplay", "relaxContext", "autopilotContext"].includes(insight.id)) ?? [];
  return <div className="osu-coaching-page osu-coaching-detail">
    <header className="osu-coaching-toolbar"><button type="button" className="osu-coaching-back" onClick={closeRun}><ArrowLeft size={20} />Overview</button><div><Brain size={24} /><span><strong>{currentRun?.title || "Replay coach"}</strong><small>{currentRun?.difficulty || "Selected replay"}</small></span></div><label><span>Replay</span><div className="osu-select-wrap"><select value={selectedPath} onChange={(event) => { setAnalysis(null); setSelectedPath(event.target.value); }}>{availableReplayPaths.map((path) => { const run = runs.find((item) => item.score.replayPath === path); return <option key={path} value={path}>{run?.title || path} · {run ? formatDate(run.score.playedAt) : "Local replay"}</option>; })}</select><CaretDown size={12} /></div></label><button type="button" className="osu-secondary-action" onClick={() => setRequestNumber((value) => value + 1)} disabled={analysisLoading}><ArrowClockwise size={17} className={analysisLoading ? "spin" : ""} />{analysisLoading ? "Reading" : "Refresh"}</button></header>
    {analysisError && <div className="osu-coaching-error" role="alert"><WarningCircle size={18} /><span>{analysisError}</span></div>}
    {analysisLoading && !analysis && <section className="osu-coaching-loading"><ArrowClockwise size={30} className="spin" /><strong>Reading the replay</strong><span>Checking the score and replay progression.</span></section>}
    {analysis && focus && <div className="osu-coaching-results">
      <section className={`osu-coaching-run-focus ${focus.tone}`}><div className="osu-coaching-focus-icon">{focus.tone === "clear" ? <CheckCircle size={31} /> : <Target size={31} />}</div><div className="osu-coaching-focus-copy"><span>Focus for your next play</span><h2>{focus.title}</h2><p>{focus.detail}</p></div><div className="osu-coaching-next-step"><span>Do this next</span><strong>{focus.action}</strong></div></section>
      <section className="osu-coaching-score-section"><div className="osu-coaching-section-heading"><div><strong>Score at a glance</strong><span>{currentRun?.title || "Selected replay"} · {analysis.source.mods.join(" + ") || "No Mod"}</span></div></div><div className="osu-coaching-score-grid"><article><span>Accuracy</span><strong>{accuracy === null ? "Not available" : `${accuracy.toFixed(2)}%`}</strong></article><article className={analysis.score.countMiss > 0 ? "needs-work" : "clean"}><span>Misses</span><strong>{analysis.score.countMiss}</strong><small>{analysis.score.countMiss === 0 ? "Clean on object misses" : "Confirmed by the score"}</small></article><article><span>Max combo</span><strong>{analysis.score.maxCombo.toLocaleString()}x</strong></article><article><span>300 / 100 / 50</span><strong>{analysis.score.count300} / {analysis.score.count100} / {analysis.score.count50}</strong></article><article><span>Slider tails</span><strong>{analysis.score.sliderTailHitCount !== null && analysis.score.maximumSliderTailCount !== null ? `${analysis.score.sliderTailHitCount} / ${analysis.score.maximumSliderTailCount}` : "Not available"}</strong></article></div></section>
      {early && late && <section className="osu-coaching-progression-section"><div className="osu-coaching-section-heading"><div><strong>Opening compared with the finish</strong><span>Recorded activity, not a skill grade</span></div><small>{formatTime(early.startMs)} to {formatTime(early.endMs)} compared with {formatTime(late.startMs)} to {formatTime(late.endMs)}</small></div><div className="osu-coaching-progress-list"><ProgressMetric label="Press rate" early={early.pressRate} late={late.pressRate} quarters={analysis.segments.map((segment) => segment.pressRate)} format={(value) => `${value.toFixed(2)}/s`} /><ProgressMetric label="Cursor travel" early={early.cursorTravelRate} late={late.cursorTravelRate} quarters={analysis.segments.map((segment) => segment.cursorTravelRate)} format={(value) => `${value.toFixed(0)} u/s`} />{early.medianPressIntervalMs !== null && late.medianPressIntervalMs !== null && <ProgressMetric label="Median press interval" early={early.medianPressIntervalMs} late={late.medianPressIntervalMs} quarters={analysis.segments.map((segment) => segment.medianPressIntervalMs ?? 0)} format={(value) => `${value.toFixed(0)} ms`} />}</div><p className="osu-coaching-progression-note"><Info size={16} />Map rhythm, sliders, breaks, and spinners can explain these changes. Compare the replay section before changing how you play.</p></section>}
      {secondaryInsights.length > 0 && <section className="osu-coaching-secondary-section"><div className="osu-coaching-section-heading"><div><strong>Also worth checking</strong><span>More supported observations from this replay</span></div></div><div className="osu-coaching-secondary-list">{secondaryInsights.map((insight) => <article key={insight.id}><strong>{insight.title}</strong><p>{insight.summary}</p><span>{insight.nextStep}</span></article>)}</div></section>}
      <details className="osu-coaching-technical"><summary><span><ChartLine size={20} /><strong>Technical details</strong></span><small>Measurements, confidence, and current limits</small></summary><div className="osu-coaching-technical-content"><section><h3>Replay source</h3><dl><div><dt>Player</dt><dd>{analysis.source.playerName}</dd></div><div><dt>Frames read</dt><dd>{analysis.source.frameCount.toLocaleString()}</dd></div><div><dt>Game version</dt><dd>{analysis.source.gameVersion}</dd></div><div><dt>Score metadata</dt><dd>{analysis.source.hasLazerScoreInfo ? "Lazer metadata present" : "Legacy header only"}</dd></div>{analysis.source.officialJudgementEngine && <div><dt>Hit results</dt><dd>{analysis.source.officialJudgementEngine}</dd></div>}<div><dt>Excluded cursor frames</dt><dd>{analysis.source.excludedPositionFrameCount}</dd></div></dl></section><section><h3>Recorded measurements</h3><div className="osu-coaching-technical-metrics">{analysis.metrics.map((metric) => <article key={metric.id}><div><strong>{metric.label}</strong><ConfidenceBadge value={metric.confidence} /></div><b>{formatMetric(metric)}</b><p>{metric.evidence}</p>{metric.limitation && <small>{metric.limitation}</small>}</article>)}</div></section>{analysis.unavailableMetrics.length > 0 && <section><h3>Not available from this replay alone</h3><div className="osu-coaching-unavailable">{analysis.unavailableMetrics.map((metric) => <article key={metric.id}><strong>{metric.label}</strong><p>{metric.reason}</p><small>Needs: {metric.requiredData}</small></article>)}</div></section>}<section><h3>Limits</h3><ul>{analysis.limitations.map((limitation) => <li key={limitation}>{limitation}</li>)}</ul></section></div></details>
    </div>}
  </div>;
}
