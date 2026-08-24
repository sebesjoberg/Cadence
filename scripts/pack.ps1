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

Write-Host ''
Write-Host "Packed into $feed" -ForegroundColor Green
Get-ChildItem $feed -Filter '*.nupkg' | ForEach-Object { Write-Host "  $($_.Name)" }
