# Linux release packaging

AimMod for osu ships as its own download. The archive contains the native AimMod shell and a separate osu worker assembly. AimMod starts that worker through its own internal `--worker` process mode. The package contains one apphost and one self-contained .NET runtime. It does not contain the KovaaK client, a web frontend, Tauri, or Node packages.

Run the complete release check without opening a window:

```sh
./scripts/build-linux-release.sh
```

The script uses `aimmod-dotnet-sdk/dotnet` and `aimmod-dotnet-nuget` under `$XDG_CACHE_HOME` (or `$HOME/.cache` when unset). Set `AIMMOD_DOTNET` or `AIMMOD_NUGET_PACKAGES` to use another cache. If the executable is missing, the script may use the already-cached `mcr.microsoft.com/dotnet/sdk:8.0` Podman image with the same NuGet cache. It checks that either route reports the exact SDK version recorded in `global.json` and `packaging/ppy-packages.json`. It never pulls a container image.

The build does the following work:

1. Checks the ppy package manifest against every source project.
2. Restores through checked-in portable and runtime-specific NuGet lock files.
3. Builds and tests the solution in Release mode.
4. Publishes one self-contained AimMod executable for one Linux runtime identifier. The worker stays in its own assembly and runs through `AimMod --worker`.
5. Rejects files outside the package allowlist and scans every file for known React, Tauri, Node, and KovaaK markers. It also rejects full osu desktop, tournament, Catch, Mania, and Taiko assemblies.
6. Starts the published executable in worker mode and checks a complete `hello` and `shutdown` exchange. Any non-protocol standard output fails the build.
7. Writes an inventory containing the size and SHA-256 digest of every shipped file.
8. Creates a stable gzip archive with sorted paths, fixed ownership, and the `SOURCE_DATE_EPOCH` timestamp.

Outputs go to `artifacts/`:

```text
artifacts/
|-- aimmod-osu-linux-x64/
|   |-- app/
|   |-- artifact-inventory.json
|   `-- ppy-packages.json
|-- aimmod-osu-linux-x64.tar.gz
`-- aimmod-osu-linux-x64.tar.gz.sha256
```

Set `AIMMOD_RUNTIME_ID=linux-arm64` for an ARM64 package. The default is `linux-x64`.

Run the package policy tests on their own with:

```sh
./tests/test-package-policy.sh
```

Check local SDK selection and the cached-container fallback without compiling with:

```sh
./scripts/build-linux-release.sh --verify-toolchain
```

The allowlist is `packaging/linux-artifact-policy.json`. Adding a new file type or release directory requires an explicit policy change. Do not weaken the content-marker checks to make an unexpected artifact pass. Find which dependency or publish setting added it first.

## Worker process model

The desktop starts `Environment.ProcessPath` with the single internal argument `--worker`. It redirects standard input and output directly, without a shell or localhost server. The worker branch runs before AimMod creates `HostOptions`, a `DesktopGameHost`, or `AimModGame`, so it cannot initialize the GUI.

`AimMod.Osu.Worker` remains a separate assembly. Standard output is reserved for one-line JSON protocol responses. Diagnostics use standard error. The package policy requires the assembly but rejects a second apphost, worker runtime configuration, or `libexec` tree.

## Measured Linux x64 baseline

The 2026-09-02 single-apphost publish contains 376 files and 298,222,873 logical and physical bytes. The gzip archive is 174,003,137 bytes. Compared with the transitional two-tree package, this removes 373 files and 298,005,391 logical bytes. The old package hard-linked shared files, so the installed physical reduction is 198,346 bytes and the archive reduction is 59,777 bytes.

`packaging/linux-x64-size-baseline.json` records the exact byte counts, package versions, archive digest, and single-apphost release gate in machine-readable form.
