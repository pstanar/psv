#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Psv.App as a self-contained, single-file executable for one or all target RIDs, then
    packages each into a single release archive (.zip for win-x64, .tar.gz for linux-x64/osx-arm64).
.PARAMETER Rid
    Target runtime identifier: win-x64, linux-x64, osx-arm64, or 'all'.
.PARAMETER Configuration
    Build configuration. Defaults to Release.
.EXAMPLE
    ./scripts/publish.ps1 -Rid win-x64
.EXAMPLE
    ./scripts/publish.ps1 -Rid all
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'all')]
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
$Sha = "g$(git rev-parse --short HEAD)"
$VersionLabel = if ($Build -eq '0') { $Version } else { "$Version.$Build" }
Write-Host "Version: $VersionLabel+$Sha"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Psv.App/Psv.App.csproj'
$artifactsDir = Join-Path $repoRoot 'artifacts'
$allRids = @('win-x64', 'linux-x64', 'osx-arm64')
$targets = if ($Rid -eq 'all') { $allRids } else { @($Rid) }

# Windows has no concept of a Unix executable bit, so a straight Compress-Archive there is fine as
# a .zip - nothing to preserve. linux-x64/osx-arm64 need the `psv` binary's execute permission to
# actually survive into the archive, and Windows' bundled tar.exe (bsdtar) can't be told to force a
# mode on creation (confirmed: it always writes whatever permission bits NTFS reports, which for a
# freshly published file is rw-rw-rw- - no execute bit at all, even for the main executable). Built
# on top of System.Formats.Tar instead, which lets each entry's mode be set explicitly regardless of
# the host OS, so publishing a Linux/macOS archive from a Windows machine still produces a correctly
# executable binary once extracted.
function New-TarGzArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationTarGz,
        [Parameter(Mandatory = $true)][string]$ExecutableName
    )

    $executableMode = [System.IO.UnixFileMode](
        [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite -bor [System.IO.UnixFileMode]::UserExecute -bor
        [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::GroupExecute -bor
        [System.IO.UnixFileMode]::OtherRead -bor [System.IO.UnixFileMode]::OtherExecute)
    $regularMode = [System.IO.UnixFileMode](
        [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite -bor
        [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::OtherRead)

    $fileStream = [System.IO.File]::Create($DestinationTarGz)
    try {
        $gzipStream = [System.IO.Compression.GZipStream]::new($fileStream, [System.IO.Compression.CompressionLevel]::Optimal)
        try {
            # leaveOpen: true - the gzip/file streams are disposed explicitly below, once for the
            # whole archive rather than per entry.
            $tarWriter = [System.Formats.Tar.TarWriter]::new($gzipStream, [System.Formats.Tar.TarEntryFormat]::Pax, $true)
            try {
                Get-ChildItem -File -Recurse -LiteralPath $SourceDir | ForEach-Object {
                    $relativePath = [System.IO.Path]::GetRelativePath($SourceDir, $_.FullName).Replace('\', '/')
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relativePath)
                    $entry.Mode = if ($_.Name -eq $ExecutableName) { $executableMode } else { $regularMode }
                    $dataStream = [System.IO.File]::OpenRead($_.FullName)
                    try {
                        $entry.DataStream = $dataStream
                        $tarWriter.WriteEntry($entry)
                    }
                    finally {
                        $dataStream.Dispose()
                    }
                }
            }
            finally {
                $tarWriter.Dispose()
            }
        }
        finally {
            $gzipStream.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

foreach ($targetRid in $targets) {
    $outDir = Join-Path $artifactsDir $targetRid
    Write-Host "Publishing $targetRid -> $outDir" -ForegroundColor Cyan

    dotnet publish $project `
        -c $Configuration `
        -r $targetRid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        "-p:Version=$Version" `
        "-p:FileVersion=$Version.$Build" `
        "-p:InformationalVersion=$VersionLabel" `
        "-p:SourceRevisionId=$Sha" `
        -o $outDir

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for RID '$targetRid' (exit code $LASTEXITCODE)"
    }

    # One package per OS, not a universal archive: the natural single-file format differs per
    # platform (Explorer opens .zip natively on Windows; Unix executables need permission bits a
    # .zip won't reliably carry, so linux-x64/osx-arm64 get a .tar.gz instead).
    $archiveBaseName = "psv-$VersionLabel-$targetRid"
    if ($targetRid -eq 'win-x64') {
        $archivePath = Join-Path $artifactsDir "$archiveBaseName.zip"
        Write-Host "Packaging $archivePath" -ForegroundColor Cyan
        if (Test-Path $archivePath) { Remove-Item $archivePath }
        Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $archivePath
    }
    else {
        $archivePath = Join-Path $artifactsDir "$archiveBaseName.tar.gz"
        Write-Host "Packaging $archivePath" -ForegroundColor Cyan
        if (Test-Path $archivePath) { Remove-Item $archivePath }
        New-TarGzArchive -SourceDir $outDir -DestinationTarGz $archivePath -ExecutableName 'psv'
    }
}
