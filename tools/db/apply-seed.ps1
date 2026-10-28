<#
.SYNOPSIS
  Explicitly applies the generated reference-data seed (tools/db/seed/seed.sql) to aircraft_predict_db.

.DESCRIPTION
  1. Verifies seed.sql is up to date with the CSV sources (AFP.SeedSql check); refuses stale artifacts.
  2. Refuses protected databases; the SQL itself also checks current_database() inside its transaction.
  3. Runs as the schema owner in a single transaction. Idempotent upserts; nothing is ever deleted.

.EXAMPLE
  .\tools\db\apply-seed.ps1
#>
[CmdletBinding()]
param()

. "$PSScriptRoot\DbCommon.ps1"
$cfg = Import-DotEnv
$dbName = Assert-TargetDatabase $cfg
$seedRoot = Join-Path $PSScriptRoot "seed"
$manifest = Get-Content (Join-Path $seedRoot "datasets.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.targetDatabase -ne $dbName) { throw "seed targetDatabase '$($manifest.targetDatabase)' does not match DB_NAME '$dbName'." }

dotnet run --project (Join-Path $script:RepoRoot "Backend\tools\AFP.SeedSql") -- check --root $seedRoot
if ($LASTEXITCODE -ne 0) { throw "seed.sql is stale or the CSVs are invalid. Fix the CSVs, run 'generate', review, then retry." }

Invoke-Psql $cfg (Get-Setting $cfg "DB_USER" -Required) (Get-Setting $cfg "DB_PASSWORD" -Required) $dbName @("-f", (Join-Path $seedRoot $manifest.output))
Write-Host "Reference seed applied to '$dbName'." -ForegroundColor Green
