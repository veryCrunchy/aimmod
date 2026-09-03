import assert from "node:assert/strict";
import test from "node:test";

import { mergeInstalledBeatmap } from "../src/osu/beatmapMerge.ts";
import type { WorkspaceBeatmap } from "../src/osu/models.ts";

function beatmap(overrides: Partial<WorkspaceBeatmap> = {}): WorkspaceBeatmap {
  return {
    provider: "official",
    beatmapsetId: "20",
    beatmapId: "10",
    artist: "Artist",
    title: "Title",
    creator: "Mapper",
    difficultyName: "Insane",
    mode: "osu",
    starRating: null,
    bpm: null,
    lengthSeconds: null,
    status: "Not supplied",
    coverImageUrl: null,
    skillsets: [],
    localState: "Not installed",
    plays: null,
    favorites: null,
    pp95: null,
    accuracy: null,
    circleSize: null,
    approachRate: null,
    overallDifficulty: null,
    hpDrain: null,
    ...overrides,
  };
}

test("local metadata fills gaps in an installed remote beatmap", () => {
  const local = beatmap({
    provider: "local",
    localState: "Installed",
    starRating: 5.42,
    bpm: 182,
    lengthSeconds: 127,
    status: "Ranked",
    coverImageUrl: "/local/background.jpg",
    plays: 17,
    accuracy: 96.24,
    circleSize: 4,
    approachRate: 9.3,
    overallDifficulty: 8.7,
    hpDrain: 6.2,
  });

  const merged = mergeInstalledBeatmap(beatmap(), local);

  assert.equal(merged.localState, "Installed");
  assert.equal(merged.starRating, 5.42);
  assert.equal(merged.bpm, 182);
  assert.equal(merged.lengthSeconds, 127);
  assert.equal(merged.status, "Ranked");
  assert.equal(merged.coverImageUrl, "/local/background.jpg");
  assert.equal(merged.plays, 17);
  assert.equal(merged.accuracy, 96.24);
  assert.equal(merged.circleSize, 4);
  assert.equal(merged.approachRate, 9.3);
  assert.equal(merged.overallDifficulty, 8.7);
  assert.equal(merged.hpDrain, 6.2);
});

test("supplied Hub metadata remains authoritative while local accuracy is retained", () => {
  const remote = beatmap({
    starRating: 6.01,
    bpm: 200,
    lengthSeconds: 140,
    status: "Loved",
    coverImageUrl: "https://assets.ppy.sh/cover.jpg",
    plays: 123456,
    favorites: 789,
    circleSize: 4.2,
    approachRate: 9.8,
    overallDifficulty: 9.1,
    hpDrain: 6.8,
  });
  const local = beatmap({
    provider: "local",
    localState: "Installed",
    starRating: 5.42,
    plays: 17,
    accuracy: 96.24,
  });

  const merged = mergeInstalledBeatmap(remote, local);

  assert.equal(merged.starRating, 6.01);
  assert.equal(merged.plays, 123456);
  assert.equal(merged.favorites, 789);
  assert.equal(merged.coverImageUrl, "https://assets.ppy.sh/cover.jpg");
  assert.equal(merged.accuracy, 96.24);
});
