import type { WorkspaceBeatmap } from "./models.ts";

function remoteStatusIsMissing(status: string) {
  return status === "" || status === "Not supplied";
}

export function mergeInstalledBeatmap(remote: WorkspaceBeatmap, local: WorkspaceBeatmap): WorkspaceBeatmap {
  return {
    ...remote,
    localState: "Installed",
    starRating: remote.starRating ?? local.starRating,
    bpm: remote.bpm ?? local.bpm,
    lengthSeconds: remote.lengthSeconds ?? local.lengthSeconds,
    status: remoteStatusIsMissing(remote.status) ? local.status : remote.status,
    coverImageUrl: remote.coverImageUrl ?? local.coverImageUrl,
    audioUrl: remote.audioUrl ?? local.audioUrl,
    audioPreviewTimeMs: remote.audioPreviewTimeMs ?? local.audioPreviewTimeMs,
    skillsets: remote.skillsets.length ? remote.skillsets : local.skillsets,
    plays: remote.plays ?? local.plays,
    favorites: remote.favorites ?? local.favorites,
    pp95: remote.pp95 ?? local.pp95,
    accuracy: local.accuracy ?? remote.accuracy,
    circleSize: remote.circleSize ?? local.circleSize,
    approachRate: remote.approachRate ?? local.approachRate,
    overallDifficulty: remote.overallDifficulty ?? local.overallDifficulty,
    hpDrain: remote.hpDrain ?? local.hpDrain,
  };
}
