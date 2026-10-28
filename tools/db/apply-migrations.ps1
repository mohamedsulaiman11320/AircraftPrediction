<#
.SYNOPSIS
  Applies the reviewed EF Core migrations to aircraft_predict_db as the schema owner, then re-applies grants.

.DESCRIPTION
  Uses DB_* from the root .env. Refuses protected databases (aircraft_app). Shows pending migrations and asks
  for confirmation unless -Yes. The EF design-time factory performs the same database guard.

.EXAMPLE
  .\tools\db\apply-migrations.ps1
#>
[CmdletBinding()]
param([switch]$Yes)

. "$PSScriptRoot\DbCommon.ps1"
$cfg = Import-DotEnv
$dbName = Assert-TargetDatabase $cfg
$owner = Get-Setting $cfg "DB_USER" -Required
$ownerPw = Get-Setting $cfg "DB_PASSWORD" -Required
$backend = Join-Path $script:RepoRoot "Backend"
$efArgs = @("--project", "src/AFP.Infrastructure", "--startup-project", "src/AFP.Api")

Push-Location $backend
try {
    dotnet tool restore | Out-Null
    Write-Host "Target: $owner@$(Get-Setting $cfg 'DB_HOST' 'localhost'):$(Get-Setting $cfg 'DB_PORT' '5432')/$dbName"
    dotnet ef migrations list @efArgs
    if ($LASTEXITCODE -ne 0) { throw "Could not list migrations (is the database provisioned and reachable?)." }
    if (-not $Yes) {
        $answer = Read-Host "Apply pending migrations to '$dbName'? (yes/no)"
        if ($answer -ne "yes") { Write-Host "Cancelled. Nothing was changed."; exit 1 }
    }
    dotnet ef database update @efArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed." }
}
finally { Pop-Location }

$roleVars = @("-v", "api_role=$(Get-Setting $cfg 'DB_APP_USER' -Required)",
              "-v", "ml_reader_role=$(Get-Setting $cfg 'ML_READER_USER' -Required)",
              "-v", "etl_writer_role=$(Get-Setting $cfg 'ETL_WRITER_USER' -Required)",
              "-v", "ingest_role=$(Get-Setting $cfg 'INGEST_USER' -Required)")
Invoke-Psql $cfg $owner $ownerPw $dbName ($roleVars + @("-f", (Join-Path $PSScriptRoot "grant-privileges.sql")))
Write-Host "Migrations and least-privilege grants applied to '$dbName'." -ForegroundColor Green
Write-Host "Next: .\tools\db\apply-seed.ps1"
