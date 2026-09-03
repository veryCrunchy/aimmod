export type OsuSkillset = "Aim" | "Speed" | "Reading" | "Consistency" | "Finger control";

export interface WorkspaceBeatmap {
  provider: string;
  sourceId?: string;
  beatmapsetId: string;
  beatmapId: string;
  artist: string;
  title: string;
  creator: string;
  difficultyName: string;
  mode: string;
  starRating: number | null;
  bpm: number | null;
  lengthSeconds: number | null;
  status: string;
  coverImageUrl: string | null;
  audioUrl: string | null;
  audioPreviewTimeMs: number | null;
  skillsets: OsuSkillset[];
  localState: "Installed" | "Not installed" | "Update available";
  plays: number | null;
  favorites: number | null;
  pp95: number | null;
  accuracy: number | null;
  circleSize: number | null;
  approachRate: number | null;
  overallDifficulty: number | null;
  hpDrain: number | null;
}

export interface WorkspaceReplay {
  path: string;
  fileName: string;
  storageSource: "export" | "lazerStore" | null;
  mode: string | null;
  playerName: string | null;
  score: number | null;
  maxCombo: number | null;
  perfect: boolean | null;
  mods: string[];
  playedAt: string | null;
  counts: { count300: number; count100: number; count50: number; countMiss: number } | null;
  beatmapTitle: string | null;
  difficultyName: string | null;
  coverImageUrl: string | null;
  parseError: string | null;
}
