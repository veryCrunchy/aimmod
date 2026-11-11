import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { convertFileSrc, isTauri } from "@tauri-apps/api/core";
import { C, accentAlpha } from "../design/tokens";
import { logError } from "../log";
import type { MetricPoint, RawPositionPoint, ReplayVideo, ScreenFrame } from "../types/mouse";

// ─── Types ────────────────────────────────────────────────────────────────────

interface OvershootMarker {
  x: number;
  y: number;
  timestamp_ms: number;
}

interface SensitivitySuggestion {
  type: "overshoot" | "undershoot" | "balanced";
  headline: string;
  detail: string;
  badge: string;
  color: string;
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function avg(arr: number[]): number {
  return arr.length === 0 ? 0 : arr.reduce((a, b) => a + b, 0) / arr.length;
}

/** Lerp between two [r,g,b] colours with optional alpha. */
function lerpColor(a: [number, number, number], b: [number, number, number], t: number, alpha = 1): string {
  const r = Math.round(a[0] + (b[0] - a[0]) * t);
  const g = Math.round(a[1] + (b[1] - a[1]) * t);
  const bl = Math.round(a[2] + (b[2] - a[2]) * t);
  return `rgba(${r},${g},${bl},${alpha})`;
}

/** Map a normalised speed value (0–1) to a colour on a blue→cyan→green→yellow→red ramp. */
function speedColor(t: number, alpha = 1): string {
  const stops: [number, [number, number, number]][] = [
    [0.00, [30, 100, 255]],   // blue   – stationary / very slow
    [0.25, [0,  200, 220]],   // cyan
    [0.50, [0,  220, 80]],    // green  – typical speed
    [0.75, [255, 220, 0]],    // yellow – fast
    [1.00, [255, 50,  30]],   // red    – spike
  ];
  for (let i = 0; i < stops.length - 1; i++) {
    const [t0, c0] = stops[i];
    const [t1, c1] = stops[i + 1];
    if (t >= t0 && t <= t1) {
      return lerpColor(c0, c1, (t - t0) / (t1 - t0), alpha);
    }
  }
  return `rgba(255,50,30,${alpha})`;
}

/** Detect overshoot events: direction-reversal in the dominant axis while moving fast. */
function detectOvershoots(pts: RawPositionPoint[]): OvershootMarker[] {
  if (pts.length < 4) return [];
  const markers: OvershootMarker[] = [];
  const n = pts.length;

  // Pre-compute per-segment velocities so we only do the arithmetic once.
  const vx: number[] = new Array(n - 1);
  const vy: number[] = new Array(n - 1);
  const spd: number[] = new Array(n - 1);
  let speedSum = 0;
  for (let i = 0; i < n - 1; i++) {
    const dt = Math.max((pts[i + 1].timestamp_ms - pts[i].timestamp_ms) / 1000, 0.001);
    vx[i] = (pts[i + 1].x - pts[i].x) / dt;
    vy[i] = (pts[i + 1].y - pts[i].y) / dt;
    spd[i] = Math.hypot(vx[i], vy[i]);
    speedSum += spd[i];
  }
  const meanSpeed = speedSum / Math.max(n - 1, 1);

  // Adaptive speed gate: must be at least 30 % of mean session speed.
  // The old absolute 30 px/s passed even micro-corrections at 30 fps sampling
  // (1 px/frame = ~900 px/s), making nearly every sample qualify.
  const spdThreshold = Math.max(meanSpeed * 0.30, 100);

  for (let i = 0; i < n - 2; i++) {
    if (spd[i] < spdThreshold || spd[i + 1] < spdThreshold) continue;

    // A click at or adjacent to the reversal point means the cursor just
    // acquired a target and is heading to the next one — not an overshoot.
    if (pts[i].is_click || pts[i + 1].is_click || pts[i + 2].is_click) continue;

    const dot = (vx[i] * vx[i + 1] + vy[i] * vy[i + 1]) / (spd[i] * spd[i + 1]);
    // Angle > 120° = sharp reversal → overshoot
    if (dot < -0.5) {
      markers.push({ x: pts[i + 1].x, y: pts[i + 1].y, timestamp_ms: pts[i + 1].timestamp_ms });
      i += 2; // skip ahead to avoid duplicate markers in the same reversal
    }
  }
  return markers;
}

/** Compute sensitivity training suggestion from per-second metric points. */
function computeSensitivitySuggestion(
  metricPoints: MetricPoint[],
): SensitivitySuggestion | null {
  if (metricPoints.length < 3) return null;
  const avgOvershoot   = avg(metricPoints.map((p) => p.metrics.overshoot_rate));
  const avgCorrection  = avg(metricPoints.map((p) => p.metrics.correction_ratio));
  const avgDirectional = avg(metricPoints.map((p) => p.metrics.directional_bias));

  if (avgOvershoot > 0.28) {
    const bias =
      avgDirectional > 0.4
        ? " (bias detected — also check your starting-position habit)"
        : "";
    return {
      type: "overshoot",
      headline: "Frequent overshoots detected",
      detail:
        `Your session averaged ${Math.round(avgOvershoot * 100)}% overshoot rate${bias}. ` +
        "Train with +20% higher sensitivity for 5–10 runs. The goal is to build smaller, " +
        "more controlled micro-corrections. Afterwards, return to your normal sens — " +
        "you'll find your flicks decelerate earlier.",
      badge: "+20% Sensitivity",
      color: "#ff6b6b",
    };
  }

  if (avgCorrection > 0.58 && avgOvershoot < 0.15) {
    return {
      type: "undershoot",
      headline: "Under-shooting / over-correcting detected",
      detail:
        `You spent ${Math.round(avgCorrection * 100)}% of movement time in the correction phase` +
        " with low overshoot — a sign of hesitant, under-committed flicks. Train with −20% lower " +
        "sensitivity for 5–10 runs to build more confident, large-scale arm movements. " +
        "Afterwards, back to your normal sens — your flicks will feel more direct.",
      badge: "−20% Sensitivity",
      color: "#ffd700",
    };
  }

  return null;
}

function shouldClusterHitIndicators(hitTimestampsMs: number[], durationMs: number): boolean {
  if (hitTimestampsMs.length < 12 || durationMs <= 0) return false;
  const hitsPerSecond = hitTimestampsMs.length / Math.max(durationMs / 1000, 1);
  return hitsPerSecond > 2;
}

function clusterHitWindows(
  hitTimestampsMs: number[],
  mergeGapMs = 220,
): Array<{ start_ms: number; end_ms: number }> {
  if (hitTimestampsMs.length === 0) return [];
  const sorted = [...hitTimestampsMs].sort((a, b) => a - b);
  const windows: Array<{ start_ms: number; end_ms: number }> = [];
  let start = sorted[0];
  let end = sorted[0];
  for (let i = 1; i < sorted.length; i++) {
    const timestampMs = sorted[i];
    if (timestampMs - end <= mergeGapMs) {
      end = timestampMs;
      continue;
    }
    windows.push({ start_ms: start, end_ms: end });
    start = timestampMs;
    end = timestampMs;
  }
  windows.push({ start_ms: start, end_ms: end });
  return windows;
}

// ─── Canvas drawing ───────────────────────────────────────────────────────────

/**
 * Two rendering modes:
 *  - Follow-cam (playHeadFraction ≥ 0): fixed scale where viewportPx fills ~78%
 *    of the canvas width; the "camera" is centred on the current cursor position.
 *    The viewport rectangle is always centred on-screen.  Path points that went
 *    off-screen appear outside the box — this is the 3-D effect.
 *  - Full-path (playHeadFraction < 0): auto-fit bounding box; no viewport overlay.
 */
function drawPath(
  canvas: HTMLCanvasElement,
  pts: RawPositionPoint[],
  overshoots: OvershootMarker[],
  playHeadFraction: number, // 0–1; or <0 to draw full path
  trailFadeMs = 0,           // 0 = no fade; >0 = fade window in ms
  playbackMs = 0,
  viewportPx = 960,          // on-screen monitor capture width in delta-px
  hitTimestampsMs: number[] = [],
) {
  const ctx = canvas.getContext("2d");
  if (!ctx || pts.length < 2) {
    ctx?.clearRect(0, 0, canvas.width, canvas.height);
    return;
  }

  const showFull = playHeadFraction < 0;
  const count = (() => {
    if (showFull) return pts.length;
    if (pts.length <= 2) return pts.length;
    if (playbackMs <= pts[0].timestamp_ms) return 2;
    let lo = 0;
    let hi = pts.length - 1;
    while (lo < hi) {
      const mid = Math.floor((lo + hi + 1) / 2);
      if (pts[mid].timestamp_ms <= playbackMs) lo = mid;
      else hi = mid - 1;
    }
    return Math.max(2, Math.min(pts.length, lo + 1));
  })();

  const CW = canvas.width;
  const CH = canvas.height;
  const PAD = 28;
  const W   = CW - PAD * 2;
  const H   = CH - PAD * 2;
  const denseHitStream = shouldClusterHitIndicators(
    hitTimestampsMs,
    pts[pts.length - 1]?.timestamp_ms ?? 0,
  );

  // ── Camera / projection ───────────────────────────────────────────────────
  let scale: number;
  let toX: (x: number) => number;
  let toY: (y: number) => number;

  if (showFull) {
    // Auto-fit all points
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const p of pts) {
      if (p.x < minX) minX = p.x;
      if (p.x > maxX) maxX = p.x;
      if (p.y < minY) minY = p.y;
      if (p.y > maxY) maxY = p.y;
    }
    const rangeX = maxX - minX || 1;
    const rangeY = maxY - minY || 1;
    scale = Math.min(W / rangeX, H / rangeY);
    const ox = PAD + (W - rangeX * scale) / 2;
    const oy = PAD + (H - rangeY * scale) / 2;
    toX = (x) => ox + (x - minX) * scale;
    toY = (y) => oy + (y - minY) * scale;
  } else {
    // Follow-cam: viewport rectangle fills 65 % of canvas width,
    // leaving ~155 px horizontal and ~95 px vertical margin for off-screen paths.
    scale = (CW * 0.65) / viewportPx;
    const cam = pts[Math.min(count - 1, pts.length - 1)];
    toX = (x) => CW / 2 + (x - cam.x) * scale;
    toY = (y) => CH / 2 + (y - cam.y) * scale;
  }

