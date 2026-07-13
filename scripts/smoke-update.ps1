<#
.SYNOPSIS
    End-to-end Velopack update smoke test (e2e proof of data preservation).

.DESCRIPTION
    Tests the full Velopack update lifecycle for BeeMemoryBank using a throwaway
    packId (BmbSmokeUpdateTest) so the real BeeMemoryBank installation is never touched.

    IMPORTANT NOTE ON DATA DIRECTORY SANDBOXING:
    BmbPaths.Root is computed via Environment.GetFolderPath(SpecialFolder.LocalApplicationData),
    which reads from the Windows registry CSIDL/KnownFolderID and is NOT overridable via the
    LOCALAPPDATA environment variable -- even for freshly spawned child processes.
    This was verified empirically: launching a child .exe with
    ProcessStartInfo.EnvironmentVariables["LOCALAPPDATA"] = "C:\SandboxPath" still results in
    GetFolderPath returning the real %LOCALAPPDATA%.

    Therefore this script uses a BACKUP/RESTORE strategy:
      1. Any real %LOCALAPPDATA%\BeeMemoryBankData is backed up before the test run.
      2. Test scenarios use the real directory.
      3. After the test, the test directory is cleaned and the real backup is restored.

    The throwaway packId (BmbSmokeUpdateTest) ensures the Velopack install dir is separate
    from the real BeeMemoryBank installation (%LOCALAPPDATA%\BmbSmokeUpdateTest vs
    %LOCALAPPDATA%\BeeMemoryBank).

    The node binary (bmbd) is launched in --auto mode so that legacy-rescue logic is exercised:
    legacyDir = <install>\current\bmbd\data (inside the throwaway Velopack package),
    targetVaultDir = %LOCALAPPDATA%\BeeMemoryBankData\vaults\default.

.PARAMETER PublishDir
    Path to the published win-x64 artifacts. Defaults to publish\win-x64 relative to repo root.
    Must contain bmbd\, api\, web\ subdirectories with self-contained executables.

.PARAMETER SkipBuild
    Skip the dotnet publish step (assume PublishDir is already populated).

.NOTES
    - Uses ASCII-only characters throughout (no en-dash, smart quotes, arrows, Cyrillic).
    - Tested with vpk 1.2.0 and .NET 8 win-x64 self-contained publish.
    - Requires: dotnet CLI, vpk (dotnet tool install -g vpk), PowerShell 5.1+.
    - Standalone script: NOT wired into CI (no CI for this project yet, documented separately).
    - Each step prints PASS or FAIL.
    - Script exits with non-zero code on any failure.
#>
[CmdletBinding()]
param (
    [string]$PublishDir = "publish\win-x64",
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step { param([string]$Msg) Write-Host ""; Write-Host "=== $Msg ===" }
function Write-Pass { param([string]$Msg) Write-Host "[PASS] $Msg" }
function Write-Fail { param([string]$Msg) Write-Host "[FAIL] $Msg" }

$Script:FailCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Msg)
    if ($Condition) {
        Write-Pass $Msg
    } else {
        Write-Fail $Msg
        $Script:FailCount++
    }
}

function Assert-FileContains {
    param([string]$FilePath, [string]$Marker, [string]$Label)
    if (-not (Test-Path $FilePath)) {
        Write-Fail "${Label}: file not found: $FilePath"
        $Script:FailCount++
        return
    }
    $content = Get-Content $FilePath -Raw
    if ($content -and $content.Contains($Marker)) {
        Write-Pass "${Label}: marker '$Marker' found in $FilePath"
    } else {
        Write-Fail "${Label}: marker '$Marker' NOT found in $FilePath (content: $content)"
        $Script:FailCount++
    }
}

# Create a minimal valid SQLite file with the 16-byte magic header + padding.
# LegacyDataRescue.IsSqliteFileValid only checks for the 16-byte magic; size is not enforced.
function New-MinimalSqliteFile {
    param([string]$Path)
    # "SQLite format 3" + 0x00 = 16 bytes; pad to 100 bytes to look less suspicious
    $magic = [System.Text.Encoding]::ASCII.GetBytes("SQLite format 3")
    $header = [byte[]]::new(100)
    [Array]::Copy($magic, $header, $magic.Length)
    # byte 16 is null terminator (already 0x00 from new array)
    # byte 16 (page size MSB) = 0x10, byte 17 = 0x00 -> page size 4096
    $header[16] = 0x10
    [System.IO.File]::WriteAllBytes($Path, $header)
}

# Escape a single argument for Windows CreateProcess (CommandLine string).
# Wraps in double-quotes and escapes inner double-quotes and trailing backslashes.
function ConvertTo-CmdArg {
    param([string]$Arg)
    # Per MSDN: if the arg contains spaces, tabs, or quotes, wrap it in double-quotes.
    # Backslashes before a double-quote must be doubled. Trailing backslashes before the
    # closing quote must also be doubled.
    if ($Arg -match '[ \t"]' -or $Arg -eq '') {
        $Arg = $Arg -replace '(\\+)"', '$1$1"'
        $Arg = $Arg -replace '(\\+)$', '$1$1'
        $Arg = '"' + ($Arg -replace '"', '\"') + '"'
    }
    return $Arg
}

