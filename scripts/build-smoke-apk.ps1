param([string]$AndroidSdk = "$env:LOCALAPPDATA/Android/Sdk")
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOut = Join-Path $taskRoot 'artifacts/smoke-apk'
$taskTools = Join-Path $AndroidSdk 'build-tools/35.0.0'
$taskAndroidJar = Join-Path $AndroidSdk 'platforms/android-35/android.jar'
New-Item -ItemType Directory -Path "$taskOut/classes","$taskOut/dex" -Force | Out-Null
function Assert-Command { if ($LASTEXITCODE -ne 0) { throw "Android build failed (exit $LASTEXITCODE)." } }
& "$taskTools/aapt2.exe" link -o "$taskOut/unsigned.apk" --manifest "$taskRoot/tests/smoke-apk/AndroidManifest.xml" -I $taskAndroidJar
Assert-Command
& javac -encoding UTF-8 --release 8 -classpath $taskAndroidJar -d "$taskOut/classes" "$taskRoot/tests/smoke-apk/MainActivity.java"
Assert-Command
& jar cf "$taskOut/classes.jar" -C "$taskOut/classes" .
Assert-Command
& "$taskTools/d8.bat" --lib $taskAndroidJar --min-api 23 --output "$taskOut/dex" "$taskOut/classes.jar"
Assert-Command
& jar uf "$taskOut/unsigned.apk" -C "$taskOut/dex" classes.dex
Assert-Command
& "$taskTools/zipalign.exe" -f 4 "$taskOut/unsigned.apk" "$taskOut/aligned.apk"
Assert-Command
$taskKey = Join-Path $taskRoot '.tools/smoke-test.keystore'
if (-not (Test-Path -LiteralPath $taskKey)) {
    & keytool -genkeypair -keystore $taskKey -storepass android -keypass android -alias adbdesk-test -dname 'CN=AdbDesk Test' -keyalg RSA -validity 3650
    Assert-Command
}
& "$taskTools/apksigner.bat" sign --ks $taskKey --ks-pass pass:android --key-pass pass:android --out "$taskOut/AdbDesk-smoke.apk" "$taskOut/aligned.apk"
Assert-Command
& "$taskTools/apksigner.bat" verify "$taskOut/AdbDesk-smoke.apk"
Assert-Command
Write-Host "Test APK (no permissions): $taskOut/AdbDesk-smoke.apk"
