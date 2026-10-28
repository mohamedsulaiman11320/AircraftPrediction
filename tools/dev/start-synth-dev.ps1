<#
.SYNOPSIS
  Starts a DEVELOPMENT copy of the platform against the isolated synthetic database: API on :5081, web on :5174.

.DESCRIPTION
  For development acceptance scenarios only (e.g. tools/data/scenarios/phase7_health_dashboard_scenario.json).
  It runs the existing API build with process-scoped overrides - the root .env is not changed, and DotEnv never
  overrides a variable that is already set:
    DB_NAME             = aircraft_predict_db_synth
    DB_FORBIDDEN_NAMES  = aircraft_predict_db,aircraft_app   (the API refuses to start against either)
    DEV_DB_NAME         = " " (blank: the development-lab view is not nested inside this development API)
    DB_AUTO_MIGRATE     = false
  The operational API (:5080) and web app (:5173) keep running untouched. Build the API first (dotnet build).
  Logs: %LOCALAPPDATA%\AircraftFailurePrediction\logs\synth-api.log and synth-web.log. Stop with -Stop.
#>
[CmdletBinding()]
param([int]$ApiPort = 5081, [int]$WebPort = 5174, [switch]$Stop)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$logDir = Join-Path $env:LOCALAPPDATA "AircraftFailurePrediction\logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$pidFile = Join-Path $logDir "synth-dev.pids"

if ($Stop) {
    if (Test-Path $pidFile) {
        foreach ($id in Get-Content $pidFile) { try { Stop-Process -Id ([int]$id) -Force -ErrorAction Stop } catch {} }
        Remove-Item $pidFile
    }
    "Synthetic development stack stopped."
    return
}

$dll = Join-Path $root "Backend\src\AFP.Api\bin\Debug\net10.0\AFP.Api.dll"
if (-not (Test-Path $dll)) { throw "Build the API first: dotnet build (missing $dll)." }

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:$ApiPort"
$env:DB_NAME = "aircraft_predict_db_synth"
$env:DB_FORBIDDEN_NAMES = "aircraft_predict_db,aircraft_app"
$env:DEV_DB_NAME = " "
$env:DB_AUTO_MIGRATE = "false"
$env:Cors__AllowedOrigins__0 = "http://localhost:$WebPort"
$api = Start-Process -FilePath "dotnet" -ArgumentList @($dll) -WorkingDirectory (Join-Path $root "Backend\src\AFP.Api") `
    -RedirectStandardOutput (Join-Path $logDir "synth-api.log") -RedirectStandardError (Join-Path $logDir "synth-api.err.log") `
    -WindowStyle Hidden -PassThru

$env:VITE_API_BASE_URL = "http://localhost:$ApiPort"
$vite = Join-Path $root "Frontend\node_modules\vite\bin\vite.js"
$web = Start-Process -FilePath "node" -ArgumentList @($vite, "--port", $WebPort, "--strictPort") -WorkingDirectory (Join-Path $root "Frontend") `
    -RedirectStandardOutput (Join-Path $logDir "synth-web.log") -RedirectStandardError (Join-Path $logDir "synth-web.err.log") `
    -WindowStyle Hidden -PassThru

@($api.Id, $web.Id) | Set-Content $pidFile
"Synthetic development stack: API http://localhost:$ApiPort (database aircraft_predict_db_synth), web http://localhost:$WebPort"