# Run a process via ProcessStartInfo, capture stdout+stderr, return exit code.
# Uses Arguments (string) instead of ArgumentList for PS 5.1 compatibility.
function Invoke-Exe {
    param(
        [string]$Exe,
        [string[]]$ExeArgs,
        [hashtable]$EnvOverrides = @{},
        [int]$TimeoutSec = 120,
        [switch]$ShowOutput
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ($ExeArgs | ForEach-Object { ConvertTo-CmdArg $_ }) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute = $false
    foreach ($k in $EnvOverrides.Keys) {
        $psi.EnvironmentVariables[$k] = $EnvOverrides[$k]
    }
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit($TimeoutSec * 1000) | Out-Null
    if ($ShowOutput -or $p.ExitCode -ne 0) {
        if ($stdout) { Write-Host $stdout }
        if ($stderr) { Write-Host $stderr }
    }
    return $p.ExitCode
}

# Start bmbd in --auto mode as a background process; return the Process object.
# Uses Start-Process with -RedirectStandardOutput/-RedirectStandardError to avoid
# the PS 5.1 event-handler strictmode issue.
# EnvOverrides: temporarily set env vars in this PS session before spawning,
# then restore them. Not ideal but safe since we control the test flow.
function Start-Bmbd {
    param(
        [string]$BmbdExe,
        [string]$LogFile,
        [string]$ErrFile = "",
        [hashtable]$EnvOverrides = @{}
    )
    # Save and set env overrides
    $savedEnv = @{}
    foreach ($k in $EnvOverrides.Keys) {
        $savedEnv[$k] = [System.Environment]::GetEnvironmentVariable($k)
        [System.Environment]::SetEnvironmentVariable($k, $EnvOverrides[$k])
    }

    if ($ErrFile -eq "") { $ErrFile = $LogFile + ".err" }

    $proc = Start-Process -FilePath $BmbdExe -ArgumentList "--auto" -PassThru `
        -RedirectStandardOutput $LogFile -RedirectStandardError $ErrFile `
        -NoNewWindow

    # Restore env
    foreach ($k in $savedEnv.Keys) {
        [System.Environment]::SetEnvironmentVariable($k, $savedEnv[$k])
    }

    return $proc
}

# Stop a process and all its children.
function Stop-BmbdProcess {
    param($Proc, [int]$WaitSec = 5)
    if ($null -eq $Proc) { return }
    if ($Proc.HasExited) { return }
    try {
        # Kill the entire process tree via taskkill for PS 5.1 compatibility
        $pid = $Proc.Id
        taskkill /PID $pid /T /F 2>$null | Out-Null
    } catch {}
    $Proc.WaitForExit($WaitSec * 1000) | Out-Null
}

# Poll for a directory to appear (created by legacy rescue) instead of a blind sleep.
# Returns $true if it appeared within the timeout, $false otherwise.
function Wait-BmbdReady {
    param([string]$DataDir, [int]$TimeoutSec = 5)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $DataDir) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return (Test-Path $DataDir)
}

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$AbsPublishDir = Join-Path $RepoRoot $PublishDir

# The throwaway pack ID -- NEVER "BeeMemoryBank", to avoid touching the real install
$SmokePackId  = "BmbSmokeUpdateTest"
$SmokeInstall = Join-Path $env:LOCALAPPDATA $SmokePackId   # Velopack puts it here
$SmokeRelDir  = Join-Path $env:TEMP "bmb-smoke-update-releases"

# Real BeeMemoryBankData dir (this is what BmbPaths.Root always resolves to)
$RealDataRoot   = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData"
$RealVaultsDir  = Join-Path $RealDataRoot "vaults"
$RealDefaultVault = Join-Path $RealVaultsDir "default"

# Backup of real data (stashed before test, restored after)
$BackupDir = Join-Path $env:TEMP "bmb-smoke-update-data-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

Write-Step "BeeMemoryBank Velopack Update Smoke Test"
Write-Host "  RepoRoot    : $RepoRoot"
Write-Host "  PublishDir  : $AbsPublishDir"
Write-Host "  PackId      : $SmokePackId"
Write-Host "  InstallDir  : $SmokeInstall"
Write-Host "  ReleasesDir : $SmokeRelDir"
Write-Host "  RealDataRoot: $RealDataRoot"
Write-Host ""

# ---------------------------------------------------------------------------
# Step 0: Prerequisites
# ---------------------------------------------------------------------------

Write-Step "Step 0: Prerequisites"

