# Overlay studio and browser sources

Open Overlays, enable the desired output under OBS and in-game, and choose a scene.
Start from Stream, Minimal HUD, Setup showcase, Match day or Practice analysis.
The shared renderer uses a 1920 by 1080 design canvas. Output resolution scales
that canvas proportionally; it does not change the scene's coordinate system.

Add widgets from the library. Drag to move, use a corner to resize, hold Alt to
bypass snapping, or Shift-click widgets to select several. Align left, align top
and Distribute arrange the selection. Distribute spaces horizontal origins evenly.
Use the scene inspector's Layers list to hide or lock positions. Undo and Redo
retain the last 60 edits during the editor visit. Copy and Paste use an internal
widget clipboard. Autosave failures retain edits and expose Retry.

The inspector controls font family, weight, text size, text colour, background,
opacity, padding, radius and shadow. A padding or radius of -1 inherits the theme.
Use Sample data to design without a running scenario. Live data reads the local
service. Data explorer lists presentation paths, types, units and preview values;
click a path to bind the selected data widget or create a new stat. Refresh values
reloads the catalog, including fields that appear when a match starts.

## Bindings

Data stat, timer, table, sparkline, line, bar, gauge and progress widgets accept a
dot-separated path. Arrays can be addressed by index, such as `recent.0.score`.
Charts accept a numeric array or rows with a numeric Series field, for example
`session.graph` and `score`. Minimum and Maximum control the visible scale. Tables
use comma-separated column paths and show at most 12 rows and 6 columns. Charts
bound processing to 200 samples. Timers format seconds as minutes and seconds.
Number formatting supports decimals, prefix, suffix, thousands abbreviations and
a threshold colour. Stat widgets can count toward a changed value or flash when
the new value reaches the threshold. Image widgets can bind an HTTPS image path.
Unknown data displays Unavailable and zero remains zero.

Visibility accepts a truthy path such as `live.active`, or a simple comparison
such as `live.projectedDelta > 0`. No expressions or scripts execute. Automatic
scene states are Menu, Run, Match and Replay. The first matching scene wins;
otherwise the default output scene is used. A URL with an explicit scene id
always keeps that scene selected.

Template widgets substitute `{{live.score}}` as text. Data HTML uses a restricted
declarative dialect: div, span, p, b, strong, em, i and br. Only double-quoted inline
styles with hexadecimal color/background-color, 1-199px font-size and numeric
font-weight survive. Scripts, images, frames, links, event handlers and all other
CSS are discarded. Values are inserted as text after parsing, so a data value
cannot add markup. Templates are bounded to 4096 characters and 16 nesting levels.

## OBS setup

1. Enable OBS output and copy the scene URL from the studio.
2. Add an OBS Browser Source and paste that URL.
3. Set width and height to the resolution shown by the studio, normally 1920 by
   1080. The page background is transparent.
4. For one widget, copy its URL from the inspector and use its displayed dimensions.
5. To use automatic scene selection, use the default OBS output URL rather than
   a URL containing `id=`. Automatic selection changes overlay content only.

OBS uses a reconnecting event stream with polling recovery. The HUD uses polling
and the same ES5 renderer. Both stop displaying data when their output is disabled.
Browser source URLs contain a local read capability; keep them private. Share
codes contain the scene layout and theme, including authored text and image URLs,
but omit peripheral profile data. Review your authored content before sharing.

## Local data protocol

All paths below are relative to the existing capability-protected local server
root, on either the workspace or OBS listener. OBS routes remain read-only.

* `GET overlay-data`: `{v:1, fields:[{path,type,unit,description,value}]}`.
* `GET overlay-feed`: presentation snapshot used by existing widgets.
* `GET overlay-stream`: SSE `data:` records containing that snapshot every 500ms.
  Disconnect cancels work; reopening returns current state immediately. There is
  no historical event replay. Gameface uses polling instead.
* Workspace reads accept `surface=game` to apply the HUD output switch. OBS reads
  always apply the OBS switch, regardless of query parameters.

The catalog registers nullable paths in `OverlayDataCatalog.Schema` and discovers
additional fields only inside the approved overlay presentation snapshot. New
presentation data belongs in `OverlayService.Feed`; register nullable field types
and units in the catalog. Never pass workspace configuration or authentication
objects into the catalog. Dynamic array indices describe current rows and are
not persistent player identities.

Registered paths:

* `live.score`, `live.shots`, `live.hits`, `live.kills`, `live.damage`,
  `live.personalBest`, `live.opponentScore`, `live.projectedScore`,
  `live.projectedDelta`, `live.scorePerMinute`, `live.killsPerSecond`,
  `live.seconds`, `live.opponentDuration`, `live.remainingSeconds`,
  `live.durationSeconds`, `live.lastTimeToKillSeconds`, `live.accuracy`,
  `live.available`, `live.active`, `live.paused`, `live.replay`, `live.transient`,
  `live.projectionEstimated`, `live.scenario`, `live.attemptId`,
  `live.opponentName`, `live.opponentSource`.
* `session.runs`, `session.pbs`, `session.scenarios`, `session.bestRatio`,
  `session.startedAt`, `session.seconds`, `session.accuracy`, `session.graph`.
* `scenario.name`, `scenario.best`, `scenario.attempts`, `scenario.average`,
  `scenario.last`, `scenario.lastAt`.
* `profile.linked`, `profile.name`, `profile.handle`.
* `recent`, `benchmarks`, `board`, `tournament`, `kovaaks`, plus available nested
  properties and the first 32 array rows in each group.

This catalog covers the overlay feed, not every internal service field. Avatar,
scenario author, uncached Hub data, detailed replay state and mouse telemetry are
not added to the catalog by this change. Existing mouse-path/input widgets retain
their dedicated motion endpoint. No OBS WebSocket control is included. All overlay
operations are presentation-only and do not alter game inputs, rules or scores.

## Verification on the target surfaces

Check transparency, source dimensions, text and chart rendering in OBS; restart
the service and confirm reconnection. Disable OBS output and verify it clears.
Test HUD visibility, text, charts, template styling and pause/replay transitions
inside Gameface. Test autosave failure and Retry, import/export, undo/redo and
automatic scene changes before relying on a scene during a live broadcast.
