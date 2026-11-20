# Booking verification harness
# ─────────────────────────────────────────────────────────────────────────────
# Exercises the storefront booking endpoint against a running API and a real
# database, covering the availability scenarios the booking engine promises:
# whole-interval overlap detection, adjacency, multi-service durations and two
# simultaneous requests for one slot.
#
#     pwsh PunchedApi/scripts/booking-verify.ps1
#
# Every assertion is a real HTTP call. Nothing is mocked.

param(
  [string]$Base = 'http://localhost:5091/v1',
  [string]$BusinessId = '1bcf4cf4-aacc-4e30-8245-d18ba25a79e2',
  [int]$DayOffset = 12
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0

function Check($name, $condition, $detail = '') {
  if ($condition) {
    $script:pass++
    Write-Host ("  PASS  {0}" -f $name) -ForegroundColor Green
  } else {
    $script:fail++
    Write-Host ("  FAIL  {0} {1}" -f $name, $detail) -ForegroundColor Red
  }
}

function Get-Services {
  $all = (Invoke-RestMethod -Uri "$Base/services/$BusinessId").data
  $one = $all | Where-Object { $_.isActive -and $_.durationMinutes -eq 60 } | Select-Object -First 1
  $two = $all | Where-Object { $_.isActive -and $_.durationMinutes -eq 120 } | Select-Object -First 1
  if (-not $one -or -not $two) { throw 'This business needs a 60-minute and a 120-minute active service.' }
  [pscustomobject]@{ One = $one; Two = $two }
}

function Get-Day($date) {
  (Invoke-RestMethod -Uri "$Base/businesses/$BusinessId/availability/calendar?serviceIds=$($script:svc.One.id)&serviceIds=$($script:svc.Two.id)&startDate=$date&endDate=$date").data.days[0]
}

function Book($slotUtc, $serviceIds, $phone, $name = 'Verification Probe') {
  $body = @{
    businessId     = $BusinessId
    serviceIds     = $serviceIds
    staffUserId    = $null
    scheduledAt    = $slotUtc
    note           = 'verification harness'
    paymentOption  = 'pay_upfront'
    fullName       = $name
    phoneNumber    = $phone
    email          = "$phone@example.test"
    mpesaMessage   = $null
    idempotencyKey = [guid]::NewGuid().ToString()
  } | ConvertTo-Json -Depth 5

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $response = Invoke-RestMethod -Uri "$Base/businesses/$BusinessId/public-bookings" `
      -Method Post -ContentType 'application/json' -Body $body
    $sw.Stop()
    [pscustomobject]@{ Ok = $true; Code = $null; Message = $null; Id = $response.data.appointment.id; Ms = [math]::Round($sw.Elapsed.TotalMilliseconds) }
  } catch {
    $sw.Stop()
    $payload = $null
    try { $payload = $_.ErrorDetails.Message | ConvertFrom-Json } catch { }
    [pscustomobject]@{
      Ok = $false; Code = $payload.error.code; Message = $payload.error.message; Id = $null
      Ms = [math]::Round($sw.Elapsed.TotalMilliseconds)
    }
  }
}

Write-Host "== Booking verification against $Base ==" -ForegroundColor Cyan

$script:svc = Get-Services

# A day where the whole book is empty, for the ONE-SERVICE grid. Booking real
# appointments is what this harness does, so a previous run (or a real customer) may
# already hold the obvious slots; rather than assume, it looks for a genuinely empty
# day and says so if it cannot find one.
function Get-DayFor($date, [string[]]$serviceIds) {
  $query = ($serviceIds | ForEach-Object { "serviceIds=$_" }) -join '&'
  (Invoke-RestMethod -Uri "$Base/businesses/$BusinessId/availability/calendar?$query&startDate=$date&endDate=$date").data.days[0]
}

$day = $null
$date = $null
$oneDay = $null

foreach ($offset in $DayOffset..($DayOffset + 13)) {
  $candidateDate = (Get-Date).AddDays($offset).ToString('yyyy-MM-dd')
  $candidateOne = Get-DayFor $candidateDate @($script:svc.One.id)
  if (-not $candidateOne -or $candidateOne.slots.Count -eq 0) { continue }
  if (@($candidateOne.slots | Where-Object { -not $_.isAvailable }).Count -gt 0) { continue }

  $candidateThree = Get-Day $candidateDate
  $at09 = $candidateThree.slots | Where-Object { $_.localTime -eq '09:00' } | Select-Object -First 1
  if (-not $at09 -or -not $at09.isAvailable) { continue }

  $day = $candidateThree
  $oneDay = $candidateOne
  $date = $candidateDate
  break
}

if (-not $day) { throw 'No empty day with a free 09:00 three-hour opening in the next two weeks.' }

$times = @($day.slots | ForEach-Object { $_.localTime } | Sort-Object -Unique)
function SlotAt($clock) { $day.slots | Where-Object { $_.localTime -eq $clock } | Select-Object -First 1 }

$at09 = SlotAt '09:00'
$at08 = (Get-DayFor $date @($script:svc.One.id)).slots | Where-Object { $_.localTime -eq '08:00' } | Select-Object -First 1
$at1030 = SlotAt '10:30'
$at12 = SlotAt '12:00'
$at16 = SlotAt '16:00'

Write-Host "`n-- whole-interval checks on $date --"

# 1. Three hours at 09:00 with an empty book is accepted, and it holds 09:00-12:00.
$three = Book $at09.startAtUtc @($script:svc.One.id, $script:svc.Two.id) '0722000001'
Check '3h booking at 09:00 is accepted' $three.Ok "$($three.Code) $($three.Message)"

# 2. The same interval again is refused: the check is the INTERVAL, not the start time.
$dup = Book $at09.startAtUtc @($script:svc.One.id, $script:svc.Two.id) '0722000002'
Check 'same 3h interval is refused' (-not $dup.Ok -and $dup.Code -eq 'SLOT_UNAVAILABLE') "$($dup.Code)"

# 3. Starting inside the held interval is refused, even though that start looks free.
if ($at1030) {
  $inside = Book $at1030.startAtUtc @($script:svc.One.id) '0722000003'
  Check 'starting inside an existing booking is refused' (-not $inside.Ok -and $inside.Code -eq 'SLOT_UNAVAILABLE') "$($inside.Code)"
}

# 4. A booking that ENDS inside the held interval (08:30-09:30) is refused.
$before30 = SlotAt '08:30'
if ($before30) {
  $endsInside = Book $before30.startAtUtc @($script:svc.One.id) '0722000004'
  Check 'ending inside an existing booking is refused' (-not $endsInside.Ok -and $endsInside.Code -eq 'SLOT_UNAVAILABLE') "$($endsInside.Code)"
}

# 4b. A requested interval that CONTAINS part of the held booking (11:00 for three
#     hours against 09:00-12:00) is refused.
$at11 = SlotAt '11:00'
if ($at11) {
  $containsHeld = Book $at11.startAtUtc @($script:svc.One.id, $script:svc.Two.id) '0722000010'
  Check 'a request that contains an existing booking is refused' (-not $containsHeld.Ok -and $containsHeld.Code -eq 'SLOT_UNAVAILABLE') "$($containsHeld.Code)"
}

# 5. Half-open adjacency is legal on both sides of the held interval.
if ($at12) {
  $after = Book $at12.startAtUtc @($script:svc.One.id) '0722000005'
  Check 'a booking starting exactly when another ends is accepted' $after.Ok "$($after.Code) $($after.Message)"
}

if ($at08) {
  $before = Book $at08.startAtUtc @($script:svc.One.id) '0722000006'
  Check 'a booking ending exactly when another starts is accepted' $before.Ok "$($before.Code) $($before.Message)"

  # 6. A request that CONTAINS existing appointments (08:00 for three hours) is refused.
  $contains = Book $at08.startAtUtc @($script:svc.One.id, $script:svc.Two.id) '0722000007'
  Check 'a request that contains an existing booking is refused' (-not $contains.Ok -and $contains.Code -eq 'SLOT_UNAVAILABLE') "$($contains.Code)"
}

# 7. Two simultaneous requests for one free slot: exactly one may win.
$free = $at16
if ($free -and $free.isAvailable) {
  $race = {
    param($b, $bid, $s, $t, $p, $name)
    try {
      $r = Invoke-RestMethod -Uri "$b/businesses/$bid/public-bookings" -Method Post -ContentType 'application/json' -Body (@{
        businessId = $bid; serviceIds = @($s); staffUserId = $null; scheduledAt = $t
        paymentOption = 'pay_upfront'; fullName = $name; phoneNumber = $p; email = "$p@example.test"
        idempotencyKey = [guid]::NewGuid().ToString()
      } | ConvertTo-Json)
      $r.success
    } catch { $false }
  }
  $jobs = @(
    Start-Job -ScriptBlock $race -ArgumentList $Base, $BusinessId, $script:svc.One.id, $free.startAtUtc, '0722000008', 'Race One'
    Start-Job -ScriptBlock $race -ArgumentList $Base, $BusinessId, $script:svc.One.id, $free.startAtUtc, '0722000009', 'Race Two'
  )
  $results = $jobs | Wait-Job | Receive-Job
  $jobs | Remove-Job
  $wins = @($results | Where-Object { $_ -eq $true }).Count
  Check 'two concurrent bookings for one slot: exactly one succeeds' ($wins -eq 1) "winners=$wins"
} else {
  Write-Host '  SKIP  concurrent check (16:00 is not a free 60-minute start)' -ForegroundColor DarkYellow
}

Write-Host "`n== $script:pass passed, $script:fail failed ==" -ForegroundColor Cyan
if ($script:fail -gt 0) { exit 1 }
