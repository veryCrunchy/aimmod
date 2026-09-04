#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT

make_valid_fixture() {
    local root=$1
    mkdir -p -- "$root/app"
    touch -- \
        "$root/app/AimMod" \
        "$root/app/AimMod.dll" \
        "$root/app/AimMod.deps.json" \
        "$root/app/AimMod.runtimeconfig.json" \
        "$root/app/aimmod-osu-worker.dll" \
        "$root/app/osu.Game.dll" \
        "$root/app/osu.Game.Rulesets.Osu.dll"
    cp -- "$repo_root/packaging/ppy-packages.json" "$root/ppy-packages.json"
    mkdir -p -- "$root/app/share/icons"
    cp -R -- "$repo_root/src/AimMod.Desktop/Resources/Brand/linux/hicolor" "$root/app/share/icons/hicolor"
    chmod +x -- "$root/app/AimMod"
}

run_audit() {
    python3 "$repo_root/scripts/audit-linux-package.py" "$1" \
        --policy "$repo_root/packaging/linux-artifact-policy.json" \
        --pins "$repo_root/packaging/ppy-packages.json"
}

valid="$fixture/valid"
make_valid_fixture "$valid"
run_audit "$valid" >/dev/null

missing_icon="$fixture/missing-icon"
cp -a -- "$valid" "$missing_icon"
rm -- "$missing_icon/app/share/icons/hicolor/256x256/apps/aimmod-osu.png"
if run_audit "$missing_icon" >/dev/null 2>&1; then
    echo "policy accepted missing branded icon" >&2
    exit 1
fi

extra_image="$fixture/extra-image"
cp -a -- "$valid" "$extra_image"
touch -- "$extra_image/app/unrelated.png"
if run_audit "$extra_image" >/dev/null 2>&1; then
    echo "policy accepted an image outside the branded icon paths" >&2
    exit 1
fi

web_fixture="$fixture/web"
cp -a -- "$valid" "$web_fixture"
printf 'ReactDOM' > "$web_fixture/app/index.js"
if run_audit "$web_fixture" >/dev/null 2>&1; then
    echo "policy accepted a JavaScript frontend asset" >&2
    exit 1
fi

kovaak_fixture="$fixture/kovaak"
cp -a -- "$valid" "$kovaak_fixture"
printf 'KovaaK payload' > "$kovaak_fixture/app/Foreign.dll"
if run_audit "$kovaak_fixture" >/dev/null 2>&1; then
    echo "policy accepted a KovaaK content marker" >&2
    exit 1
fi

outside_fixture="$fixture/outside"
cp -a -- "$valid" "$outside_fixture"
touch -- "$outside_fixture/app/debug.pdb"
if run_audit "$outside_fixture" >/dev/null 2>&1; then
    echo "policy accepted a file outside the allowlist" >&2
    exit 1
fi

second_apphost_fixture="$fixture/second-apphost"
cp -a -- "$valid" "$second_apphost_fixture"
mkdir -p -- "$second_apphost_fixture/libexec/aimmod-osu-worker"
touch -- "$second_apphost_fixture/libexec/aimmod-osu-worker/aimmod-osu-worker"
if run_audit "$second_apphost_fixture" >/dev/null 2>&1; then
    echo "policy accepted a second worker apphost" >&2
    exit 1
fi

for worker_host_file in \
    aimmod-osu-worker \
    aimmod-osu-worker.deps.json \
    aimmod-osu-worker.runtimeconfig.json; do
    duplicate_host_fixture="$fixture/duplicate-${worker_host_file}"
    cp -a -- "$valid" "$duplicate_host_fixture"
    touch -- "$duplicate_host_fixture/app/$worker_host_file"
    if run_audit "$duplicate_host_fixture" >/dev/null 2>&1; then
        echo "policy accepted a second worker host file: $worker_host_file" >&2
        exit 1
    fi
done

for assembly in \
    osu.Desktop.dll \
    osu.Game.Tournament.dll \
    osu.Game.Rulesets.Catch.dll \
    osu.Game.Rulesets.Mania.dll \
    osu.Game.Rulesets.Taiko.dll; do
    denied_osu_fixture="$fixture/denied-${assembly}"
    cp -a -- "$valid" "$denied_osu_fixture"
    touch -- "$denied_osu_fixture/app/$assembly"
    if run_audit "$denied_osu_fixture" >/dev/null 2>&1; then
        echo "policy accepted denied osu assembly: $assembly" >&2
        exit 1
    fi
done

echo "Packaging policy tests passed."
