<#
.SYNOPSIS
    Packages the Windows blind app (tray app) as a self-contained folder and a zip.

.DESCRIPTION
    1. Publishes desktop\BeeMemoryBank.BlindDesktop for win-x64, self-contained (no .NET install needed on the
       target machine) into <OutputDir>\app.
    2. Checks the bundle: the executable is there, and it carries no Vault code (the blind app must never contain
       the master-key code) and no data folder.
    3. Runs the app's own headless check from the published folder (--self-check with a NEW scratch data folder;
       it refuses the real data folder) and requires exit code 0.
    4. Writes BeeMemoryBank-Blind-<version>-win-x64.zip next to the app folder.

.NOTES
    - Nothing is deleted or overwritten: an existing output folder is refused. Choose another -OutputDir.
    - Code signing is intentionally OMITTED here (the Windows code-signing application is still open); the zip is
      unsigned and Windows SmartScreen will say so on the first start.
    - Requires: dotnet CLI (SDK 10).

.PARAMETER OutputDir
    Where the app folder and the zip go. Default: publish\windows-blind\win-x64 under the repository.
#>
[CmdletBinding()]
param (
    [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$Proj     = Join-Path $RepoRoot "desktop\BeeMemoryBank.BlindDesktop\BeeMemoryBank.BlindDesktop.csproj"
$Version  = (Get-Content (Join-Path $RepoRoot "VERSION") -TotalCount 1).Trim()
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "publish\windows-blind\win-x64" }
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$AppDir    = Join-Path $OutputDir "app"
$ZipPath   = Join-Path $OutputDir ("BeeMemoryBank-Blind-{0}-win-x64.zip" -f $Version)
$CheckDir  = Join-Path $OutputDir "selfcheck-data"

function Say($text) { Write-Host ("pack-windows-blind: " + $text) }
function Fail($text) { Write-Host ("pack-windows-blind: FAILED - " + $text) -ForegroundColor Red; exit 1 }

if (Test-Path -LiteralPath $OutputDir) {
    if (@(Get-ChildItem -LiteralPath $OutputDir -Force).Count -gt 0) { Fail "$OutputDir already exists and is not empty: choose another -OutputDir (this script does not delete)." }
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Say "publishing the blind app (win-x64, self-contained) -> $AppDir"
& dotnet publish $Proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $AppDir --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish exited with $LASTEXITCODE" }

$Exe = Join-Path $AppDir "BeeMemoryBank.BlindDesktop.exe"
if (-not (Test-Path -LiteralPath $Exe)) { Fail "the executable is missing: $Exe" }

Say "checking the bundle"
$vault = @(Get-ChildItem -LiteralPath $AppDir -Recurse -File -Filter "BeeMemoryBank.Vault*.dll")
if ($vault.Count -gt 0) { Fail ("the blind app must not contain Vault code: " + ($vault.Name -join ", ")) }
$infra = @(Get-ChildItem -LiteralPath $AppDir -Recurse -File -Filter "BeeMemoryBank.Infrastructure*.dll")
if ($infra.Count -gt 0) { Fail ("the blind app must not contain the server infrastructure: " + ($infra.Name -join ", ")) }
$data = @(Get-ChildItem -LiteralPath $AppDir -Recurse -Force | Where-Object { $_.Name -match '^(BeeMemoryBankData|beememorybank\.db.*|blind-state\.json|blind-log\.jsonl)$' })
if ($data.Count -gt 0) { Fail ("user data inside the bundle: " + ($data.Name -join ", ")) }

Say "running the app's own headless check from the published folder (scratch data folder $CheckDir)"
& $Exe --self-check --data-dir $CheckDir
if ($LASTEXITCODE -ne 0) { Fail "--self-check exited with $LASTEXITCODE" }

Say "writing $ZipPath"
Compress-Archive -Path (Join-Path $AppDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $ZipPath)) { Fail "the zip was not written" }

$size = [math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 1)
Say ("done: {0} ({1} MB), unsigned" -f $ZipPath, $size)
Say "app folder: $AppDir"
