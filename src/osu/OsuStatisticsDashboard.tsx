import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { invoke, isTauri } from "@tauri-apps/api/core";
import { ArrowClockwise, ChartLineUp, Database, WarningCircle } from "@phosphor-icons/react";
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  ComposedChart,
  Legend,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import "./OsuStatisticsDashboard.css";

interface LocalScoreLibrary {
  items: LocalScore[];
  error: string | null;
}

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

interface DailyScoreBucket {
  date: string;
  dateMs: number;
  runs: number;
  totalScore: number;
  cumulativeRuns: number;
  cumulativeScore: number;
  averageAccuracy: number | null;
  rollingAccuracy: number | null;
  averageCombo: number | null;
  rollingCombo: number | null;
  averagePp: number | null;
  rollingPp: number | null;
  misses: number | null;
  missesPerRun: number | null;
}

type RangeChoice = "30d" | "90d" | "1y" | "all";

const DAY_MS = 86_400_000;
const RANGE_OPTIONS: Array<{ value: RangeChoice; label: string; days: number | null }> = [
  { value: "30d", label: "30 days", days: 30 },
  { value: "90d", label: "90 days", days: 90 },
  { value: "1y", label: "1 year", days: 365 },
  { value: "all", label: "All time", days: null },
];

function messageOf(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason);
}

function utcDay(timestamp: number) {
  return new Date(timestamp).toISOString().slice(0, 10);
}

function parseTimestamp(value: string) {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

function parseMisses(value: string) {
  if (!value) return null;
  try {
    const parsed: unknown = JSON.parse(value);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;
    for (const [key, statistic] of Object.entries(parsed)) {
      const normalized = key.replace(/[^a-z0-9]/gi, "").toLowerCase();
      if (normalized === "miss" && typeof statistic === "number" && Number.isFinite(statistic) && statistic >= 0) return statistic;
    }
    return null;
  } catch {
    return null;
  }
}

function mean(values: number[]) {
  return values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : null;
}

function compactNumber(value: number) {
  return new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 }).format(value);
}

function seriesChange(buckets: DailyScoreBucket[], key: "rollingAccuracy" | "rollingCombo" | "rollingPp") {
  const values = buckets.map((bucket) => bucket[key]).filter((value): value is number => value !== null);
  return values.length >= 2 ? values[values.length - 1] - values[0] : null;
}

function signed(value: number, suffix: string) {
  return `${value >= 0 ? "+" : ""}${value.toFixed(2)}${suffix}`;
}

function formatDateLabel(value: string) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" }).format(new Date(`${value}T00:00:00Z`));
}

function tooltipNumber(value: unknown, name: unknown) {
  const number = typeof value === "number" ? value : Number(value);
  const label = String(name);
  if (!Number.isFinite(number)) return ["Not supplied", label];
  if (label.toLowerCase().includes("accuracy")) return [`${number.toFixed(2)}%`, label];
  if (label.toLowerCase().includes("pp")) return [`${number.toFixed(2)}pp`, label];
  if (label.toLowerCase().includes("combo")) return [`${Math.round(number).toLocaleString()}x`, label];
  if (label.toLowerCase().includes("score")) return [Math.round(number).toLocaleString(), label];
  return [number.toLocaleString(undefined, { maximumFractionDigits: 2 }), label];
}

function rollingMean(buckets: Array<{ values: number[] }>, index: number, days: number) {
  const values = buckets.slice(Math.max(0, index - days + 1), index + 1).flatMap((bucket) => bucket.values);
  return mean(values);
}