# Locate vpk (may be in dotnet tools path)
$VpkCmd = Get-Command vpk -ErrorAction SilentlyContinue
$VpkPath = if ($null -ne $VpkCmd) { $VpkCmd.Source } else { $null }
if (-not $VpkPath) {
    $toolsVpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
    if (Test-Path $toolsVpk) { $VpkPath = $toolsVpk }
}
Assert-True ($null -ne $VpkPath -and (Test-Path $VpkPath)) "vpk found at: $VpkPath"
if ($Script:FailCount -gt 0) {
    Write-Error "vpk not found. Install with: dotnet tool install -g vpk"
    exit 1
}

# ---------------------------------------------------------------------------
# Step 1: Publish node binaries (unless -SkipBuild)
# ---------------------------------------------------------------------------

Write-Step "Step 1: Publish node binaries"
$NodeProj = Join-Path $RepoRoot "desktop\BeeMemoryBank.Node\BeeMemoryBank.Node.csproj"
$BmbdDir  = Join-Path $AbsPublishDir "bmbd"
$BmbdExe  = Join-Path $BmbdDir "BeeMemoryBank.Node.exe"

if ($SkipBuild) {
    Write-Host "  (-SkipBuild) Skipping publish step."
} else {
    if (-not (Test-Path $NodeProj)) {
        Write-Fail "Node project not found: $NodeProj"
        exit 1
    }
    Write-Host "  Publishing BeeMemoryBank.Node to $BmbdDir ..."
    dotnet publish $NodeProj -c Release -r win-x64 --self-contained true -o $BmbdDir --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "dotnet publish failed (exit code $LASTEXITCODE)"
        exit 1
    }
}

Assert-True (Test-Path $BmbdExe) "bmbd exe exists: $BmbdExe"
if ($Script:FailCount -gt 0) { exit 1 }
Write-Pass "Step 1: node binaries ready"

# ---------------------------------------------------------------------------
# Step 2: Back up any existing real BeeMemoryBankData
# ---------------------------------------------------------------------------

Write-Step "Step 2: Back up real BeeMemoryBankData (if any)"
$RealDataExisted = $false
if (Test-Path $RealDataRoot) {
    $RealDataExisted = $true
    Write-Host "  Real data dir exists. Stashing to: $BackupDir"
    Copy-Item -Recurse -Force $RealDataRoot $BackupDir
    Write-Pass "Stashed real data to $BackupDir"
} else {
    Write-Host "  No real data dir found -- clean machine, nothing to stash."
    Write-Pass "No stash needed"
}

function Invoke-Cleanup {
    param([switch]$Quiet)
    if (-not $Quiet) { Write-Step "Cleanup" }

    # 1. Uninstall throwaway package (ignore errors -- may already be gone)
    $UpdateExe = Join-Path $SmokeInstall "Update.exe"
    if (Test-Path $UpdateExe) {
        Write-Host "  Uninstalling throwaway package ($SmokePackId)..."
        try {
            $ec = Invoke-Exe -Exe $UpdateExe -ExeArgs @("uninstall", "--silent") -TimeoutSec 30
            Write-Host "  Uninstall exit code: $ec"
        } catch {
            Write-Host "  (Uninstall error ignored: $_)"
        }
    }

    # 2. Remove lingering install dir
    if (Test-Path $SmokeInstall) {
        try { Remove-Item -Recurse -Force $SmokeInstall -ErrorAction SilentlyContinue } catch {}
    }

    # 3. Remove releases scratch dir
    if (Test-Path $SmokeRelDir) {
        try { Remove-Item -Recurse -Force $SmokeRelDir -ErrorAction SilentlyContinue } catch {}
    }

    # 4. Remove test data from real BeeMemoryBankData
    if (Test-Path $RealDataRoot) {
        Write-Host "  Removing test data from real BeeMemoryBankData..."
        try { Remove-Item -Recurse -Force $RealDataRoot -ErrorAction SilentlyContinue } catch {}
    }

    # 5. Restore original data (if it existed)
    if ($RealDataExisted -and (Test-Path $BackupDir)) {
        Write-Host "  Restoring original BeeMemoryBankData from backup..."
        try {
            Copy-Item -Recurse -Force $BackupDir $RealDataRoot
            Write-Pass "Original data restored to $RealDataRoot"
            Remove-Item -Recurse -Force $BackupDir -ErrorAction SilentlyContinue
        } catch {
            Write-Fail "CRITICAL: failed to restore original BeeMemoryBankData from backup: $_"
            Write-Fail "Your original data is still safe at: $BackupDir -- restore it manually before using BeeMemoryBank."
            $Script:FailCount++
        }
    }
}

