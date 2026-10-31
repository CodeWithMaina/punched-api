# Aurelia Luxe Hair Atelier — end-to-end verification (development only).
$ErrorActionPreference = 'Continue'
$base = 'http://localhost:5091/v1'
$slug = 'aurelia-luxe-hair-atelier'

function Show($label, $value) { Write-Host ("{0}: {1}" -f $label, $value) }

function Login($email, $password) {
    try {
        return Invoke-RestMethod -Uri "$base/auth/login" -Method Post -ContentType 'application/json' -Body (@{
            email = $email; password = $password
        } | ConvertTo-Json)
    } catch {
        throw "LOGIN FAILED for $email -> $($_.Exception.Response.StatusCode.value__)"
    }
}

# ── 1. Public storefront (no auth) ─────────────────────────────
$boot = Invoke-RestMethod -Uri "$base/businesses/by-slug/$slug/bootstrap" -Method Get
$businessId = $boot.data.business.id
Show 'Storefront bootstrap success' $boot.success
Show 'Business id' $businessId
Show 'Business name' $boot.data.business.name
Show 'Category' $boot.data.business.category
Show 'Location' $boot.data.business.location
Show 'Currency/timezone' "$($boot.data.business.currency) / $($boot.data.business.timeZoneId)"

$svc = Invoke-RestMethod -Uri "$base/services/$businessId" -Method Get
Show 'Public service count' $svc.data.Count

# ── 2. Owner ───────────────────────────────────────────────────
$ownerLogin = Login 'elena.njeri@aurelialuxe.co.ke' 'Owner@1234!'
Show 'Owner login success' $ownerLogin.success
Show 'Owner role' $ownerLogin.data.user.role
$ownerToken = $ownerLogin.data.accessToken
$ownerHeaders = @{ Authorization = "Bearer $ownerToken" }

$ownerBoot = Invoke-RestMethod -Uri "$base/businesses/me/dashboard/bootstrap" -Headers $ownerHeaders -Method Get
Show 'Owner dashboard bootstrap' $ownerBoot.success
Show 'Plan' $ownerBoot.data.plan.name
Show 'Owner permission count' $ownerBoot.data.permissions.Count
Show 'Entitlements' (($ownerBoot.data.entitlements) -join ', ')
Show 'Owner nav tabs' (($ownerBoot.data.navigation.tabs) -join ', ')
Show 'Reminder policy enabled' $ownerBoot.data.settings.reminderPolicyEnabled

$dashboard = Invoke-RestMethod -Uri "$base/businesses/me/dashboard" -Headers $ownerHeaders -Method Get
Show 'Owner dashboard stats' ($dashboard.data | ConvertTo-Json -Depth 3 -Compress)

$analytics = Invoke-RestMethod -Uri "$base/businesses/me/analytics?period=90d" -Headers $ownerHeaders -Method Get
Show 'Owner analytics success' $analytics.success
Show 'Analytics overview' ($analytics.data.overview | ConvertTo-Json -Depth 2 -Compress)
Show 'Revenue' ($analytics.data.revenue | ConvertTo-Json -Depth 2 -Compress)

$staff = Invoke-RestMethod -Uri "$base/businesses/me/staff?pageSize=20" -Headers $ownerHeaders -Method Get
Show 'Owner sees staff count' $staff.data.total
Show 'Owner staff titles' (($staff.data.items | ForEach-Object { $_.fullName }) -join ', ')

$customers = Invoke-RestMethod -Uri "$base/businesses/me/customers?pageSize=20" -Headers $ownerHeaders -Method Get
Show 'Owner sees customer count' $customers.data.totalCount