function buildDailyBuckets(scores: LocalScore[], range: RangeChoice): DailyScoreBucket[] {
  const rows = scores
    .map((score) => ({ score, timestamp: parseTimestamp(score.playedAt) }))
    .filter((row): row is { score: LocalScore; timestamp: number } => row.timestamp !== null)
    .sort((left, right) => left.timestamp - right.timestamp);
  if (!rows.length) return [];

  const latestDay = Date.parse(`${utcDay(rows[rows.length - 1].timestamp)}T00:00:00Z`);
  const rangeDays = RANGE_OPTIONS.find((option) => option.value === range)?.days ?? null;
  const earliestAvailableDay = Date.parse(`${utcDay(rows[0].timestamp)}T00:00:00Z`);
  const firstDay = rangeDays === null ? earliestAvailableDay : Math.max(earliestAvailableDay, latestDay - (rangeDays - 1) * DAY_MS);
  const filtered = rows.filter((row) => row.timestamp >= firstDay && row.timestamp < latestDay + DAY_MS);

  const grouped = new Map<string, {
    scores: LocalScore[];
    accuracies: number[];
    combos: number[];
    pp: number[];
    misses: number[];
  }>();
  for (const { score, timestamp } of filtered) {
    const key = utcDay(timestamp);
    const bucket = grouped.get(key) ?? { scores: [], accuracies: [], combos: [], pp: [], misses: [] };
    bucket.scores.push(score);
    if (Number.isFinite(score.accuracyPercent)) bucket.accuracies.push(score.accuracyPercent);
    if (Number.isFinite(score.maxCombo) && score.maxCombo >= 0) bucket.combos.push(score.maxCombo);
    if (score.pp !== null && Number.isFinite(score.pp)) bucket.pp.push(score.pp);
    const misses = parseMisses(score.statisticsJson);
    if (misses !== null) bucket.misses.push(misses);
    grouped.set(key, bucket);
  }

  const base = [] as Array<{
    date: string;
    dateMs: number;
    runs: number;
    totalScore: number;
    accuracies: number[];
    combos: number[];
    pp: number[];
    misses: number[];
  }>;
  for (let timestamp = firstDay; timestamp <= latestDay; timestamp += DAY_MS) {
    const date = utcDay(timestamp);
    const bucket = grouped.get(date);
    base.push({
      date,
      dateMs: timestamp,
      runs: bucket?.scores.length ?? 0,
      totalScore: bucket?.scores.reduce((sum, score) => sum + score.totalScore, 0) ?? 0,
      accuracies: bucket?.accuracies ?? [],
      combos: bucket?.combos ?? [],
      pp: bucket?.pp ?? [],
      misses: bucket?.misses ?? [],
    });
  }

  let cumulativeRuns = 0;
  let cumulativeScore = 0;
  return base.map((bucket, index) => {
    cumulativeRuns += bucket.runs;
    cumulativeScore += bucket.totalScore;
    const rollingAccuracy = rollingMean(base.map((item) => ({ values: item.accuracies })), index, 7);
    const rollingCombo = rollingMean(base.map((item) => ({ values: item.combos })), index, 7);
    const rollingPp = rollingMean(base.map((item) => ({ values: item.pp })), index, 7);
    const misses = bucket.misses.length === bucket.runs && bucket.runs > 0
      ? bucket.misses.reduce((sum, value) => sum + value, 0)
      : null;
    return {
      date: bucket.date,
      dateMs: bucket.dateMs,
      runs: bucket.runs,
      totalScore: bucket.totalScore,
      cumulativeRuns,
      cumulativeScore,
      averageAccuracy: mean(bucket.accuracies),
      rollingAccuracy,
      averageCombo: mean(bucket.combos),
      rollingCombo,
      averagePp: mean(bucket.pp),
      rollingPp,
      misses,
      missesPerRun: misses === null ? null : misses / bucket.runs,
    };
  });
}

