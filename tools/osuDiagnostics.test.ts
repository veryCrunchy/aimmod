import assert from "node:assert/strict";
import { BoundedDiagnosticReporter, privateSourceId, type OsuDiagnosticEvent } from "../src/osu/osuDiagnostics.ts";

const sent: OsuDiagnosticEvent[] = [];
const reporter = new BoundedDiagnosticReporter((event) => sent.push(event), 1_000, 3);
const event: OsuDiagnosticEvent = {
  area: "previewAudio",
  event: "waiting",
  sourceId: "a".repeat(64),
  networkState: 2,
  readyState: 1,
};

assert.equal(reporter.report(event, 10_000), true);
assert.equal(reporter.report(event, 10_500), false, "identical media state is deduplicated");
assert.equal(reporter.report({ ...event, readyState: 2 }, 10_500), true, "a state transition is retained");
assert.equal(reporter.report({ ...event, event: "playing" }, 10_600), true);
assert.equal(reporter.report({ ...event, event: "pause" }, 10_700), false, "the minute cap is enforced");
assert.equal(reporter.report({ ...event, event: "pause" }, 70_000), true, "a new minute accepts events");

assert.equal(privateSourceId(`aimmod-media://localhost/${"B".repeat(64)}`), "b".repeat(64));
assert.match(privateSourceId("/home/player/private replay.osr", "replay") ?? "", /^replay-[0-9a-f]{8}$/);
assert.equal(sent.length, 4);

console.log("osu diagnostics tests passed");
