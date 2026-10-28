<#
.SYNOPSIS
  The daily job: telemetry simulation (+ ETL confirmation), scoring, sensor forecasting, then anomaly processing -
  four separately recorded stages.

.DESCRIPTION
  Stage 1  python -m afp_ml simulate          -> pipeline_run SimulateTelemetry (succeeds only when PostgreSQL has the data)
  Stage 2  python -m afp_ml score             -> pipeline_run Score            (only if stage 1 succeeded)
  Stage 3  python -m afp_ml forecast          -> pipeline_run ForecastSensors (only if stage 1 succeeded; writes
                                                 forecasts with the ACTIVE SensorForecast model only, else records that
                                                 nothing was forecast)
  Stage 4  AFP.Api process-anomalies          -> pipeline_run AnomalyProcessing (only if stage 1 succeeded; runs even if
                                                 scoring failed - threshold incidents come from the published telemetry)
  A failed stage never hides behind a later one, and nothing downstream runs on a day whose data did not publish, so the
  last successful assessment stays in place. Exit code: the simulate exit code if it failed, else
  max(score, forecast, anomalies).
  Output goes to %LOCALAPPDATA%\AircraftFailurePrediction\logs.
  Invoked by the scheduled task (register-daily-task.ps1); safe to run by hand.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$python = Join-Path $root "ml\.venv\Scripts\python.exe"
$logDir = Join-Path $env:LOCALAPPDATA "AircraftFailurePrediction\logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("daily-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

function Write-Log([string[]]$lines) {
    # An antivirus/indexer scan of the just-written log holds it briefly (sharing violation). Retry; if the log stays
    # locked, warn and carry on - a logging failure must never abort a stage or change the run's outcome (every stage
    # also records its own pipeline_run row in PostgreSQL).
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try { [IO.File]::AppendAllLines($log, [string[]]$lines); return }
        catch [IO.IOException] { Start-Sleep -Milliseconds (100 * $attempt) }
    }
    Write-Warning ("could not write to {0}: {1}" -f $log, ($lines -join ' | '))
}

function Write-Event([string]$text) { Write-Log ("[{0:u}] {1}" -f (Get-Date).ToUniversalTime(), $text) }

function Invoke-Stage([string]$name, [string]$exe, [string[]]$arguments, [string]$workingDirectory) {
    Write-Event ("{0}: {1} {2}" -f $name, (Split-Path -Leaf $exe), ($arguments -join ' '))
    # Start-Process with redirected streams: Python logs to stderr, which Windows PowerShell 5.1 would otherwise turn
    # into a terminating NativeCommandError. The exit code is the process's own.
    $out = [IO.Path]::GetTempFileName(); $err = [IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
            -WorkingDirectory $workingDirectory -RedirectStandardOutput $out -RedirectStandardError $err
        $code = $proc.ExitCode
        Write-Log (@(Get-Content $err) + @(Get-Content $out))
    } finally { Remove-Item $out, $err -ErrorAction SilentlyContinue }
    Write-Event ("{0}: exit {1}" -f $name, $code)
    return $code
}

try {
    $mlDir = Join-Path $root "ml"
    $sim = Invoke-Stage "simulate" $python @("-m", "afp_ml", "simulate") $mlDir
    if ($sim -ne 0) {
        Write-Event "scoring, forecasting and anomaly processing skipped: the simulation/ETL stage did not succeed; the last published assessment is kept."
        exit $sim
    }
    $score = Invoke-Stage "score" $python @("-m", "afp_ml", "score") $mlDir
    $forecast = Invoke-Stage "forecast" $python @("-m", "afp_ml", "forecast") $mlDir

    # Stage 4: anomalies from the stored forecast (approved BR-01 limits) / validated assessments, their work orders and the maintenance-status sync (idempotent).
    $cfg = Get-Content (Join-Path $PSScriptRoot "telemetry_simulator.json") -Raw | ConvertFrom-Json
    $dll = Join-Path $root $cfg.etl.api_dll
    $anomalies = Invoke-Stage "process-anomalies" "dotnet" @($dll, "process-anomalies", "--run-as", $cfg.etl.run_as) (Join-Path $root "Backend")
    exit ([Math]::Max([Math]::Max($score, $forecast), $anomalies))
} catch {
    # The wrapper itself failed (not a stage): record why, and never report success.
    Write-Event ("wrapper error: {0}" -f $_)
    exit 1
}
