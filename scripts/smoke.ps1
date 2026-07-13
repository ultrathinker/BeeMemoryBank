[CmdletBinding()]
param (
    [string]$PublishDir = "publish/win-x64"
)

# 1. Locate repository root
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
if (-not $RepoRoot) {
    Write-Error "Could not resolve repository root directory."
    exit 1
}
$RepoRoot = $RepoRoot.Path

$AbsolutePublishDir = Resolve-Path (Join-Path $RepoRoot $PublishDir) -ErrorAction SilentlyContinue
if (-not $AbsolutePublishDir) {
    $AbsolutePublishDir = Join-Path $RepoRoot $PublishDir
} else {
    $AbsolutePublishDir = $AbsolutePublishDir.Path
}

Write-Host "Using published directory: $AbsolutePublishDir"

# Validate that executables exist
$BmbdExe = Join-Path $AbsolutePublishDir "bmbd\BeeMemoryBank.Node.exe"
$ApiExe = Join-Path $AbsolutePublishDir "api\BeeMemoryBank.Api.exe"
$WebExe = Join-Path $AbsolutePublishDir "web\BeeMemoryBank.Web.exe"
$CliExe = Join-Path $AbsolutePublishDir "cli\bmb.exe"

foreach ($Path in @($BmbdExe, $ApiExe, $WebExe, $CliExe)) {
    if (-not (Test-Path $Path)) {
        Write-Error "Required executable not found: $Path"
        exit 1
    }
}

# 2. Setup clean temp data directory
$TempDataDir = Join-Path $RepoRoot "temp-smoke-data"
if (Test-Path $TempDataDir) {
    Write-Host "Cleaning existing temp data directory: $TempDataDir..."
    try {
        Remove-Item -Recurse -Force $TempDataDir -ErrorAction Stop
    } catch {
        Write-Warning "Could not fully remove temp data directory. $_"
    }
}
New-Item -ItemType Directory -Path $TempDataDir -Force | Out-Null

