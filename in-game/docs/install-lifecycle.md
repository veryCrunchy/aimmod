# AimMod in-game: install lifecycle

How the in-game stack (UE4SS + AimModCore with its service + AimModNativeUI + AimModSteam) is
released, installed, updated, repaired and rolled back, and how map ports are prepared for the
Steam Workshop.

## How a friend gets AimMod and its updates

1. **Once:** download `AimMod-InGame-<version>.zip` (the version release, or the rolling
   `aimmod-latest` release), unzip it, close KovaaK's and run `Install-AimMod.cmd`. The folder can be
   deleted afterwards.
2. **From then on, automatically:** the service checks the update feed when the game starts
   and every four hours, downloads a newer release in the background, verifies it and stages it.
   The workspace shows *"Update ready: AimMod x.y.z applies when you close KovaaK's"* with the
   release notes. When the game closes, the update is installed; the next start runs the new
   version. Nobody sends zips any more.
3. **After a KovaaK's update or "Verify integrity of game files":** if the mod still loads but
   something is off, the workspace shows *"AimMod needs a repair"* with a one-click repair that runs
   when the game closes. If the mod no longer loads at all, the friend closes the game and runs
   `%LOCALAPPDATA%\AimMod\Repair-AimMod.cmd`, which re-applies the install from the copy saved at
   install time. Nothing is downloaded.

Settings > **Updates & repair** has automatic updates on/off and the channel (Stable or Beta).

## Pieces and where they live

