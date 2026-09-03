#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
project="$repo_root/tools/osu-replay-host/osu-replay-host.csproj"
tauri_bin_dir="$repo_root/src-tauri/bin"
target_name="osu-replay-host-x86_64-unknown-linux-gnu"
target="$tauri_bin_dir/$target_name"

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  echo "osu-replay-host packaging currently supports Linux x86_64 only." >&2
  exit 2
fi

mkdir -p -- "$repo_root/.cache"
publish_dir="$(mktemp -d "$repo_root/.cache/osu-replay-host-publish.XXXXXXXX")"
staged_target="$tauri_bin_dir/.$target_name.tmp.$$"

cleanup() {
  rm -rf -- "$publish_dir"
  rm -f -- "$staged_target"
}
trap cleanup EXIT

if command -v dotnet >/dev/null 2>&1 && [[ -n "$(dotnet --list-sdks 2>/dev/null)" ]]; then
  dotnet publish "$project" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$publish_dir" \
    --nologo
elif command -v podman >/dev/null 2>&1 && podman image exists mcr.microsoft.com/dotnet/sdk:8.0; then
  mkdir -p -- "$repo_root/.cache/dotnet-home/.nuget/packages"
  container_output="/workspace/${publish_dir#"$repo_root/"}"
  podman run --rm \
    --userns=keep-id \
    --security-opt label=disable \
    --env DOTNET_CLI_HOME=/workspace/.cache/dotnet-home \
    --env NUGET_PACKAGES=/workspace/.cache/dotnet-home/.nuget/packages \
    --volume "$repo_root:/workspace" \
    --workdir /workspace \
    mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet publish tools/osu-replay-host/osu-replay-host.csproj \
      --configuration Release \
      --runtime linux-x64 \
      --self-contained true \
      --output "$container_output" \
      --nologo
else
  echo "The .NET 8 SDK is required to build the official osu! replay host." >&2
  echo "Install it locally or provide the mcr.microsoft.com/dotnet/sdk:8.0 Podman image." >&2
  exit 2
fi

published="$publish_dir/osu-replay-host"
if [[ ! -x "$published" ]]; then
  echo "dotnet publish did not produce an executable osu-replay-host." >&2
  exit 1
fi

# The Tauri sidecar must remain one file. ppy's native BASS, SDL, Realm, and
# Veldrid libraries are embedded by IncludeNativeLibrariesForSelfExtract.
if find "$publish_dir" -maxdepth 1 -type f -name '*.so*' -print -quit | grep -q .; then
  echo "osu-replay-host publish left native libraries outside the executable." >&2
  exit 1
fi

for native_library in libbass.so libbassmix.so libSDL3.so librealm-wrappers.so; do
  if ! grep -aFq -- "$native_library" "$published"; then
    echo "single-file host is missing embedded native payload: $native_library" >&2
    exit 1
  fi
done

# Probe the staged executable without a display server. This catches accidental
# framework initialisation in --probe as well as broken self-contained bundles.
probe="$({
  env \
    -u DISPLAY \
    -u WAYLAND_DISPLAY \
    -u XDG_SESSION_TYPE \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$publish_dir/extracted" \
    timeout 15s "$published" --probe
} 2>&1)" || {
  echo "osu-replay-host --probe failed:" >&2
  echo "$probe" >&2
  exit 1
}

# The single-quoted JavaScript deliberately keeps its template literal out of Bash.
# shellcheck disable=SC2016
node -e '
  const probe = JSON.parse(process.argv[1]);
  const expected = {
    type: "probe",
    protocolVersion: "aimmod.osu-replay-host.v1",
    engine: "ppy.osu.Game",
    renderer: "native-window",
    audio: "native-bass-mixer",
  };
  for (const [key, value] of Object.entries(expected)) {
    if (probe[key] !== value) {
      throw new Error(`unexpected probe field ${key}: ${JSON.stringify(probe[key])}`);
    }
  }
' "$probe"

mkdir -p -- "$tauri_bin_dir"
install -m 0755 -- "$published" "$staged_target"
mv -f -- "$staged_target" "$target"

echo "Prepared Tauri sidecar: $target"
echo "$probe"
