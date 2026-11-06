# Friend-facing DS4 DualTouch installer (script form; see INSTALLER.md).
# Copies a DS4 publish folder to C:\Program Files\DS4Windows, registers
# the Scheduled Task "DS4Windows", and starts DS4 ONLY via that task.
# It does NOT install Python or pip packages; DS4 itself prompts for
# the runtime on first start (Auto-install with DS4, or manual).
param(
    [string]$Source = (Split-Path -Parent $PSScriptRoot),
    [string]$Dest = 'C:\Program Files\DS4Windows'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Source 'DS4Windows.exe'))) {
    throw "Source folder has no DS4Windows.exe: $Source"
}
if (-not (Test-Path (Join-Path $Source 'DualTouchKeyboard\ds4_keyboard.py'))) {
    throw 'Source folder is missing DualTouchKeyboard\ds4_keyboard.py'
}
# Stop DS4 via the task / process, copy, then restart via the task only.
Stop-Process -Name 'DS4Windows' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if (-not (Test-Path $Dest)) { New-Item $Dest -ItemType Directory -Force | Out-Null }
Copy-Item (Join-Path $Source '*') $Dest -Recurse -Force
$exe = Join-Path $Dest 'DS4Windows.exe'
$task = Get-ScheduledTask -TaskName 'DS4Windows' -ErrorAction SilentlyContinue
if ($null -eq $task) {
    $action = New-ScheduledTaskAction -Execute $exe
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName 'DS4Windows' -Action $action -Trigger $trigger -Settings $settings -RunLevel Highest | Out-Null
} else {
    Set-ScheduledTask -TaskName 'DS4Windows' -Action (New-ScheduledTaskAction -Execute $exe) | Out-Null
}
Start-ScheduledTask -TaskName 'DS4Windows'
Write-Output "Installed DS4 to $Dest and started it via Scheduled Task DS4Windows."
Write-Output 'On first start, DS4 will ask whether to auto-install the Python DualTouch runtime or let you install it yourself.'
