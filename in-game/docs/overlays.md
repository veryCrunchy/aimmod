# Overlay studio and browser sources

Open Overlays and choose a scene. Assigning it to In-game HUD also enables the
master in-game output. If that output was switched off separately, the editor
shows **In-game overlay is off - Turn on** on the assigned scene. Turn on saves
the master setting without changing OBS. A failed save leaves the warning visible.
OBS output has its own master switch under OBS and in-game.
Start from Stream, Minimal HUD, Setup showcase, Match day or Practice analysis.
Each scene has its own canvas width and height, initially 1920 by 1080. Change
these in the scene inspector, from 320 by 180 up to 7680 by 4320, including portrait
sizes. Output resolution scales that scene proportionally. Resizing one scene
does not resize others. Scene URLs carry that scene's dimensions; individual
widget URLs carry the widget dimensions.

Add widgets from the library. Drag to move, use a corner to resize, hold Alt to
bypass snapping, or Shift-click widgets to select several. Align left, align top
and Distribute arrange the selection. Distribute spaces horizontal origins evenly.
Use the scene inspector's Layers list to hide or lock positions. Undo and Redo
retain the last 60 edits during the editor visit. Copy and Paste use an internal
widget clipboard. Autosave failures retain edits and expose Retry.

The inspector controls font family, weight, text size, text colour, background,
opacity, padding, radius and shadow. A padding or radius of -1 inherits the theme.
Use Sample data to design without a running scenario. Live data reads the local
service. Data explorer lists binding paths, types, units and preview values;
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
the new value reaches the threshold. Image widgets can bind an HTTPS image path
or `profile.avatar`, which uses the locally cached avatar route.
Unknown data displays Unavailable and zero remains zero.

Visibility accepts a truthy path such as `live.active`, or a simple comparison
such as `live.projectedDelta > 0`. No expressions or scripts execute. Automatic
scene states are Menu, Run, Match and Replay. The first matching scene wins;
otherwise the default output scene is used. A URL with an explicit scene id
always keeps that scene selected.

Template widgets substitute `{{live.score}}` as text. Both text and Data HTML
support bounded loops and truthy conditionals:

```html
{{#if multiplayer.cs}}
<div style="display:flex;flex-direction:column;padding:12px">
  {{#each multiplayer.killFeed}}
  <div>{{killer}} - {{weapon}} - {{victim}}{{#if head}} HS{{/if}}</div>
  {{/each}}
</div>
{{else}}Waiting for a match{{/if}}
```

Use `{{#each board.rows}}...{{/each}}` for a board. Within a loop, fields resolve
against the current row first, then the root snapshot. `this`, `this.name`,
`@index` and `root.live.score` explicitly select a value. `{{#if path}}` accepts
an optional `{{else}}`; it tests a value, without executing expressions. Missing
or false values, zero and empty strings select the else branch.

Data HTML accepts div, span, p, b, strong, em, i, br, table, thead, tbody, tr, td,
th, ul, ol and li. Only double-quoted inline `style` attributes survive. Allowed
CSS declarations are:

| Properties | Values |
| --- | --- |
| color, background-color, border-color | Six-digit hexadecimal colours |
| width, height, font-size, padding, margin, border-radius, border-width | Whole pixels from 0px to 4099px |
| opacity | 0 to 1, up to three decimal places |
| display | block, flex, none |
| flex-direction | row, column |
| align-items | flex-start, center, flex-end, stretch |
| justify-content | flex-start, center, flex-end, space-between, space-around |
| text-align | left, center, right |
| font-weight | 100 through 900, in hundreds |
| font-family | sans-serif, monospace |
| border-style | solid, dashed, dotted, none |

Scripts, images, frames, links, event handlers, selectors, external style sheets,
URLs and other CSS are discarded. Values are escaped before parsing and rendered
as text nodes, so a value cannot introduce markup or load a URL. Source length is
4096 characters; loops have at most 32 rows each, eight control nesting levels,
2000 evaluated nodes and 32768 output characters. HTML nesting stops at 16.
Malformed control blocks render empty. The same sandbox runs in OBS and Gameface.

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

