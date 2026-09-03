#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
project="$repo_root/tools/osu-realm-reader/osu-realm-reader.csproj"
tauri_bin_dir="$repo_root/src-tauri/bin"
target_name="osu-realm-reader-x86_64-unknown-linux-gnu"
target="$tauri_bin_dir/$target_name"

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  echo "osu-realm-reader packaging currently supports Linux x86_64 only." >&2
  exit 2
fi

mkdir -p -- "$repo_root/.cache"
publish_dir="$(mktemp -d "$repo_root/.cache/osu-realm-reader-publish.XXXXXXXX")"
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
    dotnet publish tools/osu-realm-reader/osu-realm-reader.csproj \
      --configuration Release \
      --runtime linux-x64 \
      --self-contained true \
      --output "$container_output" \
      --nologo
else
  echo "The .NET 8 SDK is required to build the osu!lazer Realm reader." >&2
  echo "Install it locally or provide the mcr.microsoft.com/dotnet/sdk:8.0 Podman image." >&2
  exit 2
fi

published="$publish_dir/osu-realm-reader"
if [[ ! -x "$published" ]]; then
  echo "dotnet publish did not produce an executable osu-realm-reader." >&2
  exit 1
fi
if find "$publish_dir" -maxdepth 1 -type f -name '*.so*' -print -quit | grep -q .; then
  echo "osu-realm-reader publish left native libraries outside the executable." >&2
  exit 1
fi
if ! grep -aFq -- "librealm-wrappers.so" "$published"; then
  echo "single-file Realm reader is missing its embedded native Realm library." >&2
  exit 1
fi

set +e
usage="$({ "$published"; } 2>&1)"
usage_status=$?
set -e
if [[ $usage_status -ne 2 ]] || ! grep -Fq -- "beatmap-set-files" <<<"$usage"; then
  echo "the staged Realm reader does not advertise the beatmap-set-files command." >&2
  exit 1
fi

mkdir -p -- "$tauri_bin_dir"
install -m 0755 -- "$published" "$staged_target"
mv -f -- "$staged_target" "$target"

echo "Prepared Tauri sidecar: $target"
