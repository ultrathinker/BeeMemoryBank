<#
.SYNOPSIS
    Packages BeeMemoryBank for Windows using Velopack.

.DESCRIPTION
    1. Publishes BeeMemoryBank.Node (bmbd), Api, Web, Cli via publish-node.ps1
       (preserving its existing behaviour exactly).
    2. Publishes BeeMemoryBank.Desktop (the Avalonia UI shell) as a self-contained
       win-x64 binary into publish/win-x64/desktop/.
    3. Assembles a single "app payload" directory (publish/win-x64/payload/) that
       the Velopack installer will distribute:
         desktop\   - the Avalonia shell (entry-point exe)
         bmbd\      - BeeMemoryBank.Node (child process spawned by Desktop)
         api\       - BeeMemoryBank.Api  (child of bmbd)
         web\       - BeeMemoryBank.Web  (child of bmbd)
         cli\       - bmb.exe CLI tool
    4. Converts the PNG icon to .ico and runs 'vpk pack' to produce
         installers/windows/velopack/releases/Setup.exe   (per-user, no UAC)
       plus the full Velopack delta-update release feed.

.NOTES
    - Code signing is intentionally OMITTED (out of scope).
    - Desktop calls VelopackApp.Build().Run() as the first line of Main(), so
      --skipVeloAppCheck is no longer needed and is not passed to 'vpk pack'.
    - Requires: dotnet CLI, vpk 1.2.0+ (dotnet tool install -g vpk)

.PARAMETER SkipNodePublish
    Skip calling publish-node.ps1 (assumes publish/win-x64/{bmbd,api,web,cli}
    already exist from a previous run).  Useful for rapid iteration.
#>
[CmdletBinding()]
param (
    [switch]$SkipNodePublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- Paths -------------------------------------------------------------------
$RepoRoot      = (Resolve-Path "$PSScriptRoot\..").Path
$PublishBase   = Join-Path $RepoRoot "publish\win-x64"
$DesktopOut    = Join-Path $PublishBase "desktop"
$PayloadDir    = Join-Path $PublishBase "payload"
$DesktopProj   = Join-Path $RepoRoot "desktop\BeeMemoryBank.Desktop\BeeMemoryBank.Desktop.csproj"
$SourceIconPng = Join-Path $RepoRoot "desktop\BeeMemoryBank.Desktop\Assets\icon.png"
$VelopackDir   = Join-Path $RepoRoot "installers\windows\velopack"
$ReleasesDir   = Join-Path $VelopackDir "releases"
$IconIco       = Join-Path $VelopackDir "icon.ico"

# ---- Step 0: Check prerequisites ---------------------------------------------
Write-Host ""
Write-Host "================================================================"
Write-Host "  BeeMemoryBank - Windows packaging (Velopack, unsigned)"
Write-Host "================================================================"
Write-Host ""

$vpkCmd = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpkCmd) {
    Write-Error "vpk is not on PATH.  Install it with:  dotnet tool install -g vpk"
    exit 1
}
Write-Host "vpk found: $($vpkCmd.Source)"

# ---- Step 1: Publish Node components (bmbd / api / web / cli) ----------------
if (-not $SkipNodePublish) {
    Write-Host ""
    Write-Host "-- Step 1: Publishing Node components via publish-node.ps1 ----------"
    & "$PSScriptRoot\publish-node.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "publish-node.ps1 failed (exit code $LASTEXITCODE)."
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "-- Step 1: Skipped (-SkipNodePublish flag set) ----------------------"
}

# ---- Step 2: Publish Desktop (Avalonia shell) --------------------------------
Write-Host ""
Write-Host "-- Step 2: Publishing Desktop project --------------------------------"
if (-not (Test-Path $DesktopProj)) {
    Write-Error "Desktop project not found: $DesktopProj"
    exit 1
}

if (Test-Path $DesktopOut) {
    Write-Host "  Cleaning $DesktopOut ..."
    Remove-Item -Recurse -Force $DesktopOut
}

