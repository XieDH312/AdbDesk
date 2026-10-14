$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = '12.2.3'
$taskHash = '04a01ea95ae96df75058a893e774957d294e69012dab1f4e256ce2b0c6725483'
$taskArchive = Join-Path $taskRoot ".tools/mitmproxy-$taskVersion-windows-x86_64.zip"
$taskDestination = Join-Path $taskRoot 'tools/mitmproxy'
New-Item -ItemType Directory -Path (Split-Path -Parent $taskArchive), $taskDestination -Force | Out-Null
if (!(Test-Path -LiteralPath $taskArchive)) {
    Invoke-WebRequest "https://downloads.mitmproxy.org/$taskVersion/mitmproxy-$taskVersion-windows-x86_64.zip" -OutFile $taskArchive
}
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskHash) {
    throw 'Archive SHA256 does not match the pinned official download. No executable was extracted.'
}
$taskZip = [System.IO.Compression.ZipFile]::OpenRead($taskArchive)
try {
    $taskEntry = $taskZip.GetEntry('mitmdump.exe')
    if ($null -eq $taskEntry) { throw 'mitmdump.exe missing from archive.' }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, (Join-Path $taskDestination 'mitmdump.exe'), $true)
} finally { $taskZip.Dispose() }
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses/mitmproxy-LICENSE.txt') -Destination $taskDestination
Write-Output "Prepared mitmdump $taskVersion at $taskDestination (download checksum verified, engine not executed)."