function ChartCard({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return <section className="osu-stats-chart-card"><header><div><strong>{title}</strong><span>{subtitle}</span></div></header><div className="osu-stats-chart">{children}</div></section>;
}

function EmptyChart({ children }: { children: ReactNode }) {
  return <div className="osu-stats-chart-empty">{children}</div>;
}

export function OsuStatisticsDashboard() {
  const [scores, setScores] = useState<LocalScore[]>([]);
  const [range, setRange] = useState<RangeChoice>("90d");
  const [player, setPlayer] = useState("all");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!isTauri()) { setScores([]); setError(null); return; }
    setLoading(true);
    try {
      const response = await invoke<LocalScoreLibrary>("list_osu_local_scores");
      setScores(response.items.filter((score) => score.mode === "osu"));
      setError(response.error);
    } catch (reason) {
      setScores([]);
      setError(messageOf(reason));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const players = useMemo(() => [...new Set(scores.map((score) => score.playerName).filter(Boolean))].sort((left, right) => left.localeCompare(right)), [scores]);
  const selectedScores = useMemo(() => player === "all" ? scores : scores.filter((score) => score.playerName === player), [player, scores]);
  const daily = useMemo(() => buildDailyBuckets(selectedScores, range), [range, selectedScores]);
  const runs = daily.reduce((sum, bucket) => sum + bucket.runs, 0);
  const activeDays = daily.filter((bucket) => bucket.runs > 0).length;
  const totalScore = daily.reduce((sum, bucket) => sum + bucket.totalScore, 0);
  const filteredScores = useMemo(() => {
    if (!daily.length) return [];
    const first = daily[0].dateMs;
    const last = daily[daily.length - 1].dateMs + DAY_MS;
    return selectedScores.filter((score) => {
      const timestamp = parseTimestamp(score.playedAt);
      return timestamp !== null && timestamp >= first && timestamp < last;
    });
  }, [daily, selectedScores]);
  const averageAccuracy = mean(filteredScores.map((score) => score.accuracyPercent).filter(Number.isFinite));
  const bestCombo = filteredScores.reduce((best, score) => Math.max(best, score.maxCombo), 0);
  const ppRows = filteredScores.filter((score) => score.pp !== null && Number.isFinite(score.pp));
  const parsedMissRows = filteredScores.map((score) => parseMisses(score.statisticsJson)).filter((value): value is number => value !== null);
  const accuracyChange = seriesChange(daily, "rollingAccuracy");
  const comboChange = seriesChange(daily, "rollingCombo");
  const ppChange = seriesChange(daily, "rollingPp");
  const hasMissCoverage = parsedMissRows.length === filteredScores.length && filteredScores.length > 0;

  if (!isTauri()) return <div className="osu-statistics-page osu-empty"><Database size={34} /><strong>Local statistics are available in AimMod desktop</strong><span>The browser route does not load or generate score history.</span></div>;

  return <div className="osu-statistics-dashboard">
    <header className="osu-stats-toolbar">
      <div><ChartLineUp size={22} /><span><strong>Play statistics</strong><small>All available local osu!standard Score rows</small></span></div>
      <div className="osu-stats-controls" role="group" aria-label="Statistics time range">
        {RANGE_OPTIONS.map((option) => <button type="button" key={option.value} className={range === option.value ? "active" : ""} onClick={() => setRange(option.value)}>{option.label}</button>)}
      </div>
      {players.length > 1 && <label><span>Player</span><select value={player} onChange={(event) => setPlayer(event.target.value)}><option value="all">All local players</option>{players.map((name) => <option value={name} key={name}>{name}</option>)}</select></label>}
      <button type="button" className="osu-icon-button" onClick={() => void refresh()} disabled={loading} aria-label="Refresh local score history"><ArrowClockwise size={16} className={loading ? "spin" : ""} /></button>
    </header>

    {error && <div className="osu-library-notice" role="status"><WarningCircle size={16} /><span>{error}</span></div>}
    {loading && !scores.length ? <div className="osu-empty"><ArrowClockwise size={34} className="spin" /><strong>Reading local score history</strong><span>AimMod is loading read-only Score rows from osu!lazer.</span></div> : !scores.length ? <div className="osu-empty"><Database size={34} /><strong>No local standard scores found</strong><span>AimMod reads osu!lazer Score rows through the read-only library reader.</span></div> : <>
      <section className="osu-stats-kpis">
        <article><span>Runs</span><strong>{runs.toLocaleString()}</strong><small>{activeDays.toLocaleString()} active days</small></article>
        <article><span>Total score</span><strong>{compactNumber(totalScore)}</strong><small>{totalScore.toLocaleString()} recorded points</small></article>
        <article><span>Average accuracy</span><strong>{averageAccuracy === null ? "Not supplied" : `${averageAccuracy.toFixed(2)}%`}</strong><small>{accuracyChange === null ? "No rolling comparison" : `${signed(accuracyChange, " points")} rolling change`}</small></article>
        <article><span>Best combo</span><strong>{bestCombo.toLocaleString()}x</strong><small>{comboChange === null ? `${filteredScores.length.toLocaleString()} Score rows in range` : `${signed(comboChange, "x")} rolling change`}</small></article>
      </section>

      <div className="osu-stats-grid">
        <ChartCard title="Play activity" subtitle="Actual runs per UTC day and cumulative runs in this range">
          <ResponsiveContainer width="100%" height="100%"><ComposedChart data={daily} margin={{ top: 10, right: 8, left: 0, bottom: 0 }}><CartesianGrid strokeDasharray="3 3" vertical={false} /><XAxis dataKey="date" tickFormatter={formatDateLabel} minTickGap={30} /><YAxis yAxisId="runs" allowDecimals={false} width={42} /><YAxis yAxisId="cumulative" orientation="right" allowDecimals={false} width={48} /><Tooltip labelFormatter={(label) => `${String(label)} UTC`} formatter={tooltipNumber} /><Legend /><Bar yAxisId="runs" dataKey="runs" name="Runs / day" fill="#ff4f9a" radius={[3, 3, 0, 0]} /><Line yAxisId="cumulative" type="monotone" dataKey="cumulativeRuns" name="Cumulative runs" stroke="#67d8ff" strokeWidth={2} dot={false} connectNulls /></ComposedChart></ResponsiveContainer>
        </ChartCard>

        <ChartCard title="Recorded score" subtitle="Cumulative total score within the selected range">
          <ResponsiveContainer width="100%" height="100%"><AreaChart data={daily} margin={{ top: 10, right: 12, left: 6, bottom: 0 }}><defs><linearGradient id="osuScoreFill" x1="0" y1="0" x2="0" y2="1"><stop offset="5%" stopColor="#ff4f9a" stopOpacity={0.38} /><stop offset="95%" stopColor="#ff4f9a" stopOpacity={0.02} /></linearGradient></defs><CartesianGrid strokeDasharray="3 3" vertical={false} /><XAxis dataKey="date" tickFormatter={formatDateLabel} minTickGap={30} /><YAxis tickFormatter={compactNumber} width={48} /><Tooltip labelFormatter={(label) => `${String(label)} UTC`} formatter={tooltipNumber} /><Area type="monotone" dataKey="cumulativeScore" name="Cumulative score" stroke="#ff4f9a" strokeWidth={2} fill="url(#osuScoreFill)" /></AreaChart></ResponsiveContainer>
        </ChartCard>

        <ChartCard title="Rolling form" subtitle="Seven-day play-weighted averages; gaps retain only scores inside each window">
          <ResponsiveContainer width="100%" height="100%"><LineChart data={daily} margin={{ top: 10, right: 8, left: 0, bottom: 0 }}><CartesianGrid strokeDasharray="3 3" vertical={false} /><XAxis dataKey="date" tickFormatter={formatDateLabel} minTickGap={30} /><YAxis yAxisId="accuracy" domain={["auto", "auto"]} tickFormatter={(value) => `${Number(value).toFixed(0)}%`} width={44} /><YAxis yAxisId="combo" orientation="right" tickFormatter={compactNumber} width={48} /><Tooltip labelFormatter={(label) => `${String(label)} UTC`} formatter={tooltipNumber} /><Legend /><Line yAxisId="accuracy" type="monotone" dataKey="rollingAccuracy" name="7-day accuracy" stroke="#a8efc3" strokeWidth={2} dot={false} connectNulls={false} /><Line yAxisId="combo" type="monotone" dataKey="rollingCombo" name="7-day combo" stroke="#67d8ff" strokeWidth={2} dot={false} connectNulls={false} /></LineChart></ResponsiveContainer>
        </ChartCard>

        <ChartCard title="Misses per run" subtitle={hasMissCoverage ? "Parsed from every Score.statistics row in this range" : `Available for ${parsedMissRows.length.toLocaleString()} of ${filteredScores.length.toLocaleString()} Score rows`}>
          {hasMissCoverage ? <ResponsiveContainer width="100%" height="100%"><BarChart data={daily} margin={{ top: 10, right: 12, left: 0, bottom: 0 }}><CartesianGrid strokeDasharray="3 3" vertical={false} /><XAxis dataKey="date" tickFormatter={formatDateLabel} minTickGap={30} /><YAxis width={42} /><Tooltip labelFormatter={(label) => `${String(label)} UTC`} formatter={tooltipNumber} /><Bar dataKey="missesPerRun" name="Misses / run" fill="#ff789f" radius={[3, 3, 0, 0]} /></BarChart></ResponsiveContainer> : <EmptyChart>Miss trends are hidden because some Score rows do not contain a trustworthy <code>miss</code> statistic.</EmptyChart>}
        </ChartCard>

        <ChartCard title="Stored performance points" subtitle={`${ppRows.length.toLocaleString()} of ${filteredScores.length.toLocaleString()} Score rows include PP${ppChange === null ? "" : ` · ${signed(ppChange, "pp")} rolling change`}`}>
          {ppRows.length >= 2 ? <ResponsiveContainer width="100%" height="100%"><LineChart data={daily} margin={{ top: 10, right: 12, left: 0, bottom: 0 }}><CartesianGrid strokeDasharray="3 3" vertical={false} /><XAxis dataKey="date" tickFormatter={formatDateLabel} minTickGap={30} /><YAxis domain={["auto", "auto"]} width={46} /><Tooltip labelFormatter={(label) => `${String(label)} UTC`} formatter={tooltipNumber} /><Line type="monotone" dataKey="rollingPp" name="7-day PP" stroke="#f2c96d" strokeWidth={2} dot={false} connectNulls={false} /></LineChart></ResponsiveContainer> : <EmptyChart>osu!lazer has not stored enough PP values in these local Score rows to draw a trend.</EmptyChart>}
        </ChartCard>
      </div>

      <details className="osu-stats-provenance"><summary>Data coverage and calculation details</summary><div><p>Source: all locally available osu!standard Score rows returned by AimMod's read-only osu!lazer Realm reader. AimMod does not write to client.realm.</p><p>Ranges end on the latest matching local score. Daily dates use UTC. Seven-day accuracy, combo, and PP lines are play-weighted means of the Score rows inside each trailing calendar window. Cumulative totals restart at the selected range boundary.</p><p>This is local history, not proof of complete account history. Deleted scores, scores stored on another device, and online scores never present in this local Realm are absent. PP and miss charts only appear when their stored fields have enough coverage.</p></div></details>
    </>}
  </div>;
}
