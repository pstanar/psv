#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Psv.App as a self-contained, single-file executable for one or all target RIDs.
.PARAMETER Rid
    Target runtime identifier: win-x64, linux-x64, osx-x64, osx-arm64, or 'all'.
.PARAMETER Configuration
    Build configuration. Defaults to Release.
.EXAMPLE
    ./scripts/publish.ps1 -Rid win-x64
.EXAMPLE
    ./scripts/publish.ps1 -Rid all
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64', 'all')]
    [string]$Rid,

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Version from nearest git tag (fallback 0.0.0) + commits since tag + short SHA
$Version = git describe --tags --abbrev=0 2>$null
if ($LASTEXITCODE -ne 0 -or -not $Version) { $Version = '0.0.0' }
$Version = $Version -replace '^v', ''
$Long = git describe --tags --long 2>$null
if ($Long -match '-(\d+)-g') { $Build = $Matches[1] } else { $Build = '0' }
$Sha = git rev-parse --short HEAD
Write-Host "Version: $Version.$Build+$Sha"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Psv.App/Psv.App.csproj'
$allRids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
$targets = if ($Rid -eq 'all') { $allRids } else { @($Rid) }

foreach ($targetRid in $targets) {
    $outDir = Join-Path $repoRoot "artifacts/$targetRid"
    Write-Host "Publishing $targetRid -> $outDir" -ForegroundColor Cyan

    dotnet publish $project `
        -c $Configuration `
        -r $targetRid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        "-p:Version=$Version" `
        "-p:FileVersion=$Version.$Build" `
        "-p:SourceRevisionId=$Sha" `
        -o $outDir

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for RID '$targetRid' (exit code $LASTEXITCODE)"
    }
}
