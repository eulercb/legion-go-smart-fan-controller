[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64'),
    [string]$Version = '0.1.0-local',
    [string]$FileVersion = '0.1.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required.' }
if ((Test-Path $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw "Output directory must be empty: $output. Choose a fresh directory to avoid stale files or overwriting a running app."
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($app in @(
    @{ Name = 'Tray'; Project = 'src\LegionGoFanControl.Tray\LegionGoFanControl.Tray.csproj' },
    @{ Name = 'Diagnostics'; Project = 'src\LegionGoFanControl.Diagnostics\LegionGoFanControl.Diagnostics.csproj' }
)) {
    dotnet publish (Join-Path $repoRoot $app.Project) -m:1 --configuration Release --runtime win-x64 --self-contained true "-p:Version=$Version" "-p:FileVersion=$FileVersion" --output (Join-Path $output $app.Name)
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($app.Name) with exit code $LASTEXITCODE." }
}
Write-Host "Published self-contained Windows x64 apps under: $output"
