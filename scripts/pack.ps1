<#
.SYNOPSIS
    Packs Cadence into the local feed the sample consumes.

.DESCRIPTION
    The sample deliberately references Cadence as a package rather than by project reference, so
    that packaging mistakes surface here rather than at first publish. Run this before building or
    running the sample.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repoRoot 'artifacts/packages'

New-Item -ItemType Directory -Force $feed | Out-Null

# Clear stale packages: a floating version range would otherwise happily resolve yesterday's build.
Get-ChildItem $feed -Filter '*.nupkg' -ErrorAction SilentlyContinue | Remove-Item -Force

dotnet pack (Join-Path $repoRoot 'Cadence.slnx') `
    --configuration $Configuration `
    --output $feed `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE." }

# Clearing the feed is not enough. NuGet keeps an extracted copy of every package it has restored,
# keyed on id and version alone, and prefers it to the feed -- so a rebuild of the same version is
# invisible to the sample, and a dependency added since the last one never reaches it. Evict exactly
# what was just packed.
$locals = dotnet nuget locals global-packages --list |
    Where-Object { $_ -match 'global-packages:' } |
    Select-Object -First 1

$globalPackages = ($locals -split ':\s*', 2)[1]

if (-not $globalPackages) { throw 'Could not read the global packages folder from dotnet nuget locals.' }

Get-ChildItem $feed -Filter '*.nupkg' | ForEach-Object {
    if ($_.BaseName -match '^(?<id>.+?)\.(?<version>\d+\.\d+\.\d+.*)$') {
        $cached = Join-Path $globalPackages ($Matches.id.ToLowerInvariant()) $Matches.version

        if (Test-Path $cached) { Remove-Item -Recurse -Force $cached }
    }
}

Write-Host ''
Write-Host "Packed into $feed" -ForegroundColor Green
Get-ChildItem $feed -Filter '*.nupkg' | ForEach-Object { Write-Host "  $($_.Name)" }
