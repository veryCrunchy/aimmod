#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)
rid=${AIMMOD_RUNTIME_ID:-linux-x64}
version=${AIMMOD_VERSION:-}
configuration=Release
version_segment=
version_args=()
if [[ -n "$version" ]]; then
    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z]+)*([+][0-9A-Za-z.-]+)?$ ]]; then
        echo "AIMMOD_VERSION must be a SemVer-compatible version without a leading v: $version" >&2
        exit 2
    fi
    version_segment="${version}-"
    version_args=(--property:Version="$version")
fi
artifact_name="aimmod-osu-${version_segment}${rid}"
artifact_root="$repo_root/artifacts"
stage="$artifact_root/$artifact_name"
archive="$artifact_root/$artifact_name.tar.gz"
archive_checksum="$archive.sha256"
local_sdk=${AIMMOD_DOTNET:-/home/crunchy/.cache/aimmod-dotnet-sdk/dotnet}
container_image=${AIMMOD_DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}
nuget_cache=${AIMMOD_NUGET_PACKAGES:-/home/crunchy/.cache/aimmod-dotnet-nuget}

case "$rid" in
    linux-x64|linux-arm64) ;;
    *)
        echo "Unsupported Linux runtime identifier: $rid" >&2
        exit 2
        ;;
esac

run_dotnet() {
    if [[ -x "$local_sdk" ]]; then
        NUGET_PACKAGES="$nuget_cache" "$local_sdk" "$@"
        return
    fi

    if ! command -v podman >/dev/null 2>&1; then
        echo "The pinned local SDK is missing and podman is unavailable." >&2
        exit 2
    fi
    if ! podman image exists "$container_image"; then
        echo "Cached SDK image is missing: $container_image" >&2
        echo "The release script will not pull an unreviewed build image." >&2
        exit 2
    fi

    mkdir -p -- "$nuget_cache"

    podman run --rm --userns=keep-id --security-opt label=disable \
        --workdir /workspace \
        --env DOTNET_CLI_HOME=/tmp/aimmod-dotnet-cli \
        --env NUGET_PACKAGES=/nuget \
        --volume "$repo_root:/workspace" \
        --volume "$nuget_cache:/nuget" \
        "$container_image" dotnet "$@"
}

required_sdk=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["dotnetSdk"])' "$repo_root/packaging/ppy-packages.json")
actual_sdk=$(run_dotnet --version)
if [[ "$actual_sdk" != "$required_sdk" ]]; then
    echo "Expected .NET SDK $required_sdk, found $actual_sdk." >&2
    exit 2
fi

python3 "$repo_root/scripts/verify-ppy-pins.py" \
    --root "$repo_root" \
    --manifest "$repo_root/packaging/ppy-packages.json"

if [[ "${1:-}" == "--verify-toolchain" ]]; then
    printf 'Toolchain ready: .NET SDK %s\n' "$actual_sdk"
    exit 0
fi
if [[ $# -gt 0 ]]; then
    echo "Usage: $0 [--verify-toolchain]" >&2
    exit 2
fi

mkdir -p -- "$artifact_root"
case "$stage" in
    "$repo_root"/artifacts/aimmod-osu-linux-*|"$repo_root"/artifacts/aimmod-osu-*-linux-*) ;;
    *)
        echo "Refusing to clear unexpected staging path: $stage" >&2
        exit 2
        ;;
esac
rm -rf -- "$stage"
rm -f -- "$archive" "$archive_checksum"
mkdir -p -- "$stage/app"

cd -- "$repo_root"
run_dotnet restore AimMod.Native.sln --locked-mode --verbosity minimal
run_dotnet build AimMod.Native.sln \
    --configuration "$configuration" \
    --no-restore \
    "${version_args[@]}" \
    --property:ContinuousIntegrationBuild=true \
    --verbosity minimal

worker_test_project="tests/AimMod.Osu.Worker.Tests/AimMod.Osu.Worker.Tests.csproj"

