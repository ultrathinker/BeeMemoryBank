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
      - A migration log is written to %LOCALAPPDATA%\BeeMemoryBankData\migration\.

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

function Test-SqliteHeader {
    param ([string]$Path)
    if (-not (Test-Path $Path -PathType Leaf)) { return $false }
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try {
            if ($fs.Length -lt $SqliteMagicBytes.Length) { return $false }
            $buf = New-Object byte[] $SqliteMagicBytes.Length
            $read = $fs.Read($buf, 0, $buf.Length)
            if ($read -lt $SqliteMagicBytes.Length) { return $false }
            for ($i = 0; $i -lt $SqliteMagicBytes.Length; $i++) {
                if ($buf[$i] -ne $SqliteMagicBytes[$i]) { return $false }
            }
            return $true
        } finally {
            $fs.Dispose()
        }
    } catch {
        return $false
    }
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

function Get-FileHash64KB {
    param ([string]$Path)
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try {
            $bytesToRead = [Math]::Min($fs.Length, 65536)
            $buf = New-Object byte[] $bytesToRead
            $read = 0
            while ($read -lt $bytesToRead) {
                $n = $fs.Read($buf, $read, $bytesToRead - $read)
                if ($n -eq 0) { break }
                $read += $n
            }
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                return $sha.ComputeHash($buf, 0, $read)
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
        $hashA = Get-FileHash64KB -Path $PathA
        $hashB = Get-FileHash64KB -Path $PathB
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

function Copy-DirectoryRecursive {
    param ([string]$Source, [string]$Dest, [ref]$FileCount, [ref]$TotalBytes)
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source) {
        if ($item.PSIsContainer) {
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

function Write-MigrationLog {
    param ([string]$LegacyDir, [string]$TargetDir, [int]$FileCount, [long]$TotalBytes, [string]$Outcome, [string]$Error)
    try {
        $migDir = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData\migration"
        New-Item -ItemType Directory -Path $migDir -Force | Out-Null
        $ts = (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
        $logFile = Join-Path $migDir "rescue-ps-$ts.log"
        $lines = @(
            "[$(Get-Date -Format 'o')] Manual PowerShell Rescue",
            "  Source  : $LegacyDir",
            "  Target  : $TargetDir",
            "  Files   : $FileCount",
            "  Bytes   : $TotalBytes",
            "  Outcome : $Outcome"
        )
        if ($Error) { $lines += "  Error   : $Error" }
        $lines | Set-Content $logFile -Encoding UTF8
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

if (-not (Test-SqliteHeader -Path $legacyDbPath)) {
    Write-Host "No valid legacy database found at '$legacyDbPath'." -ForegroundColor Yellow
    Write-Host "NoLegacyFound -- nothing to do." -ForegroundColor Yellow
    exit 0
}

Write-Host "Found valid legacy database: $legacyDbPath" -ForegroundColor Green

# Check node.lock
$legacyLockPath = Join-Path $LegacyDir "node.lock"
if (Test-FileLocked -Path $legacyLockPath) {
    Write-Error "Legacy source is locked (node.lock is held by another process: '$legacyLockPath'). Stop the running node first."
    Write-MigrationLog -LegacyDir $LegacyDir -TargetDir $TargetVaultDir -FileCount 0 -TotalBytes 0 `
        -Outcome "LegacyFoundButRescueFailed" -Error "node.lock is locked"
    exit 5
}

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