  // ── Speed palette ─────────────────────────────────────────────────────────
  const speeds: number[] = [];
  for (let i = 0; i < pts.length - 1; i++) {
    const p = pts[i], q = pts[i + 1];
    const dx = q.x - p.x, dy = q.y - p.y;
    const dt = Math.max((q.timestamp_ms - p.timestamp_ms) / 1000, 0.001);
    speeds.push(Math.hypot(dx, dy) / dt);
  }
  const sorted = [...speeds].sort((a, b) => a - b);
  const p5  = sorted[Math.floor(sorted.length * 0.05)] ?? 0;
  const p95 = sorted[Math.floor(sorted.length * 0.95)] ?? 1;
  const norm = (s: number) => Math.max(0, Math.min(1, (s - p5) / (p95 - p5 || 1)));

  // ── Clear + grid ──────────────────────────────────────────────────────────
  ctx.clearRect(0, 0, CW, CH);
  ctx.strokeStyle = "rgba(255,255,255,0.04)";
  ctx.lineWidth = 1;
  for (let gx = 0; gx <= 4; gx++) {
    const px = PAD + (W / 4) * gx;
    ctx.beginPath(); ctx.moveTo(px, PAD); ctx.lineTo(px, CH - PAD); ctx.stroke();
  }
  for (let gy = 0; gy <= 4; gy++) {
    const py = PAD + (H / 4) * gy;
    ctx.beginPath(); ctx.moveTo(PAD, py); ctx.lineTo(CW - PAD, py); ctx.stroke();
  }

