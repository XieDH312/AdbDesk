# Run with PowerShell 7. The private signing key never enters the package.
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
$taskPublish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRoot "dist/releases/v$Version" }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if ($taskOutput.StartsWith($taskPublish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $taskOutput -eq $taskPublish) { throw 'Output must be outside the published application.' }
if (Test-Path -LiteralPath $taskOutput) { throw 'Output directory already exists; never overwrite release assets.' }
$taskRequired = @('AdbDesk.exe', 'AdbDesk.dll', 'AdbDesk.Core.dll', 'AdbDesk.runtimeconfig.json', 'AdbDesk.deps.json', 'tools/scrcpy/adb.exe', 'tools/scrcpy/scrcpy.exe')
foreach ($taskFile in $taskRequired) { if (!(Test-Path -LiteralPath (Join-Path $taskPublish $taskFile) -PathType Leaf)) { throw "Missing $taskFile" } }
$taskBinaryVersion = (Get-Item -LiteralPath (Join-Path $taskPublish 'AdbDesk.dll')).VersionInfo.ProductVersion.Split('+')[0]
if ($taskBinaryVersion -ne $Version) { throw "Binary version $taskBinaryVersion does not match $Version" }
$taskVersionPath = Join-Path $taskPublish 'AdbDesk.version.json'
$taskVersionInfo = Get-Content -LiteralPath $taskVersionPath -Raw | ConvertFrom-Json
$taskCode = $taskVersionInfo.versionCode
if (($taskCode -isnot [long] -and $taskCode -isnot [int]) -or $taskCode -le 0 -or $taskVersionInfo.versionName -ne $Version) { throw 'Invalid numeric version metadata in published application.' }
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRoot 'src/AdbDesk.App/AdbDesk.App.csproj') -Raw
if ([long]$taskProject.Project.PropertyGroup.VersionCode -ne $taskCode) { throw 'Published versionCode differs from project configuration.' }
$taskKeyPath = Join-Path $env:LOCALAPPDATA 'AdbDesk/ReleaseKeys/update-private-key.dpapi'
if (!(Test-Path -LiteralPath $taskKeyPath)) { throw 'Release signing key is missing. Do not generate a replacement key for existing installations.' }
$taskKeyBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($taskKeyPath), [Text.Encoding]::UTF8.GetBytes('AdbDesk update signing key v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
$taskRsa = [Security.Cryptography.RSA]::Create()
$taskPublicRsa = [Security.Cryptography.RSA]::Create()
try {
    $taskRead = 0
    $taskRsa.ImportPkcs8PrivateKey($taskKeyBytes, [ref]$taskRead)
    [Array]::Clear($taskKeyBytes, 0, $taskKeyBytes.Length)
    $taskPublicSource = Get-Content -LiteralPath (Join-Path $taskRoot 'src/AdbDesk.Core/UpdateTrust.cs') -Raw
    $taskPublicPem = [regex]::Match($taskPublicSource, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value
    $taskPublicRsa.ImportFromPem($taskPublicPem)
    if ($taskPublicRsa.ExportSubjectPublicKeyInfoPem() -ne $taskRsa.ExportSubjectPublicKeyInfoPem()) { throw 'Signing key does not match pinned application public key.' }
    $taskMarker = @{schemaVersion=1; applicationId='AdbDesk'; version=$Version} | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $taskPublish 'AdbDesk.installation.json'), $taskMarker, [Text.UTF8Encoding]::new($false))
    $taskVersionJson = [ordered]@{versionCode=[long]$taskCode; versionName=$Version} | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($taskVersionPath, $taskVersionJson, [Text.UTF8Encoding]::new($false))
    $taskFiles = @(Get-ChildItem -LiteralPath $taskPublish -File -Recurse | Sort-Object FullName | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not allowed in releases.' }
        $taskRelative = [IO.Path]::GetRelativePath($taskPublish, $_.FullName).Replace('\', '/')
        if ($taskRelative -match '(?i)(\.dpapi$|\.pfx$|\.p12$|private.key|adbkey|update-manifest\.)') { throw "Private or unexpected file: $taskRelative" }
        [ordered]@{path=$taskRelative; size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    foreach ($taskDir in Get-ChildItem -LiteralPath $taskPublish -Directory -Recurse) { if ($taskDir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse directories are not allowed.' } }
    New-Item -ItemType Directory -Path $taskOutput | Out-Null
    $taskAsset = "AdbDesk-v$Version-win-x64.zip"
    $taskArchive = Join-Path $taskOutput $taskAsset
    [IO.Compression.ZipFile]::CreateFromDirectory($taskPublish, $taskArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskManifest = [ordered]@{schemaVersion=1; applicationId='AdbDesk'; version=$Version; assetName=$taskAsset; size=(Get-Item -LiteralPath $taskArchive).Length; sha256=$taskHash; files=$taskFiles}
    $taskManifestBytes = [Text.Encoding]::UTF8.GetBytes(($taskManifest | ConvertTo-Json -Depth 8 -Compress))
    $taskSignature = $taskRsa.SignData($taskManifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    if (!$taskPublicRsa.VerifyData($taskManifestBytes, $taskSignature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Signature self-verification failed.' }
    [IO.File]::WriteAllBytes((Join-Path $taskOutput 'update-manifest.json'), $taskManifestBytes)
    [IO.File]::WriteAllText((Join-Path $taskOutput 'update-manifest.sig'), [Convert]::ToBase64String($taskSignature), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $taskOutput 'SHA256SUMS.txt'), "$taskHash  $taskAsset`n", [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $taskVersionPath -Destination (Join-Path $taskOutput 'AdbDesk.version.json')
    Write-Host "Signed release: $taskOutput"
    Write-Host "SHA256: $taskHash"
    Write-Host "VersionCode: $taskCode"
} finally {
    if ($taskKeyBytes) { [Array]::Clear($taskKeyBytes, 0, $taskKeyBytes.Length) }
    $taskRsa.Dispose(); $taskPublicRsa.Dispose()
}
