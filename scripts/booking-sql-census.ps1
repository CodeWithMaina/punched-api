# Exact SQL census for one booking
# ─────────────────────────────────────────────────────────────────────────────
# Answers "how many SQL statements does one booking make, and of what kind" by
# reading the API's own EF Core command log — no inference, and no noise from
# Neon telemetry or background workers.
#
# It starts a SECOND API instance on a spare port with the EF command logger at
# Information and WORKERS_ENABLED=false, issues exactly one booking against it,
# then counts the logged commands. The instance is stopped at the end.
#
#     pwsh PunchedApi/scripts/booking-sql-census.ps1

param(
  [string]$Base = 'http://localhost:5091/v1',
  [int]$CensusPort = 5092,
  [string]$Email = 'elena.njeri@aurelialuxe.co.ke',
  [string]$Password = 'Owner@1234!'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$apiProject = Join-Path $repo 'PunchedApi'
$logOut = Join-Path $env:TEMP 'punched-census-out.log'
$logErr = Join-Path $env:TEMP 'punched-census-err.log'
Remove-Item $logOut, $logErr -ErrorAction SilentlyContinue

$TABLES = @('appointments','appointment_resources','appointment_status_history','payments',
  'notification_inbox','notifications','notification_logs','notification_suppressions','scheduled_jobs',
  'domain_events','businesses','business_memberships','business_reminder_policies','users',
  'customer_business_enrollments','service_catalog_items','staff_shifts','staff_availability')

# ── Resolve what to book, against the already-running instance ───────────
$login = Invoke-RestMethod -Uri "$Base/auth/login" -Method Post -ContentType 'application/json' `
  -Body (@{ email = $Email; password = $Password } | ConvertTo-Json)
$auth = @{ Authorization = "Bearer $($login.data.accessToken)" }
$businessId = (Invoke-RestMethod -Uri "$Base/businesses/me/dashboard/bootstrap" -Headers $auth).data.business.id
$service = (Invoke-RestMethod -Uri "$Base/services/me" -Headers $auth).data |
  Where-Object { $_.isActive -and -not $_.isArchived } | Select-Object -First 1
$customer = (Invoke-RestMethod -Uri "$Base/businesses/me/customers?pageSize=5&status=all" -Headers $auth).data.items |
  Select-Object -First 1
$day = (Get-Date).AddDays(6).ToString('yyyy-MM-dd')
$slot = @((Invoke-RestMethod -Uri "$Base/businesses/$businessId/availability?serviceIds=$($service.id)&startDate=$day&endDate=$day" -Headers $auth).data |
  Where-Object { $_.isAvailable }) | Select-Object -First 1
if (-not $slot) { throw "No free slot on $day." }

# ── Start the census instance ────────────────────────────────────────────
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:WORKERS_ENABLED = 'false'
# The category name contains dots, which PowerShell would read as member access on
# $env:, so it is set through the environment API.
[Environment]::SetEnvironmentVariable(
  'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command', 'Information', 'Process')
[Environment]::SetEnvironmentVariable(
  'Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command', 'Information', 'Process')

$proc = Start-Process -FilePath 'dotnet' `
  -ArgumentList @('run', '--project', $apiProject, '-c', 'Release', '--no-build', '--urls', "http://localhost:$CensusPort") `
  -RedirectStandardOutput $logOut -RedirectStandardError $logErr -PassThru -WindowStyle Hidden

Write-Host "Waiting for the census instance on :$CensusPort ..." -ForegroundColor DarkCyan
$ready = $false
for ($i = 0; $i -lt 100; $i++) {
  Start-Sleep -Milliseconds 700
  try {
    $r = Invoke-WebRequest -Uri "http://localhost:$CensusPort/" -TimeoutSec 3 -SkipHttpErrorCheck
    if ($r.StatusCode -eq 200) { $ready = $true; break }
  } catch { }
}
if (-not $ready) {
  Write-Host 'Census instance did not start. Tail of stderr:' -ForegroundColor Red
  Get-Content $logErr -Tail 25 -ErrorAction SilentlyContinue
  if ($proc -and -not $proc.HasExited) { $proc.Kill() }
  exit 1

try {
  $marker = "=== CENSUS START $(Get-Date -Format o) ==="
  Add-Content -Path $logOut -Value $marker

  $body = @{
    businessId      = $businessId
    customerId      = $customer.userId
    serviceIds      = @($service.id)
    scheduledAt     = $slot.startAtUtc
    payAfterService = $false
  } | ConvertTo-Json

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $created = Invoke-RestMethod -Uri "http://localhost:$CensusPort/v1/businesses/me/appointments" `
    -Method Post -ContentType 'application/json' -Headers $auth -Body $body
  $sw.Stop()
  Start-Sleep -Milliseconds 500   # let the logger flush
  Add-Content -Path $logOut -Value "=== CENSUS END ==="
  Write-Host "Booked appointment $($created.data.id)" -ForegroundColor DarkGray

  # ── Count only what happened between the markers ───────────────────────
  $lines = Get-Content $logOut
  $startIndex = ($lines | Select-String -SimpleMatch 'CENSUS START' | Select-Object -Last 1).LineNumber
  $window = $lines[$startIndex..($lines.Length - 1)]
  $sqlLines = @($window | Where-Object { $_ -match 'Executed DbCommand' })
  $total = $sqlLines.Count

  Write-Host ''
  Write-Host ("One booking: {0} ms of API time, {1} SQL statements." -f `
      [math]::Round($sw.Elapsed.TotalMilliseconds), $total) -ForegroundColor Green
  if ($total -gt 0) {
    Write-Host ("  → {0} ms per statement at this distance" -f `
        [math]::Round($sw.Elapsed.TotalMilliseconds / $total)) -ForegroundColor Green
  }

  Write-Host ''
  Write-Host 'By statement type:' -ForegroundColor Cyan
  $sqlLines | ForEach-Object {
    if ($_ -match '\b(SELECT|INSERT|UPDATE|DELETE|BEGIN|COMMIT|ROLLBACK)\b') { $Matches[1] } else { 'OTHER' }
  } | Group-Object | Sort-Object Count -Descending | Format-Table Name, Count -AutoSize

  Write-Host 'By table (statements whose text references it):' -ForegroundColor Cyan
  $rows = foreach ($name in $TABLES) {
    $count = @($sqlLines | Where-Object { $_ -match "(?i)\b$name\b" }).Count
    if ($count -gt 0) { [pscustomobject]@{ Table = $name; Statements = $count } }
  }
  $rows | Sort-Object Statements -Descending | Format-Table -AutoSize
}
finally {
  if ($proc -and -not $proc.HasExited) { $proc.Kill() }
  Write-Host 'Census instance stopped.' -ForegroundColor DarkGray
}

}