$appts = Invoke-RestMethod -Uri "$base/businesses/me/appointments?pageSize=20" -Headers $ownerHeaders -Method Get
Show 'Owner sees appointments' $appts.data.totalCount
Show 'Owner appointment statuses (page 1)' (($appts.data.appointments | Group-Object status | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')
Show 'Owner appointment filter buckets' "customers=$($appts.data.customers.Count) staff=$($appts.data.staff.Count) services=$($appts.data.services.Count)"

$payments = Invoke-RestMethod -Uri "$base/payments?pageSize=20" -Headers $ownerHeaders -Method Get
Show 'Owner sees payments' $payments.data.totalCount
Show 'Payment methods (page 1)' (($payments.data.items | Group-Object method | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')
Show 'Payment statuses (page 1)' (($payments.data.items | Group-Object status | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')

$payDash = Invoke-RestMethod -Uri "$base/payments/dashboard" -Headers $ownerHeaders -Method Get
Show 'Payments dashboard' ($payDash.data | ConvertTo-Json -Depth 2 -Compress)

$programs = Invoke-RestMethod -Uri "$base/programs/me" -Headers $ownerHeaders -Method Get
Show 'Owner loyalty programs' (($programs.data | ForEach-Object { "$($_.name)[stamps=$($_.stampsRequired)]" }) -join ', ')

$programId = ($programs.data | Select-Object -First 1).id
$stampCards = Invoke-RestMethod -Uri "$base/programs/me/$programId/stamp-cards" -Headers $ownerHeaders -Method Get
Show 'Owner stamp cards' (($stampCards.data | ForEach-Object { "$($_.name)($($_.stampsRequired))" }) -join ', ')

$reviews = Invoke-RestMethod -Uri "$base/businesses/me/reviews?pageSize=100" -Headers $ownerHeaders -Method Get
Show 'Owner reviews total' $reviews.data.totalCount

$attendance = Invoke-RestMethod -Uri "$base/businesses/me/attendance/overview" -Headers $ownerHeaders -Method Get
Show 'Owner attendance overview' ($attendance.data | ConvertTo-Json -Depth 3 -Compress)

$attendanceRecords = Invoke-RestMethod -Uri "$base/businesses/me/attendance/records?pageSize=20" -Headers $ownerHeaders -Method Get
Show 'Owner attendance records' $attendanceRecords.data.totalCount

$attendanceLocations = Invoke-RestMethod -Uri "$base/businesses/me/attendance/locations" -Headers $ownerHeaders -Method Get
Show 'Owner attendance locations' (($attendanceLocations.data | ForEach-Object { "$($_.name)[active=$($_.isActive)]" }) -join ', ')

$insights = Invoke-RestMethod -Uri "$base/businesses/me/insights" -Headers $ownerHeaders -Method Get
Show 'Owner insights' $insights.data.Count

$segments = Invoke-RestMethod -Uri "$base/businesses/me/segments" -Headers $ownerHeaders -Method Get
Show 'Owner segments' (($segments.data | Group-Object segment | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')

$invites = Invoke-RestMethod -Uri "$base/businesses/me/staff/invitations" -Headers $ownerHeaders -Method Get
Show 'Owner pending staff invitations' $invites.data.Count

# ── 3. Staff (two different staff accounts) ───────────────────
foreach ($staffAccount in @(
        @{ Email = 'chris.mwangi@aurelialuxe.co.ke'; Label = 'Senior Hairstylist (Chris Mwangi)' },
        @{ Email = 'zawadi.omondi@aurelialuxe.co.ke'; Label = 'Braiding Specialist (Zawadi Omondi)' },
        @{ Email = 'ruth.kendi@aurelialuxe.co.ke'; Label = 'Receptionist (Ruth Kendi)' })) {
    $login = Login $staffAccount.Email 'Staff@1234!'
    $h = @{ Authorization = "Bearer $($login.data.accessToken)" }
    Show "$($staffAccount.Label) login" $login.success

    $myAppts = Invoke-RestMethod -Uri "$base/businesses/staff/appointments?pageSize=20" -Headers $h -Method Get
    Show "  assigned appointments" $myAppts.data.totalCount

    $profile = Invoke-RestMethod -Uri "$base/users/profile" -Headers $h -Method Get
    Show "  profile" "$($profile.data.fullName) / $($profile.data.role)"

    $att = Invoke-RestMethod -Uri "$base/attendance/status" -Headers $h -Method Get
    Show "  attendance status" "$($att.data.state) (clockedIn=$($att.data.isClockedIn))"

    # Owner-only endpoint must be refused for staff.
    try {
        Invoke-RestMethod -Uri "$base/businesses/me/staff?pageSize=5" -Headers $h -Method Get | Out-Null
        Show "  owner-only staff list" 'UNEXPECTEDLY ALLOWED'
    } catch {
        Show "  owner-only staff list blocked" $_.Exception.Response.StatusCode.value__
    }

    try {
        Invoke-RestMethod -Uri "$base/businesses/me/dashboard" -Headers $h -Method Get | Out-Null
        Show "  owner-only dashboard blocked" 'UNEXPECTEDLY ALLOWED'
    } catch {
        Show "  owner-only dashboard blocked" $_.Exception.Response.StatusCode.value__
    }
}

# ── 4. Customers (different loyalty states) ───────────────────
$customerAccounts = @(
    @{ Email = 'diana.achieng.b101@demo.punched.app'; Password = 'Customer@1234!'; Label = 'Customer b101 (returning guest, mid-card)' },
    @{ Email = 'hassan.achieng.b111@demo.punched.app'; Password = 'Cust@1234!'; Label = 'Customer b111 (brand new, 0 stamps)' },
    @{ Email = 'james.achieng.b103@demo.punched.app'; Password = 'Cust@1234!'; Label = 'Customer b103 (2 stamps)' },
    @{ Email = 'terry.achieng.b115@demo.punched.app'; Password = 'Cust@1234!'; Label = 'Customer b115 (card complete, unclaimed)' },
    @{ Email = 'yvonne.maina.b108@demo.punched.app'; Password = 'Cust@1234!'; Label = 'Customer b108 (2 rewards redeemed, ready again)' })

foreach ($acct in $customerAccounts) {
    $login = Login $acct.Email $acct.Password
    $h = @{ Authorization = "Bearer $($login.data.accessToken)" }
    Show "$($acct.Label) login" "OK (role=$($login.data.user.role))"

    $cboot = Invoke-RestMethod -Uri "$base/customers/me/dashboard/bootstrap" -Headers $h -Method Get
    Show "  dashboard business" "$($cboot.data.business.name)"
    Show "  capabilities" (($cboot.data.capabilities) -join ', ')
    Show "  summary" ($cboot.data.summary | ConvertTo-Json -Compress)

    $cappts = Invoke-RestMethod -Uri "$base/appointments?pageSize=50" -Headers $h -Method Get
    Show "  appointments" $cappts.data.totalCount

    $cards = Invoke-RestMethod -Uri "$base/cards" -Headers $h -Method Get
    $card = $cards.data | Select-Object -First 1
    if ($card) {
        Show "  loyalty card" "program=$($card.programName) current=$($card.totalStamps)/$($card.stampsRequired) lifetime=$($card.lifetimeStamps) redemptions=$($card.totalRedemptions)"
        $detail = Invoke-RestMethod -Uri "$base/cards/$($card.id)" -Headers $h -Method Get
        Show "  card detail keys" (($detail.data | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name) -join ', ')
    }

    $cnotifications = Invoke-RestMethod -Uri "$base/users/notifications?pageSize=20" -Headers $h -Method Get
    $notifItems = if ($cnotifications.data.items) { $cnotifications.data.items } else { $cnotifications.data }
    Show "  in-app notifications" (@($notifItems).Count)
}

# ── 5. Customer booking flow (authenticated, reuses the first customer token) ──
$login = Login 'diana.achieng.b101@demo.punched.app' 'Customer@1234!'
$ch = @{ Authorization = "Bearer $($login.data.accessToken)" }

$targetDate = (Get-Date).AddDays(3).ToString('yyyy-MM-dd')
$availability = Invoke-RestMethod -Uri "$base/businesses/$businessId/availability?serviceIds=$($svc.data[0].id)&startDate=$targetDate&endDate=$targetDate" -Headers $ch -Method Get
Show 'Booking availability success' $availability.success
$slots = @($availability.data | Where-Object { $_.isAvailable })
Show 'Bookable slots found (3 days out)' $slots.Count

if ($slots.Count -gt 0) {
    $newAppt = Invoke-RestMethod -Uri "$base/appointments" -Method Post -Headers $ch -ContentType 'application/json' -Body (@{
        businessId   = $businessId
        serviceIds   = @($svc.data[0].id)
        staffUserId  = $slots[0].staffUserId
        scheduledAt  = $slots[0].startAtUtc
        note         = 'Seeded verification booking.'
    } | ConvertTo-Json)
    Show 'Customer booked an appointment' $newAppt.success
    if ($newAppt.success) {
        Show '  new appointment id' $newAppt.data.id
        Show '  status' $newAppt.data.status
    }
}