  // ── Viewport rectangle (follow-cam only) ─────────────────────────────────
  if (!showFull) {
    const vpW = viewportPx * scale;
    const vpH = vpW * (9 / 16);
    const vpX = CW / 2 - vpW / 2;
    const vpY = CH / 2 - vpH / 2;

    ctx.save();
    ctx.fillStyle = "rgba(0,0,0,0.35)";
    ctx.fillRect(0,          0,    CW,        vpY);
    ctx.fillRect(0,          vpY + vpH, CW,   CH - (vpY + vpH));
    ctx.fillRect(0,          vpY,  vpX,       vpH);
    ctx.fillRect(vpX + vpW,  vpY,  CW - (vpX + vpW), vpH);
    ctx.restore();

    ctx.save();
    ctx.strokeStyle = "rgba(0,210,255,0.55)";
    ctx.lineWidth = 1.5;
    ctx.setLineDash([6, 4]);
    ctx.strokeRect(vpX, vpY, vpW, vpH);
    ctx.setLineDash([]);
    ctx.restore();

    ctx.save();
    ctx.strokeStyle = "rgba(0,210,255,0.35)";
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(CW / 2 - 8, CH / 2); ctx.lineTo(CW / 2 + 8, CH / 2); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(CW / 2, CH / 2 - 8); ctx.lineTo(CW / 2, CH / 2 + 8); ctx.stroke();
    ctx.restore();
  }

  // ── Path segments ─────────────────────────────────────────────────────────
  for (let i = 0; i < Math.min(count - 1, speeds.length); i++) {
    let alpha = 1;
    if (trailFadeMs > 0 && !showFull) {
      const age = playbackMs - pts[i + 1].timestamp_ms;
      if (age > trailFadeMs) continue;
      alpha = Math.max(0, 1 - age / trailFadeMs);
    }
    ctx.beginPath();
    ctx.moveTo(toX(pts[i].x), toY(pts[i].y));
    ctx.lineTo(toX(pts[i + 1].x), toY(pts[i + 1].y));
    ctx.strokeStyle = speedColor(norm(speeds[i]), alpha);
    ctx.lineWidth = 1.8;
    ctx.lineCap = "round";
    ctx.stroke();
  }

  // ── Overshoot markers ─────────────────────────────────────────────────────
  const cutoff = showFull
    ? Infinity
    : (pts[Math.min(count - 1, pts.length - 1)]?.timestamp_ms ?? Infinity);

  for (const o of overshoots) {
    if (o.timestamp_ms > cutoff) continue;
    let oA = 0.85;
    if (trailFadeMs > 0 && !showFull) {
      const age = playbackMs - o.timestamp_ms;
      if (age > trailFadeMs) continue;
      oA = Math.max(0, 0.85 * (1 - age / trailFadeMs));
    }
    const px = toX(o.x), py = toY(o.y);
    ctx.beginPath();
    ctx.moveTo(px,     py - 10);
    ctx.lineTo(px + 7, py +  4);
    ctx.lineTo(px - 7, py +  4);
    ctx.closePath();
    ctx.fillStyle   = `rgba(255,80,30,${oA})`;
    ctx.strokeStyle = `rgba(255,160,80,${Math.min(1, oA * 1.05)})`;
    ctx.lineWidth   = 1;
    ctx.fill();
    ctx.stroke();
  }

  // ── Click markers ─────────────────────────────────────────────────────────
  for (let i = 0; i < Math.min(count, pts.length); i++) {
    const p = pts[i];
    if (!p.is_click) continue;
    let cA = 0.9;
    if (trailFadeMs > 0 && !showFull) {
      const age = playbackMs - p.timestamp_ms;
      if (age > trailFadeMs) continue;
      cA = Math.max(0, 0.9 * (1 - age / trailFadeMs));
    }
    ctx.beginPath();
    ctx.arc(toX(p.x), toY(p.y), 5, 0, Math.PI * 2);
    ctx.fillStyle   = `rgba(255,220,30,${cA})`;
    ctx.strokeStyle = `rgba(255,255,255,${cA * 0.55})`;
    ctx.lineWidth   = 1;
    ctx.fill();
    ctx.stroke();
  }

  if (!showFull && hitTimestampsMs.length > 0) {
    const activeHit = hitTimestampsMs.find((ts) => Math.abs(playbackMs - ts) <= 90);
    if (activeHit != null) {
      const anchor = pts[Math.max(0, Math.min(count - 1, pts.length - 1))];
      const pulse = Math.max(0, 1 - Math.abs(playbackMs - activeHit) / 90);
      const px = toX(anchor.x);
      const py = toY(anchor.y);
      const size = denseHitStream ? 8 : 10;
      const gap = denseHitStream ? 3 : 4;
      const alpha = denseHitStream ? 0.4 + pulse * 0.3 : 0.55 + pulse * 0.35;
      ctx.save();
      ctx.strokeStyle = `rgba(255,255,255,${alpha})`;
      ctx.lineWidth = 2;
      ctx.lineCap = "round";
      ctx.beginPath();
      ctx.moveTo(px - size, py - size);
      ctx.lineTo(px - gap, py - gap);
      ctx.moveTo(px + gap, py + gap);
      ctx.lineTo(px + size, py + size);
      ctx.moveTo(px + size, py - size);
      ctx.lineTo(px + gap, py - gap);
      ctx.moveTo(px - gap, py + gap);
      ctx.lineTo(px - size, py + size);
      ctx.stroke();
      ctx.strokeStyle = `rgba(0,245,160,${alpha * 0.7})`;
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.restore();
    }
  }