### Optional OBS scene switching

The OBS and in-game tab offers a separate, disabled-by-default OBS WebSocket v5
connection. Enable the WebSocket server in OBS, enter its local port and password,
then enter exact OBS scene names for match start/end, run start/end and replay
start/end. Leave an event blank to ignore it. Enable automatic switching and save.
These are OBS program scene names, independent of overlay scene names.

Only the local computer at 127.0.0.1 is contacted. The service uses the
[OBS v5 protocol](https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md)
to authenticate and send SetCurrentProgramScene. It retries failed connections
every three seconds, with a five-second attempt timeout. A newer state cancels
an obsolete pending switch. Replay changes take precedence over match changes,
then run changes; run events cannot interrupt an open replay. Enabling the feature
during an active state applies that state's start mapping. Saving new settings
also reapplies the current state. No browser source needs to be open.

The password is stored in the local service data directory's
`obs-scene-switching.json`, under the user's local file permissions. It is not
encrypted, returned by the settings API, included in share codes or catalog data,
or written to logs. A blank password field keeps the saved password; Remove saved
password clears it. Refresh connection status displays the latest result. Turning
off automatic switching cancels an in-flight connection and stops new requests.

## Local data protocol

All paths below are relative to the existing capability-protected local server
root, on either the workspace or OBS listener. OBS routes remain read-only.

* `GET overlay-data`: `{v:1, fields:[{path,type,unit,description,value}]}`.
* `GET overlay-feed`: snapshot used by all bindings and existing widgets.
* `GET overlay-stream`: SSE `data:` records containing that snapshot every 500ms.
  Disconnect cancels work; reopening returns current state immediately. There is
  no historical event replay. Gameface uses polling instead.
* Workspace reads accept `surface=game` to apply the HUD output switch. OBS reads
  always apply the OBS switch, regardless of query parameters.
* `GET overlay-avatar/{name}`: a cached avatar PNG, subject to the output switch.
  Use the relative URL in `profile.avatar` or a lobby player's `avatar` field.
* Workspace only: `GET/POST overlay-obs-settings` reads or saves connection settings.
  Writes require JSON and `X-AimMod-UI: 1`; reads include `hasPassword`, never the
  password. These control routes are absent from the read-only OBS listener.

The catalog registers all source record types in `OverlayDataCatalog.Sources`
and legacy nullable fields in `Schema`; units are centralized in `Unit`. A new
source adds its typed record there and its read-only snapshot to
`OverlayService.Feed`. Nullable fields and an example `.0` row remain discoverable
even before a match or replay is loaded. Every field has a type, unit (empty for
dimensionless values), description and current value. Numeric array indices
describe current rows and are not persistent player identities. The explorer
expands the first 32 rows; a list binding receives the original array.

Never pass workspace configuration, credentials or invitation codes into a source.
The multiplayer source exposes the existing public match snapshot, plus a lobby
projection without invitation codes or chat. Optional practice analysis can publish
a bounded typed snapshot through `WorkspaceHost.OverlayAnalysis.Publish(metrics)`;
this registers its nullable fields under `mouse.analysis` in the same catalog.

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
* `profile.linked`, `profile.name`, `profile.handle`, `profile.avatar`.
* `multiplayer.now`, `multiplayer.self`, `multiplayer.lobby.id`,
  `multiplayer.lobby.mode`, `multiplayer.lobby.readyCheck`,
  `multiplayer.lobby.autoStartAt`, `multiplayer.lobby.players`.
  Each player has `id`, `name`, `team`, `role`, `ready`, `away`, `connection`, `avatar`.
* `multiplayer.match`: `id`, `phase`, `mode`, `scenario`, `timeLimit`, `round`,
  `totalRounds`, `firstTo`, `startsAt`, `endsAt`, `nextAt`, `players`, `live`,
  `rounds`, `standings`, `winnerId`, `rematch`, `rematchDeadline`, `loaded`,
  `attacker`, `tracking`, `combat`, `cs`, `loadIssues`, `loadFailed`, `loadAttempt`.
  Standings rows include `memberId`, `name`, `place`, `wins`, `points`, `best`,
  `total`, `played`. Live and round score rows retain their typed score fields.
