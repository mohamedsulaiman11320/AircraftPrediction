<#
.SYNOPSIS
  Creates the dedicated roles and the aircraft_predict_db database from the root .env (non-destructive).

.DESCRIPTION
  1. Verifies DB_NAME is not protected (aircraft_app is always refused) and the server is reachable.
  2. If the database already exists it is inspected and the script STOPS (pass -AllowExisting to only
     ensure roles/privileges on it). Nothing is ever dropped or renamed.
  3. Creates missing roles with the passwords from .env (existing roles keep their passwords unless -ResetPasswords).
  4. Creates the database only when DB_AUTO_CREATE=true.
  The superuser password is prompted (or taken from PGPASSWORD) and never stored or printed.

.EXAMPLE
  .\tools\db\provision-database.ps1
#>
[CmdletBinding()]
param([switch]$AllowExisting, [switch]$ResetPasswords)

. "$PSScriptRoot\DbCommon.ps1"
$cfg = Import-DotEnv
$dbName = Assert-TargetDatabase $cfg
$superUser = Get-Setting $cfg "DB_SUPERUSER" "postgres"
$roles = [ordered]@{
    owner_role      = Get-Setting $cfg "DB_USER" -Required
    api_role        = Get-Setting $cfg "DB_APP_USER" -Required
    ml_reader_role  = Get-Setting $cfg "ML_READER_USER" -Required
    etl_writer_role = Get-Setting $cfg "ETL_WRITER_USER" -Required
    ingest_role     = Get-Setting $cfg "INGEST_USER" -Required
}
$passwords = [ordered]@{
    owner_pw      = Get-Setting $cfg "DB_PASSWORD" -Required
    api_pw        = Get-Setting $cfg "API_USER_PASSWORD" -Required
    ml_reader_pw  = Get-Setting $cfg "ML_READER_PASSWORD" -Required
    etl_writer_pw = Get-Setting $cfg "ETL_WRITER_PASSWORD" -Required
    ingest_pw     = Get-Setting $cfg "INGEST_PASSWORD" -Required
}
if (@($roles.Values | Select-Object -Unique).Count -ne 5) { throw "DB_USER, DB_APP_USER, ML_READER_USER, ETL_WRITER_USER and INGEST_USER must be five different roles." }
foreach ($k in "api_role", "ml_reader_role", "etl_writer_role", "ingest_role") {
    if ($roles[$k] -eq $superUser) { throw "The API/ML roles must not be the superuser '$superUser'." }
}
$ownerIsSuperuser = $roles["owner_role"] -eq $superUser
if ($ownerIsSuperuser) { Write-Host "Note: DB_USER is the superuser '$superUser'; it will own the schema (only the API/ML roles are created)." -ForegroundColor Yellow }

$superPw = if ($ownerIsSuperuser) { $passwords["owner_pw"] }
           elseif ($env:PGPASSWORD) { $env:PGPASSWORD }
           else { Read-SecretPrompt "Password for PostgreSQL superuser '$superUser'" }

Write-Host "Target: $dbName on $(Get-Setting $cfg 'DB_HOST' 'localhost'):$(Get-Setting $cfg 'DB_PORT' '5432')"
Invoke-Psql $cfg $superUser $superPw "postgres" @("-t", "-c", "SELECT 'server reachable: ' || version();")

$exists = (& {
    $psql = Get-PsqlPath $cfg; $previous = $env:PGPASSWORD
    try { $env:PGPASSWORD = $superPw
          & $psql -h (Get-Setting $cfg "DB_HOST" "localhost") -p (Get-Setting $cfg "DB_PORT" "5432") -U $superUser -d postgres -X -t -A `
              -c "SELECT count(*) FROM pg_database WHERE datname = '$dbName';" }   # DB_NAME is regex-validated
    finally { $env:PGPASSWORD = $previous }
}) -eq "1"

if ($exists) {
    Write-Host "Database '$dbName' already exists. Inspecting (read-only):" -ForegroundColor Yellow
    Invoke-Psql $cfg $superUser $superPw $dbName @("-c", "SELECT current_database() AS database, count(*) AS tables FROM information_schema.tables WHERE table_schema = 'public';")
    if (-not $AllowExisting) {
        Write-Host "Stopping: nothing was changed. Re-run with -AllowExisting to only ensure roles and privileges." -ForegroundColor Yellow
        exit 0
    }
}
$createDb = if ((Get-Setting $cfg "DB_AUTO_CREATE" "false") -eq "true") { "true" } else { "false" }
if (-not $exists -and $createDb -ne "true") {
    Write-Host "Database '$dbName' does not exist and DB_AUTO_CREATE is not true. Set DB_AUTO_CREATE=true in .env to create it." -ForegroundColor Yellow
    exit 1
}

$vars = @("-v", "db_name=$dbName", "-v", "create_db=$createDb", "-v", "reset_pw=$(if ($ResetPasswords) { 'true' } else { 'false' })")
foreach ($k in $roles.Keys) { $vars += @("-v", "$k=$($roles[$k])") }
foreach ($k in $passwords.Keys) { $vars += @("-v", "$k=$($passwords[$k])") }
Invoke-Psql $cfg $superUser $superPw "postgres" ($vars + @("-f", (Join-Path $PSScriptRoot "provision.sql")))

Write-Host "Provisioned '$dbName' with roles $($roles.Values -join ', ')." -ForegroundColor Green
Write-Host "Next: .\tools\db\apply-migrations.ps1   then   .\tools\db\apply-seed.ps1"
