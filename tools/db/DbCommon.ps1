# Shared helpers for the database scripts. Dot-source: . "$PSScriptRoot\DbCommon.ps1"
# Reads the repository-root .env (git-ignored). Never prints passwords or full connection strings.
$ErrorActionPreference = "Stop"
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:ProtectedDatabases = @("aircraft_app")   # must never be provisioned, migrated or seeded by these scripts

function Import-DotEnv {
    param([string]$Path = (Join-Path $script:RepoRoot ".env"))
    if (-not (Test-Path $Path)) { throw "Missing $Path. Copy .env.example to .env and fill it in (see README, 'Configuration')." }
    $values = @{}
    foreach ($raw in [IO.File]::ReadAllLines($Path)) {
        $line = $raw.Trim()
        if ($line -eq "" -or $line.StartsWith("#")) { continue }
        if ($line.StartsWith("export ")) { $line = $line.Substring(7).TrimStart() }
        $eq = $line.IndexOf("=")
        if ($eq -le 0) { continue }
        $key = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and ($value[0] -eq '"' -or $value[0] -eq "'") -and $value[-1] -eq $value[0]) { $value = $value.Substring(1, $value.Length - 2) }
        elseif ($value.Contains(" #")) { $value = $value.Substring(0, $value.IndexOf(" #")).TrimEnd() }
        # Real environment variables keep precedence over the file.
        $existing = [Environment]::GetEnvironmentVariable($key)
        $values[$key] = if ($existing) { $existing } else { $value }
    }
    return $values
}

function Get-Setting {
    param([hashtable]$Env, [string]$Key, [string]$Default = $null, [switch]$Required)
    $value = $Env[$Key]
    if ([string]::IsNullOrWhiteSpace($value)) { $value = $Default }
    if ($Required -and [string]::IsNullOrWhiteSpace($value)) { throw "$Key is not set in .env." }
    return $value
}

function Assert-TargetDatabase {
    param([hashtable]$Env)
    $name = Get-Setting $Env "DB_NAME" "aircraft_predict_db"
    $forbidden = @($script:ProtectedDatabases) + @((Get-Setting $Env "DB_FORBIDDEN_NAMES" "") -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($forbidden -contains $name.ToLowerInvariant() -or ($forbidden | Where-Object { $_ -ieq $name })) {
        throw "DB_NAME '$name' is a protected database. This project must target aircraft_predict_db."
    }
    if ($name -notmatch '^[a-z_][a-z0-9_]{0,62}$') { throw "DB_NAME '$name' is not a valid lower-case identifier." }
    return $name
}

function Get-PsqlPath {
    param([hashtable]$Env)
    $configured = Get-Setting $Env "PSQL_PATH" "C:\Program Files\PostgreSQL\17\bin\psql.exe"
    if (Test-Path $configured) { return $configured }
    $found = Get-Command psql -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    throw "psql.exe not found. Set PSQL_PATH in .env."
}

# Runs psql with the password supplied through PGPASSWORD for this call only. Extra args are passed through.
function Invoke-Psql {
    param([hashtable]$Env, [string]$User, [string]$Password, [string]$Database, [string[]]$Arguments)
    $psql = Get-PsqlPath $Env
    $previous = $env:PGPASSWORD
    $previousOptions = $env:PGOPTIONS
    $previousPreference = $ErrorActionPreference
    try {
        $env:PGPASSWORD = $Password
        $env:PGOPTIONS = "-c client_min_messages=warning"   # hide benign NOTICEs (e.g. DROP ... IF EXISTS)
        # PowerShell 5.1 turns native stderr into terminating errors under 'Stop'; rely on psql's exit code instead.
        $ErrorActionPreference = "Continue"
        & $psql -h (Get-Setting $Env "DB_HOST" "localhost") -p (Get-Setting $Env "DB_PORT" "5432") -U $User -d $Database `
            -v ON_ERROR_STOP=1 -X -q @Arguments
        if ($LASTEXITCODE -ne 0) { throw "psql failed (exit $LASTEXITCODE) as $User on $Database." }
    }
    finally {
        $env:PGPASSWORD = $previous
        $env:PGOPTIONS = $previousOptions
        $ErrorActionPreference = $previousPreference
    }
}

function Read-SecretPrompt([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
