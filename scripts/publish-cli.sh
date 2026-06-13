#!/usr/bin/env bash
# Publishes LanInspector CLI for all supported targets.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/LanInspector.Cli/LanInspector.Cli.csproj"

rids=(win-x64 linux-x64 osx-x64 osx-arm64)

for rid in "${rids[@]}"; do
    output="$ROOT/artifacts/laninspector-cli-$rid"
    echo "Publishing CLI ($rid)..."
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o "$output"

    zip_file="$output.zip"
    rm -f "$zip_file"
    (cd "$output" && zip -r "$zip_file" .)
    echo "  Archive: $zip_file"
done

echo ""
echo "All CLI targets published."
