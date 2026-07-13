<#
.SYNOPSIS
    Packages BeeMemoryBank for Windows as an MSI service installer.

.DESCRIPTION
    1. Runs publish-node.ps1 if its output doesn't already exist.
    2. Builds the WiX v5 project to produce the MSI installer.

.NOTES
    - Requires: dotnet CLI, WiX Toolset v5.0.2.
#>
[CmdletBinding()]
param (
    [switch]$ForcePublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- Paths -------------------------------------------------------------------
$RepoRoot     = (Resolve-Path "$PSScriptRoot\..").Path
$PublishBase  = Join-Path $RepoRoot "publish\win-x64"
$WixProj      = Join-Path $RepoRoot "installers\windows\msi\BeeMemoryBank.ServerService.wixproj"

Write-Host ""
Write-Host "================================================================"
Write-Host "  BeeMemoryBank - Windows Server Service MSI Packaging"
Write-Host "================================================================"
Write-Host ""

# ---- Step 1: Ensure Node components are published ----------------------------
$RequiredExes = @(
    "bmbd\BeeMemoryBank.Node.exe",
    "api\BeeMemoryBank.Api.exe",
    "web\BeeMemoryBank.Web.exe",
    "cli\bmb.exe"
)

$NeedsPublish = $ForcePublish
if (-not $NeedsPublish) {
    foreach ($exe in $RequiredExes) {
        $path = Join-Path $PublishBase $exe
        if (-not (Test-Path $path)) {
            Write-Host "Missing required published artifact: $path"
            $NeedsPublish = $true
            break
        }
    }
}

if ($NeedsPublish) {
    Write-Host "-- Step 1: Publishing Node components via publish-node.ps1 ----------"
    & "$PSScriptRoot\publish-node.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "publish-node.ps1 failed (exit code $LASTEXITCODE)."
        exit 1
    }
} else {
    Write-Host "-- Step 1: Using existing published artifacts in $PublishBase -------"
}

# ---- Step 2: Build MSI via WiX v5 -------------------------------------------
Write-Host ""
Write-Host "-- Step 2: Building MSI project --------------------------------------"
if (-not (Test-Path $WixProj)) {
    Write-Error "WiX project file not found: $WixProj"
    exit 1
}

# Build the wixproj
dotnet build $WixProj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed for WiX project: $WixProj"
    exit 1
}

Write-Host ""
Write-Host "================================================================"
Write-Host "  MSI PACKAGING COMPLETE"
Write-Host "================================================================"
Write-Host ""

$MsiPath = Join-Path $RepoRoot "installers\windows\msi\bin\x64\Release\en-US\BeeMemoryBank.ServerService.msi"
if (-not (Test-Path $MsiPath)) {
    # Fallback to find any .msi in bin
    $MsiPath = Get-ChildItem (Join-Path $RepoRoot "installers\windows\msi\bin") -Filter "*.msi" -Recurse | Select-Object -First 1 -ExpandProperty FullName
}

if ($MsiPath -and (Test-Path $MsiPath)) {
    $szMB = [math]::Round((Get-Item $MsiPath).Length / 1MB, 2)
    Write-Host "  MSI Installer : $MsiPath  ($szMB MB)"
} else {
    Write-Warning "  MSI file not found in bin directory."
}
Write-Host ""
