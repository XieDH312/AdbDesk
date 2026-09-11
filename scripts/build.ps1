param([switch]$SkipSetup)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if (-not $SkipSetup) { & (Join-Path $PSScriptRoot 'setup.ps1') }
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Run scripts/setup.ps1 first.' }
Push-Location $taskRoot
try {
    & $taskDotnet build 'AdbDesk.slnx' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $taskDotnet run --project 'tests/AdbDesk.Core.Tests' -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    # A new output directory avoids overwriting a running app or retaining stale dependencies.
    $taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $taskOutput = Join-Path $taskRoot "dist/AdbDesk-win-x64-$taskStamp"
    & $taskDotnet publish 'src/AdbDesk.App/AdbDesk.App.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $taskOutput
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskOutput 'tools/scrcpy/scrcpy.exe'))) { throw 'scrcpy missing from release.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskOutput 'tools/scrcpy/LICENSE.txt'))) { throw 'scrcpy license missing from release.' }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput
    Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput
    New-Item -ItemType Directory -Path (Join-Path $taskOutput 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/verification.md') -Destination (Join-Path $taskOutput 'docs')
    Compress-Archive -LiteralPath $taskOutput -DestinationPath "$taskOutput.zip"
    Write-Host "Portable app: $taskOutput/AdbDesk.exe"
    Write-Host "ZIP: $taskOutput.zip"
} finally { Pop-Location }
