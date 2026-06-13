#!/usr/bin/env pwsh
# Publishes the WPF Windows application as a self-contained single-file executable.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\LanInspector-win-x64"

Write-Host "Publishing LanInspector WPF (win-x64)..."
dotnet publish "$root\src\LanInspector.UI\LanInspector.UI.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -o $output

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Output: $output"

$zip = "$output.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$output\*" -DestinationPath $zip
Write-Host "Archive: $zip"
