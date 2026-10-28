<#
.SYNOPSIS
  Registers the daily telemetry simulator in Windows Task Scheduler at 00:00 India Standard Time.

.DESCRIPTION
  - The start boundary carries an explicit +05:30 offset (Asia/Kolkata has no DST), so the schedule is midnight IST
    whatever time zone this machine is set to.
  - StartWhenAvailable: a run missed while the PC was off starts as soon as it is back (the simulator then catches up
    every missing hour).
  - MultipleInstancesPolicy IgnoreNew + the simulator's own OS lock: never two runs at once.
  - Registering does NOT run the job. Use -RunNow to start one immediately (manual/test path).
  - Runs as the current user while logged on (Docker Desktop also needs the user session); no password is stored.
  Re-running this script replaces the task definition (idempotent). Remove with -Unregister.

.EXAMPLE
  .\tools\simulator\register-daily-task.ps1
#>
[CmdletBinding()]
param([switch]$RunNow, [switch]$Unregister)
$ErrorActionPreference = "Stop"
$taskName = "AFP Daily Telemetry Simulator"
$runner = Join-Path $PSScriptRoot "run-daily.ps1"

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed scheduled task '$taskName'."
    return
}

# Next midnight in IST, written with an explicit offset.
$ist = [TimeZoneInfo]::FindSystemTimeZoneById("India Standard Time")
$nowIst = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $ist)
$start = $nowIst.Date.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00+05:30"
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Aircraft Failure Prediction: extend the rolling raw telemetry file at 00:00 IST, publish it through the existing ETL, then score. See tools\simulator\run-daily.ps1.</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>$start</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <StartWhenAvailable>true</StartWhenAvailable>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$runner"</Arguments>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $taskName -Xml $xml -Force | Out-Null
$task = Get-ScheduledTask -TaskName $taskName
Write-Host "Registered '$taskName': daily at 00:00 IST, first run $start (catch-up on missed runs, single instance)."
Write-Host "Next run time: $((Get-ScheduledTaskInfo -TaskName $taskName).NextRunTime)"
if ($RunNow) { Start-ScheduledTask -TaskName $taskName; Write-Host "Started one run now (manual)." }