* `multiplayer.cs`: `round`, `phase`, `phaseEndsAt`, `liveAt`, `score`,
  `team1Side`, `halfRounds`, `overtime`, `players`, `bomb`, `lastWinner`,
  `lastReason`, `winnerTeam`, `events`, `spawns`, `sites`, `grenades`, `drops`.
  Player rows expose `member`, `team`, `side`, `money`, `alive`, `health`, `armor`,
  `helmet`, `kit`, `primary`, `secondary`, `kills`, `deaths`, `inBuyZone`, `site`,
  `callout`, `holding`, `grenades`, `flash`, `heldGrenade`, `adr`, `hsPercent`, `mvps`.
  Bomb fields are `state`, `carrier`, `site`, `position`, `explodesAt`, `planter`,
  `plantDoneAt`, `defuser`, `defuseDoneAt`.
* `multiplayer.bombSeconds` is the planted bomb countdown in seconds, or null.
  `multiplayer.killFeed` contains up to 20 rows with `id`, `killer`, `victim`,
  `weapon`, `head`, `you`, `killerTeam`, `teamKill`. `you` identifies a local kill
  or death; event timestamps remain available in `multiplayer.cs.events`.
* `replay.id`, `replay.scenario`, `replay.time`, `replay.duration`, `replay.playing`,
  `replay.speed`, `replay.visible`, `replay.stats`. Sampled stats expose `score`,
  `shots`, `hits`, `kills`, `damage`, `seconds`, `hitTime`, `hitDelta`, `hitTarget`.
* `mouse.speed` is angular speed in degrees per second, derived from recent camera
  telemetry, not physical mouse centimetres. `mouse.trail` retains up to 120 recent
  points with `milliseconds`, `yaw`, `pitch`; milliseconds are relative to the
  five-second motion window. `mouse.analysis` is null until a producer publishes.
* `kovaaks.available`, `kovaaks.dpi`, `kovaaks.sens`, `kovaaks.sensScale`,
  `kovaaks.cm360`, `kovaaks.fov`, `kovaaks.fovScale`, `kovaaks.theme`,
  `kovaaks.crosshair`, `kovaaks.crosshairScale`, `kovaaks.crosshairColor`,
  `kovaaks.crosshairImage`, `kovaaks.hitSounds`.
* `benchmarks` rows contain `id`, `name`, `author`, `rank`, `rankIndex`,
  `scenarioRank`, `progress` (0 to 1), `next`. Values use the local Hub cache.
* `recent`, `benchmarks`, `board`, `tournament`, `kovaaks`, plus available nested
  properties and the first 32 array rows in each group.

Scenario authors and per-flick/tracking analysis are unavailable until their source
modules provide them; benchmark authors are exposed now. Uncached Hub information
is not invented. Existing mouse-path/input widgets keep their dedicated motion
endpoint. All overlay operations are display-only and do not alter game inputs,
rules or scores.

## Verification on the target surfaces

Check transparency, source dimensions, text and chart rendering in OBS; restart
the service and confirm reconnection. Disable OBS output and verify it clears.
Test HUD visibility, text, charts, template styling and pause/replay transitions
inside Gameface. Test autosave failure and Retry, import/export, undo/redo and
automatic scene changes before relying on a scene during a live broadcast.
For the HUD, verify the inline Turn on action clears the warning and the Live stats
and PB pace widgets appear during a run. Restart the service and game to check
persistence. Disable the master on OBS and in-game, return to the assigned scene,
and verify the warning returns. The native HudHost loads `overlay?surface=game`
from `live-overlay-url.txt`; its ready capability makes the Lua fallback stand
down. Both host visibility and the feed honor the same persisted `gameEnabled`.
The native host also hides the HUD while a modal panel, pause or replay is active.
