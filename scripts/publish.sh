#!/usr/bin/env bash
# Publishes Psv.App as a self-contained, single-file executable for one or all target RIDs.
#
# Usage:
#   ./scripts/publish.sh <win-x64|linux-x64|osx-arm64|all> [configuration]
#
# Examples:
#   ./scripts/publish.sh linux-x64
#   ./scripts/publish.sh all
set -euo pipefail

rid="${1:-}"
configuration="${2:-Release}"

all_rids=(win-x64 linux-x64 osx-arm64)

case "$rid" in
    win-x64|linux-x64|osx-arm64|all) ;;
    *)
        echo "Usage: $0 <win-x64|linux-x64|osx-arm64|all> [configuration]" >&2
        exit 1
        ;;
esac

# Version from nearest git tag (fallback 0.0.0) + commits since tag + short SHA
version=$(git describe --tags --abbrev=0 2>/dev/null || echo '0.0.0')
version="${version#v}"
long=$(git describe --tags --long 2>/dev/null || echo '')
if [[ "$long" =~ -([0-9]+)-g[0-9a-f]+$ ]]; then
    build="${BASH_REMATCH[1]}"
else
    build='0'
fi
sha=$(git rev-parse --short HEAD)
echo "Version: ${version}.${build}+${sha}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Psv.App/Psv.App.csproj"

if [[ "$rid" == 'all' ]]; then
    targets=("${all_rids[@]}")
else
    targets=("$rid")
fi

for target_rid in "${targets[@]}"; do
    out_dir="$repo_root/artifacts/$target_rid"
    echo "Publishing $target_rid -> $out_dir"

    dotnet publish "$project" \
        -c "$configuration" \
        -r "$target_rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishReadyToRun=true \
        "-p:Version=$version" \
        "-p:FileVersion=$version.$build" \
        "-p:InformationalVersion=$version.$build" \
        "-p:SourceRevisionId=$sha" \
        -o "$out_dir"
done
