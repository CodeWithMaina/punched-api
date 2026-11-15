# Query census for the booking write
# ─────────────────────────────────────────────────────────────────────────────
# Answers "how many database round trips does one booking actually make, and where
# do they go" with the database's own accounting (pg_stat_statements), rather than
# inferring it from wall-clock time.
#
#     pwsh PunchedApi/scripts/booking-queries.ps1
#
# Run it before and after a change to see the round-trip count move.

param(
  [string]$Base = 'http://localhost:5091/v1',
  [string]$Email = 'elena.njeri@aurelialuxe.co.ke',
  [string]$Password = 'Owner@1234!',
  [int]$Top = 25
)

$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot '..\bin\Debug\net8.0' | Resolve-Path

# ── Connection (same source the API uses) ────────────────────────────────
$url = (& dotnet user-secrets list --project (Join-Path $PSScriptRoot '..') 2>$null |
  Where-Object { $_ -like 'DATABASE_URL*' }) -replace '^DATABASE_URL\s*=\s*', ''
if (-not $url) { throw 'DATABASE_URL not found in user secrets.' }
$uri = [System.Uri]$url
$userPass = $uri.UserInfo.Split(':', 2)
$port = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
$connectionString = "Host=$($uri.Host);Port=$port;Database=$($uri.AbsolutePath.Trim('/'));" +
  "Username=$($userPass[0]);Password=$($userPass[1]);SSL Mode=Require"

Add-Type -Path (Join-Path $bin 'Npgsql.dll')
$conn = New-Object Npgsql.NpgsqlConnection($connectionString)
$conn.Open()

function Query($sql) {
  $cmd = $conn.CreateCommand()
  $cmd.CommandText = $sql
  $cmd.CommandTimeout = 120
  $reader = $cmd.ExecuteReader()
  $rows = @()
  while ($reader.Read()) {
    $row = @{}
    for ($i = 0; $i -lt $reader.FieldCount; $i++) { $row[$reader.GetName($i)] = $reader.GetValue($i) }
    $rows += [pscustomobject]$row
  }
  $reader.Close()
  $cmd.Dispose()
  return $rows
}

# ── Is the extension available? (create it if the role may) ─────────────
$hasStats = $false
try {
  Query 'SELECT 1 FROM pg_stat_statements LIMIT 1' | Out-Null
  $hasStats = $true
} catch {
  try {
    Query 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements' | Out-Null
    Query 'SELECT 1 FROM pg_stat_statements LIMIT 1' | Out-Null
    $hasStats = $true
    Write-Host 'pg_stat_statements enabled for this census.' -ForegroundColor DarkCyan
  } catch {
    Write-Host "pg_stat_statements is unavailable on this database: $($_.Exception.Message)" -ForegroundColor DarkYellow
  }
}

function Stats {
  if (-not $hasStats) { return @() }
  return Query @"
SELECT calls, rows, round(total_exec_time)::bigint AS total_ms, round(mean_exec_time)::bigint AS mean_ms,
       left(regexp_replace(query, '\s+', ' ', 'g'), 110) AS query
FROM pg_stat_statements
WHERE query NOT LIKE '%pg_stat_statements%'
ORDER BY calls DESC
"@
}

# ── Sign in, resolve a bookable slot ────────────────────────────────────
$login = Invoke-RestMethod -Uri "$Base/auth/login" -Method Post -ContentType 'application/json' `
  -Body (@{ email = $Email; password = $Password } | ConvertTo-Json)
$auth = @{ Authorization = "Bearer $($login.data.accessToken)" }
$boot = Invoke-RestMethod -Uri "$Base/businesses/me/dashboard/bootstrap" -Headers $auth
$businessId = $boot.data.business.id
$service = (Invoke-RestMethod -Uri "$Base/services/me" -Headers $auth).data |
  Where-Object { $_.isActive -and -not $_.isArchived } | Select-Object -First 1
$customer = (Invoke-RestMethod -Uri "$Base/businesses/me/customers?pageSize=5&status=all" -Headers $auth).data.items |
  Select-Object -First 1
$day = (Get-Date).AddDays(4).ToString('yyyy-MM-dd')
$slot = @((Invoke-RestMethod -Uri "$Base/businesses/$businessId/availability?serviceIds=$($service.id)&startDate=$day&endDate=$day" -Headers $auth).data |
  Where-Object { $_.isAvailable }) | Select-Object -First 1
if (-not $slot) { throw "No free slot on $day." }

if (-not $hasStats) { $conn.Dispose(); exit 0 }

# ── Census: reset, book once, count ─────────────────────────────────────
Query 'SELECT pg_stat_statements_reset()' | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$appointment = (Invoke-RestMethod -Uri "$Base/businesses/me/appointments" -Method Post `
    -ContentType 'application/json' -Headers $auth `
    -Body (@{
        businessId = $businessId
        customerId = $customer.userId
        serviceIds = @($service.id)
        scheduledAt = $slot.startAtUtc
        payAfterService = $false
      } | ConvertTo-Json)).data
$sw.Stop()

$after = Stats
$totalCalls = ($after | Measure-Object -Property calls -Sum).Sum

Write-Host ''
Write-Host ("One booking (POST /businesses/me/appointments) took {0} ms and made {1} database statements." -f `
  [math]::Round($sw.Elapsed.TotalMilliseconds), $totalCalls) -ForegroundColor Green
Write-Host ("  → {0} ms per statement at this distance" -f [math]::Round($sw.Elapsed.TotalMilliseconds / [math]::Max($totalCalls, 1))) -ForegroundColor Green
Write-Host ''
$after | Select-Object -First $Top | Format-Table -AutoSize

$conn.Dispose()
