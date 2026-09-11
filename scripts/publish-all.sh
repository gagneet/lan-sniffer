#!/usr/bin/env bash
# Builds every release artifact this platform can produce.
#
# The WPF desktop application requires Windows; on Linux and macOS only the CLI targets are
# published. Every binary is stamped with the current commit, so `laninspector version` reports
# exactly what it was built from.
#
# Usage: ./scripts/publish-all.sh [--skip-tests]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifacts="$root/artifacts"
skip_tests=0

for arg in "$@"; do
    case "$arg" in
        --skip-tests) skip_tests=1 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

revision="$(git -C "$root" rev-parse --short HEAD 2>/dev/null || echo unknown)"
if [ -n "$(git -C "$root" status --porcelain 2>/dev/null)" ]; then
    revision="$revision-dirty"
fi

echo "LanInspector release build"
echo "  revision: $revision"
echo "  output:   $artifacts"
echo

# A stale executable left in place looks identical to a fresh one, and running one is a reliable
# way to conclude a feature is broken when it is simply absent from that build.
rm -rf "${artifacts:?}"/*
mkdir -p "$artifacts"

if [ "$skip_tests" -eq 0 ]; then
    echo "Running tests..."
    dotnet test "$root/tests/LanInspector.Tests/LanInspector.Tests.csproj" -c Release
    echo
fi

for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
    output="$artifacts/laninspector-cli-$rid"
    echo "Publishing CLI ($rid)..."
    dotnet publish "$root/src/LanInspector.Cli/LanInspector.Cli.csproj" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:SourceRevisionId="$revision" \
        -o "$output"

    (cd "$artifacts" && zip -qr "laninspector-cli-$rid.zip" "laninspector-cli-$rid")
done

echo
echo "Published the CLI for 4 target(s) at revision $revision."
echo "The WPF desktop application can only be published on Windows; use scripts/publish-all.ps1 there."
echo
echo "Confirm what you are about to run with:"
echo "  $artifacts/laninspector-cli-linux-x64/laninspector version"
