[CmdletBinding()]
param (
    [string]$OutputPath = "publish/win-x64"
)

# 1. Locate repository root
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
if (-not $RepoRoot) {
    Write-Error "Could not resolve repository root directory."
    exit 1
}
$RepoRoot = $RepoRoot.Path

$PublishDir = Join-Path $RepoRoot $OutputPath

Write-Host "Repository root: $RepoRoot"
Write-Host "Publish directory: $PublishDir"

# 2. Idempotency: clean up existing publish directory
if (Test-Path $PublishDir) {
    Write-Host "Cleaning existing publish directory: $PublishDir..."
    try {
        Remove-Item -Recurse -Force $PublishDir -ErrorAction Stop
    } catch {
        Write-Warning "Could not clean all files in $($PublishDir): $($_.Exception.Message). Retrying after 1s..."
        Start-Sleep -Seconds 1
        Remove-Item -Recurse -Force $PublishDir -ErrorAction Stop
    }
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

# 3. Publish configuration list
$Projects = @(
    @{ Path = "desktop\BeeMemoryBank.Node\BeeMemoryBank.Node.csproj"; Out = "bmbd" },
    @{ Path = "server\BeeMemoryBank.Api\BeeMemoryBank.Api.csproj"; Out = "api" },
    @{ Path = "server\BeeMemoryBank.Web\BeeMemoryBank.Web.csproj"; Out = "web" },
    @{ Path = "server\BeeMemoryBank.Cli\BeeMemoryBank.Cli.csproj"; Out = "cli" }
)

# 4. Build/Publish each project
foreach ($Proj in $Projects) {
    $ProjPath = Join-Path $RepoRoot $Proj.Path
    $OutPath = Join-Path $PublishDir $Proj.Out
    
    if (-not (Test-Path $ProjPath)) {
        Write-Error "Project file not found: $ProjPath"
        exit 1
    }
    
    Write-Host "------------------------------------------------------------"
    Write-Host "Publishing $ProjPath"
    Write-Host "Destination: $OutPath"
    Write-Host "------------------------------------------------------------"
    
    dotnet publish $ProjPath -c Release -r win-x64 --self-contained true -o $OutPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish $ProjPath"
        exit 1
    }
}

# 5. Read VERSION file
$VersionFile = Join-Path $RepoRoot "VERSION"
$Version = "1.0.0-placeholder"
if (Test-Path $VersionFile) {
    $Version = (Get-Content $VersionFile -Raw).Trim()
}
Write-Host "Version: $Version"

# 6. Read Git Commit SHA
$CommitSha = "unknown"
try {
    # Run git command, suppress error stream if not inside a git repo or git is not installed
    $CommitSha = (git rev-parse HEAD 2>$null).Trim()
} catch {
    # Ignore and keep placeholder
}
if (-not $CommitSha) {
    $CommitSha = "unknown"
}
Write-Host "Commit SHA: $CommitSha"

# 7. Get Timestamp (UTC, ISO 8601)
$Timestamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
Write-Host "Timestamp: $Timestamp"

# 8. List key executable artifacts and calculate their SHA-256 hashes
$Artifacts = @()
$Executables = @(
    @{ Name = "bmbd/BeeMemoryBank.Node.exe"; File = "bmbd\BeeMemoryBank.Node.exe" },
    @{ Name = "api/BeeMemoryBank.Api.exe"; File = "api\BeeMemoryBank.Api.exe" },
    @{ Name = "web/BeeMemoryBank.Web.exe"; File = "web\BeeMemoryBank.Web.exe" },
    @{ Name = "cli/bmb.exe"; File = "cli\bmb.exe" }
)

foreach ($Exe in $Executables) {
    $ExePath = Join-Path $PublishDir $Exe.File
    if (Test-Path $ExePath) {
        Write-Host "Calculating hash for: $($Exe.File)..."
        $HashInfo = Get-FileHash -Path $ExePath -Algorithm SHA256
        $Artifacts += @{
            name = $Exe.Name
            sha256 = $HashInfo.Hash.ToLowerInvariant()
        }
    } else {
        Write-Warning "Expected executable artifact not found: $ExePath"
    }
}

# 9. Construct release manifest
$Manifest = [Ordered]@{
    version = $Version
    commit = $CommitSha
    timestamp = $Timestamp
    artifacts = $Artifacts
}

$ManifestJson = $Manifest | ConvertTo-Json -Depth 5
$ManifestPath = Join-Path $PublishDir "release-manifest.json"

Write-Host "Writing release manifest to $ManifestPath..."
[System.IO.File]::WriteAllText($ManifestPath, $ManifestJson, [System.Text.Encoding]::UTF8)

Write-Host "Publish complete! Output located at $PublishDir"
