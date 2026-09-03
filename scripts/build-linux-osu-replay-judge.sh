#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
project="$repo_root/tools/osu-replay-judge/osu-replay-judge.csproj"
tauri_bin_dir="$repo_root/src-tauri/bin"
target_name="osu-replay-judge-x86_64-unknown-linux-gnu"
target="$tauri_bin_dir/$target_name"

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  echo "osu-replay-judge packaging currently supports Linux x86_64 only." >&2
  exit 2
fi

mkdir -p -- "$repo_root/.cache"
publish_dir="$(mktemp -d "$repo_root/.cache/osu-replay-judge-publish.XXXXXXXX")"
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
    dotnet publish tools/osu-replay-judge/osu-replay-judge.csproj \
      --configuration Release \
      --runtime linux-x64 \
      --self-contained true \
      --output "$container_output" \
      --nologo
else
  echo "The .NET 8 SDK is required to build the official osu! replay judge." >&2
  exit 2
fi

published="$publish_dir/osu-replay-judge"
if [[ ! -x "$published" ]]; then
  echo "dotnet publish did not produce an executable osu-replay-judge." >&2
  exit 1
fi
if find "$publish_dir" -maxdepth 1 -type f -name '*.so*' -print -quit | grep -q .; then
  echo "osu-replay-judge publish left native libraries outside the executable." >&2
  exit 1
fi
for native_library in libbass.so libbassmix.so libSDL3.so librealm-wrappers.so; do
  if ! grep -aFq -- "$native_library" "$published"; then
    echo "single-file judge is missing embedded native payload: $native_library" >&2
    exit 1
  fi
done

probe="$({
  env \
    -u DISPLAY \
    -u WAYLAND_DISPLAY \
    -u XDG_SESSION_TYPE \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$publish_dir/extracted" \
    timeout 15s "$published" --probe
} 2>&1)" || {
  echo "osu-replay-judge --probe failed:" >&2
  echo "$probe" >&2
  exit 1
}

node -e '
  const probe = JSON.parse(process.argv[1]);
  if (probe.type !== "probe") throw new Error("unexpected probe type");
  if (probe.headlessAudioMuted !== true) throw new Error("headless judge audio is not muted");
  if (probe.timeoutClock !== "wall") throw new Error("judge timeout does not use wall time");
  if (probe.timeoutMs !== 120000) throw new Error(`unexpected timeout: ${probe.timeoutMs}`);
' "$probe"

mkdir -p -- "$tauri_bin_dir"
install -m 0755 -- "$published" "$staged_target"
mv -f -- "$staged_target" "$target"

echo "Prepared Tauri sidecar: $target"
echo "$probe"
