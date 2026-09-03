export interface BeatmapSearchFilters {
  mode: string | null;
  status: string | null;
  minStarRating: number | null;
  maxStarRating: number | null;
  minBpm: number | null;
  maxBpm: number | null;
  minLengthSeconds: number | null;
  maxLengthSeconds: number | null;
  minApproachRate: number | null;
  maxApproachRate: number | null;
  minCircleSize: number | null;
  maxCircleSize: number | null;
  minOverallDifficulty: number | null;
  maxOverallDifficulty: number | null;
  sort: string;
  descending: boolean;
}

export function filtersForBeatmapProvider(provider: string, filters: BeatmapSearchFilters): Partial<BeatmapSearchFilters> {
  if (provider !== "osuCollector") return filters;
  return {
    mode: filters.mode,
    minStarRating: filters.minStarRating,
    maxStarRating: filters.maxStarRating,
    minBpm: filters.minBpm,
    maxBpm: filters.maxBpm,
    sort: filters.sort,
    descending: filters.descending,
  };
}
