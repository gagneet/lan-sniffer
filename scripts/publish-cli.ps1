#!/usr/bin/env pwsh
# Publishes LanInspector CLI for all supported targets.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = "$root\src\LanInspector.Cli\LanInspector.Cli.csproj"

$targets = @(
    "win-x64",
    "linux-x64",
    "osx-x64",
    "osx-arm64"
)

foreach ($rid in $targets) {
    $output = "$root\artifacts\laninspector-cli-$rid"
    Write-Host "Publishing CLI ($rid)..."
    dotnet publish $project `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $output

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $zip = "$output.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$output\*" -DestinationPath $zip
    Write-Host "  Archive: $zip"
}

Write-Host ""
Write-Host "All CLI targets published."
