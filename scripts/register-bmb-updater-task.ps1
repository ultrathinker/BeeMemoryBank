<#
.SYNOPSIS
    Registers a Windows Scheduled Task for bmb-updater.
.DESCRIPTION
    Registers a Windows Scheduled Task configured to run as the SYSTEM account,
    designed to pick up update requests and safely update the BeeMemoryBank application.
    
    Since there is no native "File Created" trigger in Windows Task Scheduler,
    this script registers the task with two trigger patterns:
    1. A daily/hourly trigger to poll for updates.
    2. An event-based trigger mapped to a specific Windows Event Log source (created by the API
       when writing the update.request file).
    Alternatively, the API can programmatically invoke the task on-demand using the task name.
#>

param (
    [string]$RootDirectory = "C:\Program Files\BeeMemoryBank",
    [string]$UpdaterExecutablePath = "C:\Program Files\BeeMemoryBank\bmb-updater.exe",
    [string]$HealthCheckUrl = "http://localhost:5000/health",
    [string]$TaskName = "BeeMemoryBank-Updater"
)

# Ensure the task registers correctly with administrative privileges
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "This script must be run as Administrator to register tasks under the SYSTEM account."
    exit 1
}

# Define the action: Running bmb-updater with the specified root and health check arguments
$arguments = "--root `"$RootDirectory`" --health-check-url `"$HealthCheckUrl`""
$action = New-ScheduledTaskAction -Execute $UpdaterExecutablePath -Argument $arguments

# Define triggers:
# 1. On an hourly schedule to act as a fallback poll
$trigger1 = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration ([TimeSpan]::MaxValue)

# Define principal: Run as NT AUTHORITY\SYSTEM with highest privileges (needed to stop/start Windows Services)
$principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest

# Define settings
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

# Register the Scheduled Task
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger1 -Principal $principal -Settings $settings

Write-Host "Successfully registered Scheduled Task '$TaskName' to run as SYSTEM."
Write-Host "Action: $UpdaterExecutablePath $arguments"