  // ── Start / end markers ───────────────────────────────────────────────────
  ctx.beginPath();
  ctx.arc(toX(pts[0].x), toY(pts[0].y), 5, 0, Math.PI * 2);
  ctx.fillStyle = "rgba(0,245,160,0.9)";
  ctx.fill();

  if (showFull) {
    const last = pts[pts.length - 1];
    ctx.beginPath();
    ctx.arc(toX(last.x), toY(last.y), 5, 0, Math.PI * 2);
    ctx.fillStyle = "rgba(255,80,80,0.9)";
    ctx.fill();
  }

}

// ─── Component ────────────────────────────────────────────────────────────────

/** Binary-search nearest recording frame for the given playback time. */
/** Maps an absolute replay-timeline ms onto the video file, in seconds. */
function videoTimeForReplayMs(video: ReplayVideo, replayMs: number): number | null {
  const segments = video.segments;
  if (segments.length === 0) return null;
  for (const segment of segments) {
    const end = segment.replay_start_ms + segment.duration_ms;
    if (replayMs < segment.replay_start_ms) {
      // In a gap before this segment: hold its first frame.
      return segment.video_start_ms / 1000;
    }
    if (replayMs <= end) {
      return (segment.video_start_ms + (replayMs - segment.replay_start_ms)) / 1000;
    }
  }
  const last = segments[segments.length - 1];
  return (last.video_start_ms + last.duration_ms) / 1000;
}

function nearestFrameIdx(frames: ScreenFrame[], ms: number): number {
  if (!frames.length) return 0;
  let lo = 0, hi = frames.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (frames[mid].timestamp_ms < ms) lo = mid + 1;
    else hi = mid;
  }
  if (lo > 0 && ms - frames[lo - 1].timestamp_ms < frames[lo].timestamp_ms - ms) lo--;
  return lo;
}

const SPEED_OPTIONS = [0.25, 0.5, 1, 2, 4];

const VIEWPORT_PRESETS: { label: string; w: number }[] = [
  { label: "1080p", w: 960  },  // 50% of 1920
  { label: "1440p", w: 1280 },  // 50% of 2560
  { label: "4K",    w: 1920 },  // 50% of 3840
];

const TRAIL_OPTIONS = [
  { label: "Off",  ms: 0     },
  { label: "0.5s", ms: 500   },
  { label: "1s",   ms: 1000  },
  { label: "3s",   ms: 3000  },
  { label: "5s",   ms: 5000  },
];

const BG_OPACITY_OPTIONS = [
  { label: "25%", v: 0.25 },
  { label: "50%", v: 0.50 },
  { label: "75%", v: 0.75 },
];

interface Props {
  rawPositions: RawPositionPoint[];
  metricPoints: MetricPoint[];
  screenFrames: ScreenFrame[];
  /** FTHR video; used instead of `screenFrames` when present. */
  screenVideo?: ReplayVideo | null;
  hitTimestampsMs?: number[];
  segmentLabel?: string | null;
  segmentWindowLabel?: string | null;
  timelineMarkers?: Array<{
    id: string;
    timestamp_ms: number;
    color: string;
    label: string;
  }>;
  timelineWindows?: Array<{
    id: string;
    start_ms: number;
    end_ms: number;
    color: string;
    label: string;
  }>;
}

