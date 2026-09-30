# Release channels

AimMod osu publishes independently from the main AimMod application. Its tags, version releases, artifacts, and channel pointers all use the `aimmod-osu` prefix, so they cannot trigger or overwrite the existing Tauri release workflow.

## Supported packages

Every release contains audited self-contained portable packages for:

- Windows x64: `aimmod-osu-VERSION-win-x64.zip`
- Linux x64: `aimmod-osu-VERSION-linux-x64.tar.gz`

Each archive has an adjacent SHA-256 file. The release also contains `aimmod-osu-VERSION-checksums.sha256`, an artifact inventory inside each package, and a machine-readable channel manifest.

The release workflow additionally packages the native application with Velopack 1.2.0. Windows receives a setup executable and Linux receives an AppImage. These are the primary end-user downloads because they support atomic in-app updates; the ZIP and tarball remain portable recovery downloads.

## Channels

Stable versions use tags such as `aimmod-osu-v1.0.0`. Preview versions use a SemVer prerelease suffix, such as `aimmod-osu-v1.1.0-preview.1`.

The latest channel manifests have fixed download URLs:

```text
https://github.com/veryCrunchy/aimmod/releases/download/aimmod-osu-stable/aimmod-osu-stable.json
https://github.com/veryCrunchy/aimmod/releases/download/aimmod-osu-preview/aimmod-osu-preview.json
```

Each manifest identifies the exact version release and records the file name, download URL, byte count, and SHA-256 digest for every supported runtime. The dedicated releases are created with `latest=false`. The repository-wide latest release is the rolling `aimmod-latest` release, maintained by the `Latest Downloads` workflow. After every release it holds only the newest stable downloads of AimMod for KovaaK's (including the `latest.json` its updater reads) and AimMod for osu!, and removes files from older versions. Version releases keep their own notes and downloads.

Native update feeds are kept separate by operating system and release channel:

```text
releases.win-stable.json
releases.win-preview.json
releases.linux-stable.json
releases.linux-preview.json
```

The selected channel release also holds the Velopack package referenced by its feed. The desktop updater therefore never reads the main AimMod `latest.json` or the repository-wide latest release. GitHub build-provenance attestations cover every published release asset.

## Publishing

Releases are cut from `main` with release-please. `osu-native` is its own release-please package (component `aimmod-osu`), tracked in `.release-please-manifest.json` and `osu-native/version.txt`. Conventional commits that touch `osu-native/` (`feat:` for a minor bump before 1.0, `fix:` and `perf:` for a patch) open or update a separate "release AimMod for osu!" pull request. That pull request bumps the version and updates `osu-native/CHANGELOG.md`.

While you work, add short, player-facing bullets to `changelogs/unreleased.md` under `##` headings. When the release pull request is created or updated, the Release Please workflow moves those entries into `changelogs/VERSION.md` with a `# AimMod VERSION` heading and resets `unreleased.md`. If `unreleased.md` has no entries, it derives the notes from the generated `CHANGELOG.md` section instead. Review and edit `changelogs/VERSION.md` in the release pull request; an existing file is never overwritten. The release workflow refuses to package a version without its own notes.

Merging the release pull request tags `aimmod-osu-vVERSION`, creates the GitHub release, and calls the `AimMod osu Release` workflow to build, attest and publish both platforms to the stable channel (or preview for prerelease versions).

The same file supplies GitHub release notes, a downloadable changelog, and the Markdown notes in both Velopack feeds. AimMod displays the target version's feed notes before downloading the update, and keeps them visible during download and before restart. Released changelog files are also embedded in the app for offline history. Keep each version within 24,000 characters and 300 lines, and use headings, paragraphs and bullets. Do not add HTML or remote images.

Versions published before this workflow may have no feed notes. AimMod shows a clear unavailable state for those versions instead of showing notes from a different release.

To publish outside release-please (for example a hotfix or a preview build), write `changelogs/VERSION.md` yourself and push a dedicated version tag to build, test, and publish both platforms:

```sh
git tag aimmod-osu-v1.0.0
git push origin aimmod-osu-v1.0.0
```

The `AimMod osu Release` workflow can also be run manually. A manual run always builds and verifies both packages; enable its `publish` input to create the version release and advance the selected channel. Stable channel versions must not contain a prerelease suffix, while preview versions must contain one.

Local package builds remain available through:

```powershell
./scripts/build-windows-release.ps1
```

```sh
./scripts/build-linux-release.sh
```

Set `AIMMOD_VERSION` to include a release version in the archive name and assembly metadata.
