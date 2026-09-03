import { useEffect, useMemo, useState } from "react";
import { convertFileSrc, invoke, isTauri } from "@tauri-apps/api/core";
import { ChartLine, Crosshair, Gauge, Keyboard, Play, Pulse, SpinnerGap, Target, Timer, WarningCircle } from "@phosphor-icons/react";
import type { WorkspaceReplay } from "./models";
import { privateSourceId, recordOsuDiagnostic } from "./osuDiagnostics";
import "./ReplayAnalyticsPanel.css";

interface ReplayAnalyticsResponse { items: ReplayAnalytics[]; error: string | null }
interface ReplayAnalytics {
  path: string;
  fileName: string;
  counts: { count300: number; count100: number; count50: number; countGeki: number; countKatu: number; countMiss: number } | null;
  maxCombo: number | null;
  accuracyPercent: number | null;
  frameMetrics: ReplayFrameMetrics | null;
  timeline: ReplayTimelineBucket[];
  notableSegments: ReplayNotableSegment[];
  traceFrames: ReplayTraceFrame[];
  beatmapTrace: ReplayBeatmapTrace | null;
  exactJudgements: ExactReplayJudgements | null;
  exactJudgementError: string | null;
  parseError: string | null;
}
interface ReplayFrameMetrics {
  frameCount: number; startTimeMs: number; endTimeMs: number; durationMs: number; cursorDistance: number; averageCursorSpeed: number; p95CursorSpeed: number; peakCursorSpeed: number;
  leftPresses: number; rightPresses: number; simultaneousPresses: number; keyPressesPerSecond: number;
}
interface ReplayTimelineBucket { startMs: number; endMs: number; cursorDistance: number; averageCursorSpeed: number; p95CursorSpeed: number; keyPresses: number }
interface ReplayNotableSegment { kind: string; label: string; startMs: number; endMs: number; detail: string; cursorSpeed: number; keyPresses: number; objectCount: number | null; firstObjectIndex: number | null; lastObjectIndex: number | null }
interface ReplayTraceFrame { timeMs: number; x: number; y: number; buttons: number }
interface ReplayHitObject {
  index: number; timeMs: number; endTimeMs: number; x: number; y: number; kind: string;
  newCombo?: boolean; comboOffset?: number; comboIndex?: number; comboIndexWithOffsets?: number;
  sampleHashes?: Record<string, string>;
}
interface ReplayBeatmapTrace {
  circleSize: number | null; playbackRate: number; preservesPitch: boolean; constantRate: boolean; audioPath: string | null;
  globalAudioOffsetMs: number; platformAudioOffsetMs: number; beatmapAudioOffsetMs: number; totalAudioOffsetMs: number;
  comboColours?: string[];
  hitObjects: ReplayHitObject[];
}
interface OsuReplayTheme {
  activeSkin: {
    id: string; name: string; creator: string; comboColours: string[];
    normalisedComboColours?: string[];
    cursorImageHash: string | null; cursor2xImageHash: string | null;
    cursorTrailImageHash: string | null; cursorTrail2xImageHash: string | null;
    sampleHashes: Record<string, string>;
  } | null;
  beatmapColoursEnabled: boolean; beatmapHitsoundsEnabled: boolean; beatmapSkinsEnabled: boolean;
  ignoreBeatmapSkins: boolean; ignoreBeatmapSamples: boolean; useSkinHitsounds: boolean;
  comboColourNormalisationAmount: number;
  preferredComboColourSource: "beatmapWithSkinFallback" | "skin";
  preferredSampleSource: "beatmapWithSkinFallback" | "skin";
  volumeUniversal: number; volumeMusic: number; volumeEffect: number;
  effectiveMusicVolume: number; effectiveSampleVolume: number; positionalHitsoundsLevel: number;
  audioOffsetMs: number; useExperimentalWasapi: boolean;
}
interface ExactObjectJudgement {
  objectIndex: number | null; nestedPath: string | null; objectType: string; startTimeMs: number; endTimeMs: number;
  result: string; maximumResult: string; judgementTimeMs: number; timeOffsetMs: number; gameplayRate: number | null;
  objectPosition: { x: number; y: number } | null; cursorPosition: { x: number; y: number } | null;
  comboBefore: number; comboAfter: number;
}
interface ExactReplayJudgements {
  engineVersion: string; timeBasis: string; pauses: number[]; judgements: ExactObjectJudgement[];
  summary: { great: number; ok: number; meh: number; miss: number; sliderBreaks: number; other: number };
  error: string | null;
}
interface CoachingMetric { id: string; value: number; unit: string }
interface CoachingInsight { id: string; title: string; summary: string; startMs: number | null; endMs: number | null; nextStep: string }
interface ReplayCoachingAnalysis {
  score: { count300: number; count100: number; count50: number; countMiss: number; maxCombo: number; perfect: boolean; largeTickMissCount: number | null; sliderTailHitCount: number | null; maximumSliderTailCount: number | null; pauseCount: number | null };
  metrics: CoachingMetric[];
  insights: CoachingInsight[];
}
interface NativeReplayLaunchResult {
  launched: boolean;
  processId: number;
  beatmapHash: string;
  stagedFileCount: number;
  stagedBytes: number;
  renderer: string;
  storageMode: string;
  activeSkinApplied: boolean;
}

