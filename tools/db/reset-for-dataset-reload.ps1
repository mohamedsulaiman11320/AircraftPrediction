<#
.SYNOPSIS
  Full operational-data reset before reloading the 6-month raw dataset (user-approved scope, 2026-10-23).

.DESCRIPTION
  DESTRUCTIVE. Removes telemetry imports/records/rejected rows, the aircraft register, and the workflow
  rows derived from that telemetry (anomalies, predictions, inspection flags, work orders, maintenance logs).

  Preserves identity and authorisation, reference data (status/role/risk_status/health_threshold),
  action_audit, pipeline_run and model_version. The schema is untouched; no migration is reverted.

  Takes a verified pg_dump backup first unless -SkipBackup is given, refuses any database other than
  aircraft_predict_db, and requires an explicit confirmation unless -Yes is given.

.EXAMPLE
  .\tools\db\reset-telemetry-data.ps1
.EXAMPLE
  .\tools\db\reset-telemetry-data.ps1 -Yes -BackupDir D:\backups
#>
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$SkipBackup,
    [string]$BackupDir = (Join-Path $env:LOCALAPPDATA "AircraftFailurePrediction\backups")
)

. "$PSScriptRoot\DbCommon.ps1"
$cfg = Import-DotEnv
$dbName = Assert-TargetDatabase $cfg
$owner = Get-Setting $cfg "DB_USER" -Required
$ownerPassword = Get-Setting $cfg "DB_PASSWORD" -Required

Write-Host "Target: $owner@$(Get-Setting $cfg 'DB_HOST' 'localhost'):$(Get-Setting $cfg 'DB_PORT' '5432')/$dbName"

if (-not $SkipBackup) {
    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null }
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $dump = Join-Path $BackupDir "${dbName}_pre_dataset_reload_$stamp.dump"
    $pgDump = Join-Path (Split-Path (Get-PsqlPath $cfg) -Parent) "pg_dump.exe"
    if (-not (Test-Path $pgDump)) { throw "pg_dump.exe not found next to psql at '$pgDump'." }

    Write-Host "Backing up to $dump ..."
    $env:PGPASSWORD = $ownerPassword
    try {
        & $pgDump -h (Get-Setting $cfg "DB_HOST" "localhost") -p (Get-Setting $cfg "DB_PORT" "5432") `
                  -U $owner -d $dbName -Fc -f $dump
        if ($LASTEXITCODE -ne 0) { throw "pg_dump failed; nothing was deleted." }

        # A backup is only a backup once it has been read back.
        $pgRestore = Join-Path (Split-Path $pgDump -Parent) "pg_restore.exe"
        & $pgRestore -l $dump | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Backup at '$dump' is not readable; nothing was deleted." }
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
    Write-Host "Backup verified: $dump" -ForegroundColor Green
}

if (-not $Yes) {
    Write-Host "This deletes telemetry, imports, aircraft, models, pipeline runs AND audit history from '$dbName'." -ForegroundColor Yellow
    $answer = Read-Host "Type 'reset' to proceed"
    if ($answer -ne "reset") { Write-Host "Cancelled. Nothing was changed."; exit 1 }
}

Invoke-Psql $cfg $owner $ownerPassword $dbName @("-v", "ON_ERROR_STOP=1", "-f", (Join-Path $PSScriptRoot "reset-for-dataset-reload.sql"))
Write-Host "Operational data reset for dataset reload in '$dbName'." -ForegroundColor Green
Write-Host "Next: upload a telemetry JSON as the Data Engineer to start a clean ingestion cycle."
