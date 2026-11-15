# Database latency probe
# ─────────────────────────────────────────────────────────────────────────────
# Separates "how far away is the database" from "how many round trips does the
# application make". Loads the same Npgsql the API uses, opens ONE connection and
# times a trivial SELECT on it, so the number is pure network round-trip cost.
#
#     pwsh PunchedApi/scripts/db-latency.ps1

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

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$conn = New-Object Npgsql.NpgsqlConnection($ConnectionString)
$conn.Open()
$sw.Stop()
Write-Host ("Open (TCP + TLS + auth): {0} ms" -f [math]::Round($sw.Elapsed.TotalMilliseconds)) -ForegroundColor Yellow

$times = 1..5 | ForEach-Object {
  $cmd = $conn.CreateCommand()
  $cmd.CommandText = 'SELECT 1'
  $t = [System.Diagnostics.Stopwatch]::StartNew()
  $cmd.ExecuteScalar() | Out-Null
  $t.Stop()
  $cmd.Dispose()
  [math]::Round($t.Elapsed.TotalMilliseconds)
}
Write-Host ("SELECT 1 round trips (ms): {0}" -f ($times -join ', ')) -ForegroundColor Green
Write-Host ("Mean round trip: {0} ms" -f [math]::Round(($times | Measure-Object -Average).Average)) -ForegroundColor Green

# Does the provider's own pool keep a warm connection, or does every request pay
# the handshake again? Same process, same connection string: the second open must
# be ~0 ms if pooling works.
$conn.Dispose()
$t = [System.Diagnostics.Stopwatch]::StartNew()
$again = New-Object Npgsql.NpgsqlConnection($ConnectionString)
$again.Open()
$t.Stop()
Write-Host ("Re-open from the same pool: {0} ms" -f [math]::Round($t.Elapsed.TotalMilliseconds)) -ForegroundColor Yellow

# Two more queries on the re-opened connection, to confirm the pooled socket is usable.
$againTimes = 1..2 | ForEach-Object {
  $cmd = $again.CreateCommand()
  $cmd.CommandText = 'SELECT 1'
  $t2 = [System.Diagnostics.Stopwatch]::StartNew()
  $cmd.ExecuteScalar() | Out-Null
  $t2.Stop()
  $cmd.Dispose()
  [math]::Round($t2.Elapsed.TotalMilliseconds)
}
Write-Host ("Pooled round trips (ms): {0}" -f ($againTimes -join ', ')) -ForegroundColor Green

# A COUNT(*) is a different animal: report it separately so a slow table scan is
# never mistaken for network latency.
foreach ($table in @('appointments', 'scheduled_jobs')) {
  $cmd = $again.CreateCommand()
  $cmd.CommandText = "SELECT count(*) FROM $table"
  $t3 = [System.Diagnostics.Stopwatch]::StartNew()
  try { $n = $cmd.ExecuteScalar(); $ok = $true } catch { $ok = $false; $n = $_.Exception.Message }
  $t3.Stop()
  if ($ok) {
    Write-Host ("COUNT(*) $table = $n in {0} ms" -f [math]::Round($t3.Elapsed.TotalMilliseconds)) -ForegroundColor Cyan
  } else {
    Write-Host ("COUNT(*) $table failed: $n") -ForegroundColor DarkYellow
  }
  $cmd.Dispose()
}

# The decisive question for request latency: does an idle pooled connection stay
# usable? If a query after a short pause costs ~1.5 s, the provider or the pooler is
# dropping the socket during every moment of quiet, and each request pays a full
# reconnect before it can run its first query.
foreach ($pause in @(1, 2, 5)) {
  Start-Sleep -Seconds $pause
  $cmd = $again.CreateCommand()
  $cmd.CommandText = 'SELECT 1'
  $t4 = [System.Diagnostics.Stopwatch]::StartNew()
  try { $cmd.ExecuteScalar() | Out-Null; $ok = $true } catch { $ok = $false }
  $t4.Stop()
  $cmd.Dispose()
  Write-Host ("After {0}s idle: {1} ms (ok={2})" -f $pause, [math]::Round($t4.Elapsed.TotalMilliseconds), $ok) -ForegroundColor Magenta
}

$again.Dispose()
