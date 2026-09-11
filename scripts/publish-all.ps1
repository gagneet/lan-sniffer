#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds every release artifact: the WPF desktop application and the CLI for all targets.

.DESCRIPTION
    Runs the test suite first, then publishes self-contained single-file executables into
    artifacts\ and zips each one. Every binary is stamped with the current commit, so
    `laninspector version` reports exactly what it was built from.

    Publishing the WPF application requires Windows. On Linux and macOS the CLI targets are
    published and the WPF step is skipped with a note, rather than failing the run.

.PARAMETER SkipTests
    Publish without running the test suite first. Not recommended.

.PARAMETER CliOnly
    Publish only the CLI targets.

.EXAMPLE
    .\scripts\publish-all.ps1
#>
param(
    [switch]$SkipTests,
    [switch]$CliOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$solution = Join-Path $root "LanInspector.sln"

# Stamped into InformationalVersion so a published binary can name its own commit.
$revision = "unknown"
try {
    $revision = (git -C $root rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $revision = "unknown" }

    if ((git -C $root status --porcelain 2>$null)) {
        $revision = "$revision-dirty"
    }
} catch {
    Write-Host "Could not read the git revision; binaries will be stamped 'unknown'." -ForegroundColor Yellow
}

Write-Host "LanInspector release build" -ForegroundColor Cyan
Write-Host "  revision: $revision"
Write-Host "  output:   $artifacts"
Write-Host ""

if (Test-Path $artifacts) {
    # A stale executable left in place is indistinguishable from a fresh one at a glance, and
    # running one is a reliable way to conclude a feature is broken when it is simply absent.
    Write-Host "Clearing previous artifacts..."
    Remove-Item "$artifacts\*" -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests\LanInspector.Tests\LanInspector.Tests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed - not publishing." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host ""
}

$published = @()

# --- CLI, every supported target ---
$cliProject = Join-Path $root "src\LanInspector.Cli\LanInspector.Cli.csproj"
foreach ($rid in @("win-x64", "linux-x64", "osx-x64", "osx-arm64")) {
    $output = Join-Path $artifacts "laninspector-cli-$rid"
    Write-Host "Publishing CLI ($rid)..." -ForegroundColor Cyan

    dotnet publish $cliProject `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:SourceRevisionId=$revision `
        -o $output

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $zip = "$output.zip"
    Compress-Archive -Path "$output\*" -DestinationPath $zip -Force
    $published += [pscustomobject]@{ Artifact = "laninspector-cli-$rid"; Zip = $zip }
}

# --- WPF desktop application (Windows only) ---
if (-not $CliOnly) {
    if ($IsWindows -or $null -eq $IsWindows) {
        $output = Join-Path $artifacts "LanInspector-win-x64"
        Write-Host "Publishing WPF desktop application (win-x64)..." -ForegroundColor Cyan

        dotnet publish (Join-Path $root "src\LanInspector.UI\LanInspector.UI.csproj") `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:PublishReadyToRun=true `
            -p:SourceRevisionId=$revision `
            -o $output

        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $zip = "$output.zip"
        Compress-Archive -Path "$output\*" -DestinationPath $zip -Force
        $published += [pscustomobject]@{ Artifact = "LanInspector-win-x64"; Zip = $zip }
    } else {
        Write-Host "Skipping the WPF application: it can only be published on Windows." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Published $($published.Count) artifact(s) at revision $revision" -ForegroundColor Green
$published | ForEach-Object { Write-Host "  $($_.Artifact)" }
Write-Host ""
Write-Host "Confirm what you are about to run with:"
Write-Host "  $artifacts\laninspector-cli-win-x64\laninspector.exe version"
Write-Host ""
Write-Host "Note: packet capture needs Npcap installed separately on the target Windows machine."
