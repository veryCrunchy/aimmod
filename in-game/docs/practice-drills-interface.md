# Replay flick paths and practice drills

The replay service consumes the analysis module directly. It writes bounded
recorded and minimum-jerk paths once per replay load. The native replay HUD
reads that local file once, draws an angular 2D overlay while paused, and
hides it during playback. Paths can be toggled; the bracket keys select the
previous or next flick. Scrubber markers select and pause at a flick onset.
Overlapping markers share a display bin; keyboard navigation reaches every
flick. Live spectator sessions never attach this overlay.

## Page integration

The workspace loads `practice-drills.js`. Coaching and Mechanics can call:

```js
var dispose = AimModPracticeDrills.mount(container, replayId);
// Call dispose when leaving the page.
```

The button reads completed replay measurements through the service and opens
a freeplay drill. No page-specific analysis shape is required.

- `GET practice-drills/{replayId}` returns `recommendation`, `mode: freeplay`
  and `rankedEligible: false`. Recommendation is null when measurements are
  insufficient. Flick cells need five samples; tracking needs five seconds.
- `POST practice-drills/{replayId}` with `X-AimMod-UI: 1` creates and requests
  the drill. HTTP 202 means accepted, not that the game has started. HTTP 422
  means insufficient measurements or an unsupported base scenario.
- `GET practice-drills` returns pending load state, a message and the native
  start sequence. The start command result remains authoritative.

Drills are generated from the measured run's installed scenario. They reuse
its map and profile references, and need embedded target bodies and a map.
The templates vary spawn offset direction and distance, body radius, target
speed and left/right reversal intervals. Angular measurements convert using
1000 cm as a reference distance. Actual angular difficulty depends on the
inherited map and spawn locations and needs in-game validation. Static and
micro templates immobilize targets; tracking templates preserve movement.

Generated names start with `AimMod Drill - `. Files disable challenge mode
and scoring, are excluded from the content library, and use a separate
manifest with the same ownership and retention checks as match scenarios.
Service and native command validation reject challenge or seeded starts.
No challenge seed exception is added. Local drill files are never uploaded.
