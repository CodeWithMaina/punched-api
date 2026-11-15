# Booking performance harness
# ─────────────────────────────────────────────────────────────────────────────
# Measures the owner booking workflow against a running API, stage by stage, so
# the cost of each call is visible instead of inferred. Run with the API up:
#
#     pwsh PunchedApi/scripts/booking-perf.ps1
#
# Every figure is a real HTTP round trip to the real endpoints.

param(
  [string]$Base = 'http://localhost:5091/v1',
  [string]$Email = 'elena.njeri@aurelialuxe.co.ke',
  [string]$Password = 'Owner@1234!',
  [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'

function Step($label, $script) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $result = & $script
  $sw.Stop()
  [pscustomobject]@{ Step = $label; Ms = [math]::Round($sw.Elapsed.TotalMilliseconds) ; Result = $result }
}

function Post($uri, $body, $headers) {
  Invoke-RestMethod -Uri $uri -Method Post -ContentType 'application/json' `
    -Headers $headers -Body ($body | ConvertTo-Json -Depth 6)
}

function Get($uri, $headers) {
  Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
}

Write-Host "== Booking performance against $Base ==" -ForegroundColor Cyan

# ── Sign in ──────────────────────────────────────────────────────────────
$login = Post "$Base/auth/login" @{ email = $Email; password = $Password } @{}
if (-not $login.success) { throw "Login failed: $($login.error.message)" }
$auth = @{ Authorization = "Bearer $($login.data.accessToken)" }

$boot = Get "$Base/businesses/me/dashboard/bootstrap" $auth
$businessId = $boot.data.business.id
$businessName = $boot.data.business.name
Write-Host "Business: $businessName ($businessId)"

# ── Catalogue + a customer to book ───────────────────────────────────────
$services = (Get "$Base/services/me" $auth).data
if (-not $services -or $services.Count -eq 0) { throw 'No services on this business.' }
$service = $services | Where-Object { $_.isActive -and -not $_.isArchived } | Select-Object -First 1

$customers = (Get "$Base/businesses/me/customers?pageSize=5&status=all" $auth).data.items
$customer = $customers | Select-Object -First 1
if (-not $customer) { throw 'No customers on this business.' }
Write-Host "Service: $($service.name) ($($service.durationMinutes) min) · Customer: $($customer.fullName)"
Write-Host ''

$report = @()

for ($run = 1; $run -le $Runs; $run++) {
  Write-Host "-- run $run --" -ForegroundColor Yellow

  # A free slot a few days out, so the booking is never refused for lead time.
  $day = (Get-Date).AddDays($run + 1).ToString('yyyy-MM-dd')
  $slots = Get "$Base/businesses/$businessId/availability?serviceIds=$($service.id)&startDate=$day&endDate=$day" $auth
  $free = @($slots.data | Where-Object { $_.isAvailable } | Select-Object -First 1)
  if ($free.Count -eq 0) { Write-Host "  no free slot on $day, skipping" -ForegroundColor DarkYellow; continue }
  $slot = $free[0]

  # 1. Availability read (what the wizard does per day)
  $report += Step "availability ($day)" { (Get "$Base/businesses/$businessId/availability?serviceIds=$($service.id)&startDate=$day&endDate=$day" $auth).data.Count }

  # 2. The booking write — the operation the user waits for
  $appointment = $null
  $report += Step 'POST appointment' {
    $script:appointment = (Post "$Base/businesses/me/appointments" @{
        businessId = $businessId
        customerId = $customer.userId
        serviceIds = @($service.id)
        scheduledAt = $slot.startAtUtc
        payAfterService = $false
      } $auth).data
    $appointment.id
  }

  # 3. Payment create + cash confirm, exactly as the wizard does them
  $payment = $null
  $report += Step 'POST payment' {
    $script:payment = (Post "$Base/payments" @{
        appointmentId = $appointment.id
        method = 'cash'
        type = 'full_payment'
        idempotencyKey = "$($appointment.id):full"
      } $auth).data
    $payment.id
  }

  $report += Step 'POST confirm-cash' {
    (Post "$Base/payments/$($payment.id)/confirm-cash" @{ note = 'perf harness' } $auth).data.status
  }

  # 4. What the screen refetches the moment the mutation succeeds
  $report += Step 'GET appointments (list refresh)' { (Get "$Base/businesses/me/appointments?page=1&pageSize=25" $auth).data.appointments.Count }
  $report += Step 'GET appointment detail' { (Get "$Base/businesses/me/appointments/$($appointment.id)" $auth).data.status }

  Write-Host ''
}

Write-Host '== Stage timings (ms) ==' -ForegroundColor Cyan
$report | Group-Object Step | ForEach-Object {
  $times = $_.Group | ForEach-Object { $_.Ms }
  [pscustomobject]@{
    Step = $_.Name
    Runs = $times.Count
    Min  = ($times | Measure-Object -Minimum).Minimum
    Avg  = [math]::Round(($times | Measure-Object -Average).Average)
    Max  = ($times | Measure-Object -Maximum).Maximum
  }
} | Sort-Object Avg -Descending | Format-Table -AutoSize

$booking = $report | Where-Object { $_.Step -eq 'POST appointment' } | ForEach-Object { $_.Ms }
if ($booking) {
  Write-Host ("Booking write: min {0} ms · avg {1} ms · max {2} ms" -f `
    ($booking | Measure-Object -Minimum).Minimum,
    [math]::Round(($booking | Measure-Object -Average).Average),
    ($booking | Measure-Object -Maximum).Maximum) -ForegroundColor Green
}
