# Schema dump for the booking hot path (read-only).
#   pwsh PunchedApi/scripts/describe-tables.ps1
param([string]$ConnectionString)

$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot '..\bin\Debug\net8.0' | Resolve-Path

if (-not $ConnectionString) {
  $url = (& dotnet user-secrets list --project (Join-Path $PSScriptRoot '..') 2>$null |
    Where-Object { $_ -like 'DATABASE_URL*' }) -replace '^DATABASE_URL\s*=\s*', ''
  if (-not $url) { throw 'Pass -ConnectionString or set DATABASE_URL in user secrets.' }
  $uri = [System.Uri]$url
  $userPass = $uri.UserInfo.Split(':', 2)
  $port = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
  $ConnectionString = "Host=$($uri.Host);Port=$port;Database=$($uri.AbsolutePath.Trim('/'));" +
    "Username=$($userPass[0]);Password=$($userPass[1]);SSL Mode=Require"
}

Add-Type -Path (Join-Path $bin 'Npgsql.dll')
$conn = New-Object Npgsql.NpgsqlConnection($ConnectionString)
$conn.Open()

$tables = @(
  'businesses', 'business_payment_configs', 'business_reminder_policies',
  'service_catalog_items', 'business_memberships', 'staff_service_categories',
  'staff_service_assignments', 'staff_shifts', 'staff_availability',
  'business_operating_hours', 'business_operating_periods',
  'business_schedule_exceptions', 'business_schedule_exception_periods',
  'appointments', 'users', 'customer_business_enrollments', 'payments',
  'businesses', 'modules', 'plan_modules', 'business_modules', 'business_subscriptions', 'subscription_plans'
)

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
select table_name, column_name, data_type, is_nullable, column_default
from information_schema.columns
where table_schema = 'public' and table_name = any(@tables)
order by table_name, ordinal_position
"@
$cmd.Parameters.AddWithValue('tables', [string[]]$tables) | Out-Null
$reader = $cmd.ExecuteReader()
$current = $null
while ($reader.Read()) {
  $t = $reader.GetString(0)
  if ($t -ne $current) { Write-Host "`n== $t ==" -ForegroundColor Cyan; $current = $t }
  Write-Host ("  {0,-38} {1,-28} null={2} default={3}" -f $reader.GetString(1), $reader.GetString(2), $reader.GetString(3), $reader.GetValue(4))
}
$reader.Close()

$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = @"
select indexname, indexdef from pg_indexes
where schemaname = 'public' and tablename = 'appointments'
"@
$r2 = $cmd2.ExecuteReader()
Write-Host "`n== appointments indexes ==" -ForegroundColor Yellow
while ($r2.Read()) { Write-Host ("  {0}`n    {1}" -f $r2.GetString(0), $r2.GetString(1)) }
$r2.Close()

$cmd3 = $conn.CreateCommand()
$cmd3.CommandText = "select conname, pg_get_constraintdef(oid) from pg_constraint where conrelid = 'appointments'::regclass"
$r3 = $cmd3.ExecuteReader()
Write-Host "`n== appointments constraints ==" -ForegroundColor Yellow
while ($r3.Read()) { Write-Host ("  {0}: {1}" -f $r3.GetString(0), $r3.GetString(1)) }
$r3.Close()

$conn.Dispose()