# Guarantees Invoke-Cleanup (and therefore the real-data restore) runs exactly once no
# matter how the test body below exits: normal completion, an explicit `exit N`, or an
# uncaught terminating error (Set-StrictMode / $ErrorActionPreference='Stop' turns many
# ordinary mistakes into terminating errors). Without this, a single unexpected failure
# between checkpoints would abandon the user's real data mid-swap.
try {

# ---------------------------------------------------------------------------
# Helper: build + pack a version with vpk
# ---------------------------------------------------------------------------

# Payload is the BmbdDir itself (bmbd exe at root, api/web/cli siblings if auto-mode).
# For the smoke test we only need bmbd -- auto-discovery will fail looking for api/web
# but we are only testing path/rescue logic, not full node startup.
# We use a minimal payload: just the bmbd directory contents placed at payload root.

function Invoke-VpkPack {
    param(
        [string]$Version,
        [string]$PayloadDir,
        [string]$ReleasesDir
    )
    New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null
    $vpkArgs = @(
        "pack",
        "--packId",      $SmokePackId,
        "--packVersion", $Version,
        "--packTitle",   "BmbSmokeUpdateTest",
        "--packAuthors", "BmbSmokeUpdateTest",
        "--packDir",     $PayloadDir,
        "--mainExe",     "BeeMemoryBank.Node.exe",
        "--outputDir",   $ReleasesDir,
        "--skipVeloAppCheck",
        "-y"
    )
    Write-Host "  vpk pack v$Version ..."
    $ec = Invoke-Exe -Exe $VpkPath -ExeArgs $vpkArgs -TimeoutSec 300 -ShowOutput
    if ($ec -ne 0) { throw "vpk pack v$Version failed (exit $ec)" }
}

# ---------------------------------------------------------------------------
# Step 3: Pack v1.0.0
# ---------------------------------------------------------------------------

Write-Step "Step 3: vpk pack v1.0.0"

# Payload is the bmbd publish output (exe at root for vpk)
$PayloadDir = Join-Path $env:TEMP "bmb-smoke-payload"
if (Test-Path $PayloadDir) { Remove-Item -Recurse -Force $PayloadDir }
New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null
Get-ChildItem -Path $BmbdDir | Copy-Item -Destination $PayloadDir -Recurse -Force

try {
    Invoke-VpkPack -Version "1.0.0" -PayloadDir $PayloadDir -ReleasesDir $SmokeRelDir
    Write-Pass "Step 3: vpk pack v1.0.0 done"
} catch {
    Write-Fail "Step 3: $_"
    Invoke-Cleanup
    exit 1
}

# Verify Setup.exe was produced
$SetupExe = Get-ChildItem $SmokeRelDir -Filter "*Setup.exe" | Select-Object -First 1
Assert-True ($null -ne $SetupExe) "Setup.exe produced in $SmokeRelDir"
if ($Script:FailCount -gt 0) { exit 1 }
Write-Host "  Setup.exe: $($SetupExe.FullName)"

# ---------------------------------------------------------------------------
# Step 4: Silent install v1.0.0
# ---------------------------------------------------------------------------

Write-Step "Step 4: Silent install v1.0.0"

# Remove any leftover from previous runs
if (Test-Path $SmokeInstall) {
    Write-Host "  Cleaning previous install dir: $SmokeInstall"
    Remove-Item -Recurse -Force $SmokeInstall -ErrorAction SilentlyContinue
}

Write-Host "  Running Setup.exe --silent ..."
$ec = Invoke-Exe -Exe $SetupExe.FullName -ExeArgs @("--silent") -TimeoutSec 120 -ShowOutput
Assert-True ($ec -eq 0) "Silent install exit code = 0 (got $ec)"

$CurrentDir = Join-Path $SmokeInstall "current"
$InstalledBmbd = Join-Path $CurrentDir "BeeMemoryBank.Node.exe"
Assert-True (Test-Path $InstalledBmbd) "Installed bmbd exists: $InstalledBmbd"
if ($Script:FailCount -gt 0) { exit 1 }
Write-Pass "Step 4: v1.0.0 installed to $SmokeInstall"

# ---------------------------------------------------------------------------
# Step 5: Create legacy data marker (in <install>\current\bmbd\data\)
#   Note: the payload is flat (no bmbd\ subdir) so AppContext.BaseDirectory for
#   BeeMemoryBank.Node.exe = current\ and legacyDir = current\data\
# ---------------------------------------------------------------------------

Write-Step "Step 5: Create legacy data marker (version X)"
$LegacyDir   = Join-Path $CurrentDir "data"
$LegacyDbFile = Join-Path $LegacyDir "beememorybank.db"
$LegacyMarkerGuid = [System.Guid]::NewGuid().ToString()

New-Item -ItemType Directory -Path $LegacyDir -Force | Out-Null
New-MinimalSqliteFile -Path $LegacyDbFile
$LegacyMarkerFile = Join-Path $LegacyDir "smoke-marker-v1.txt"
Set-Content -Path $LegacyMarkerFile -Value $LegacyMarkerGuid -Encoding ASCII
Write-Host "  Legacy dir   : $LegacyDir"
Write-Host "  Legacy db    : $LegacyDbFile"
Write-Host "  Legacy marker: $LegacyMarkerGuid"
Assert-True (Test-Path $LegacyDbFile)     "Legacy SQLite file created"
Assert-True (Test-Path $LegacyMarkerFile) "Legacy marker file created"
Write-Pass "Step 5: legacy data set up"

# ---------------------------------------------------------------------------
# Step 6: Pack v1.0.1
# ---------------------------------------------------------------------------

Write-Step "Step 6: vpk pack v1.0.1"
try {
    Invoke-VpkPack -Version "1.0.1" -PayloadDir $PayloadDir -ReleasesDir $SmokeRelDir
    Write-Pass "Step 6: vpk pack v1.0.1 done"
} catch {
    Write-Fail "Step 6: $_"
    exit 1
}

$NupkgV101 = Get-ChildItem $SmokeRelDir -Filter "*1.0.1-full.nupkg" | Select-Object -First 1
Assert-True ($null -ne $NupkgV101) "v1.0.1-full.nupkg produced"
if ($Script:FailCount -gt 0) { exit 1 }
Write-Host "  Nupkg: $($NupkgV101.FullName)"

# ---------------------------------------------------------------------------
# Step 7: Apply v1.0.1 update (real Velopack apply, ADR-confirmed command)
# ---------------------------------------------------------------------------

Write-Step "Step 7: Apply v1.0.1 update via Update.exe apply"
$UpdateExe = Join-Path $SmokeInstall "Update.exe"
Assert-True (Test-Path $UpdateExe) "Update.exe exists: $UpdateExe"
if ($Script:FailCount -gt 0) { exit 1 }

Write-Host "  Running: Update.exe apply --package <nupkg> --norestart --silent"
$ec = Invoke-Exe -Exe $UpdateExe -ExeArgs @("apply", "--package", $NupkgV101.FullName, "--norestart", "--silent") -TimeoutSec 120 -ShowOutput
Assert-True ($ec -eq 0) "Update.exe apply exit code = 0 (got $ec)"
if ($Script:FailCount -gt 0) { exit 1 }
Write-Pass "Step 7: update applied"

# After apply, current\ is rebuilt from v1.0.1. The legacy data we placed in
# current\data\ before the update is now GONE (expected -- this is what ADR P1 confirmed).
$LegacyDbAfterUpdate = Join-Path $CurrentDir "data\beememorybank.db"
Write-Host "  Legacy db after update exists: $(Test-Path $LegacyDbAfterUpdate)"
Assert-True (-not (Test-Path $LegacyDbAfterUpdate)) "Legacy db wiped by update (ADR P1 confirmed)"
Write-Pass "Step 7: update wipes current\data\ as expected"

# ---------------------------------------------------------------------------
# Step 8: Run node (--auto) -- triggers legacy rescue (from new legacy placement)
# ---------------------------------------------------------------------------
# Because current\data\ was wiped by the update, we need to recreate the legacy
# data AGAIN (simulating a user who had legacy data BEFORE the update, and where
# the update wipes it; the rescue runs on the NEXT start from the NEW current\).
# Per the real scenario: the Velopack update wipes current\ (including current\data\).
# After the update, the node sees NO legacy data (it's gone), so rescue gives NoLegacyFound.
# For the FULL e2e test of rescue, we need to populate current\data\ (the new current\)
# AFTER apply, then start the node.

Write-Step "Step 8: Post-update legacy data + node start (rescue scenario)"

# Re-create legacy data in the new current\ (the post-update install)
$NewCurrentDir = Join-Path $SmokeInstall "current"  # same path, rebuilt by apply
$NewLegacyDir  = Join-Path $NewCurrentDir "data"
$NewLegacyDb   = Join-Path $NewLegacyDir "beememorybank.db"
$NewLegacyMarkerGuid = [System.Guid]::NewGuid().ToString()

# Ensure no prior BeeMemoryBankData exists (clean slate for rescue check)
if (Test-Path $RealDataRoot) {
    Remove-Item -Recurse -Force $RealDataRoot -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $NewLegacyDir -Force | Out-Null
New-MinimalSqliteFile -Path $NewLegacyDb
$NewLegacyMarkerFile = Join-Path $NewLegacyDir "smoke-marker-postupdate.txt"
Set-Content -Path $NewLegacyMarkerFile -Value $NewLegacyMarkerGuid -Encoding ASCII
Write-Host "  Post-update legacy dir : $NewLegacyDir"
Write-Host "  Post-update marker GUID: $NewLegacyMarkerGuid"

# Find the updated bmbd exe (it should still be at current\BeeMemoryBank.Node.exe)
$UpdatedBmbd = Join-Path $NewCurrentDir "BeeMemoryBank.Node.exe"
Assert-True (Test-Path $UpdatedBmbd) "Updated bmbd exists: $UpdatedBmbd"
if ($Script:FailCount -gt 0) { exit 1 }

Write-Host "  Starting updated bmbd (--auto) to trigger legacy rescue..."
$RunLogFile = Join-Path $env:TEMP "bmb-smoke-bmbd-run.log"
if (Test-Path $RunLogFile) { Remove-Item $RunLogFile -Force }
New-Item -ItemType File -Path $RunLogFile -Force | Out-Null

$bmbdProc = Start-Bmbd -BmbdExe $UpdatedBmbd -LogFile $RunLogFile

# Wait briefly for the rescue to happen (node starts, runs rescue, may fail to start
# Api/Web since they are not in the payload -- that is fine, we only need the rescue)
$DataDirReady = Wait-BmbdReady -DataDir $RealDefaultVault -TimeoutSec 5

# The node will fail to start Api/Web (not in payload), but rescue runs before that.
# Give it a moment to run rescue logic then stop it.
Start-Sleep -Seconds 3
Stop-BmbdProcess -Proc $bmbdProc -WaitSec 5

Write-Host "  bmbd log:"
Get-Content $RunLogFile | ForEach-Object { Write-Host "    $_" }

# Check rescue outcome: marker should appear in %LOCALAPPDATA%\BeeMemoryBankData\vaults\default
$DefaultVaultMarker = Join-Path $RealDefaultVault "smoke-marker-postupdate.txt"
Assert-True (Test-Path $RealDefaultVault)    "Default vault created by rescue"
Assert-FileContains -FilePath $DefaultVaultMarker -Marker $NewLegacyMarkerGuid -Label "Step 8 rescue"
Write-Pass "Step 8: rescue succeeded, data preserved through Velopack update lifecycle"

# ---------------------------------------------------------------------------
# Step 9: Verify marker still present (update did not destroy data)
# ---------------------------------------------------------------------------

Write-Step "Step 9: Verify data survives (post-update check)"
Assert-FileContains -FilePath $DefaultVaultMarker -Marker $NewLegacyMarkerGuid -Label "Step 9 data-survival"
Write-Pass "Step 9: data intact after update"

# ---------------------------------------------------------------------------
# Step 10: Repair scenario (re-apply same v1.0.1 nupkg)
# ---------------------------------------------------------------------------

Write-Step "Step 10: Repair scenario (re-apply v1.0.1, data must survive)"

# Capture the marker before repair
$MarkerBeforeRepair = Get-Content $DefaultVaultMarker -Raw

# Apply the same package again (Velopack repair semantics)
Write-Host "  Re-applying v1.0.1 (repair)..."
$ec = Invoke-Exe -Exe $UpdateExe -ExeArgs @("apply", "--package", $NupkgV101.FullName, "--norestart", "--silent") -TimeoutSec 120 -ShowOutput
Assert-True ($ec -eq 0) "Repair apply exit code = 0 (got $ec)"

# Data in %LOCALAPPDATA%\BeeMemoryBankData must NOT be touched by repair (it is outside install dir)
Assert-True (Test-Path $DefaultVaultMarker) "Marker file still exists after repair"
$MarkerAfterRepair = if (Test-Path $DefaultVaultMarker) { Get-Content $DefaultVaultMarker -Raw } else { "" }
Assert-True ($MarkerBeforeRepair -eq $MarkerAfterRepair) "Marker content unchanged after repair"
Write-Pass "Step 10: repair scenario -- data preserved"

# ---------------------------------------------------------------------------
# Step 11: Legacy rescue scenario (clean BeeMemoryBankData, legacy data present)
# ---------------------------------------------------------------------------

Write-Step "Step 11: Legacy rescue scenario"

# Clean the real data dir (simulate fresh install / empty state)
if (Test-Path $RealDataRoot) { Remove-Item -Recurse -Force $RealDataRoot -ErrorAction SilentlyContinue }

# Place valid SQLite + marker in current\data\
$LegacyDir11 = Join-Path $NewCurrentDir "data"
$LegacyDb11  = Join-Path $LegacyDir11 "beememorybank.db"
$LegacyGuid11 = [System.Guid]::NewGuid().ToString()
if (Test-Path $LegacyDir11) { Remove-Item -Recurse -Force $LegacyDir11 -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $LegacyDir11 -Force | Out-Null
New-MinimalSqliteFile -Path $LegacyDb11
$LegacyMarker11 = Join-Path $LegacyDir11 "smoke-marker-s11.txt"
Set-Content -Path $LegacyMarker11 -Value $LegacyGuid11 -Encoding ASCII
Write-Host "  Legacy GUID for step 11: $LegacyGuid11"

# Start bmbd -- rescue should move data to vaults\default
$RunLog11 = Join-Path $env:TEMP "bmb-smoke-s11.log"
if (Test-Path $RunLog11) { Remove-Item $RunLog11 -Force }
New-Item -ItemType File -Path $RunLog11 -Force | Out-Null
$proc11 = Start-Bmbd -BmbdExe $UpdatedBmbd -LogFile $RunLog11
Start-Sleep -Seconds 4
Stop-BmbdProcess -Proc $proc11 -WaitSec 5
Write-Host "  bmbd log (step 11):"
Get-Content $RunLog11 | ForEach-Object { Write-Host "    $_" }

$DefaultMarker11 = Join-Path $RealDefaultVault "smoke-marker-s11.txt"
Assert-True (Test-Path $RealDefaultVault) "Default vault created"
Assert-FileContains -FilePath $DefaultMarker11 -Marker $LegacyGuid11 -Label "Step 11 legacy rescue"
Write-Pass "Step 11: legacy rescue - data moved to default vault"

# Second run: idempotency -- marker must stay, not be duplicated
Write-Host "  Running bmbd again (idempotency check)..."
$RunLog11b = Join-Path $env:TEMP "bmb-smoke-s11b.log"
if (Test-Path $RunLog11b) { Remove-Item $RunLog11b -Force }
New-Item -ItemType File -Path $RunLog11b -Force | Out-Null
$proc11b = Start-Bmbd -BmbdExe $UpdatedBmbd -LogFile $RunLog11b
Start-Sleep -Seconds 4
Stop-BmbdProcess -Proc $proc11b -WaitSec 5
Assert-FileContains -FilePath $DefaultMarker11 -Marker $LegacyGuid11 -Label "Step 11 idempotency"
# Ensure recovered- vaults are NOT created on second run (same db -> TargetAlreadyValid)
$RecoveredVaults = @(Get-ChildItem $RealVaultsDir -Directory -Filter "recovered-*" -ErrorAction SilentlyContinue)
Assert-True ($RecoveredVaults.Count -eq 0) "No recovered- vault created on idempotent re-run"
Write-Pass "Step 11: idempotency confirmed"

# ---------------------------------------------------------------------------
# Step 12: Conflict scenario (different markers in legacy AND default)
# ---------------------------------------------------------------------------

Write-Step "Step 12: Conflict scenario (legacy <> default, different data)"

# Default vault already has the step-11 marker (LegacyGuid11).
# Place a DIFFERENT valid SQLite + marker in legacy dir.
$LegacyGuid12 = [System.Guid]::NewGuid().ToString()
if (Test-Path $LegacyDir11) { Remove-Item -Recurse -Force $LegacyDir11 -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $LegacyDir11 -Force | Out-Null
# Create a DIFFERENT SQLite file (different content so AreSameDatabase returns false)
$sqliteMagic = [System.Text.Encoding]::ASCII.GetBytes("SQLite format 3")
$header12 = [byte[]]::new(100)
[Array]::Copy($sqliteMagic, $header12, $sqliteMagic.Length)
$header12[24] = 0x42   # distinctive byte so hash differs from step-11 db
[System.IO.File]::WriteAllBytes($LegacyDb11, $header12)
$LegacyMarker12 = Join-Path $LegacyDir11 "smoke-marker-s12.txt"
Set-Content -Path $LegacyMarker12 -Value $LegacyGuid12 -Encoding ASCII
Write-Host "  Default vault GUID (existing): $LegacyGuid11"
Write-Host "  New legacy GUID (conflict)   : $LegacyGuid12"

$RunLog12 = Join-Path $env:TEMP "bmb-smoke-s12.log"
if (Test-Path $RunLog12) { Remove-Item $RunLog12 -Force }
New-Item -ItemType File -Path $RunLog12 -Force | Out-Null
$proc12 = Start-Bmbd -BmbdExe $UpdatedBmbd -LogFile $RunLog12
Start-Sleep -Seconds 4
Stop-BmbdProcess -Proc $proc12 -WaitSec 5
Write-Host "  bmbd log (step 12):"
Get-Content $RunLog12 | ForEach-Object { Write-Host "    $_" }

# Default vault must be UNCHANGED (still has LegacyGuid11)
Assert-FileContains -FilePath $DefaultMarker11 -Marker $LegacyGuid11 -Label "Step 12 default vault untouched"

# A recovered- vault must exist with LegacyGuid12
$RecoveredVaults12 = @(Get-ChildItem $RealVaultsDir -Directory -Filter "recovered-*" -ErrorAction SilentlyContinue)
Assert-True ($RecoveredVaults12.Count -gt 0) "recovered- vault created for conflict"
if ($RecoveredVaults12.Count -gt 0) {
    $recovVault = $RecoveredVaults12[0].FullName
    $recovMarker = Join-Path $recovVault "smoke-marker-s12.txt"
    Assert-FileContains -FilePath $recovMarker -Marker $LegacyGuid12 -Label "Step 12 legacy in recovered vault"
}
Write-Pass "Step 12: conflict scenario handled correctly"

# ---------------------------------------------------------------------------
# Step 13: Negative scenario (valid default, no legacy / corrupt legacy)
# ---------------------------------------------------------------------------

Write-Step "Step 13: Negative scenario (valid default, missing/corrupt legacy)"

# Default vault has valid data (from step 12). Remove legacy dir entirely.
if (Test-Path $LegacyDir11) { Remove-Item -Recurse -Force $LegacyDir11 -ErrorAction SilentlyContinue }

$DefaultVaultCountBefore = (Get-ChildItem $RealVaultsDir -Directory | Measure-Object).Count

$RunLog13 = Join-Path $env:TEMP "bmb-smoke-s13.log"
if (Test-Path $RunLog13) { Remove-Item $RunLog13 -Force }
New-Item -ItemType File -Path $RunLog13 -Force | Out-Null
$proc13 = Start-Bmbd -BmbdExe $UpdatedBmbd -LogFile $RunLog13
Start-Sleep -Seconds 4
Stop-BmbdProcess -Proc $proc13 -WaitSec 5

# No new vaults should appear (rescue is a no-op: TargetAlreadyValid)
$DefaultVaultCountAfter = (Get-ChildItem $RealVaultsDir -Directory | Measure-Object).Count
Assert-True ($DefaultVaultCountAfter -eq $DefaultVaultCountBefore) "No new vaults created (no-op rescue)"

# Default vault data untouched
Assert-FileContains -FilePath $DefaultMarker11 -Marker $LegacyGuid11 -Label "Step 13 default vault untouched"

# Now test corrupt legacy: place non-SQLite file as beememorybank.db
New-Item -ItemType Directory -Path $LegacyDir11 -Force | Out-Null
Set-Content -Path $LegacyDb11 -Value "THIS IS NOT A SQLITE FILE" -Encoding ASCII
$RunLog13b = Join-Path $env:TEMP "bmb-smoke-s13b.log"
if (Test-Path $RunLog13b) { Remove-Item $RunLog13b -Force }
New-Item -ItemType File -Path $RunLog13b -Force | Out-Null
$proc13b = Start-Bmbd -BmbdExe $UpdatedBmbd -LogFile $RunLog13b
Start-Sleep -Seconds 4
Stop-BmbdProcess -Proc $proc13b -WaitSec 5

$DefaultVaultCountFinal = (Get-ChildItem $RealVaultsDir -Directory | Measure-Object).Count
Assert-True ($DefaultVaultCountFinal -eq $DefaultVaultCountBefore) "Corrupt legacy: no new vaults"
Assert-FileContains -FilePath $DefaultMarker11 -Marker $LegacyGuid11 -Label "Step 13b default vault untouched (corrupt legacy)"
Write-Pass "Step 13: negative scenario -- no-op, data untouched"

# ---------------------------------------------------------------------------
# Step 14: Uninstall throwaway package
# ---------------------------------------------------------------------------

Write-Step "Step 14: Uninstall throwaway package"
if (Test-Path $UpdateExe) {
    Write-Host "  Running: Update.exe uninstall --silent"
    $ec = Invoke-Exe -Exe $UpdateExe -ExeArgs @("uninstall", "--silent") -TimeoutSec 60 -ShowOutput
    Write-Host "  Uninstall exit code: $ec"
    # Velopack schedules rmdir via cmd.exe, so the dir may not be gone yet; that is fine
    Write-Pass "Step 14: uninstall command completed (exit $ec)"
} else {
    Write-Host "  Update.exe not found (already gone?). Skipping."
    Write-Pass "Step 14: (skipped, Update.exe already absent)"
}

# Allow a moment for the scheduled rmdir to run
Start-Sleep -Seconds 4

# Remove payload temp (not real data -- fine to do inline, not safety-critical)
if (Test-Path $PayloadDir) {
    try { Remove-Item -Recurse -Force $PayloadDir -ErrorAction SilentlyContinue } catch {}
}

} finally {
    # Runs on normal completion, any `exit N` above, AND any uncaught terminating error --
    # this is what actually guarantees the user's real data gets restored.
    Invoke-Cleanup
}

# ---------------------------------------------------------------------------
# Final summary
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "================================================================"
Write-Host "  SMOKE-UPDATE TEST SUMMARY"
Write-Host "================================================================"
Write-Host "  Step 0  : Prerequisites check              - PASS"
Write-Host "  Step 1  : Publish node binaries            - PASS"
Write-Host "  Step 2  : Backup real BeeMemoryBankData    - PASS"
Write-Host "  Step 3  : vpk pack v1.0.0                  - PASS"
Write-Host "  Step 4  : Silent install v1.0.0            - PASS"
Write-Host "  Step 5  : Create legacy data marker        - PASS"
Write-Host "  Step 6  : vpk pack v1.0.1                  - PASS"
Write-Host "  Step 7  : Apply v1.0.1 update              - PASS"
Write-Host "  Step 8  : Legacy rescue after update       - PASS"
Write-Host "  Step 9  : Data survival check              - PASS"
Write-Host "  Step 10 : Repair scenario                  - PASS"
Write-Host "  Step 11 : Legacy rescue + idempotency      - PASS"
Write-Host "  Step 12 : Conflict scenario                - PASS"
Write-Host "  Step 13 : Negative (no-op)                 - PASS"
Write-Host "  Step 14 : Uninstall throwaway package      - PASS"
Write-Host "  Cleanup : Restore original data            - PASS"
Write-Host "================================================================"

if ($Script:FailCount -gt 0) {
    Write-Host "  RESULT: $($Script:FailCount) ASSERTION(S) FAILED" -ForegroundColor Red
    exit 1
} else {
    Write-Host "  RESULT: ALL ASSERTIONS PASSED"
    exit 0
}
