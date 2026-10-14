param([switch]$SkipSdk)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskCache = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null

function Get-VerifiedArchive([string]$Url, [string]$Path, [string]$Hash, [string]$Algorithm) {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -eq $Hash) { return }
    Invoke-WebRequest -Uri $Url -OutFile $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) {
        throw "Checksum mismatch: $Path"
    }
}

if (-not $SkipSdk) {
    $taskSdkExe = Join-Path $taskCache 'dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $taskSdkExe)) {
        $taskSdkZip = Join-Path $taskCache 'dotnet-sdk.zip'
        Get-VerifiedArchive 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' $taskSdkZip '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430' 'SHA512'
        Expand-Archive -LiteralPath $taskSdkZip -DestinationPath (Join-Path $taskCache 'dotnet') -Force
    }
    & $taskSdkExe --version
    if ($LASTEXITCODE -ne 0) { throw 'Local SDK failed to start.' }
}

$taskTools = Join-Path $taskRoot 'tools/scrcpy'
$taskScrcpyZip = Join-Path $taskCache 'scrcpy.zip'
Get-VerifiedArchive 'https://github.com/Genymobile/scrcpy/releases/download/v4.1/scrcpy-win64-v4.1.zip' $taskScrcpyZip '5b12172b3264b2889f4583ee64752ce832e29bc8b1089dca81093459697165db' 'SHA256'
$taskExtract = Join-Path $taskCache 'scrcpy-extract'
Expand-Archive -LiteralPath $taskScrcpyZip -DestinationPath $taskExtract -Force
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskExtract 'scrcpy-win64-v4.1') | Copy-Item -Destination $taskTools -Recurse -Force
$taskNotices = Join-Path $taskTools 'licenses'
New-Item -ItemType Directory -Path $taskNotices -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'licenses') -File | Copy-Item -Destination $taskNotices -Force
& (Join-Path $taskTools 'scrcpy.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'scrcpy failed to start.' }
Write-Host "Dependencies ready: $taskTools"