| What | Where |
| --- | --- |
| Release scripts | `in-game/install/` (`New-AimModInGameRelease.ps1`, `AimModRelease.ps1`) |
| Package helper scripts | `in-game/install/package/` (`Install-AimMod.cmd`, `Repair-AimMod.cmd`, `Uninstall-AimMod.cmd`, `README.txt`) |
| Service code | `in-game/native-service/`: `ReleaseFormat.cs`, `Updater.cs`, `PackageApplier.cs`, `InstallHealth.cs`, `InstallLayout.cs`, `Lifecycle.cs` |
| Workspace UI | `in-game/ui/lifecycle.js` (toast, banner, Settings section) |
| Workflow | `.github/workflows/aimmod-ingame-release.yml` |
| Workshop helper | `in-game/tools/workshop/` |
| Installed game files | `FPSAimTrainer\Binaries\Win64\dwmapi.dll`, `...\Win64\ue4ss\` (record: `ue4ss\aimmod-install.json`) |
| Local state | `%LOCALAPPDATA%\AimMod\KovaaksNative\` (see below) |

Local state under `%LOCALAPPDATA%\AimMod\KovaaksNative\`:

| Path | Purpose |
| --- | --- |
| `update-settings.json` | `autoUpdate`, `channel`, optional `feedUrl` |
| `package\current\` | verified copy of the installed release (used by repairs) |
| `package\previous\` | the release before the last update (used by rollback) |
| `updates\staged\<version>-<id>\` + `updates\staged.json` | downloaded, verified update waiting for the game to close |
| `updates\backups\<stamp>-<version>\` | per-transaction backup of every changed file + `journal.json` |
| `updates\requests.json` | repair / install-on-close requests from the UI, a version to skip after a failed install |
| `updates\last-result.json` | outcome of the last install/update/repair/rollback, shown once in the UI |
| `updates\install.log` | log of every install command |
| `..\Repair-AimMod.cmd` | i.e. `%LOCALAPPDATA%\AimMod\Repair-AimMod.cmd`, placed by every install |

Run data (history, replays, settings) in the same folder is never touched by install, update,
repair, rollback or uninstall.

## Release format

A release is three files on the `aimmod-ingame-v<version>` GitHub release plus a feed on the
channel release:

- `AimMod-InGame-<version>.zip`: the package.
- `AimMod-InGame-<version>.manifest.json`: copy of the manifest inside the zip.
- `AimMod-InGame-<version>.sha256`: SHA-256 of the zip and the manifest.
- `aimmod-ingame-<channel>.json` on the `aimmod-ingame-stable` / `aimmod-ingame-beta`
  release: the update feed.

Package layout (zip root):

```
aimmod-release.json            release manifest
files\dwmapi.dll               paths relative to FPSAimTrainer\Binaries\Win64
files\ue4ss\UE4SS.dll
files\ue4ss\UE4SS-settings.ini
files\ue4ss\Mods\shared\...
files\ue4ss\Mods\AimModCore\dlls\main.dll
files\ue4ss\Mods\AimModCore\service\AimMod.InGame.exe
files\ue4ss\Mods\AimModNativeUI\...
files\ue4ss\Mods\AimModCore\service\cosmetics\catalog.json   and catalog-manifest.json
files\paks\~AimMod\<name>.pak  installed into FPSAimTrainer\Content\Paks\~AimMod
files\ue4ss\Mods\AimModSteam\dlls\main.dll
Install-AimMod.cmd  Repair-AimMod.cmd  Uninstall-AimMod.cmd  README.txt
```

`aimmod-release.json` (`schema` `aimmod.ingame.release/1`):

```json
{
  "schema": "aimmod.ingame.release/1",
  "product": "AimMod for KovaaK's (in-game)",
  "version": "0.2.0",
  "channel": "stable",
  "publishedAt": "2026-10-01T12:00:00Z",
  "commit": "<git sha>",
  "ue4ss": { "version": "v3.0.1-1152-ge3ba1016", "zipSha256": "af8ea9d8...17252" },
  "game": { "appId": 824270, "minimumSteamBuildId": 0,
            "testedBuilds": [ { "version": "3.9.11", "steamBuildId": 25635011 } ] },
  "mods": [ "AimModCore", "AimModNativeUI", "AimModSteam" ],
  "files": [ { "path": "dwmapi.dll", "sha256": "<hex>", "size": 123456 }, "..." ]
}
```

Rules the service enforces: SemVer version; channel `stable` or `beta`; app id 824270; at most 4096
files; each path uses `/`, is either `dwmapi.dll`, under `ue4ss/`, or an AimMod cosmetics pak
`paks/~AimMod/<name>.pak` (flat, never a `_P` patch pak), and has no `..`, drive, stream, reserved
device name or trailing dot/space; paths are unique; every listed mod has files.

Cosmetics paks are the only files placed outside `Binaries\Win64`. The install manifest records
them as `paks\~AimMod\<name>.pak`, and the applier resolves that to the game's own
`Content\Paks\~AimMod`. It refuses the install if `Content\Paks\FPSAimTrainer-WindowsNoEditor.pak`
is not there. Install, repair, update, rollback and uninstall therefore cover paks like every other
file. See [cosmetics](cosmetics.md) for the catalog and its hash-pinned manifest.

`aimmod-ingame-<channel>.json` (`schema` `aimmod.ingame.feed/1`):

```json
{
  "schema": "aimmod.ingame.feed/1",
  "channel": "stable",
  "version": "0.2.0",
  "publishedAt": "2026-10-01T12:00:00Z",
  "notes": "<release body, Markdown>",
  "minimumSteamBuildId": 0,
  "manifestSha256": "<SHA-256 of aimmod-release.json>",
  "package": { "url": "https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-v0.2.0/AimMod-InGame-0.2.0.zip",
               "sha256": "<SHA-256 of the zip>", "size": 76543210 }
}
```

Integrity is hash pinning, without signatures: the feed is fetched over HTTPS from the configured
release host and pins the zip (SHA-256 and size) and the manifest (SHA-256); the manifest pins every
file (SHA-256 and size). The scripts write the JSON as UTF-8 without BOM and with LF line endings,
so the hashes are stable.

A stable release is also published as the beta feed unless the beta feed already offers a newer
version, so beta users never fall behind stable.

## Trust model

There is no signing key (a deliberate decision). What protects friends:

- **Transport and host:** the feed and the package are fetched only over HTTPS, from the configured
  feed URL (GitHub releases of this repository by default); HTTPS-to-HTTP redirects are refused.
  Whoever can publish releases on the host can publish updates, so release access on the repository
  is the trust boundary.
- **Hash pinning:** the zip must match the feed's SHA-256 and size before it is opened, the manifest
  must match the feed's SHA-256, and every installed file must match the manifest. A corrupted or
  swapped download, a truncated file, or a zip whose content differs from its manifest is rejected
  and nothing is installed.
- **Policy checks:** no downgrades, the feed must be for the selected channel, size limits, game build
  requirements, and only files the manifest lists (`dwmapi.dll`, under `ue4ss/`, or `paks/~AimMod/*.pak`) are extracted.
- **Nothing downloaded is executed before it is applied:** the applier is a copy of the
  already-installed service; the new files only run when the game next starts.
- The publish job runs the service's own check (`--verify-release`) on the built feed and zip before
  uploading, and attests the zip and manifest (GitHub artifact attestations), so a release can also be
  checked with `gh attestation verify` by anyone who wants to.

A signature layer can be added later without changing the feed format: an extra `.sig` next to the
feed, checked by a client that knows the public key.

## Release pipeline

`.github/workflows/aimmod-ingame-release.yml`:

1. **metadata**: version, channel and tag (`aimmod-ingame-v<version>`); prerelease versions must use
   the beta channel.
2. **build** (Windows): lifecycle self-tests and the workspace UI tests; RE-UE4SS `e3ba1016` with its
   UEPseudo submodule (`UE4SS_GITHUB_TOKEN`, the same token as the desktop release); the UE4SS zip
   from `vars.AIMMOD_UE4SS_ZIP_URL` (any HTTPS mirror, accepted only by SHA-256
   `af8ea9d8...17252`); `in-game/native-mod/install/Build-AimModPackage.ps1` with
   `AimModVersion` set; AimModSteam (`in-game/steam-bridge`, `Game__Shipping__Win64`) and its tests;
   `New-AimModInGameRelease.ps1 -Step Stage`. Uploads the staged folder as an artifact.
3. **publish** (only with `publish: true`, no secrets): takes the release body as
   notes, `-Step Finish` (zip and write the feed), checks the feed and zip with
   `--verify-release` as the updater would, attests the zip and manifest, then uploads to the version release
   and the channel feed release. Releases are created with `--latest=false`; the repository-wide
   latest release stays the rolling `aimmod-latest` downloads. The package is uploaded before the
   feed, so a feed never points at a missing file.

Triggers: a pushed `aimmod-ingame-v*` tag (publishes), `workflow_dispatch` (choose version, channel
and whether to publish; a dry run builds and stages only), and `workflow_call` from release-please.

release-please has an `in-game` component (`component: aimmod-ingame`, `in-game/version.txt`,
`in-game/CHANGELOG.md`); the desktop package now excludes `in-game`. When an in-game release PR is
merged, release-please creates the tag and release, and `ingame-build` runs the workflow only if the
repository variable `AIMMOD_INGAME_RELEASES` is `true`. The rolling `aimmod-latest` release also
carries the newest stable in-game zip and manifest.

Repository configuration the workflow needs:

| Name | Kind | Purpose |
| --- | --- | --- |
| `UE4SS_GITHUB_TOKEN` | secret | UEPseudo access (exists for the desktop release) |
| `AIMMOD_UE4SS_ZIP_URL` | variable | HTTPS URL of `UE4SS_v3.0.1-1152-ge3ba1016.zip` |
| `AIMMOD_TESTED_BUILDS` | variable, optional | `version=buildid[,...]`, default `3.9.11=25635011` |
| `AIMMOD_MINIMUM_STEAM_BUILD` | variable, optional | refuse updates on older game builds, default `0` |
| `AIMMOD_INGAME_RELEASES` | variable | `true` lets release-please run the in-game release |

The workflow builds AimModCore from `in-game/native-mod` (`Build-AimModPackage.ps1`, output
`in-game/native-mod/out/package`) and AimModSteam from `in-game/steam-bridge` (`main.dll` of
`AimModSteam`). AimModSteam's `config.txt` is user-editable and is not shipped, so updates and
repairs never overwrite it; the mod uses its defaults until the user creates one.

Building a release by hand (same steps, locally):

```
pwsh in-game/install/New-AimModInGameRelease.ps1 -Version 0.2.0 -Channel stable `
  -Package in-game/native-mod/out/package -SteamDll <AimModSteam main.dll> -Ue4ssZip <UE4SS zip> `
  -TestedBuild 3.9.11=25635011 -NotesFile notes.md -Output in-game/out/release
```

`in-game/out/` is a build output; do not commit it.

## Updates in the service

- **When:** 20 seconds after the service starts (the game is loading), then every 4 hours plus up to
  20 minutes of jitter; failures retry after 15, 30, 60 and 120 minutes. *Check for updates* in
  Settings checks immediately. With automatic updates off there are no network requests unless the
  user clicks *Check for updates*.
- **Where:** `https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-<channel>/aimmod-ingame-<channel>.json`
  `feedUrl` in `update-settings.json` overrides it (HTTPS only, `{channel}` is
  replaced), for example a Hub mirror. A non-HTTPS override turns updates off.
- **Checks, in order:** the install was made from a release (installs
  from `Install-AimModCore.ps1` have no version and are never auto-updated); the feed is at most 256
  KB and parses; the feed is for the selected channel; the version is newer than
  the installed one (no downgrades); the game build is not older than `minimumSteamBuildId`.
- **Download:** HTTPS only (redirects to HTTP are refused and the final URL is checked), size capped
  by the feed (at most 512 MB), SHA-256 compared with the feed before anything is extracted.
- **Extraction:** only `aimmod-release.json`, the four helper files and the files the
  manifest lists are extracted, each capped at its declared size; other entries (including
  `..\` paths) are ignored. The manifest's SHA-256 must match the feed,
  and every file's size and SHA-256 must match.
- **Staging:** the verified folder is kept under `updates\staged\` and `staged.json` is written last.
  Older staged versions are removed.

Nothing that was downloaded is executed before it has been verified, and the staged package is
verified again (manifest hash and every file) immediately before it is applied. The applier itself is a
copy of the already-installed service, not the downloaded one.

## Applying updates and repairs

The service runs with `--exit-with-game`. When it sees KovaaK's close it checks for work (a staged
update with automatic updates on or *Install when I close KovaaK's* clicked, a requested repair, or
an interrupted install) and, if there is any, copies its own folder to `updates\applier\<id>\` and
starts that copy with `--apply-pending --wait-pid <service pid> --game-dir <Win64>`. The copy waits
for the service to exit, waits up to two minutes for the game to stay closed (it never touches files
while the game runs), and applies.

Every apply (install, update, repair) is one transaction (`PackageApplier`):

1. Plan: files whose hash differs, files from the previous release that are no longer shipped
   (removed only while unchanged), the mod lists and the install record.
2. Copy every file that will change into `updates\backups\<stamp>-<version>\files\` and write
   `journal.json` (state `applying`) before the first change.
3. Replace files through `<file>.aimmod-new` and an atomic rename; update `mods.txt`/`mods.json`
   (AimMod mods first and enabled; other mods kept while their folder exists); write
   `ue4ss\aimmod-install.json`; re-hash every installed file.
4. Mark the journal `applied`. On any error, restore every journalled file, delete files that did not
   exist before and remove created folders (journal `failed`). A journal left `applying` by a crash
   or power loss is undone on the next run.

Files AimMod did not place (for example another tool's `dwmapi.dll`) are kept as
`<file>.aimmod-backup` and restored by the uninstaller, as `Install-AimModCore.ps1` does. The
install record keeps that script's fields, so `Uninstall-AimModCore.ps1` also works on release
installs; it adds `version`, `channel`, `releaseManifestSha256` and `mods`.

After a successful update the staged folder becomes `package\current` (the old one moves to
`package\previous`), `last-result.json` reports the update, and the next service start confirms it.
If an update fails, the previous install is restored, the failure is shown, and that version is not
retried on every close until the user clicks *Check for updates* or *Install*.

**Rollback:** `Repair-AimMod.cmd -Rollback` undoes the newest version-changing transaction exactly
(the two newest transactions and the newest update are kept) and restores `package\previous`.

## Repair

`InstallHealth` compares the game folder with `ue4ss\aimmod-install.json`:

- `dwmapi.dll` missing or replaced (Steam "Verify integrity" or another tool);
- UE4SS files missing or changed, `UE4SS-settings.ini` changed;
- AimMod files missing or changed (a Lua or C++ mod that no longer matches the installed release);
- an AimMod mod missing from or disabled in `ue4ss\Mods\mods.txt`;
- the Steam build (`steamapps\appmanifest_824270.acf` `buildid`) against the release's tested
  builds: unknown builds are a warning, builds older than `minimumSteamBuildId` are refused for
  updates.

When the mod loads, the service inspects on start and after every check; problems appear as the
workspace banner and in Settings with *Repair when I close KovaaK's*, applied by the same hand-off.
When the mod does not load (nothing in-game can notice), `%LOCALAPPDATA%\AimMod\Repair-AimMod.cmd`
copies the cached service to `%TEMP%` and runs `--repair` from `package\current` (falling back to
the folder the script sits in, such as the unzipped release). `AIMMOD_GAME_DIR` overrides the Steam
lookup.

The old desktop app (`src-tauri`) deletes `Win64\dwmapi.dll` when it syncs its own UE4SS payload
(`cleanup_legacy_ue4ss_files`). Running it on a machine with the in-game install breaks the
in-game install until it is repaired.

## Command line

`AimMod.InGame.exe` (from the package or `package\current`):

| Command | Does |
| --- | --- |
| `--install [--package <root>]` | install from a package folder (default: the package the exe sits in) |
| `--repair [--package <root>]` | re-apply `package\current` (or the given package) |
| `--rollback` | undo the last update |
| `--uninstall [--force]` | remove what AimMod placed and restore backups; `--force` also removes changed files |
| `--install-status` | print the health report; exit code 3 means a repair is needed |
| `--apply-pending --wait-pid <pid>` | the post-exit hand-off (started by the service) |
| `--verify-release <feed.json> --zip <zip>` | check a built release as the updater would (feed, zip hash and size, manifest, every file) |
| `--self-test-lifecycle` | run the lifecycle checks only |

Common options: `--game-dir <FPSAimTrainer or Win64 folder>` (default: from the exe location, then
Steam) and `--output <data folder>` (default `%LOCALAPPDATA%\AimMod\KovaaksNative`). Exit codes: 0
done, 1 failed (nothing left half-installed), 2 KovaaK's is running.

## Workshop publishing for map ports

### How KovaaK's publishes

- Workshop items for KovaaK's (app 824270) contain a single `.sce`; downloaded items appear as
  `steamapps\workshop\content\824270\<id>\<Scenario name>.sce`. map-port scenarios embed the map
  (`[Map Data]`) and the Shift ability, so the `.sce` alone is a complete item.
- The game has its own uploader. The game executable carries the UWorks ISteamUGC bindings
  (`STEAMUGC_INTERFACE_VERSION014`, CreateItem, SubmitItemUpdate, SendQueryUGCRequest), a
  `UWorkshopUploadPromptWidget`, a `UWorkshopUpdateItemNode`, `UFindUgcIdByTitleAsync`, and the
  scenario browser prompts "{Scen} is not on the workshop. Click here to upload it." and "The version
  of {Scen} that you have on the workshop is different than your local copy. Click to upload your
  local version". Items are matched to local scenarios by title, which is why the Workshop title must
  equal the scenario name (also the leaderboard key).
- Scenario-level tags live in the `.sce` (`AimTypeTag`, `AimSubTypeTag`, `GameTag`, `SearchTags`,
  `DifficultyTag`, `AuthorsTag`, `Description`). Which of them the uploader writes as Workshop tags,
  metadata or description has not been captured yet (see "Before automating" below).
- ISteamUGC limits: title 128 bytes, description 8000, change note 8000, a preview file under 1 MB.

### The helper

```
cd in-game/tools/workshop
python -m aimmod_workshop prepare <map-port --out folder> --out <bundles folder> \
  --source-author "<original authors>" --source-url <url> --source-license "<permission>" \
  [--credit "<line>"] [--preview <screenshot.png>] [--published-file-id <id> --change-note "..."] \
  [--visibility private|friends|unlisted|public] [--scenario "<name>"] [--include-map-files]
python -m unittest discover -s tests
```

It reads map-port's `*.report.json` and writes one bundle per scenario:

| File | Content |
| --- | --- |
| `content/<scenario>.sce` | the Workshop content folder (map and ability embedded) |
| `extras/maps/*.json`, `extras/Abilities/*` | the map-creator map and ability for manual installs (`--include-map-files` puts them in `content/`) |
| `preview.png` | the converter preview (or `--preview`), at most 1024 px and under 1 MB |
| `description.txt` | Steam BBCode: title, source map and game, original authors, link, permission, credits, port notes, the leaderboard-key note |
| `item.json` | app id, title, item id for updates, tags (from the `.sce` plus `AimMod`, `Map Port`, the game), visibility (default private), change note, metadata, `checks.ready` and a to-do list |
| `workshop_item.vdf` | `steamcmd +workshop_build_item` input (untested with KovaaK's; no tags) |
| `PUBLISH.txt` | the steps below |

It refuses a bundle when the name is not an AimMod port name, the `.sce` name, file name and report
disagree, or the map is not embedded. Missing attribution is listed in `checks.todo` and marked
`TODO` in the description. Nothing is uploaded; the user publishes.

Recommended publishing path: copy the `.sce` to `FPSAimTrainer\Saved\SaveGames\Scenarios`, play it
once, publish it with the game's own Workshop prompt, then set the description, preview and tags on
the item's Steam page from the bundle, keep it private until it has been downloaded and played on a
second account, and only then make it public.

### Design: in-process publish through AimModSteam (not implemented)

Calling ISteamUGC from AimModSteam is technically safe: it is the same Steam user and API the game's
own uploader uses, call results are tracked per `SteamAPICall_t` (as the bridge already does for
`ugc.subscribe`), and nothing touches scores or leaderboards. The risk is publishing by accident or
publishing items the game does not recognise, so the design is:

- Pipe commands (JSON contract of `in-game/docs/multiplayer.md`):
  - `ugc.publish.prepare {bundle}`: the bridge reads `item.json`, validates it as the helper does,
    hashes `content/`, the preview and the description, and returns a summary plus a one-time
    `token` bound to those hashes (valid five minutes).
  - `ugc.publish.commit {token}`: only for a token issued in this session. Without
    `publishedFileId`: `CreateItem(824270, k_EWorkshopFileTypeCommunity)`; on
    `m_bUserNeedsToAcceptWorkshopLegalAgreement` return `legal-agreement` with the URL. Then
    `StartItemUpdate`, `SetItemTitle`, `SetItemDescription`, `SetItemUpdateLanguage`, `SetItemTags`,
    `SetItemMetadata` (`aimmod.map-port` + scenario name), `SetItemVisibility`, `SetItemContent`,
    `SetItemPreview`, `SubmitItemUpdate(changeNote)`; progress from `GetItemUpdateProgress` as
    `ugc.publish.progress` events; the result carries the item id, which is written back to
    `item.json`.
  - Updates require `publishedFileId` and are refused for items the user does not own
    (`GetQueryUGCResult` owner check).
- Trigger: only a Workshop page in the AimMod workspace (the capability-protected UI with the
  `X-AimMod-UI` header), showing the summary, the visibility and the legal-agreement notice, with a
  separate confirm click. The service never calls `commit` on its own, on a timer or from a feed.
- Before automating, publish one port with the game's uploader and read it back
  (`CreateQueryUGCDetailsRequest` with `SetReturnMetadata`/`SetReturnKeyValueTags`) so the bridge
  writes the same tags, metadata and content layout the game expects.

## Tests

- `AimMod.InGame.exe --self-test-lifecycle` (also part of `--self-test`): SemVer order, path rules, manifest and feed validation,
  package verification and tampering, a fresh install over a foreign UE4SS, repair detection
  (missing proxy, changed settings, disabled or missing mod entries, changed mod files, unknown and
  old game builds), repair, a failing update rolled back exactly, crash recovery, update, rollback,
  uninstall restoring the original folder, the updater against a fake HTTPS server (staging,
  re-verification, malformed feed, zip files not matching their manifest, hash and size mismatch, HTTP, wrong channel, downgrade, newer game
  needed, developer installs, zip entries outside the manifest), update preferences and the
  command-line hand-off.
- `node --test in-game/ui/lifecycle.test.cjs`: toast, banner, repair request, settings patches,
  explicit install, developer installs, malformed responses.
- `python -m unittest discover -s in-game/tools/workshop/tests`: PNG handling, bundle contents,
  preview scaling, attribution to-dos, updates, name and embedding checks, CLI.

## Open decisions

- Hosting: GitHub releases by default; `feedUrl` can point at a Hub mirror later without a client
  change.
- Channels: Stable and Beta exist; whether friends start on Beta.
- Where the verified UE4SS zip is mirrored for CI (`AIMMOD_UE4SS_ZIP_URL`), and setting
  `AIMMOD_INGAME_RELEASES=true` when release-please should publish.
- The tested Steam build list (`3.9.11=25635011` is the build this was developed against).
