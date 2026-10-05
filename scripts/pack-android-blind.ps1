<#
.SYNOPSIS
    Builds the Android blind app as a RELEASE APK signed with the project's release key.

.DESCRIPTION
    1. Publishes mobile\BeeMemoryBank.BlindMobile for net10.0-android (Release, APK).
    2. Signs the aligned APK with the keystore given by -KeystorePath using the SDK apksigner and writes
       <OutputDir>\BeeMemoryBank-Blind-<version>-android.apk. The password is read from a FILE (-PasswordFile):
       it never appears on a command line, in the output or in the log. (PKCS12 keystore: the key password is
       the keystore password.)
    3. Verifies the signature with apksigner and prints the certificate SHA-256 (public data). It refuses the
       result when the certificate is not the expected one (-ExpectedSha256, if given) or is an Android debug key.

.NOTES
    - The release key is created ONCE and must never change: Android installs an update only over an APK signed
      with the same key. Keep the keystore and the password file outside every repository.
    - Nothing is deleted or overwritten: an existing output folder is refused.
    - Requires: dotnet CLI with the android workload, Android SDK build-tools (apksigner).

.PARAMETER KeystorePath   The release keystore (.jks / .keystore).
.PARAMETER PasswordFile   A file with the keystore password (one line); the key password must be the same.
.PARAMETER Alias          Key alias in the keystore. Default: bmb-release.
.PARAMETER OutputDir      Where the APK goes. Default: publish\android-blind under the repository.
.PARAMETER ExpectedSha256 Optional: the certificate SHA-256 the APK must carry (colon-separated hex).
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)][string]$KeystorePath,
    [Parameter(Mandatory = $true)][string]$PasswordFile,
    [string]$Alias = "bmb-release",
    [string]$OutputDir,
    [string]$ExpectedSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$Proj     = Join-Path $RepoRoot "mobile\BeeMemoryBank.BlindMobile\BeeMemoryBank.BlindMobile.csproj"
$Version  = (Get-Content (Join-Path $RepoRoot "VERSION") -TotalCount 1).Trim()
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "publish\android-blind" }
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$BuildDir  = Join-Path $OutputDir "build"
$Apk       = Join-Path $OutputDir ("BeeMemoryBank-Blind-{0}-android.apk" -f $Version)

function Say($text) { Write-Host ("pack-android-blind: " + $text) }
function Fail($text) { Write-Host ("pack-android-blind: FAILED - " + $text) -ForegroundColor Red; exit 1 }

if (-not (Test-Path -LiteralPath $KeystorePath)) { Fail "keystore not found: $KeystorePath" }
if (-not (Test-Path -LiteralPath $PasswordFile)) { Fail "password file not found: $PasswordFile" }
$KeystorePath = (Resolve-Path -LiteralPath $KeystorePath).Path
$PasswordFile = (Resolve-Path -LiteralPath $PasswordFile).Path
if (Test-Path -LiteralPath $OutputDir) {
    if (@(Get-ChildItem -LiteralPath $OutputDir -Force).Count -gt 0) { Fail "$OutputDir already exists and is not empty: choose another -OutputDir (this script does not delete)." }
}

$sdkRoots = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "C:\Program Files (x86)\Android\android-sdk", "C:\Program Files\Android\android-sdk",
              (Join-Path $env:LOCALAPPDATA "Android\Sdk")) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$apksigner = $null
foreach ($root in $sdkRoots) {
    $found = Get-ChildItem -LiteralPath (Join-Path $root "build-tools") -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName "apksigner.bat" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($found) { $apksigner = $found; break }
}
if (-not $apksigner) { Fail "apksigner.bat was not found in the Android SDK build-tools" }

New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
Say "publishing the blind app (net10.0-android, Release, APK) -> $BuildDir"
& dotnet publish $Proj -f net10.0-android -c Release -o $BuildDir --nologo -v q -p:AndroidPackageFormat=apk
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish exited with $LASTEXITCODE" }

# The build leaves the aligned APK without our signature (and a copy signed with the Android DEBUG key, which is never used).
# The release signature is made here, by the SDK apksigner, with the release keystore. One password read from the file:
# for a PKCS12 keystore the key password is the keystore password.
$unsigned = @(Get-ChildItem -LiteralPath $BuildDir -Filter "*.apk" -File -Recurse | Where-Object { $_.Name -notmatch '-Signed\.apk$' })
if ($unsigned.Count -ne 1) { Fail ("expected exactly one aligned APK in $BuildDir, found " + $unsigned.Count) }
Say ("signing {0} with the release key (alias {1})" -f $unsigned[0].Name, $Alias)
& $apksigner sign --ks $KeystorePath --ks-pass "file:$PasswordFile" --ks-key-alias $Alias --out $Apk $unsigned[0].FullName
if ($LASTEXITCODE -ne 0) { Fail "apksigner sign exited with $LASTEXITCODE" }
Say "signed APK: $Apk"

Say "verifying the signature"
$out = & $apksigner verify --print-certs $Apk 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Fail ("apksigner verify failed: " + $out) }
$sha = ([regex]::Match($out, 'certificate SHA-256 digest:\s*([0-9a-fA-F]+)')).Groups[1].Value.ToLowerInvariant()
$dn  = ([regex]::Match($out, 'certificate DN:\s*(.+)')).Groups[1].Value.Trim()
if (-not $sha) { Fail "could not read the certificate digest from apksigner" }
Say ("certificate DN: {0}" -f $dn)
Say ("certificate SHA-256: {0}" -f $sha)
if ($dn -match 'Android Debug') { Fail "the APK is signed with an Android DEBUG key: not a release" }
if ($ExpectedSha256) {
    $want = ($ExpectedSha256 -replace '[: ]', '').ToLowerInvariant()
    if ($sha -ne $want) { Fail "the certificate is not the expected one (expected $want)" }
    Say "the certificate matches -ExpectedSha256"
}
$size = [math]::Round((Get-Item -LiteralPath $Apk).Length / 1MB, 1)
Say ("done: {0} ({1} MB)" -f $Apk, $size)