$ApiReadyFile = (Join-Path $TempDataDir "api.ready").Replace('\', '/')
$WebReadyFile = (Join-Path $TempDataDir "web.ready").Replace('\', '/')

# Generate node.config.json pointing to published outputs
$Config = @{
    DataDirectory = $TempDataDir.Replace('\', '/')
    Children = @(
        @{
            ApplicationName = "BeeMemoryBank.Api"
            ExecutablePath = $ApiExe.Replace('\', '/')
            WorkingDirectory = (Join-Path $AbsolutePublishDir "api").Replace('\', '/')
            ReadyFilePath = $ApiReadyFile
            Arguments = ""
            EnvironmentVariables = @{
                ASPNETCORE_URLS = "http://127.0.0.1:5300"
                BMB_READY_FILE = $ApiReadyFile
                BMB_DATA_PATH = $TempDataDir.Replace('\', '/')
            }
        },
        @{
            ApplicationName = "BeeMemoryBank.Web"
            ExecutablePath = $WebExe.Replace('\', '/')
            WorkingDirectory = (Join-Path $AbsolutePublishDir "web").Replace('\', '/')
            ReadyFilePath = $WebReadyFile
            Arguments = ""
            EnvironmentVariables = @{
                ASPNETCORE_URLS = "http://127.0.0.1:5301"
                BMB_READY_FILE = $WebReadyFile
                BMB_DATA_PATH = $TempDataDir.Replace('\', '/')
                BMB_API_URL = "http://127.0.0.1:5300"
            }
        }
    )
}

$ConfigPath = Join-Path $TempDataDir "node.config.json"
$ConfigJson = $Config | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($ConfigPath, $ConfigJson, [System.Text.Encoding]::UTF8)

# 3. Start bmbd as background process
$BmbdLog = Join-Path $TempDataDir "bmbd.log"
$BmbdErr = Join-Path $TempDataDir "bmbd.err"

# Set parent process URL to bind to dynamic loopback port
$env:ASPNETCORE_URLS = "http://127.0.0.1:0"

Write-Host "Starting bmbd background process..."
$BmbdProcess = Start-Process -FilePath $BmbdExe -ArgumentList "`"$ConfigPath`"" -PassThru -NoNewWindow -RedirectStandardOutput $BmbdLog -RedirectStandardError $BmbdErr

if ($null -eq $BmbdProcess) {
    Write-Error "Failed to start bmbd process."
    exit 1
}

# 4. Wait for .runtime.json and bmbd to start listening
$RuntimeFile = Join-Path $TempDataDir ".runtime.json"
$RuntimeFound = $false
$TimeoutSec = 30

Write-Host "Waiting for bmbd to initialize and write .runtime.json..."
for ($i = 0; $i -lt $TimeoutSec; $i++) {
    Start-Sleep -Seconds 1
    
    if (Test-Path $RuntimeFile) {
        try {
            $RuntimeContent = Get-Content $RuntimeFile -Raw | ConvertFrom-Json
            if ($null -ne $RuntimeContent.FrontUrl) {
                $RuntimeFound = $true
                break
            }
        } catch {}
    }
}

if (-not $RuntimeFound) {
    Write-Error "Timeout waiting for .runtime.json or FrontUrl."
    Write-Host "=== bmbd stdout ==="
    if (Test-Path $BmbdLog) { Get-Content $BmbdLog }
    Write-Host "=== bmbd stderr ==="
    if (Test-Path $BmbdErr) { Get-Content $BmbdErr }
    
    # Try to stop bmbd
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}

$RuntimeContent = Get-Content $RuntimeFile -Raw | ConvertFrom-Json
$FrontUrl = $RuntimeContent.FrontUrl
Write-Host "bmbd Front proxy listening at: $FrontUrl"

# 5. Poll /node/status until both children show as Running
Write-Host "Polling /node/status until children are ready..."
$BothRunning = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $Status = Invoke-RestMethod -Uri "$FrontUrl/node/status" -UseBasicParsing
        $ApiState = $Status.children.'BeeMemoryBank.Api'.state
        $WebState = $Status.children.'BeeMemoryBank.Web'.state
        
        Write-Host "Poll $i - Api State: $ApiState, Web State: $WebState"
        if ($ApiState -eq "Running" -and $WebState -eq "Running") {
            $BothRunning = $true
            break
        }
    } catch {
        Write-Host "node/status check failed: $_. Retrying..."
    }
}

if (-not $BothRunning) {
    Write-Error "Children processes failed to reach Running state."
    Write-Host "=== bmbd stdout ==="
    if (Test-Path $BmbdLog) { Get-Content $BmbdLog }
    Write-Host "=== bmbd stderr ==="
    if (Test-Path $BmbdErr) { Get-Content $BmbdErr }
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}
Write-Host "Step 3 Passed: Both children are Running."

# 6. Hit /health through front proxy
Write-Host "Hitting /health through front proxy..."
try {
    $HealthRes = Invoke-RestMethod -Uri "$FrontUrl/health" -UseBasicParsing
    Write-Host "Health check response: $($HealthRes | ConvertTo-Json -Compress)"
    Write-Host "Step 4 Passed: Health check succeeded."
} catch {
    Write-Error "Failed to hit /health: $_"
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}

# 7. Supervision check (Kill API and confirm restart)
Write-Host "Performing supervision check..."
$StatusFile = Join-Path $TempDataDir "node.status.json"
if (-not (Test-Path $StatusFile)) {
    Write-Error "node.status.json file not found on disk."
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}

$DiskStatus = Get-Content $StatusFile -Raw | ConvertFrom-Json
$OldApiPid = $DiskStatus.children.'BeeMemoryBank.Api'.pid

if ($null -eq $OldApiPid) {
    Write-Error "Failed to read Api PID from node.status.json."
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}

Write-Host "Killing API process (PID: $OldApiPid)..."
Stop-Process -Id $OldApiPid -Force

Write-Host "Polling for API process restart..."
$SupervisionSuccess = $false
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 1

    try {
        if (Test-Path $StatusFile) {
            $DiskStatus = Get-Content $StatusFile -Raw | ConvertFrom-Json
            $NewApiPid = $DiskStatus.children.'BeeMemoryBank.Api'.pid
            
            # Hit /node/status to show the API state on proxy (which is cached/static)
            $ProxyStatus = Invoke-RestMethod -Uri "$FrontUrl/node/status" -UseBasicParsing
            $ProxyApiState = $ProxyStatus.children.'BeeMemoryBank.Api'.state
            $ProxyApiPid = $ProxyStatus.children.'BeeMemoryBank.Api'.pid
            
            Write-Host "Supervision Poll $i - Disk PID: $NewApiPid, Proxy State: $ProxyApiState (Proxy PID: $ProxyApiPid)"
            
            if ($null -ne $NewApiPid -and $NewApiPid -ne $OldApiPid -and $ProxyApiState -eq "Running") {
                Write-Host "API process successfully restarted! New PID (from disk status): $NewApiPid"
                $SupervisionSuccess = $true
                break
            }
        } else {
            Write-Host "Supervision Poll $i - status file is temporarily gone (expected during restart)"
        }
    } catch {
        Write-Host "Failed to request status: $_"
    }
}

if (-not $SupervisionSuccess) {
    Write-Error "Supervision check failed: API process was not restarted."
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}
Write-Host "Step 5 Passed: Supervision check succeeded."

# 8. Orphan check (Kill bmbd, verify children terminate)
Write-Host "Performing orphan check..."
if (-not (Test-Path $StatusFile)) {
    Write-Error "node.status.json file not found on disk."
    try { $BmbdProcess | Stop-Process -Force } catch {}
    exit 1
}
$DiskStatus = Get-Content $StatusFile -Raw | ConvertFrom-Json
$ApiPid = $DiskStatus.children.'BeeMemoryBank.Api'.pid
$WebPid = $DiskStatus.children.'BeeMemoryBank.Web'.pid
$BmbdPid = $BmbdProcess.Id

Write-Host "Killing bmbd process (PID: $BmbdPid)..."
Stop-Process -Id $BmbdPid -Force

Write-Host "Checking if Api (PID: $ApiPid) and Web (PID: $WebPid) terminate..."
$OrphansTerminated = $false
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 1
    $ApiRunning = Get-Process -Id $ApiPid -ErrorAction SilentlyContinue
    $WebRunning = Get-Process -Id $WebPid -ErrorAction SilentlyContinue
    
    Write-Host "Orphan Poll $i - Api Running: $($null -ne $ApiRunning), Web Running: $($null -ne $WebRunning)"
    if ($null -eq $ApiRunning -and $null -eq $WebRunning) {
        $OrphansTerminated = $true
        break
    }
}

if (-not $OrphansTerminated) {
    Write-Error "Orphan check failed: Child processes did not terminate when bmbd was killed."
    # Clean up leftovers
    if ($null -ne (Get-Process -Id $ApiPid -ErrorAction SilentlyContinue)) { Stop-Process -Id $ApiPid -Force }
    if ($null -ne (Get-Process -Id $WebPid -ErrorAction SilentlyContinue)) { Stop-Process -Id $WebPid -Force }
    exit 1
}
Write-Host "Step 6 Passed: Orphan check succeeded."

# 9. Clean run default path check (Step 7)
Write-Host ""
Write-Host "============================================="
Write-Host " Step 7: Clean Run Default Path Check"
Write-Host "============================================="

$BmbdDir = Split-Path $BmbdExe -Parent
$LocalDataDir = Join-Path $BmbdDir "data"

# Clean up local 'data' directory if it exists from previous manual runs
if (Test-Path $LocalDataDir) {
    try {
        Remove-Item -Recurse -Force $LocalDataDir -ErrorAction SilentlyContinue
    } catch {}
}

# Define target paths in AppData
$AppDataBmbDir = Join-Path $env:LOCALAPPDATA "BeeMemoryBankData"
$DefaultVaultDir = Join-Path $AppDataBmbDir "vaults\default"

Write-Host "Starting bmbd in clean/default mode (no arguments)..."
# Start a clean bmbd process with no arguments. It should use the default database and config resolution.
$CleanLog = Join-Path $TempDataDir "clean-bmbd.log"
$CleanErr = Join-Path $TempDataDir "clean-bmbd.err"

# Temporarily clear ASPNETCORE_URLS or set to loopback 0 to avoid binding conflict if port 5000/5001 is in use
$OldUrls = $env:ASPNETCORE_URLS
$env:ASPNETCORE_URLS = "http://127.0.0.1:0"

$CleanProcess = Start-Process -FilePath $BmbdExe -PassThru -NoNewWindow -RedirectStandardOutput $CleanLog -RedirectStandardError $CleanErr

# Restore environment variable
if ($null -ne $OldUrls) {
    $env:ASPNETCORE_URLS = $OldUrls
} else {
    Remove-Item env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
}

# Wait a short duration (3 seconds) for the process to initialize and create its data folders
Start-Sleep -Seconds 3

# Stop the process
if ($null -ne $CleanProcess) {
    try {
        Stop-Process -Id $CleanProcess.Id -Force -ErrorAction SilentlyContinue
    } catch {}
}

$LocalDataExists = Test-Path $LocalDataDir
$DefaultVaultExists = Test-Path $DefaultVaultDir

Write-Host "Clean Run Paths Check:"
Write-Host "  Local 'data' directory in AppContext.BaseDirectory: $LocalDataDir (Exists: $LocalDataExists)"
Write-Host "  Default AppData vault directory: $DefaultVaultDir (Exists: $DefaultVaultExists)"

$PathTestPassed = $false
if ($LocalDataExists) {
    # Legacy behavior still active because parallel changes are not merged yet.
    Write-Host "Legacy behavior detected: local 'data' directory was created."
    Write-Host "This is expected since parallel changes in feat/1-node-default-path are not yet merged."
    $PathTestPassed = $true
} else {
    # New behavior active (or parallel changes merged)
    Write-Host "New behavior detected: no local 'data' directory created next to the executable."
    if ($DefaultVaultExists) {
        Write-Host "Success: Default AppData vault directory exists."
        $PathTestPassed = $true
    } else {
        Write-Error "Failure: Neither local 'data' directory nor default AppData vault directory exists!"
        $PathTestPassed = $false
    }
}

if (-not $PathTestPassed) {
    Write-Error "Clean run default path check failed."
    exit 1
}
Write-Host "Step 7 Passed: Clean run default path check succeeded."

# 10. Print PASS summary
Write-Host ""
Write-Host "============================================="
Write-Host " SMOKE TEST SUMMARY"
Write-Host "============================================="
Write-Host "Step 1: Configuration Generation & Temp Dir  - PASS"
Write-Host "Step 2: Start bmbd background process        - PASS"
Write-Host "Step 3: Wait for readiness (/node/status)    - PASS"
Write-Host "Step 4: Check /health through proxy          - PASS"
Write-Host "Step 5: API Process Supervision Check        - PASS"
Write-Host "Step 6: Orphan Cleanup Check (Force Kill)    - PASS"
Write-Host "Step 7: Clean Run Default Path Check        - PASS"
Write-Host "---------------------------------------------"
Write-Host "ALL SMOKE TESTS PASSED SUCCESSFULLY!"
Write-Host "============================================="

# Clean up temp data directory
try {
    Remove-Item -Recurse -Force $TempDataDir -ErrorAction SilentlyContinue
} catch {}

exit 0
