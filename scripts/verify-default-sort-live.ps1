# Confirms the default sort on a FRESHLY BUILT instance (the dev watcher can hold an
# older binary, which would otherwise look like the change had not worked).
param([int]$Port = 5093)
$ErrorActionPreference = 'Stop'
$apiProject = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) '.'
$out = Join-Path $env:TEMP 'punched-sortcheck-out.log'
$err = Join-Path $env:TEMP 'punched-sortcheck-err.log'
Remove-Item $out, $err -ErrorAction SilentlyContinue

$env:WORKERS_ENABLED = 'false'
$proc = Start-Process -FilePath 'dotnet' `
  -ArgumentList @('run', '--project', $apiProject, '-c', 'Release', '--no-build', '--urls', "http://localhost:$Port") `
  -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -WindowStyle Hidden

$ready = $false
for ($i = 0; $i -lt 100; $i++) {
  Start-Sleep -Milliseconds 700
  try {
    if ((Invoke-WebRequest -Uri "http://localhost:$Port/" -TimeoutSec 3 -SkipHttpErrorCheck).StatusCode -eq 200) { $ready = $true; break }
  } catch { }
}
if (-not $ready) { if (-not $proc.HasExited) { $proc.Kill() }; throw 'instance did not start' }

try {
  $b = "http://localhost:$Port/v1"
  $t = (Invoke-RestMethod -Uri "$b/auth/login" -Method Post -ContentType 'application/json' `
      -Body (@{ email = 'elena.njeri@aurelialuxe.co.ke'; password = 'Owner@1234!' } | ConvertTo-Json)).data.accessToken
  $h = @{ Authorization = "Bearer $t" }

  foreach ($q in @('pageSize=3', 'pageSize=3&sortBy=createdAt&sortDirection=desc')) {
    $p = (Invoke-RestMethod -Uri "$b/businesses/me/appointments?$q" -Headers $h).data
    $created = @($p.appointments | ForEach-Object { ([datetime]$_.createdAt).ToString('MM-dd HH:mm') })
    $isDesc = ($created -join '|') -eq (@($created | Sort-Object -Descending) -join '|')
    Write-Host ("{0,-45} => {1,-34} createdAt desc: {2}" -f $q, ($created -join ', '), $isDesc) `
      -ForegroundColor $(if ($isDesc) { 'Green' } else { 'Red' })
  }
}
finally {
  if (-not $proc.HasExited) { $proc.Kill() }
}