dotnet publish $DesktopProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $DesktopOut

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish BeeMemoryBank.Desktop failed."
    exit 1
}
Write-Host "  Desktop published to: $DesktopOut"

# ---- Step 3: Assemble payload directory --------------------------------------
Write-Host ""
Write-Host "-- Step 3: Assembling combined payload directory ---------------------"

if (Test-Path $PayloadDir) {
    Write-Host "  Cleaning $PayloadDir ..."
    Remove-Item -Recurse -Force $PayloadDir
}
New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null

# vpk requires the main exe at the ROOT of --packDir.
# Desktop publish output goes to the payload root (so BeeMemoryBank.Desktop.exe
# is directly at payload\BeeMemoryBank.Desktop.exe).
# Server components go as named subdirectories inside the payload.
$DesktopSrc = Join-Path $PublishBase "desktop"
if (-not (Test-Path $DesktopSrc)) {
    Write-Error "Desktop publish output not found: $DesktopSrc"
    exit 1
}
Write-Host "  Copying desktop files -> payload root ..."
# Copy contents (not the folder itself) so exe lands at payload\BeeMemoryBank.Desktop.exe
Get-ChildItem -Path $DesktopSrc | Copy-Item -Destination $PayloadDir -Recurse -Force

# Server component subdirectories
$ServerComponents = @(
    @{ Src = "bmbd"; Dst = "bmbd" },
    @{ Src = "api";  Dst = "api" },
    @{ Src = "web";  Dst = "web" },
    @{ Src = "cli";  Dst = "cli" }
)

foreach ($c in $ServerComponents) {
    $Src = Join-Path $PublishBase $c.Src
    $Dst = Join-Path $PayloadDir  $c.Dst
    if (-not (Test-Path $Src)) {
        Write-Error "Expected component directory not found: $Src"
        exit 1
    }
    Write-Host "  Copying $($c.Src) -> payload\$($c.Dst) ..."
    Copy-Item -Recurse -Force $Src $Dst
}

Write-Host "  Payload assembled at: $PayloadDir"

# ---- Step 4: Convert PNG icon to ICO -----------------------------------------
Write-Host ""
Write-Host "-- Step 4: Converting icon.png -> icon.ico ---------------------------"

if (-not (Test-Path $SourceIconPng)) {
    Write-Error "Source icon not found: $SourceIconPng"
    exit 1
}

New-Item -ItemType Directory -Path $VelopackDir -Force | Out-Null

Add-Type -AssemblyName System.Drawing

