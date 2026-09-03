import assert from "node:assert/strict";
import test from "node:test";

import { filtersForBeatmapProvider, type BeatmapSearchFilters } from "../src/osu/beatmapSearchFilters.ts";

const filters: BeatmapSearchFilters = {
  mode: "osu",
  status: "ranked",
  minStarRating: 4,
  maxStarRating: 7,
  minBpm: 150,
  maxBpm: 240,
  minLengthSeconds: 60,
  maxLengthSeconds: 240,
  minApproachRate: 8,
  maxApproachRate: 10,
  minCircleSize: 3,
  maxCircleSize: 5,
  minOverallDifficulty: 7,
  maxOverallDifficulty: 10,
  sort: "stars-high",
  descending: true,
};

test("Collector requests keep only filters its collection API can represent", () => {
  assert.deepEqual(filtersForBeatmapProvider("osuCollector", filters), {
    mode: "osu",
    minStarRating: 4,
    maxStarRating: 7,
    minBpm: 150,
    maxBpm: 240,
    sort: "stars-high",
    descending: true,
  });
});

test("official osu requests retain difficulty-level filters", () => {
  assert.equal(filtersForBeatmapProvider("official", filters), filters);
});
