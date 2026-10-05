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
       result when the certificate is not the expected release one (the default of -ExpectedSha256) or is an
       Android debug key.

.NOTES
    - The release key is created ONCE and must never change: Android installs an update only over an APK signed
      with the same key. Keep the keystore and the password file outside every repository.
    - Nothing is deleted or overwritten: an existing output folder is refused.
    - Requires: dotnet CLI with the android workload, Android SDK build-tools (apksigner).

.PARAMETER KeystorePath   The release keystore (.jks / .keystore).
.PARAMETER PasswordFile   A file with the keystore password (one line); the key password must be the same.
.PARAMETER Alias          Key alias in the keystore. Default: bmb-release.
.PARAMETER OutputDir      Where the APK goes. Default: publish\android-blind under the repository.
.PARAMETER ExpectedSha256 The certificate SHA-256 the APK must carry (colon-separated hex). Defaults to the
                          project's release certificate (a public fingerprint); another value is allowed only
                          explicitly, when deliberately releasing under a NEW key.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)][string]$KeystorePath,
    [Parameter(Mandatory = $true)][string]$PasswordFile,
    [string]$Alias = "bmb-release",
    [string]$OutputDir,
    # The project's release certificate SHA-256 (a public fingerprint, not a secret). It is the DEFAULT, not an
    # option: omitting the parameter cannot skip the check. Pass another value only with a NEW release key.
    [string]$ExpectedSha256 = "72:69:15:97:7B:99:9F:54:AB:51:3E:DC:14:51:01:21:6C:D1:59:6A:86:D6:98:36:A4:53:BA:FA:A9:4C:36:3F"
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
$want = ($ExpectedSha256 -replace '[: ]', '').ToLowerInvariant()
if (-not $want) { Fail "no expected certificate SHA-256 to compare with: pass it in -ExpectedSha256" }
if ($sha -ne $want) {
    Fail ("the APK certificate is not the expected one (expected $want, got $sha). " +
          "If the release key was deliberately changed, pass the NEW certificate's SHA-256 in -ExpectedSha256; " +
          "Android installs an update only over an APK signed with the same key.")
}
Say "the certificate matches the expected SHA-256"
$size = [math]::Round((Get-Item -LiteralPath $Apk).Length / 1MB, 1)
Say ("done: {0} ({1} MB)" -f $Apk, $size)
