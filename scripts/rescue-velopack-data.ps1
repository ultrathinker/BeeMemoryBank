<#
.SYNOPSIS
    Manually rescues BeeMemoryBank data locked in an old Velopack installation directory.

.DESCRIPTION
    Before a Velopack update or reinstall can wipe the legacy data path
    (<InstallDir>\current\data), this script copies the data to the new stable
    location (%LOCALAPPDATA%\BeeMemoryBankData\vaults\default) using the same
    algorithm as the built-in C# LegacyDataRescue.TryRescue.

    Rules (matching the C# implementation exactly):
      - Source is NEVER deleted or moved.
      - Copy is atomic: first to a temp sibling, then Directory.Move/Rename.
      - Transient files (node.lock, .runtime.json, node.status.json, *.ready)
        are excluded from the copy.
      - desktop-settings.json goes to the Root (%LOCALAPPDATA%\BeeMemoryBankData),
        not into the vault.
      - A rescued-from.json marker is written into the destination vault.
      - A migration log is written to both %LOCALAPPDATA%\BeeMemoryBankData\migration\
        AND %LOCALAPPDATA%\BeeMemoryBankData\logs\ (Fix #7).
      - Reparse points / junctions are skipped (Fix #3).
      - node.lock is held exclusively throughout the entire copy (Fix #2).
      - Unreadable but existing legacy DB returns a hard error (Fix #1).
      - Full-file SHA-256 is used for "same database" comparison (Fix #6).

.PARAMETER LegacyDir
    Path to the legacy data directory. Defaults to <script location>\data
    (i.e. the old default: <Install>\current\data).

.PARAMETER TargetVaultDir
    Path to the new stable vault directory.
    Defaults to %LOCALAPPDATA%\BeeMemoryBankData\vaults\default.

.EXAMPLE
    # Run before doing a Velopack update/repair:
    .\rescue-velopack-data.ps1

.EXAMPLE
    # Specify paths explicitly:
    .\rescue-velopack-data.ps1 `
        -LegacyDir "C:\Users\evgeny\AppData\Local\BeeMemoryBank\current\data" `
        -TargetVaultDir "C:\Users\evgeny\AppData\Local\BeeMemoryBankData\vaults\default"

.NOTES
    Run this BEFORE applying a Velopack update or reinstalling.
    The built-in rescue (called automatically at startup) covers the common case --
    this script is for manual/emergency use or pre-update preparation.
#>

[CmdletBinding(SupportsShouldProcess)]
param (
    [string]$LegacyDir    = (Join-Path $PSScriptRoot "data"),
    [string]$TargetVaultDir = (Join-Path $env:LOCALAPPDATA "BeeMemoryBankData\vaults\default")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

# SQLite magic header bytes: "SQLite format 3\0"
$SqliteMagicString = "SQLite format 3`0"
$SqliteMagicBytes  = [System.Text.Encoding]::ASCII.GetBytes($SqliteMagicString)

# Transient files that must NOT be copied
$TransientFileNames = @('node.lock', '.runtime.json', 'node.status.json')
$TransientSuffix    = '.ready'

# ---------------------------------------------------------------------------
# Helper functions
# ---------------------------------------------------------------------------

# Fix #1: Three-way probe -- returns 'FileNotFound', 'Unreadable', 'InvalidHeader', or 'ValidSqlite'
function Get-SqliteFileStatus {
    param ([string]$Path)
    if (-not (Test-Path $Path -PathType Leaf)) { return 'FileNotFound' }
    try {
        $fs = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try {
            if ($fs.Length -lt $SqliteMagicBytes.Length) { return 'InvalidHeader' }
            $buf = New-Object byte[] $SqliteMagicBytes.Length
            $read = $fs.Read($buf, 0, $buf.Length)
            if ($read -lt $SqliteMagicBytes.Length) { return 'InvalidHeader' }
            for ($i = 0; $i -lt $SqliteMagicBytes.Length; $i++) {
                if ($buf[$i] -ne $SqliteMagicBytes[$i]) { return 'InvalidHeader' }
            }
            return 'ValidSqlite'
        } finally {
            $fs.Dispose()
        }
    } catch [System.IO.IOException] {
        return 'Unreadable'
    } catch [System.UnauthorizedAccessException] {
        return 'Unreadable'
    } catch {
        return 'Unreadable'
    }
}

function Test-SqliteHeader {
    param ([string]$Path)
    return (Get-SqliteFileStatus -Path $Path) -eq 'ValidSqlite'
}

function Test-FileLocked {
    param ([string]$Path)
    if (-not (Test-Path $Path -PathType Leaf)) { return $false }
    try {
        $fs = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $fs.Dispose()
        return $false
    } catch [System.IO.IOException] {
        return $true
    } catch {
        return $false
    }
}

# Fix #6: Hash the ENTIRE file (streaming) instead of just first 64 KB
function Get-FileHashFull {
    param ([string]$Path)
    try {
        $fs = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                return $sha.ComputeHash($fs)
            } finally {
                $sha.Dispose()
            }
        } finally {
            $fs.Dispose()
        }
    } catch {
        return $null
    }
}

function Compare-Databases {
    param ([string]$PathA, [string]$PathB)
    try {
        $a = Get-Item $PathA
        $b = Get-Item $PathB
        if ($a.Length -ne $b.Length)                     { return $false }
        if ($a.LastWriteTimeUtc -ne $b.LastWriteTimeUtc) { return $false }
        $hashA = Get-FileHashFull -Path $PathA
        $hashB = Get-FileHashFull -Path $PathB
        if ($null -eq $hashA -or $null -eq $hashB)       { return $false }
        return [System.Linq.Enumerable]::SequenceEqual($hashA, $hashB)
    } catch {
        return $false
    }
}

function Test-IsTransient {
    param ([string]$FileName)
    if ($TransientFileNames -contains $FileName) { return $true }
    if ($FileName.EndsWith($TransientSuffix, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $false
}

# Fix #3: Skip reparse points (junctions / symlinks) to prevent infinite recursion
function Copy-DirectoryRecursive {
    param ([string]$Source, [string]$Dest, [ref]$FileCount, [ref]$TotalBytes)
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.PSIsContainer) {
            # Fix #3: check for reparse point (junction/symlink) on directories
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                Write-Verbose "  Skipping reparse point/junction: $($item.FullName)"
                continue
            }
            Copy-DirectoryRecursive -Source $item.FullName -Dest (Join-Path $Dest $item.Name) `
                -FileCount $FileCount -TotalBytes $TotalBytes
        } else {
            if (Test-IsTransient -FileName $item.Name) {
                Write-Verbose "  Skipping transient: $($item.Name)"
                continue
            }
            $destFile = Join-Path $Dest $item.Name
            Copy-Item -LiteralPath $item.FullName -Destination $destFile -Force
            $FileCount.Value++
            $TotalBytes.Value += $item.Length
        }
    }
}

# Fix #7: Write log to BOTH migration\ and logs\ directories
function Write-MigrationLog {
    param ([string]$LegacyDir, [string]$TargetDir, [int]$FileCount, [long]$TotalBytes, [string]$Outcome, [string]$Error)
    try {
        $ts = (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
        $lines = @(
            "[$(Get-Date -Format 'o')] Manual PowerShell Rescue",
            "  Source  : $LegacyDir",
            "  Target  : $TargetDir",
            "  Files   : $FileCount",
            "  Bytes   : $TotalBytes",
            "  Outcome : $Outcome"
        )
        if ($Error) { $lines += "  Error   : $Error" }

        # Primary: migration\
        $migDir = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData\migration"
        New-Item -ItemType Directory -Path $migDir -Force | Out-Null
        $lines | Set-Content (Join-Path $migDir "rescue-ps-$ts.log") -Encoding UTF8

        # Secondary: logs\ (Fix #7 / spec section 3.2 point 5)
        $logsDir = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData\logs"
        New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
        $lines | Set-Content (Join-Path $logsDir "rescue-ps-$ts.log") -Encoding UTF8
    } catch {
        Write-Warning "Could not write migration log: $_"
    }
}

# ---------------------------------------------------------------------------
# Main logic
# ---------------------------------------------------------------------------

Write-Host "BeeMemoryBank Legacy Data Rescue (PowerShell)" -ForegroundColor Cyan
Write-Host "  Legacy dir  : $LegacyDir"
Write-Host "  Target vault: $TargetVaultDir"
Write-Host ""

# Step 1 -- Validate source
$legacyDbPath = Join-Path $LegacyDir "beememorybank.db"

# Fix #1: tri-state probe
$legacyStatus = Get-SqliteFileStatus -Path $legacyDbPath
if ($legacyStatus -eq 'Unreadable') {
    Write-Error "Legacy database exists but is currently unreadable: '$legacyDbPath'. Check for running processes or security software holding the file, then retry."
    Write-MigrationLog -LegacyDir $LegacyDir -TargetDir $TargetVaultDir -FileCount 0 -TotalBytes 0 `
        -Outcome "LegacyFoundButRescueFailed" -Error "Legacy DB unreadable"
    exit 5
}
if ($legacyStatus -ne 'ValidSqlite') {
    Write-Host "No valid legacy database found at '$legacyDbPath'." -ForegroundColor Yellow
    Write-Host "NoLegacyFound -- nothing to do." -ForegroundColor Yellow
    exit 0
}

Write-Host "Found valid legacy database: $legacyDbPath" -ForegroundColor Green

# Fix #2: Acquire exclusive hold on node.lock and keep it throughout the entire copy
$legacyLockPath = Join-Path $LegacyDir "node.lock"
$lockHandle = $null
if (Test-Path $legacyLockPath -PathType Leaf) {
    try {
        $lockHandle = [System.IO.File]::Open($legacyLockPath, 'Open', 'ReadWrite', 'None')
    } catch [System.IO.IOException] {
        Write-Error "Legacy source is locked (node.lock is held by another process: '$legacyLockPath'). Stop the running node first."
        Write-MigrationLog -LegacyDir $LegacyDir -TargetDir $TargetVaultDir -FileCount 0 -TotalBytes 0 `
            -Outcome "LegacyFoundButRescueFailed" -Error "node.lock is locked"
        exit 5
    } catch {
        Write-Error "Cannot acquire exclusive hold on '$legacyLockPath': $_"
        exit 5
    }
}

try {
    # Step 2/3/4 -- Determine scenario
    $targetDbPath = Join-Path $TargetVaultDir "beememorybank.db"
    $targetValid  = Test-SqliteHeader -Path $targetDbPath

    if (-not $targetValid) {
        # Case 1: target empty/invalid -> copy to target
        Write-Host "Target vault has no valid DB. Performing rescue..." -ForegroundColor Cyan
        $destinationDir = $TargetVaultDir
        $isRecovery = $false
    } else {
        $sameDb = Compare-Databases -PathA $legacyDbPath -PathB $targetDbPath
        if ($sameDb) {
            # Case 2: both have the same DB -> no-op
            Write-Host "Target vault already contains the same database as legacy. No-op." -ForegroundColor Green
            Write-Host "TargetAlreadyValid -- nothing to do."
            exit 0
        }
        # Case 3: conflict -> copy to recovered-<date>
        $recoveredName = "recovered-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        $destinationDir = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData\vaults\$recoveredName"
        $isRecovery = $true
        Write-Host "Both vaults have different valid databases. Copying legacy to recovery vault: $destinationDir" -ForegroundColor Yellow
    }

    # Atomic copy: temp sibling -> rename
    $tempDir   = "$destinationDir.rescue-tmp-$([Guid]::NewGuid().ToString('N'))"
    $fileCount = 0
    $totalBytes = [long]0
    $refFileCount  = [ref]$fileCount
    $refTotalBytes = [ref]$totalBytes

    try {
        if ($PSCmdlet.ShouldProcess($destinationDir, "Copy legacy data")) {
            Copy-DirectoryRecursive -Source $LegacyDir -Dest $tempDir `
                -FileCount $refFileCount -TotalBytes $refTotalBytes

            # Handle desktop-settings.json -> BeeMemoryBankData root
            $settingsInTemp = Join-Path $tempDir "desktop-settings.json"
            if (Test-Path $settingsInTemp -PathType Leaf) {
                $settingsDest = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData\desktop-settings.json"
                if (-not (Test-Path $settingsDest)) {
                    Copy-Item -LiteralPath $settingsInTemp -Destination $settingsDest
                    Write-Verbose "  Moved desktop-settings.json to Root."
                }
                Remove-Item $settingsInTemp -Force
            }

            # Write rescued-from.json marker
            $marker = [ordered]@{
                sourcePath  = $LegacyDir
                rescuedAt   = (Get-Date -Format 'o')
                appVersion  = "manual-ps-rescue"
                fileCount   = $refFileCount.Value
                totalBytes  = $refTotalBytes.Value
            }
            $markerJson = $marker | ConvertTo-Json
            Set-Content -Path (Join-Path $tempDir "rescued-from.json") -Value $markerJson -Encoding UTF8

            # Ensure parent directory exists
            $parentDir = Split-Path $destinationDir -Parent
            if ($parentDir -and -not (Test-Path $parentDir)) {
                New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
            }

            # Atomic rename: remove empty target if present, then move
            if (Test-Path $destinationDir) {
                $existing = @(Get-ChildItem -LiteralPath $destinationDir -Force)
                if ($existing.Count -eq 0) {
                    Remove-Item $destinationDir -Force
                } else {
                    # Fix #5: re-validate before failing -- could be a concurrent rescue that already succeeded
                    $recheckStatus = Get-SqliteFileStatus -Path (Join-Path $destinationDir "beememorybank.db")
                    if ($recheckStatus -eq 'ValidSqlite') {
                        Write-Host "Target vault was populated concurrently by another rescue process. TargetAlreadyValid." -ForegroundColor Green
                        if (Test-Path $tempDir) { try { Remove-Item $tempDir -Recurse -Force } catch { } }
                        exit 0
                    }
                    throw "Destination '$destinationDir' is non-empty. Aborting to avoid data loss."
                }
            }

            Move-Item -LiteralPath $tempDir -Destination $destinationDir

            $outcomeMsg = if ($isRecovery) { "RescuedToRecoveredVault" } else { "RescuedSuccessfully" }
            Write-MigrationLog -LegacyDir $LegacyDir -TargetDir $destinationDir `
                -FileCount $refFileCount.Value -TotalBytes $refTotalBytes.Value `
                -Outcome $outcomeMsg -Error ""

            Write-Host ""
            Write-Host "Rescue complete! $outcomeMsg" -ForegroundColor Green
            Write-Host "  Files copied : $($refFileCount.Value)"
            Write-Host "  Total bytes  : $($refTotalBytes.Value)"
            Write-Host "  Destination  : $destinationDir"
            Write-Host ""
            Write-Host "The source at '$LegacyDir' was NOT deleted (Velopack will handle it on next update/uninstall)." -ForegroundColor Yellow
        }
    } catch {
        # Clean up temp dir on failure
        if (Test-Path $tempDir) {
            try { Remove-Item $tempDir -Recurse -Force } catch { }
        }
        Write-MigrationLog -LegacyDir $LegacyDir -TargetDir $destinationDir `
            -FileCount $refFileCount.Value -TotalBytes $refTotalBytes.Value `
            -Outcome "LegacyFoundButRescueFailed" -Error $_.Exception.Message
        Write-Error "Rescue failed: $_"
        exit 5
    }
} finally {
    # Fix #2: release the exclusive lock hold after copy is fully done (or failed)
    if ($null -ne $lockHandle) {
        $lockHandle.Dispose()
    }
}
