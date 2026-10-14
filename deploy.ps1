# Checks every .csproj/.xaml under RouteWeatherPlanner\ for a literal "--" inside an
# XML comment before touching dotnet build. Invalid XML (see MSB4025) -- this exact
# thing broke the build here three times during scaffolding from a habitual "--" used
# as a dash in prose comments. Catching it here means a bad comment fails loudly and
# instantly instead of burning a build cycle.
$badLines = @()
$files = Get-ChildItem -Path "RouteWeatherPlanner" -Recurse -File |
    Where-Object { $_.Extension -eq '.csproj' -or $_.Extension -eq '.xaml' }

foreach ($file in $files) {
    $lineNumber = 0
    foreach ($line in Get-Content $file.FullName) {
        $lineNumber++
        $stripped = $line.Replace('<!--', '').Replace('-->', '')
        if ($stripped.Contains('--')) {
            $badLines += "$($file.FullName):$lineNumber"
        }
    }
}

if ($badLines.Count -gt 0) {
    Write-Host "Found '--' inside an XML comment (invalid XML) in:" -ForegroundColor Red
    $badLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

dotnet clean RouteWeatherPlanner\RouteWeatherPlanner.csproj -f net10.0-android -c Debug
dotnet build RouteWeatherPlanner\RouteWeatherPlanner.csproj -f net10.0-android -c Debug

# Without this check, a failed build (e.g. the real NU1107 AndroidX version conflict
# hit when the AppLovin banner ad was first added - see README item 14) left no APK
# behind, but the script kept going anyway: adb uninstall/install/launch ran against a
# missing or stale APK and threw confusing errors (DELETE_FAILED_INTERNAL_ERROR,
# "Activity does not exist") that look like their own separate problem but are really
# just fallout from the build never having produced anything. Stop here instead.
#
# ASCII-only in this file, deliberately: Windows PowerShell 5.1 (the default
# powershell.exe, not pwsh.exe) reads a .ps1 file with no BOM using the system's
# legacy ANSI codepage, not UTF-8. A non-ASCII character like an em dash inside a
# double-quoted string (not just a comment) then decodes as garbage bytes that can
# include something the parser reads as a stray quote - which is exactly what
# produced "The string is missing the terminator" pointing at a totally unrelated
# later line the first time this script had one added. Every other file in this
# project uses em dashes freely in comments because C#/XAML compilers handle UTF-8
# correctly regardless of BOM; PowerShell's default host does not, so this file
# doesn't get them.
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet build failed (exit code $LASTEXITCODE) - stopping before adb install/launch, which would otherwise fail confusingly against a stale or missing APK. Scroll up for the actual build error." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Package is com.fivestringcode.weatherline now - see RouteWeatherPlanner.csproj's
# ApplicationId comment for the full rename history (personal-account listing kept a
# testing requirement even after transfer -> fresh org-account listing under
# com.fivestringcode.weatherroute -> that name came back as already in use ->
# com.fivestringcode.weatherline, matching the app's actual display name).
adb -s ZD222B7KYC uninstall com.fivestringcode.weatherline
adb -s ZD222B7KYC install -r "RouteWeatherPlanner\bin\Debug\net10.0-android\com.fivestringcode.weatherline-Signed.apk"
adb -s ZD222B7KYC shell am start -n "com.fivestringcode.weatherline/.MainActivity" -a android.intent.action.MAIN -c android.intent.category.LAUNCHER
