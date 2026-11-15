# Verifies the list endpoint's default order is createdAt DESC and that an explicit
# sort is still honoured — the two things B2 promises.
param([string]$Base = 'http://localhost:5091/v1')
$ErrorActionPreference = 'Stop'

$login = Invoke-RestMethod -Uri "$Base/auth/login" -Method Post -ContentType 'application/json' `
  -Body (@{ email = 'elena.njeri@aurelialuxe.co.ke'; password = 'Owner@1234!' } | ConvertTo-Json)
$auth = @{ Authorization = "Bearer $($login.data.accessToken)" }

function Show($label, $query) {
  $page = (Invoke-RestMethod -Uri "$Base/businesses/me/appointments?$query" -Headers $auth).data
  $dates = $page.appointments | ForEach-Object { $_.createdAt }
  $sorted = @($dates | Sort-Object -Descending)
  $isDesc = (($dates -join '|') -eq ($sorted -join '|'))
  Write-Host "$label  (createdAt descending: $isDesc)" -ForegroundColor $(if ($isDesc) { 'Green' } else { 'Yellow' })
  $page.appointments | ForEach-Object {
    Write-Host ("   created {0}  scheduled {1}  {2}" -f `
      ([datetime]$_.createdAt).ToString('yyyy-MM-dd HH:mm'), `
      ([datetime]$_.scheduledAt).ToString('yyyy-MM-dd HH:mm'), `
      $_.serviceName)
  }
}

Show 'default (no sort sent)' 'pageSize=5'
Write-Host ''
$page = (Invoke-RestMethod -Uri "$Base/businesses/me/appointments?pageSize=5&sortBy=scheduledAt&sortDirection=asc" -Headers $auth).data
$sched = $page.appointments | ForEach-Object { $_.scheduledAt }
$isAsc = (($sched -join '|') -eq (@($sched | Sort-Object) -join '|'))
Write-Host "explicit sortBy=scheduledAt&sortDirection=asc honoured: $isAsc" -ForegroundColor $(if ($isAsc) { 'Green' } else { 'Red' })
