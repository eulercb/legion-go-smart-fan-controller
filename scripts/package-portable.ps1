[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version = '0.1.0-local',
    [string]$FileVersion = '0.1.0.0'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a simple semantic version.' }
$ArchiveName = "LegionGoSmartFanControl-$Version-win-x64.zip"
if ((Test-Path $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) { throw "Output directory must be empty: $output" }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$stage = Join-Path $output 'portable'
& (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $stage -Version $Version -FileVersion $FileVersion
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $stage -Recurse
$archive = Join-Path $output $ArchiveName
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $ArchiveName`n", [Text.UTF8Encoding]::new($false))
Write-Host "Portable archive: $archive"