function messageOf(reason: unknown) { return reason instanceof Error ? reason.message : String(reason); }
function localMediaHashUrl(value: string | null | undefined) { if (!value || !isTauri() || !/^[0-9a-f]{64}$/i.test(value)) return null; return convertFileSrc(value.toLowerCase(), "aimmod-media"); }
function formatTime(milliseconds: number) { const negative = milliseconds < 0; const totalSeconds = Math.abs(Math.round(milliseconds / 1000)); return `${negative ? "−" : ""}${Math.floor(totalSeconds / 60)}:${String(totalSeconds % 60).padStart(2, "0")}`; }
function formatWindow(startMs: number, endMs: number) { return `${formatTime(startMs)} to ${formatTime(endMs)}`; }
function metricValue(metrics: CoachingMetric[], id: string) { return metrics.find((metric) => metric.id === id)?.value ?? null; }
function linePoints(values: number[], width: number, height: number) {
  const maximum = Math.max(...values, 1);
  return values.map((value, index) => `${(values.length === 1 ? width / 2 : index / (values.length - 1) * width).toFixed(1)},${(height - value / maximum * height).toFixed(1)}`).join(" ");
}
function frameAt(frames: ReplayTraceFrame[], timeMs: number) {
  let low = 0; let high = frames.length - 1;
  while (low <= high) { const middle = Math.floor((low + high) / 2); if (frames[middle].timeMs <= timeMs) low = middle + 1; else high = middle - 1; }
  return frames[Math.max(0, high)] ?? null;
}
function median(values: number[]) {
  if (!values.length) return null;
  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

function resolvedComboColours(trace: ReplayBeatmapTrace | null, theme: OsuReplayTheme | null) {
  const result = new Map<number, string>();
  const skinColours = theme?.activeSkin?.normalisedComboColours?.length
    ? theme.activeSkin.normalisedComboColours
    : theme?.activeSkin?.comboColours ?? [];
  const beatmapColours = trace?.comboColours ?? [];
  let skinIndex = 0;
  let beatmapIndex = 0;
  trace?.hitObjects.forEach((object, index) => {
    const startsCombo = index === 0 || object.newCombo === true;
    if (startsCombo) {
      skinIndex += 1;
      beatmapIndex += (object.comboOffset ?? 0) + 1;
    }
    const useBeatmap = theme?.preferredComboColourSource === "beatmapWithSkinFallback" && beatmapColours.length > 0;
    const palette = useBeatmap ? beatmapColours : skinColours;
    const colourIndex = useBeatmap
      ? object.comboIndexWithOffsets ?? beatmapIndex
      : object.comboIndex ?? skinIndex;
    if (palette.length > 0) result.set(object.index, palette[((colourIndex % palette.length) + palette.length) % palette.length]);
  });
  return result;
}

function ReplayTraceViewer({ analytics, currentTime, setCurrentTime }: { analytics: ReplayAnalytics; currentTime: number; setCurrentTime: (value: number) => void }) {
  const [theme, setTheme] = useState<OsuReplayTheme | null>(null);
  const [launching, setLaunching] = useState(false);
  const [launchResult, setLaunchResult] = useState<NativeReplayLaunchResult | null>(null);
  const [launchError, setLaunchError] = useState<string | null>(null);
  const metrics = analytics.frameMetrics;
  const startTime = metrics?.startTimeMs ?? 0;
  const endTime = metrics?.endTimeMs ?? startTime;
  useEffect(() => { let current = true; if (!isTauri()) return () => { current = false; }; const sourceId = privateSourceId(analytics.path, "replay"); recordOsuDiagnostic({ area: "replayAnalysis", event: "theme-load-start", sourceId }); void invoke<OsuReplayTheme>("get_osu_replay_theme").then((value) => { recordOsuDiagnostic({ area: "replayAnalysis", event: "theme-load-complete", sourceId }); if (current) setTheme(value); }).catch(() => { recordOsuDiagnostic({ area: "replayAnalysis", event: "theme-load-error", sourceId }); if (current) setTheme(null); }); return () => { current = false; }; }, [analytics.path]);
  const comboColours = useMemo(() => resolvedComboColours(analytics.beatmapTrace, theme), [analytics.beatmapTrace, theme]);
  const cursorAccent = theme?.activeSkin?.normalisedComboColours?.[0] ?? theme?.activeSkin?.comboColours?.[0] ?? null;
  const cursorImageUrl = localMediaHashUrl(theme?.activeSkin?.cursor2xImageHash ?? theme?.activeSkin?.cursorImageHash);
  const cursorTrailImageUrl = localMediaHashUrl(theme?.activeSkin?.cursorTrail2xImageHash ?? theme?.activeSkin?.cursorTrailImageHash);
  const watchInLazer = () => {
    const sourceId = privateSourceId(analytics.path, "replay");
    setLaunching(true);
    setLaunchError(null);
    setLaunchResult(null);
    recordOsuDiagnostic({ area: "nativeReplay", event: "launch-request", sourceId });
    void invoke<NativeReplayLaunchResult>("watch_osu_replay_in_aimmod_lazer", { replayPath: analytics.path })
      .then((result) => { recordOsuDiagnostic({ area: "nativeReplay", event: "launch-ready", sourceId }); setLaunchResult(result); })
      .catch((reason) => { recordOsuDiagnostic({ area: "nativeReplay", event: "launch-error", sourceId }); setLaunchError(messageOf(reason)); })
      .finally(() => setLaunching(false));
  };
  const cursor = frameAt(analytics.traceFrames, currentTime);
  const trail = analytics.traceFrames.filter((frame) => frame.timeMs >= currentTime - 650 && frame.timeMs <= currentTime);
  const objects = analytics.beatmapTrace?.hitObjects.filter((object) => object.timeMs >= currentTime - 350 && object.timeMs <= currentTime + 1400) ?? [];
  const radius = Math.max(18, Math.min(36, 54.4 - 4.48 * (analytics.beatmapTrace?.circleSize ?? 4)));
  return <section className="osu-trace-viewer">
    <header><div><Play size={18} weight="fill" /><span><strong>Replay trace</strong><small>{theme?.activeSkin ? `${theme.activeSkin.name} · ${theme.preferredComboColourSource === "skin" ? "skin colours" : "beatmap colours with skin fallback"}` : analytics.beatmapTrace ? `${analytics.beatmapTrace.hitObjects.length.toLocaleString()} local beatmap objects aligned` : "Cursor and input trace"}</small></span></div><button type="button" className="osu-native-watch" onClick={watchInLazer} disabled={launching}>{launching ? <SpinnerGap size={16} className="spin" /> : <Play size={16} weight="fill" />}{launching ? "Preparing lazer" : "Watch in AimMod lazer"}</button></header>
    <div className="osu-playfield-wrap"><svg className="osu-playfield" viewBox="0 0 512 384" role="img" aria-label="Real replay cursor trace over the local beatmap playfield">
      {objects.map((object) => { const delta = object.timeMs - currentTime; const opacity = delta < 0 ? Math.max(0.15, 1 + delta / 350) : Math.max(0.25, 1 - delta / 1800); const colour = comboColours.get(object.index); return <g key={object.index} opacity={opacity}><circle cx={object.x} cy={object.y} r={radius} className={`hit-object ${object.kind}`} style={colour ? { stroke: colour } : undefined} /><circle cx={object.x} cy={object.y} r={Math.max(5, radius - 5)} className="hit-object-inner" style={colour ? { fill: `color-mix(in srgb, ${colour} 26%, rgba(12,14,22,.58))` } : undefined} /><text x={object.x} y={object.y + 4} textAnchor="middle">{object.index + 1}</text></g>; })}
      {trail.length > 1 && (cursorTrailImageUrl ? trail.map((frame, index) => <image key={`${frame.timeMs}:${index}`} className="skin-cursor-trail" href={cursorTrailImageUrl} x={frame.x - 8} y={frame.y - 8} width="16" height="16" opacity={(index + 1) / trail.length * .72} preserveAspectRatio="xMidYMid meet" />) : <polyline points={trail.map((frame) => `${frame.x},${frame.y}`).join(" ")} className="cursor-trail" style={cursorAccent ? { stroke: cursorAccent } : undefined} />)}
      {cursor && (cursorImageUrl ? <image className={cursor.buttons ? "skin-cursor pressing" : "skin-cursor"} href={cursorImageUrl} x={cursor.x - 18} y={cursor.y - 18} width="36" height="36" preserveAspectRatio="xMidYMid meet" /> : <g className={cursor.buttons ? "cursor pressing" : "cursor"}><circle cx={cursor.x} cy={cursor.y} r="12" style={cursorAccent ? { stroke: cursorAccent } : undefined} /><circle cx={cursor.x} cy={cursor.y} r="4" style={cursorAccent ? { fill: cursorAccent } : undefined} /></g>)}
    </svg>{!analytics.beatmapTrace && <div className="osu-playfield-note"><WarningCircle size={16} /><span>The matching local .osu file could not be resolved. Showing the recorded cursor path without objects.</span></div>}</div>
    <div className="osu-viewer-controls"><span>{formatTime(currentTime)}</span><input type="range" min={startTime} max={Math.max(startTime + 1, endTime)} step={16} value={Math.min(Math.max(currentTime, startTime), endTime)} onChange={(event) => setCurrentTime(Number(event.target.value))} aria-label="Replay trace position" /><span>{formatTime(endTime)}</span></div>
    {launchResult && <div className="osu-native-watch-status" role="status"><Play size={16} weight="fill" /><span>Replay opened in AimMod lazer.</span></div>}
    {launchError && <div className="osu-native-watch-status error" role="alert"><WarningCircle size={16} /><span>{launchError}</span></div>}
  </section>;
}

function TimelineChart({ buckets, notable, selectedIndex, onSelect }: { buckets: ReplayTimelineBucket[]; notable: ReplayNotableSegment[]; selectedIndex: number; onSelect: (index: number) => void }) {
  const width = 720; const height = 150; const selected = buckets[selectedIndex] ?? buckets[0]; if (!selected) return null;
  const bucketWidth = width / Math.max(1, buckets.length);
  return <div className="osu-analytics-chart"><div className="osu-analytics-chart-heading"><div><strong>Replay timeline</strong><span>Five-second windows from recorded frames</span></div><b>{formatWindow(selected.startMs, selected.endMs)}</b></div><svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Recorded cursor speed and key press timeline"><rect x={selectedIndex * bucketWidth} y="0" width={bucketWidth} height={height} className="selected-window" />{[0.25, 0.5, 0.75].map((ratio) => <line key={ratio} x1="0" x2={width} y1={height * ratio} y2={height * ratio} className="grid" />)}{notable.map((segment) => { const index = buckets.findIndex((bucket) => segment.startMs >= bucket.startMs && segment.startMs < bucket.endMs); return index < 0 ? null : <line key={`${segment.kind}:${segment.startMs}`} x1={(index + 0.5) * bucketWidth} x2={(index + 0.5) * bucketWidth} y1="0" y2={height} className="notable" />; })}<polyline points={linePoints(buckets.map((bucket) => bucket.p95CursorSpeed), width, height)} className="speed" /><polyline points={linePoints(buckets.map((bucket) => bucket.keyPresses), width, height)} className="inputs" /></svg><div className="osu-timeline-scrubber">{buckets.map((bucket, index) => <button type="button" key={bucket.startMs} className={index === selectedIndex ? "active" : ""} onClick={() => onSelect(index)} title={formatWindow(bucket.startMs, bucket.endMs)} aria-label={`Inspect ${formatWindow(bucket.startMs, bucket.endMs)}`} />)}</div><div className="osu-chart-legend"><span><i className="speed" />P95 cursor speed</span><span><i className="inputs" />Key presses</span><span><i className="marker" />Measured peak</span></div><div className="osu-window-inspector"><div><span>P95 cursor</span><strong>{Math.round(selected.p95CursorSpeed).toLocaleString()} u/s</strong></div><div><span>Average cursor</span><strong>{Math.round(selected.averageCursorSpeed).toLocaleString()} u/s</strong></div><div><span>Cursor travel</span><strong>{Math.round(selected.cursorDistance).toLocaleString()} u</strong></div><div><span>Presses</span><strong>{selected.keyPresses}</strong></div></div></div>;
}

export function ReplayAnalyticsPanel({ selected }: { selected: WorkspaceReplay }) {
  const [analytics, setAnalytics] = useState<ReplayAnalytics | null>(null); const [coaching, setCoaching] = useState<ReplayCoachingAnalysis | null>(null); const [selectedBucket, setSelectedBucket] = useState(0); const [currentTime, setCurrentTime] = useState(0); const [loading, setLoading] = useState(false); const [error, setError] = useState<string | null>(null);
  useEffect(() => { let current = true; const sourceId = privateSourceId(selected.path, "replay"); setAnalytics(null); setCoaching(null); setSelectedBucket(0); setCurrentTime(0); setError(null); if (!isTauri()) return () => { current = false; }; recordOsuDiagnostic({ area: "replayAnalysis", event: "analysis-start", sourceId }); setLoading(true); void Promise.allSettled([invoke<ReplayAnalyticsResponse>("analyze_osu_replay_files", { paths: [selected.path] }), invoke<ReplayCoachingAnalysis>("analyze_osu_replay", { path: selected.path })]).then(([analyticsResult, coachingResult]) => { recordOsuDiagnostic({ area: "replayAnalysis", event: analyticsResult.status === "fulfilled" ? "decode-complete" : "decode-error", sourceId }); recordOsuDiagnostic({ area: "replayAnalysis", event: coachingResult.status === "fulfilled" ? "coaching-complete" : "coaching-error", sourceId }); if (!current) return; if (analyticsResult.status === "fulfilled") { const item = analyticsResult.value.items[0] ?? null; setAnalytics(item); setCurrentTime(item?.frameMetrics?.startTimeMs ?? 0); setError(analyticsResult.value.error ?? item?.parseError ?? null); } else setError(messageOf(analyticsResult.reason)); if (coachingResult.status === "fulfilled") setCoaching(coachingResult.value); }).finally(() => { recordOsuDiagnostic({ area: "replayAnalysis", event: "analysis-finished", sourceId }); if (current) setLoading(false); }); return () => { current = false; }; }, [selected.path]);
  const focus = useMemo(() => { const exactMiss = analytics?.exactJudgements?.judgements.find((judgement) => judgement.result === "Miss" && judgement.objectIndex !== null); if (exactMiss) return { title: `Miss on object ${(exactMiss.objectIndex ?? 0) + 1}`, detail: `The official osu! ruleset judged this ${exactMiss.objectType.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase()} as a miss at ${formatTime(exactMiss.judgementTimeMs)}.`, startMs: exactMiss.judgementTimeMs - 750, endMs: exactMiss.judgementTimeMs + 750 }; const timeLinked = coaching?.insights.find((insight) => insight.startMs !== null && insight.endMs !== null && insight.id !== "aggregateMisses"); if (timeLinked?.startMs !== null && timeLinked?.startMs !== undefined && timeLinked.endMs !== null) return { title: timeLinked.title, detail: timeLinked.summary, startMs: timeLinked.startMs, endMs: timeLinked.endMs }; const notable = analytics?.notableSegments[0]; return notable ? { title: notable.label, detail: `${notable.detail} This identifies activity, not an exact hit or aim error.`, startMs: notable.startMs, endMs: notable.endMs } : null; }, [analytics, coaching]);
  const openWindow = (startMs: number) => { if (!analytics?.timeline.length) return; const index = analytics.timeline.findIndex((bucket) => startMs >= bucket.startMs && startMs < bucket.endMs); setSelectedBucket(Math.max(0, index)); setCurrentTime(startMs); };
  if (!isTauri()) return <div className="osu-analytics-state"><Pulse size={18} /><span>Replay analysis runs in the AimMod desktop client.</span></div>;
  if (loading) return <div className="osu-analytics-state"><SpinnerGap size={18} className="spin" /><span>Decoding this replay's score and input frames</span></div>;
  const metrics = analytics?.frameMetrics;
  const coachingMetrics = coaching?.metrics ?? [];
  const pressMedian = metricValue(coachingMetrics, "medianPressInterval");
  const pressIqr = metricValue(coachingMetrics, "pressIntervalIqr");
  const alternatingShare = metricValue(coachingMetrics, "alternatingChannelShare");
  const exact = analytics?.exactJudgements ?? null;
  const hitOffsets = exact?.judgements.filter((judgement) => judgement.cursorPosition && ["Great", "Ok", "Meh"].includes(judgement.result)).map((judgement) => judgement.timeOffsetMs) ?? [];
  const hitOffsetMedian = median(hitOffsets);
  const timingSplit = { early: hitOffsets.filter((offset) => offset < -5).length, centred: hitOffsets.filter((offset) => Math.abs(offset) <= 5).length, late: hitOffsets.filter((offset) => offset > 5).length };
  const exactProblems = exact?.judgements.filter((judgement) => judgement.result === "Miss" || ["LargeTickMiss", "SmallTickMiss", "SliderTailMiss"].includes(judgement.result)) ?? [];
  return <section className="osu-replay-analytics"><div className="osu-analytics-heading"><div><Pulse size={20} /><span><strong>Replay analysis</strong><small>Score, cursor, beatmap, and input data from this local replay</small></span></div></div>{error && <div className="osu-library-notice" role="status"><WarningCircle size={16} /><span>{error}</span></div>}{analytics && metrics && <><ReplayTraceViewer analytics={analytics} currentTime={currentTime} setCurrentTime={setCurrentTime} />{focus && <button type="button" className="osu-replay-focus" onClick={() => openWindow(focus.startMs)}><Target size={23} /><span><small>{formatWindow(focus.startMs, focus.endMs)}</small><strong>{focus.title}</strong><p>{focus.detail}</p></span></button>}{exact && <section className="osu-judgement-summary"><header><div><strong>What happened</strong><span>Open an event in the replay viewer</span></div><small>{exact.summary.miss} misses · {exact.summary.sliderBreaks} slider breaks</small></header>{exactProblems.length > 0 ? <div className="osu-judgement-events">{exactProblems.map((judgement, index) => <button type="button" key={`${judgement.objectIndex}:${judgement.nestedPath}:${judgement.judgementTimeMs}:${index}`} onClick={() => openWindow(judgement.judgementTimeMs)}><span className={judgement.result === "Miss" ? "miss" : "slider"}>{judgement.result === "Miss" ? "Miss" : "Slider break"}</span><strong>{judgement.objectIndex === null ? judgement.objectType : `Object ${judgement.objectIndex + 1}`}</strong><small>{formatTime(judgement.judgementTimeMs)}</small></button>)}</div> : <p className="osu-judgement-clear">No object misses or slider breaks were reconstructed for this play.</p>}<div className="osu-hit-offsets"><div><span>Early</span><strong>{timingSplit.early}</strong></div><i><b style={{ width: `${hitOffsets.length ? timingSplit.early / hitOffsets.length * 100 : 0}%` }} /><b style={{ width: `${hitOffsets.length ? timingSplit.centred / hitOffsets.length * 100 : 0}%` }} /><b style={{ width: `${hitOffsets.length ? timingSplit.late / hitOffsets.length * 100 : 0}%` }} /></i><div><strong>{timingSplit.centred}</strong><span>within 5 ms</span></div><div><strong>{timingSplit.late}</strong><span>Late</span></div></div><small className="osu-judgement-engine">Judged with {exact.engineVersion}</small></section>}<div className="osu-analysis-groups"><section><header><ChartLine size={17} /><strong>Score</strong></header><div><article><span>Accuracy</span><strong>{analytics.accuracyPercent === null ? "N/A" : `${analytics.accuracyPercent.toFixed(2)}%`}</strong></article><article><span>Max combo</span><strong>{analytics.maxCombo === null ? "N/A" : `${analytics.maxCombo.toLocaleString()}x`}</strong></article><article><span>Misses</span><strong className={(analytics.counts?.countMiss ?? 0) > 0 ? "miss" : ""}>{analytics.counts?.countMiss ?? "N/A"}</strong></article><article><span>300 / 100 / 50</span><strong>{analytics.counts ? `${analytics.counts.count300} / ${analytics.counts.count100} / ${analytics.counts.count50}` : "N/A"}</strong></article></div></section><section><header><Crosshair size={17} /><strong>Aim trace</strong></header><div><article><span>Cursor travel</span><strong>{Math.round(metrics.cursorDistance).toLocaleString()} u</strong></article><article><span>Average speed</span><strong>{Math.round(metrics.averageCursorSpeed).toLocaleString()} u/s</strong></article><article><span>P95 speed</span><strong>{Math.round(metrics.p95CursorSpeed).toLocaleString()} u/s</strong></article><article><span>Peak sample</span><strong>{Math.round(metrics.peakCursorSpeed).toLocaleString()} u/s</strong></article></div></section><section><header><Keyboard size={17} /><strong>Input</strong></header><div><article><span>Left / right</span><strong>{metrics.leftPresses} / {metrics.rightPresses}</strong></article><article><span>Press rate</span><strong>{metrics.keyPressesPerSecond.toFixed(2)}/s</strong></article><article><span>Median interval</span><strong>{pressMedian === null ? "N/A" : `${pressMedian.toFixed(1)} ms`}</strong></article><article><span>Alternating share</span><strong>{alternatingShare === null ? "N/A" : `${alternatingShare.toFixed(0)}%`}</strong></article></div></section><section><header><Timer size={17} /><strong>Timing</strong></header><div><article><span>Duration</span><strong>{formatTime(metrics.durationMs)}</strong></article><article><span>Input frames</span><strong>{metrics.frameCount.toLocaleString()}</strong></article><article><span>Interval IQR</span><strong>{pressIqr === null ? "N/A" : `${pressIqr.toFixed(1)} ms`}</strong></article><article><span>Median hit offset</span><strong className={hitOffsetMedian === null ? "unavailable" : ""}>{hitOffsetMedian === null ? "Not available" : `${hitOffsetMedian > 0 ? "+" : ""}${hitOffsetMedian.toFixed(1)} ms`}</strong></article></div></section></div><TimelineChart buckets={analytics.timeline} notable={analytics.notableSegments} selectedIndex={selectedBucket} onSelect={(index) => { setSelectedBucket(index); setCurrentTime(analytics.timeline[index]?.startMs ?? 0); }} />{analytics.notableSegments.length > 0 && <section className="osu-notable-segments"><header><Gauge size={17} /><div><strong>Notable replay windows</strong><span>Measured peaks you can open in the viewer</span></div></header><div>{analytics.notableSegments.map((segment) => <button type="button" key={`${segment.kind}:${segment.startMs}`} onClick={() => openWindow(segment.startMs)}><small>{formatWindow(segment.startMs, segment.endMs)}</small><strong>{segment.label}</strong><span>{segment.detail}</span><em>{Math.round(segment.cursorSpeed).toLocaleString()} u/s · {segment.keyPresses} presses</em></button>)}</div></section>}{!exact && <div className="osu-object-link-note"><WarningCircle size={17} /><div><strong>Detailed hit results unavailable</strong><span>{analytics.exactJudgementError ?? "This desktop build could not run the matching beatmap and replay."}</span></div></div>}</>}</section>;
}