function ConvertTo-Ico {
    param (
        [string]$PngPath,
        [string]$IcoPath,
        [int[]]$Sizes = @(256, 64, 48, 32, 16)
    )

    $srcBmp = [System.Drawing.Image]::FromFile($PngPath)
    $images = @()

    foreach ($sz in $Sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($srcBmp, 0, 0, $sz, $sz)
        $g.Dispose()

        $imgStream = New-Object System.IO.MemoryStream
        $bmp.Save($imgStream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,$imgStream.ToArray()
        $imgStream.Dispose()
        $bmp.Dispose()
    }
    $srcBmp.Dispose()

    # Build ICO binary manually (ICO = ICONDIR header + N ICONDIRENTRY + image data)
    $count    = $images.Count
    $headerSz = 6 + $count * 16   # ICONDIR (6 bytes) + ICONDIRENTRY * count (16 each)

    $ms     = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($ms)

    # ICONDIR
    $writer.Write([uint16]0)      # reserved
    $writer.Write([uint16]1)      # type = 1 (ICO)
    $writer.Write([uint16]$count)

    # ICONDIRENTRY array
    $offset = $headerSz
    for ($i = 0; $i -lt $count; $i++) {
        $sz  = $Sizes[$i]
        $bsz = $images[$i].Length
        $w   = if ($sz -ge 256) { 0 } else { [byte]$sz }
        $h   = if ($sz -ge 256) { 0 } else { [byte]$sz }
        $writer.Write([byte]$w)
        $writer.Write([byte]$h)
        $writer.Write([byte]0)      # color count (0 = 256+)
        $writer.Write([byte]0)      # reserved
        $writer.Write([uint16]1)    # planes
        $writer.Write([uint16]32)   # bit count
        $writer.Write([uint32]$bsz)
        $writer.Write([uint32]$offset)
        $offset += $bsz
    }

    # image data blobs
    foreach ($imgBytes in $images) {
        $writer.Write($imgBytes)
    }
    $writer.Flush()

    [System.IO.File]::WriteAllBytes($IcoPath, $ms.ToArray())
    $writer.Dispose()
    $ms.Dispose()

    Write-Host "  ICO written: $IcoPath  (sizes: $($Sizes -join ', '))"
}

ConvertTo-Ico -PngPath $SourceIconPng -IcoPath $IconIco

# ---- Step 5: Read version ----------------------------------------------------
$VersionFile = Join-Path $RepoRoot "VERSION"
$Version     = "1.0.0"
if (Test-Path $VersionFile) {
    $Version = (Get-Content $VersionFile -Raw).Trim()
}

# Velopack requires clean SemVer; strip pre-release labels with hyphens
if ($Version -match '-') {
    Write-Warning "VERSION '$Version' contains a pre-release label - stripping for Velopack."
    $Version = ($Version -split '-')[0]
}

Write-Host ""
Write-Host "-- Step 5: Version = $Version ----------------------------------------"

# ---- Step 6: Run vpk pack ----------------------------------------------------
Write-Host ""
Write-Host "-- Step 6: Running vpk pack ------------------------------------------"

New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null

$vpkArgs = @(
    'pack',
    '--packId',         'BeeMemoryBank',
    '--packVersion',    $Version,
    '--packTitle',      'Bee Memory Bank',
    '--packAuthors',    'BeeMemoryBank Contributors',
    '--packDir',        $PayloadDir,
    '--mainExe',        'BeeMemoryBank.Desktop.exe',
    '--icon',           $IconIco,
    '--outputDir',      $ReleasesDir,
    '-y'
)

Write-Host "  Command: vpk $($vpkArgs -join ' ')"
Write-Host ""

vpk @vpkArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "vpk pack failed (exit code $LASTEXITCODE)."
    exit 1
}

# ---- Packaging Assert --------------------------------------------------------
$PayloadDataDir = Join-Path $PayloadDir "data"
if (Test-Path $PayloadDataDir) {
    Write-Error "Packaging Assert Failed: The mutable user data directory 'data' was found directly inside the payload folder ($PayloadDataDir). Mutable user data must not be packaged."
    exit 1
}

# ---- Step 7: Report results --------------------------------------------------
Write-Host ""
Write-Host "================================================================"
Write-Host "  PACKAGING COMPLETE"
Write-Host "================================================================"
Write-Host ""

$SetupExe = Join-Path $ReleasesDir "BeeMemoryBank-win-Setup.exe"
if (-not (Test-Path $SetupExe)) {
    # Fallback: find any *Setup.exe in the releases dir
    $SetupExe = Get-ChildItem $ReleasesDir -Filter "*Setup.exe" | Select-Object -First 1 -ExpandProperty FullName
}
if ($SetupExe -and (Test-Path $SetupExe)) {
    $szMB = [math]::Round((Get-Item $SetupExe).Length / 1MB, 1)
    Write-Host "  Setup.exe : $SetupExe  ($szMB MB)"
} else {
    Write-Warning "  Setup.exe not found in releases folder."
}

Write-Host ""
Write-Host "  Releases folder contents:"
Get-ChildItem $ReleasesDir | ForEach-Object {
    $szMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "    $($_.Name)  ($szMB MB)"
}
Write-Host ""
Write-Host "  NOTE: Package is UNSIGNED (code signing out of scope)."
Write-Host "  To install, run Setup.exe - installs per-user, no UAC required."
Write-Host ""
