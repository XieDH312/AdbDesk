param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesFile,
    [string]$ReleaseDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskGh = Join-Path $taskRoot '.tools/github-cli/bin/gh.exe'
if (!(Test-Path -LiteralPath $taskGh)) { $taskGh = (Get-Command gh -ErrorAction Stop).Source }
$taskRepository = 'XieDH312/AdbDesk'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
$taskTag = "v$Version"
if (!$ReleaseDirectory) { $ReleaseDirectory = Join-Path $taskRoot "dist/releases/$taskTag" }
$taskManifestPath = Join-Path $ReleaseDirectory 'update-manifest.json'
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
if ($taskManifest.version -ne $Version -or $taskManifest.assetName -ne "AdbDesk-v$Version-win-x64.zip") { throw 'Manifest version mismatch.' }
$taskAssets = @($taskManifest.assetName, 'update-manifest.json', 'update-manifest.sig', 'SHA256SUMS.txt', 'AdbDesk.version.json')
$taskPaths = @($taskAssets | ForEach-Object { (Resolve-Path -LiteralPath (Join-Path $ReleaseDirectory $_)).Path })
$taskHashes = @{}
foreach ($taskPath in $taskPaths) { $taskHashes[[IO.Path]::GetFileName($taskPath)] = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($taskHashes[$taskManifest.assetName] -ne $taskManifest.sha256 -or (Get-Item -LiteralPath $taskPaths[0]).Length -ne $taskManifest.size) { throw 'ZIP integrity check failed.' }
$taskVersionInfo = Get-Content -LiteralPath $taskPaths[4] -Raw | ConvertFrom-Json
$taskVersionEntry = @($taskManifest.files | Where-Object path -CEQ 'AdbDesk.version.json')
if ($taskVersionEntry.Count -ne 1 -or $taskVersionEntry[0].sha256 -ne $taskHashes['AdbDesk.version.json'] -or $taskVersionEntry[0].size -ne (Get-Item -LiteralPath $taskPaths[4]).Length) { throw 'Version metadata differs from signed file list.' }
if (($taskVersionInfo.versionCode -isnot [long] -and $taskVersionInfo.versionCode -isnot [int]) -or $taskVersionInfo.versionCode -le 0 -or $taskVersionInfo.versionName -ne $Version) { throw 'Version metadata is invalid.' }
$taskPublic = [Security.Cryptography.RSA]::Create()
try {
    $taskSource = Get-Content -LiteralPath (Join-Path $taskRoot 'src/AdbDesk.Core/UpdateTrust.cs') -Raw
    $taskPublic.ImportFromPem([regex]::Match($taskSource, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value)
    if (!$taskPublic.VerifyData([IO.File]::ReadAllBytes($taskManifestPath), [Convert]::FromBase64String((Get-Content -LiteralPath $taskPaths[2] -Raw).Trim()), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Invalid release signature.' }
} finally { $taskPublic.Dispose() }
& $taskGh auth status --hostname github.com
if ($LASTEXITCODE -ne 0) { throw 'GitHub login required.' }
$taskPublishedJson = & $taskGh api "repos/$taskRepository/releases?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Unable to check previous release version codes.' }
$taskCheckClient = [Net.Http.HttpClient]::new()
$taskCheckClient.Timeout = [TimeSpan]::FromSeconds(30)
try {
    foreach ($taskPrevious in @($taskPublishedJson | ConvertFrom-Json | Where-Object { !$_.draft })) {
        $taskPreviousAssets = @($taskPrevious.assets | Where-Object name -CEQ 'AdbDesk.version.json')
        if ($taskPreviousAssets.Count -eq 0) { continue }
        if ($taskPreviousAssets.Count -ne 1) { throw 'Duplicate remote version metadata.' }
        $taskExpectedUrl = 'https://github.com/' + $taskRepository + '/releases/download/' + [Uri]::EscapeDataString($taskPrevious.tag_name) + '/AdbDesk.version.json'
        if ($taskPreviousAssets[0].browser_download_url -cne $taskExpectedUrl -or $taskPreviousAssets[0].size -gt 16384) { throw 'Invalid remote version metadata asset.' }
        $taskPreviousBytes = $taskCheckClient.GetByteArrayAsync($taskExpectedUrl).GetAwaiter().GetResult()
        if ($taskPreviousBytes.Length -gt 16384) { throw 'Remote version metadata exceeds limit.' }
        $taskPreviousInfo = [Text.Encoding]::UTF8.GetString($taskPreviousBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
        if (($taskPreviousInfo.versionCode -isnot [long] -and $taskPreviousInfo.versionCode -isnot [int]) -or $taskPreviousInfo.versionCode -le 0) { throw 'Invalid previously published versionCode.' }
        if ($taskVersionInfo.versionCode -le $taskPreviousInfo.versionCode) { throw "VersionCode must exceed published $($taskPreviousInfo.versionCode) ($($taskPrevious.tag_name))." }
    }
} finally { $taskCheckClient.Dispose() }
$taskExisting = & $taskGh release view $taskTag --repo $taskRepository --json isDraft,url 2>$null
if ($LASTEXITCODE -eq 0) {
    if (!(($taskExisting | ConvertFrom-Json).isDraft)) { throw 'Release is already public; refusing to overwrite.' }
    & $taskGh release upload $taskTag @taskPaths --repo $taskRepository --clobber
} else {
    $taskArguments = @('release','create',$taskTag) + $taskPaths + @('--repo',$taskRepository,'--verify-tag','--draft','--title',"AdbDesk $taskTag",'--notes-file',(Resolve-Path -LiteralPath $NotesFile).Path)
    if ($Version.Contains('-')) { $taskArguments += '--prerelease' }
    & $taskGh @taskArguments
}
if ($LASTEXITCODE -ne 0) { throw 'Upload failed. Any draft is left unpublished.' }
$taskJson = & $taskGh api "repos/$taskRepository/releases?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Unable to verify uploaded assets.' }
$taskCandidates = @($taskJson | ConvertFrom-Json | Where-Object tag_name -EQ $taskTag)
if ($taskCandidates.Count -ne 1 -or !$taskCandidates[0].draft) { throw 'Expected one unpublished draft.' }
foreach ($taskAsset in $taskAssets) {
    $taskRemote = @($taskCandidates[0].assets | Where-Object name -EQ $taskAsset)
    $taskLocal = Get-Item -LiteralPath (Join-Path $ReleaseDirectory $taskAsset)
    if ($taskRemote.Count -ne 1 -or $taskRemote[0].digest -ne "sha256:$($taskHashes[$taskAsset])" -or $taskRemote[0].size -ne $taskLocal.Length -or $taskRemote[0].state -ne 'uploaded') { throw "Remote asset mismatch: $taskAsset. Draft left unpublished." }
}
$taskPublishArguments = @('release','edit',$taskTag,'--repo',$taskRepository,'--draft=false')
if ($Version.Contains('-')) { $taskPublishArguments += @('--prerelease','--latest=false') } else { $taskPublishArguments += '--latest=true' }
& $taskGh @taskPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
& $taskGh release view $taskTag --repo $taskRepository --json url,isDraft,isPrerelease,assets
if ($LASTEXITCODE -ne 0) { throw 'Final metadata verification failed.' }