export function MousePathViewer({
  rawPositions,
  metricPoints,
  screenFrames,
  screenVideo = null,
  hitTimestampsMs = [],
  segmentLabel,
  segmentWindowLabel,
  timelineMarkers = [],
  timelineWindows = [],
}: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const animRef   = useRef<number | null>(null);
  const videoRef  = useRef<HTMLVideoElement>(null);
  const [videoError, setVideoError] = useState(false);

  const video = screenVideo && screenVideo.path && screenVideo.segments.length > 0 ? screenVideo : null;
  const videoSrc = useMemo(
    () => (video && isTauri() ? convertFileSrc(video.path) : null),
    [video],
  );
  useEffect(() => setVideoError(false), [videoSrc]);

  const replayStartMs = useMemo(() => {
    let start = Number.POSITIVE_INFINITY;
    if (rawPositions.length > 0) {
      start = Math.min(start, rawPositions[0].timestamp_ms);
    }
    if (screenFrames.length > 0) {
      start = Math.min(start, screenFrames[0].timestamp_ms);
    }
    return Number.isFinite(start) ? start : 0;
  }, [rawPositions, screenFrames]);

  const videoEndMs = useMemo(() => {
    if (!video) return 0;
    const last = video.segments[video.segments.length - 1];
    return Math.max(0, last.replay_start_ms + last.duration_ms - replayStartMs);
  }, [video, replayStartMs]);

  const positions = useMemo(
    () => rawPositions.map((point) => ({
      ...point,
      timestamp_ms: Math.max(0, point.timestamp_ms - replayStartMs),
    })),
    [rawPositions, replayStartMs],
  );

  const frames = useMemo(
    () => screenFrames.map((frame) => ({
      ...frame,
      timestamp_ms: Math.max(0, frame.timestamp_ms - replayStartMs),
    })),
    [screenFrames, replayStartMs],
  );

  const normalizedHitTimestamps = useMemo(
    () => hitTimestampsMs.map((ts) => Math.max(0, ts - replayStartMs)),
    [hitTimestampsMs, replayStartMs],
  );

  const [isPlaying,    setIsPlaying]    = useState(false);
  const [playbackMs,   setPlaybackMs]   = useState(0);
  const [speed,        setSpeed]        = useState(1);
  const [showFull,     setShowFull]     = useState(false);
  const [trailFadeMs,  setTrailFadeMs]  = useState(5000);
  const [bgOpacity,    setBgOpacity]    = useState(0.5);
  const [viewportW,    setViewportW]    = useState(960);

  const playableVideo = Boolean(videoSrc) && !videoError;
  const hasVideo = frames.length > 0 || playableVideo;

  const lastPositionMs = positions.length > 0
    ? positions[positions.length - 1].timestamp_ms
    : 0;
  const lastFrameMs = frames.length > 0
    ? frames[frames.length - 1].timestamp_ms
    : 0;
  const durationMs = Math.max(lastPositionMs, lastFrameMs, videoEndMs);
  const denseHitStream = useMemo(
    () => shouldClusterHitIndicators(normalizedHitTimestamps, durationMs),
    [durationMs, normalizedHitTimestamps],
  );
  const hitTimelineWindows = useMemo(
    () => (denseHitStream ? clusterHitWindows(normalizedHitTimestamps) : []),
    [denseHitStream, normalizedHitTimestamps],
  );

  const overshoots = useCallback(() => detectOvershoots(positions), [positions])();
  const suggestion = useCallback(
    () => computeSensitivitySuggestion(metricPoints),
    [metricPoints],
  )();
  const externalTimelineMarkers = useMemo(
    () => timelineMarkers
      .map((marker) => ({
        ...marker,
        timestamp_ms: Math.max(0, marker.timestamp_ms - replayStartMs),
      }))
      .filter((marker) => marker.timestamp_ms <= durationMs),
    [durationMs, replayStartMs, timelineMarkers],
  );
  const externalTimelineWindows = useMemo(
    () => timelineWindows
      .map((window) => ({
        ...window,
        start_ms: Math.max(0, window.start_ms - replayStartMs),
        end_ms: Math.max(0, window.end_ms - replayStartMs),
      }))
      .filter((window) => window.end_ms >= 0 && window.start_ms <= durationMs),
    [durationMs, replayStartMs, timelineWindows],
  );

  // Keep the video on the replay clock: seek while paused or scrubbing, and
  // let it play natively (correcting drift) while the replay plays.
  useEffect(() => {
    const element = videoRef.current;
    if (!element || !video || !playableVideo) return;
    const target = videoTimeForReplayMs(video, playbackMs + replayStartMs);
    if (target === null) return;
    if (!isPlaying || showFull) {
      if (!element.paused) element.pause();
      if (Math.abs(element.currentTime - target) > 0.02) element.currentTime = target;
      return;
    }
    if (element.playbackRate !== speed) element.playbackRate = speed;
    if (Math.abs(element.currentTime - target) > 0.25) element.currentTime = target;
    if (element.paused) void element.play().catch(() => undefined);
  }, [video, playableVideo, playbackMs, replayStartMs, isPlaying, showFull, speed]);

  // Nearest game recording frame for the current playback position
  const currentFrameSrc = useMemo<string | null>(() => {
    if (frames.length === 0 || playableVideo || showFull) return null;
    const idx = nearestFrameIdx(frames, playbackMs);
    return `data:image/jpeg;base64,${frames[idx].jpeg_b64}`;
  }, [frames, playbackMs, showFull, hasVideo]);

  // ── Redraw on state change ────────────────────────────────────────────────
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    if (positions.length < 2) {
      const ctx = canvas.getContext("2d");
      ctx?.clearRect(0, 0, canvas.width, canvas.height);
      return;
    }
    const fraction = showFull
      ? -1
      : durationMs > 0
        ? Math.min(1, playbackMs / durationMs)
        : -1;
    drawPath(canvas, positions, overshoots, fraction, showFull ? 0 : trailFadeMs, playbackMs, viewportW, normalizedHitTimestamps);
  }, [positions, overshoots, playbackMs, durationMs, showFull, trailFadeMs, viewportW, normalizedHitTimestamps]);

  // ── Playback loop ─────────────────────────────────────────────────────────
  useEffect(() => {
    if (!isPlaying) {
      if (animRef.current != null) {
        cancelAnimationFrame(animRef.current);
        animRef.current = null;
      }
      return;
    }

    let lastTs: number | null = null;

    const tick = (ts: number) => {
      if (lastTs !== null) {
        const delta = (ts - lastTs) * speed;
        setPlaybackMs((prev) => {
          const next = prev + delta;
          if (next >= durationMs) {
            setIsPlaying(false);
            return durationMs;
          }
          return next;
        });
      }
      lastTs = ts;
      animRef.current = requestAnimationFrame(tick);
    };

    animRef.current = requestAnimationFrame(tick);
    return () => {
      if (animRef.current != null) {
        cancelAnimationFrame(animRef.current);
        animRef.current = null;
      }
    };
  }, [isPlaying, speed, durationMs]);

  const handleScrub = (e: React.ChangeEvent<HTMLInputElement>) => {
    setIsPlaying(false);
    setShowFull(false);
    setPlaybackMs(Number(e.target.value));
  };

  const handlePlayPause = () => {
    setShowFull(false);
    if (playbackMs >= durationMs) setPlaybackMs(0);
    setIsPlaying((v) => !v);
  };

  const handleShowFull = () => {
    setIsPlaying(false);
    setShowFull((v) => !v);
  };

  const formatMs = (ms: number) =>
    `${Math.floor(ms / 60000)}:${String(Math.floor((ms % 60000) / 1000)).padStart(2, "0")}.${String(Math.floor((ms % 1000) / 100)).padStart(1, "0")}`;

  const overshootTimelineMarkers = useMemo(
    () => overshoots.map((marker, index) => ({
      id: `overshoot-${index}`,
      timestamp_ms: marker.timestamp_ms,
      color: "#ff6b6b",
      label: `Overshoot at ${formatMs(marker.timestamp_ms)}`,
    })),
    [overshoots],
  );

  if (positions.length < 2) {
    return (
      <div
        className="rounded-xl p-4 mb-4"
        style={{
          background: C.surface,
          border: `1px solid ${C.border}`,
        }}
      >
        <h3
          className="text-xs uppercase tracking-widest mb-2"
          style={{ color: C.textFaint }}
        >
          Mouse Path Replay
        </h3>
        <p style={{ color: C.textMuted, fontSize: 13 }}>
          {segmentLabel
            ? "No mouse path was recorded in the selected moment."
            : "No mouse path was recorded for this run."}
        </p>
      </div>
    );
  }

  return (
    <div className="mb-4 flex flex-col gap-3">
      {/* Canvas */}
      <div
        className="rounded-xl overflow-hidden"
        style={{
          background: C.glass,
          border: `1px solid ${C.border}`,
          backdropFilter: "blur(16px) saturate(180%)",
        }}
      >
        {/* Header */}
        <div
          className="flex items-center justify-between px-4 py-2"
          style={{ borderBottom: `1px solid ${C.borderSub}` }}
        >
          <h3
            className="text-xs uppercase tracking-widest"
            style={{ color: C.textFaint }}
          >
            Mouse Path Replay
          </h3>
          <div className="flex items-center gap-3 flex-wrap justify-end" style={{ fontSize: 11, color: C.textFaint }}>
            {segmentLabel && (
              <span
                style={{
                  color: "rgba(0,245,160,0.82)",
                  border: "1px solid rgba(0,245,160,0.24)",
                  background: "rgba(0,245,160,0.08)",
                  borderRadius: 999,
                  padding: "3px 8px",
                }}
              >
                Segment: {segmentLabel}{segmentWindowLabel ? ` · ${segmentWindowLabel}` : ""}
              </span>
            )}
            {/* Stat pills */}
            {playableVideo && video && (
              <span style={{ color: "rgba(0,245,160,0.7)" }}>
                ● {video.codec.replace(/_nvenc$|_amf$|_qsv$/, "").toUpperCase()} video {video.width}×{video.height}
              </span>
            )}
            {!playableVideo && frames.length > 0 && (
              <span style={{ color: "rgba(0,245,160,0.7)" }}>
                ● {frames.length} frames
              </span>
            )}
            {videoSrc && videoError && (
              <span style={{ color: "rgba(255,120,120,0.7)" }} title="This replay's video codec isn't supported by AimMod's built-in browser.">
                ● video can't play here
              </span>
            )}
            {!hasVideo && !(videoSrc && videoError) && (
              <span style={{ color: "rgba(255,120,120,0.7)" }}>
                ● no video frames
              </span>
            )}
            <span>
              <span style={{ color: "rgba(255,220,30,0.9)" }}>●</span>{" "}
              {positions.filter((p) => p.is_click).length} clicks
            </span>
            {hitTimestampsMs.length > 0 && (
              <span>
                <span style={{ color: "rgba(0,245,160,0.9)" }}>✦</span>{" "}
                {hitTimestampsMs.length} hits
              </span>
            )}
            <span>
              <span style={{ color: "rgba(255,80,30,0.8)" }}>▲</span>{" "}
              {overshoots.length} overshoots
            </span>
            <span>{positions.length.toLocaleString()} samples</span>
          </div>
        </div>

        {/* Legend bar */}
        <div
          className="flex items-center gap-3 px-4 py-1.5 text-xs"
          style={{
            borderBottom: `1px solid ${C.borderSub}`,
            color: C.textFaint,
          }}
        >
          <span>Speed:</span>
          <div
            className="rounded"
            style={{
              width: 120,
              height: 8,
              background: "linear-gradient(to right, rgb(30,100,255), rgb(0,200,220), rgb(0,220,80), rgb(255,220,0), rgb(255,50,30))",
            }}
          />
          <span>slow → fast</span>
          <span className="ml-2">
            <span style={{ color: "rgba(0,245,160,0.9)" }}>●</span> start
          </span>
          <span>
            <span style={{ color: "rgba(255,80,80,0.9)" }}>●</span> end
          </span>
          <span>
            <span style={{ color: "rgba(255,220,30,0.9)" }}>●</span> click
          </span>
          <span>
            <span style={{ color: "rgba(255,80,30,0.85)" }}>▲</span> overshoot
          </span>
          <span>
            <span style={{ color: "rgba(0,245,160,0.9)" }}>✦</span> hit
          </span>
          <span>
            <span style={{ color: "rgba(0,210,255,0.7)" }}>⬜</span> viewport
          </span>
        </div>

        {/* Canvas + game recording underlay */}
        <div style={{ position: "relative", lineHeight: 0 }}>
          {videoSrc && !videoError && (
            <video
              ref={videoRef}
              src={videoSrc}
              muted
              playsInline
              preload="auto"
              onError={(event) => {
                setVideoError(true);
                const element = event.currentTarget;
                const probe = document.createElement("video");
                const canPlay = [
                  'video/mp4; codecs="hev1.1.6.L93.B0"',
                  'video/mp4; codecs="hvc1.1.6.L93.B0"',
                  'video/mp4; codecs="avc1.42E01E"',
                ].map((type) => `${type} => "${probe.canPlayType(type)}"`).join("; ");
                const src = element.currentSrc || videoSrc || "";
                void fetch(src, { method: "HEAD" })
                  .then((res) => `HTTP ${res.status} ${res.headers.get("content-type") ?? ""}`)
                  .catch((err) => `fetch failed: ${String(err)}`)
                  .then((fetchResult) => {
                    logError(
                      "replay_viewer",
                      `video error code=${element.error?.code ?? "?"} message=${element.error?.message ?? ""} src=${src} fetch=${fetchResult} canPlayType: ${canPlay}`,
                    );
                  });
              }}
              style={{
                // Same placement as the JPEG underlay below.
                position: "absolute",
                left:   "17.5%",
                top:    "18.37%",
                width:  "65%",
                height: "63.27%",
                objectFit: "fill",
                opacity: showFull ? 0 : bgOpacity,
                pointerEvents: "none",
              }}
            />
          )}
          {currentFrameSrc && !showFull && (
            <img
              src={currentFrameSrc}
              alt=""
              style={{
                // The canvas is 900×520 internal px. The viewport rectangle is always
                // 65% wide centred horizontally, and 65%×(9/16) tall centred vertically.
                // Match those proportions exactly so the footage aligns with the path.
                position: "absolute",
                left:   "17.5%",   // (1 - 0.65) / 2 × 100
                top:    "18.37%",  // (1 - 0.65×9/16 / (520/900)) / 2 × 100
                width:  "65%",
                height: "63.27%",  // 0.65 × (9/16) × (900/520)
                objectFit: "fill",
                opacity: bgOpacity,
                pointerEvents: "none",
              }}
            />
          )}
          <canvas
            ref={canvasRef}
            width={900}
            height={520}
            style={{ width: "100%", height: "auto", display: "block", position: "relative", zIndex: 1 }}
          />
        </div>

        {/* Controls */}
        <div
          className="px-4 py-3 flex flex-col gap-2"
          style={{ borderTop: `1px solid ${C.borderSub}` }}
        >
          {/* Timeline scrubber */}
          <div className="flex items-center gap-3">
            <span
              className="tabular-nums"
              style={{ color: C.textMuted, fontSize: 11, minWidth: 60 }}
            >
              {formatMs(showFull ? durationMs : playbackMs)}
            </span>
            <div className="flex-1" style={{ display: "flex", flexDirection: "column", gap: 4 }}>
              <input
                type="range"
                min={0}
                max={durationMs}
                step={50}
                value={showFull ? durationMs : playbackMs}
                onChange={handleScrub}
                className="flex-1"
                style={{ accentColor: "#00f5a0" }}
              />
              {durationMs > 0 && (externalTimelineMarkers.length > 0 || externalTimelineWindows.length > 0 || overshootTimelineMarkers.length > 0 || hitTimestampsMs.length > 0) && (
                <div
                  style={{
                    position: "relative",
                    height: 14,
                    borderRadius: 999,
                    background: "rgba(255,255,255,0.04)",
                    overflow: "hidden",
                  }}
                >
                  {externalTimelineWindows.map((window) => {
                    const leftPct = (window.start_ms / durationMs) * 100;
                    const widthPct = Math.max(1, ((window.end_ms - window.start_ms) / durationMs) * 100);
                    return (
                      <div
                        key={window.id}
                        title={window.label}
                        style={{
                          position: "absolute",
                          left: `${leftPct}%`,
                          width: `${widthPct}%`,
                          top: 0,
                          bottom: 0,
                          background: `${window.color}30`,
                          borderLeft: `1px solid ${window.color}`,
                          borderRight: `1px solid ${window.color}`,
                        }}
                      />
                    );
                  })}
                  {[...overshootTimelineMarkers, ...externalTimelineMarkers].map((marker) => (
                    <div
                      key={marker.id}
                      title={marker.label}
                      style={{
                        position: "absolute",
                        left: `${(marker.timestamp_ms / durationMs) * 100}%`,
                        top: 0,
                        bottom: 0,
                        width: 2,
                        background: marker.color,
                        boxShadow: `0 0 0 1px ${marker.color}55`,
                      }}
                    />
                  ))}
                  {denseHitStream
                    ? hitTimelineWindows.map((window, index) => {
                        const leftPct = (window.start_ms / durationMs) * 100;
                        const widthPct = Math.max(0.8, ((Math.max(window.end_ms, window.start_ms + 120) - window.start_ms) / durationMs) * 100);
                        return (
                          <div
                            key={`hit-window-${window.start_ms}-${index}`}
                            title={`Contact window around ${formatMs(window.start_ms)}`}
                            style={{
                              position: "absolute",
                              left: `${leftPct}%`,
                              width: `${widthPct}%`,
                              top: 2,
                              bottom: 2,
                              background: "rgba(0,245,160,0.28)",
                              borderLeft: "1px solid rgba(0,245,160,0.75)",
                              borderRight: "1px solid rgba(0,245,160,0.32)",
                            }}
                          />
                        );
                      })
                    : normalizedHitTimestamps.map((timestampMs, index) => (
                        <div
                          key={`hit-${timestampMs}-${index}`}
                          title={`Hit at ${formatMs(timestampMs)}`}
                          style={{
                            position: "absolute",
                            left: `${(timestampMs / durationMs) * 100}%`,
                            top: 2,
                            bottom: 2,
                            width: 1,
                            background: "#00f5a0",
                            opacity: 0.65,
                          }}
                        />
                      ))}
                </div>
              )}
            </div>
            <span
              className="tabular-nums"
              style={{ color: C.textFaint, fontSize: 11, minWidth: 60, textAlign: "right" }}
            >
              {formatMs(durationMs)}
            </span>
          </div>

          {/* Buttons */}
          <div className="flex items-center gap-2 flex-wrap">
            {/* Play / pause */}
            <button
              onClick={handlePlayPause}
              className="am-btn rounded-lg px-4 py-1.5 text-sm font-semibold"
              style={{
                background: isPlaying ? C.accentDim : accentAlpha("1f"),
                border: `1px solid ${C.accentBorder}`,
                color: C.accent,
                cursor: "pointer",
              }}
            >
              {isPlaying ? "⏸ Pause" : playbackMs >= durationMs ? "↺ Replay" : "▶ Play"}
            </button>

            {/* Reset */}
            <button
              onClick={() => { setIsPlaying(false); setPlaybackMs(0); setShowFull(false); }}
              className="am-btn rounded-lg px-3 py-1.5 text-sm"
              style={{
                background: "rgba(255,255,255,0.05)",
                border: `1px solid ${C.border}`,
                color: C.textMuted,
                cursor: "pointer",
              }}
            >
              ↺ Reset
            </button>

            {/* Show full path toggle */}
            <button
              onClick={handleShowFull}
              className="am-btn rounded-lg px-3 py-1.5 text-sm"
              style={{
                background: showFull ? "rgba(167,139,250,0.18)" : "rgba(167,139,250,0.08)",
                border: `1px solid rgba(167,139,250,${showFull ? 0.5 : 0.2})`,
                color: "#a78bfa",
                cursor: "pointer",
              }}
            >
              {showFull ? "● Full Path" : "Show Full Path"}
            </button>

            {/* Speed selector */}
            <div className="flex items-center gap-1 ml-auto">
              <span style={{ color: C.textFaint, fontSize: 11, marginRight: 4 }}>Speed</span>
              {SPEED_OPTIONS.map((s) => (
                <button
                  key={s}
                  onClick={() => setSpeed(s)}
                  className="am-btn rounded px-2 py-1 text-xs"
                  style={{
                    background: speed === s ? `${C.info}33` : "rgba(255,255,255,0.05)",
                    border: `1px solid ${speed === s ? `${C.info}80` : C.borderSub}`,
                    color: speed === s ? C.info : C.textMuted,
                    cursor: "pointer",
                  }}
                >
                  {s}×
                </button>
              ))}
            </div>
          </div>

          {/* Trail fade + video opacity row */}
          <div
            className="flex items-center gap-4 flex-wrap"
            style={{ borderTop: `1px solid ${C.borderSub}`, paddingTop: 8 }}
          >
            {/* Trail fade */}
            <div className="flex items-center gap-1">
              <span style={{ color: C.textFaint, fontSize: 11, marginRight: 4 }}>Trail</span>
              {TRAIL_OPTIONS.map(({ label, ms }) => (
                <button
                  key={label}
                  onClick={() => setTrailFadeMs(ms)}
                  className="am-btn rounded px-2 py-1 text-xs"
                  style={{
                    background: trailFadeMs === ms ? "rgba(167,139,250,0.2)" : "rgba(255,255,255,0.05)",
                    border: `1px solid ${trailFadeMs === ms ? "rgba(167,139,250,0.5)" : C.borderSub}`,
                    color: trailFadeMs === ms ? "#a78bfa" : C.textMuted,
                    cursor: "pointer",
                  }}
                >
                  {label}
                </button>
              ))}
            </div>

            {/* Viewport size — selects the monitor resolution so the FOV rect scales correctly */}
            <div className="flex items-center gap-1">
              <span style={{ color: C.textFaint, fontSize: 11, marginRight: 4 }}>FOV</span>
              {VIEWPORT_PRESETS.map(({ label, w }) => (
                <button
                  key={label}
                  onClick={() => setViewportW(w)}
                  className="am-btn rounded px-2 py-1 text-xs"
                  style={{
                    background: viewportW === w ? "rgba(0,210,255,0.18)" : "rgba(255,255,255,0.05)",
                    border: `1px solid ${viewportW === w ? "rgba(0,210,255,0.5)" : C.borderSub}`,
                    color: viewportW === w ? "#00d2ff" : C.textMuted,
                    cursor: "pointer",
                  }}
                >
                  {label}
                </button>
              ))}
            </div>

            {/* Video opacity — only shown when game recording is available */}
            {hasVideo && (
              <div className="flex items-center gap-1 ml-auto">
                <span style={{ color: C.textFaint, fontSize: 11, marginRight: 4 }}>Video</span>
                {BG_OPACITY_OPTIONS.map(({ label, v }) => (
                  <button
                    key={label}
                    onClick={() => setBgOpacity(v)}
                    className="am-btn rounded px-2 py-1 text-xs"
                    style={{
                      background: bgOpacity === v ? C.accentDim : "rgba(255,255,255,0.05)",
                      border: `1px solid ${bgOpacity === v ? C.accentBorder : C.borderSub}`,
                      color: bgOpacity === v ? C.accent : C.textMuted,
                      cursor: "pointer",
                    }}
                  >
                    {label}
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Overshoot summary (only if any detected) */}
      {overshoots.length > 0 && (
        <div
          className="rounded-xl px-4 py-3"
          style={{
            background: C.surface,
            border: `1px solid ${C.border}`,
            borderLeft: `3px solid ${C.danger}`,
          }}
        >
          <div className="flex items-center gap-2 mb-1">
            <span style={{ color: C.danger, fontSize: 13, fontWeight: 600 }}>
              ▲ {overshoots.length} Overshoot{overshoots.length !== 1 ? "s" : ""} Detected
            </span>
          </div>
          <p style={{ color: C.textSub, fontSize: 12, lineHeight: 1.5 }}>
            Orange triangles indicate where your cursor reversed direction sharply after a fast
            movement — a sign the flick carried past the intended target. The same moments are also marked on the replay timeline so you can scrub directly to them.
          </p>
        </div>
      )}

      {/* Sensitivity training suggestion */}
      {suggestion && (
        <div
          className="rounded-xl px-4 py-4"
          style={{
            background: C.surface,
            border: `1px solid ${C.border}`,
            borderLeft: `3px solid ${suggestion.color}`,
          }}
        >
          <div className="flex items-start justify-between gap-3 mb-2">
            <span style={{ color: suggestion.color, fontSize: 13, fontWeight: 700 }}>
              Sensitivity Training Suggestion
            </span>
            <span
              className="rounded-full px-3 py-0.5 text-xs font-bold"
              style={{
                background: `${suggestion.color}25`,
                border:     `1px solid ${suggestion.color}60`,
                color:      suggestion.color,
                whiteSpace: "nowrap",
              }}
            >
              {suggestion.badge}
            </span>
          </div>
          <p
            className="text-xs font-semibold mb-1"
            style={{ color: suggestion.color }}
          >
            {suggestion.headline}
          </p>
          <p style={{ color: C.textSub, fontSize: 12, lineHeight: 1.6 }}>
            {suggestion.detail}
          </p>
          <p
            className="mt-2 text-xs italic"
            style={{ color: C.textFaint }}
          >
            This is a temporary training sensitivity — not a permanent setting. Switch back after 5–10 runs.
          </p>
        </div>
      )}
    </div>
  );
}