run_isolated_worker_tests() {
    local filter=$1
    local attempt

    for attempt in 1 2; do
        if run_dotnet test "$worker_test_project" \
            --configuration "$configuration" \
            --no-restore \
            --property:ContinuousIntegrationBuild=true \
            --filter "$filter" \
            --verbosity minimal; then
            return 0
        fi

        if [[ "$attempt" -eq 1 ]]; then
            echo "The isolated Realm test host aborted; retrying once in a fresh process." >&2
        fi
    done

    return 1
}

for test_project in tests/*/*.csproj; do
    run_dotnet restore "$test_project" --locked-mode --verbosity minimal

    if [[ "$test_project" == "$worker_test_project" ]]; then
        # Realm's coordinator is process-global and can abort the entire test host
        # when separate snapshot fixtures tear down native state in sequence. Keep
        # every test in the release gate, but give each Realm fixture its own host.
        realm_fixtures=(
            ExternalLazerCatalogReaderTests
            ExternalLazerRealmBridgeTests
            ExternalLazerSkinCatalogReaderTests
        )
        non_realm_filter="FullyQualifiedName!~ExternalLazerCatalogReaderTests&FullyQualifiedName!~ExternalLazerRealmBridgeTests&FullyQualifiedName!~ExternalLazerSkinCatalogReaderTests"

        run_isolated_worker_tests "$non_realm_filter"

        for fixture in "${realm_fixtures[@]}"; do
            run_isolated_worker_tests "FullyQualifiedName~$fixture"
        done
        continue
    fi

    run_dotnet test "$test_project" \
        --configuration "$configuration" \
        --no-restore \
        --property:ContinuousIntegrationBuild=true \
        --verbosity minimal
done

run_dotnet restore src/AimMod.Desktop/AimMod.Desktop.csproj \
    --runtime "$rid" \
    --locked-mode \
    --property:NuGetLockFilePath="packages.$rid.lock.json" \
    --verbosity minimal
run_dotnet publish src/AimMod.Desktop/AimMod.Desktop.csproj \
    --configuration "$configuration" \
    --runtime "$rid" \
    --self-contained true \
    --no-restore \
    --output "artifacts/$artifact_name/app" \
    "${version_args[@]}" \
    --property:NuGetLockFilePath="packages.$rid.lock.json" \
    --property:ContinuousIntegrationBuild=true \
    --property:DebugSymbols=false \
    --property:DebugType=None \
    --verbosity minimal

cp -- "$repo_root/packaging/ppy-packages.json" "$stage/ppy-packages.json"
python3 "$repo_root/scripts/audit-linux-package.py" "$stage" \
    --policy "$repo_root/packaging/linux-artifact-policy.json" \
    --pins "$repo_root/packaging/ppy-packages.json" \
    --inventory "$stage/artifact-inventory.json"

"$repo_root/tests/test-worker-mode.sh" "$stage/app/AimMod"

source_date_epoch=${SOURCE_DATE_EPOCH:-0}
if [[ ! "$source_date_epoch" =~ ^[0-9]+$ ]]; then
    echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2
    exit 2
fi

tar --sort=name \
    --mtime="@$source_date_epoch" \
    --owner=0 \
    --group=0 \
    --numeric-owner \
    --pax-option=delete=atime,delete=ctime \
    -C "$artifact_root" \
    -cf - "$artifact_name" | gzip -n -9 > "$archive"

(
    cd -- "$artifact_root"
    sha256sum "$(basename -- "$archive")" > "$(basename -- "$archive_checksum")"
)

logical_bytes=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["logicalBytes"])' "$stage/artifact-inventory.json")
physical_bytes=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["physicalBytes"])' "$stage/artifact-inventory.json")
archive_bytes=$(stat --format=%s "$archive")
printf 'Release: %s\n' "$archive"
printf 'Inventory: %s\n' "$stage/artifact-inventory.json"
printf 'Logical publish bytes: %s\n' "$logical_bytes"
printf 'Installed physical bytes: %s\n' "$physical_bytes"
printf 'Archive bytes: %s\n' "$archive_bytes"
