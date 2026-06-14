param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$publishDir = Join-Path $root "artifacts\LanInspector-$Runtime"
$zipPath = Join-Path $root "artifacts\LanInspector-$Runtime.zip"
$project = Join-Path $root "src\LanInspector.UI\LanInspector.UI.csproj"
$solution = Join-Path $root "LanInspector.sln"

Write-Host "Cleaning publish output..."
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Restoring..."
dotnet restore $solution

Write-Host "Building..."
dotnet build $solution -c $Configuration --no-restore

Write-Host "Publishing self-contained single-file executable..."
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -o $publishDir

Write-Host "Creating ZIP artifact..."
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Publish complete."
Write-Host "Executable folder: $publishDir"
Write-Host "ZIP artifact:       $zipPath"
Write-Host ""
Write-Host "Note: Npcap must be installed separately on the target Windows machine for packet capture."