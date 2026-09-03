import { invoke, isTauri } from "@tauri-apps/api/core";

export interface OsuDiagnosticEvent {
  area: "workspace" | "previewAudio" | "replayAnalysis" | "nativeReplay";
  event: string;
  sourceId?: string | null;
  mediaErrorCode?: number | null;
  networkState?: number | null;
  readyState?: number | null;
}

type DiagnosticSink = (event: OsuDiagnosticEvent) => void;

export class BoundedDiagnosticReporter {
  private readonly lastSent = new Map<string, number>();
  private readonly sink: DiagnosticSink;
  private readonly dedupeMs: number;
  private readonly maxPerMinute: number;
  private windowStartedAt = 0;
  private sentInWindow = 0;

  constructor(sink: DiagnosticSink, dedupeMs = 1_000, maxPerMinute = 80) {
    this.sink = sink;
    this.dedupeMs = dedupeMs;
    this.maxPerMinute = maxPerMinute;
  }

  report(event: OsuDiagnosticEvent, now = Date.now()) {
    if (now - this.windowStartedAt >= 60_000) {
      this.windowStartedAt = now;
      this.sentInWindow = 0;
    }
    if (this.sentInWindow >= this.maxPerMinute) return false;

    const normalized = normalizeEvent(event);
    const key = JSON.stringify(normalized);
    const previous = this.lastSent.get(key);
    if (previous !== undefined && now - previous < this.dedupeMs) return false;

    this.lastSent.set(key, now);
    this.sentInWindow += 1;
    if (this.lastSent.size > 128) {
      for (const [candidate, sentAt] of this.lastSent) {
        if (now - sentAt >= this.dedupeMs) this.lastSent.delete(candidate);
        if (this.lastSent.size <= 96) break;
      }
    }
    this.sink(normalized);
    return true;
  }
}

function normalizeEvent(event: OsuDiagnosticEvent): OsuDiagnosticEvent {
  const state = (value: number | null | undefined) => Number.isInteger(value) ? value : null;
  return {
    area: event.area,
    event: event.event.slice(0, 48),
    sourceId: event.sourceId?.slice(0, 72) ?? null,
    mediaErrorCode: state(event.mediaErrorCode),
    networkState: state(event.networkState),
    readyState: state(event.readyState),
  };
}

function shortHash(value: string) {
  let hash = 0x811c9dc5;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}

export function privateSourceId(value: string | null | undefined, prefix = "item") {
  if (!value) return null;
  const contentHash = value.match(/[0-9a-f]{64}/i)?.[0];
  return contentHash ? contentHash.toLowerCase() : `${prefix}-${shortHash(value)}`;
}

export function mediaDiagnosticState(audio: HTMLAudioElement) {
  return {
    mediaErrorCode: audio.error?.code ?? null,
    networkState: audio.networkState,
    readyState: audio.readyState,
  };
}

const reporter = new BoundedDiagnosticReporter((event) => {
  if (!isTauri()) return;
  void invoke("record_osu_diagnostic", { diagnostic: event }).catch(() => undefined);
});

export function recordOsuDiagnostic(event: OsuDiagnosticEvent) {
  return reporter.report(event);
}
