#!/usr/bin/env bash
# Publishes Psv.App as a self-contained, single-file executable for one or all target RIDs, then
# packages each into a single release archive (.zip for win-x64, .tar.gz for linux-x64/osx-arm64).
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
sha="g$(git rev-parse --short HEAD)"
if [[ "$build" == '0' ]]; then
    version_label="$version"
else
    version_label="${version}.${build}"
fi
echo "Version: ${version_label}+${sha}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Psv.App/Psv.App.csproj"

if [[ "$rid" == 'all' ]]; then
    targets=("${all_rids[@]}")
else
    targets=("$rid")
fi

artifacts_dir="$repo_root/artifacts"

for target_rid in "${targets[@]}"; do
    out_dir="$artifacts_dir/$target_rid"
    echo "Publishing $target_rid -> $out_dir"

    dotnet publish "$project" \
        -c "$configuration" \
        -r "$target_rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishReadyToRun=true \
        "-p:Version=$version" \
        "-p:FileVersion=$version.$build" \
        "-p:InformationalVersion=$version_label" \
        "-p:SourceRevisionId=$sha" \
        -o "$out_dir"

    # One package per OS, not a universal archive: the natural single-file format differs per
    # platform (Explorer opens .zip natively on Windows; Unix executables need permission bits a
    # .zip won't reliably carry, so linux-x64/osx-arm64 get a .tar.gz instead).
    archive_base="psv-${version_label}-${target_rid}"
    if [[ "$target_rid" == 'win-x64' ]]; then
        archive_path="$artifacts_dir/${archive_base}.zip"
        echo "Packaging $archive_path"
        rm -f "$archive_path"
        (cd "$out_dir" && zip -rq "$archive_path" .)
    else
        archive_path="$artifacts_dir/${archive_base}.tar.gz"
        echo "Packaging $archive_path"
        rm -f "$archive_path"

        # Belt-and-suspenders: dotnet publish already marks a self-contained Linux/macOS apphost
        # executable when it runs on a matching (or POSIX) host, but this makes the bit explicit
        # rather than trusting that as an invariant - tar only ever archives whatever mode is
        # already on disk, so a missing +x here would silently ship a non-executable binary. This
        # assumes a real POSIX filesystem (Linux, macOS): under Git Bash/MSYS on Windows, chmod
        # doesn't actually persist onto the underlying NTFS file, so the bit silently doesn't stick
        # there - use publish.ps1 on Windows instead, which sets it explicitly via .NET's tar writer
        # rather than relying on chmod.
        chmod +x "$out_dir/psv"
        tar -czf "$archive_path" -C "$out_dir" .
    fi
done
