# Builds the signed Release .aab for Google Play Console.
# Run from this folder:  powershell -ExecutionPolicy Bypass -File .\publish-release.ps1
#
# ASCII-only on purpose (see deploy.ps1: Windows PowerShell 5.1 misreads non-ASCII
# characters in a BOM-less script). The keystore password is asked for at run time so
# it is never stored in this file or in git.

$ErrorActionPreference = "Stop"

$keystore = Join-Path $PSScriptRoot "weatherline.keystore"
$alias    = "weatherline"
$project  = Join-Path $PSScriptRoot "RouteWeatherPlanner\RouteWeatherPlanner.csproj"

if (-not (Test-Path $keystore)) {
    Write-Host "Keystore not found: $keystore" -ForegroundColor Red
    exit 1
}

# Show the version code that will be built so a forgotten bump is caught before upload.
$version = Select-String -Path $project -Pattern "<ApplicationVersion>(\d+)</ApplicationVersion>" |
    ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
Write-Host "Building version code $version (must be higher than every code already uploaded)." -ForegroundColor Cyan

$secure = Read-Host "Keystore password" -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
$password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)

# Fail fast on a mistyped password: jarsigner's own error is hidden inside the build
# output as a bare "jarsigner.exe exited with code 1", and by then the build has
# already run for a couple of minutes.
$keytoolCmd = Get-Command keytool -ErrorAction SilentlyContinue
if ($null -ne $keytoolCmd) {
    & keytool -list -keystore $keystore -storepass $password -alias $alias *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Keystore password or alias is wrong (keytool could not open $keystore). Re-run and retype it." -ForegroundColor Red
        exit 1
    }
    Write-Host "Keystore password OK." -ForegroundColor Green
}

# Delete old bundles first so a stale (debug-signed) file can never be mistaken for the new one.
$publishDir = Join-Path $PSScriptRoot "RouteWeatherPlanner\bin\Release\net10.0-android\publish"
if (Test-Path $publishDir) { Get-ChildItem $publishDir -Filter "*.aab" | Remove-Item -Force }

# Arguments are quoted so PowerShell passes each -p: property through intact.
dotnet publish "$project" -f net10.0-android -c Release `
    "-p:AndroidPackageFormat=aab" `
    "-p:AndroidKeyStore=true" `
    "-p:AndroidSigningKeyStore=$keystore" `
    "-p:AndroidSigningKeyAlias=$alias" `
    "-p:AndroidSigningKeyPass=$password" `
    "-p:AndroidSigningStorePass=$password"

if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed (exit code $LASTEXITCODE). Scroll up for the actual error." -ForegroundColor Red
    exit $LASTEXITCODE
}

$aab = Get-ChildItem -Path (Join-Path $PSScriptRoot "RouteWeatherPlanner\bin\Release\net10.0-android\publish") -Filter "*-Signed.aab" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($null -eq $aab) {
    Write-Host "Build reported success but no -Signed.aab was found." -ForegroundColor Red
    exit 1
}

# Play Console expects the upload key with this SHA1 (weatherline.keystore). A bundle
# signed with the Android Debug key is rejected as "signed with the wrong key".
$expectedSha1 = "46:12:50:25:AE:B6:3C:65:B3:C2:AE:17:EF:26:E4:C1:49:3C:0D:E3"
$keytool = Get-Command keytool -ErrorAction SilentlyContinue
if ($null -ne $keytool) {
    $cert = & keytool -printcert -jarfile $aab.FullName
    $sha1Line = $cert | Select-String "SHA1:" | Select-Object -First 1
    if ($null -eq $sha1Line -or $sha1Line.ToString() -notmatch [regex]::Escape($expectedSha1)) {
        Write-Host "WRONG SIGNING KEY. Expected SHA1 $expectedSha1" -ForegroundColor Red
        Write-Host "Got: $sha1Line" -ForegroundColor Red
        Write-Host "Do not upload this file." -ForegroundColor Red
        exit 1
    }
    Write-Host "Signature check passed (upload key SHA1 matches Play Console)." -ForegroundColor Green
}
else {
    Write-Host "keytool not found on PATH; skipped the signature check." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Signed bundle ready (version code $version):" -ForegroundColor Green
Write-Host $aab.FullName
Write-Host "Last written: $($aab.LastWriteTime)"
