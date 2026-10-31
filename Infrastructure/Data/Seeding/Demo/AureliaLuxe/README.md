# Aurelia Luxe Hair Atelier — demo tenant seeder

This folder contains the dedicated seed implementation that turns the existing
**Aurelia Luxe Hair Atelier** business into a fully operational demonstration
salon. It adopts the tenant that already exists (matched by slug
`aurelia-luxe-hair-atelier`) and never creates a second business.

## Layout

| File | Responsibility |
|------|----------------|
| `AureliaLuxeDemoData.cs` | Static, deterministic definitions: profile, opening hours, policies, the 29-service catalogue, the 9-person roster with service eligibility and shift hours, loyalty/reward constants, activity windows. |
| `AureliaLuxeDemoIds.cs` | Deterministic id factory + demo-scoped RNG + resolved tenant handle types. The ids are the marker that separates seeder-managed rows from rows a human created in the app. |
| `AureliaLuxeDemoResolver.cs` | Resolves the existing business and its identities (from the in-flight seed context, else the database), and guarantees the demo credentials are usable. |
| `AureliaLuxeSetupSeedStep.cs` | Configuration domain: profile, subscription/entitlements, payment + messaging + reminder + attendance configuration, storefront config, automation rule, service catalogue, staff eligibility, rota, loyalty templates, one pending staff invitation. |
| `AureliaLuxeActivitySeedStep.cs` | Operational domain orchestrator + the safe refresh (deletes only its own rows for this tenant). |
| `AureliaLuxeReferralSeeder.cs` | Referral links + referrals in every lifecycle state; reports the reward stamps it earned. |
| `AureliaLuxeLoyaltySeeder.cs` | Rebuilds each guest's loyalty state from an explicit credit/debit ledger so `TotalStamps`, the stamp ledger, entitlements and redemptions always agree. |
| `AureliaLuxeAppointmentSeeder.cs` | Appointment book over the previous 90 days and next 30 days, generated per staff per day with an advancing cursor so the `appointments_no_staff_overlap` exclusion constraint can never fire. |
| `AureliaLuxePaymentSeeder.cs` | Payments and attempts in every supported state; amounts are computed from the appointment's own service snapshots. |
| `AureliaLuxeOperationsSeeder.cs` | Attendance sessions/events, reviews, in-app notifications, the notification ledger, customer segments and business insights. |

## Running it

```powershell
# From the repository root
dotnet run --project .\PunchedApi\PunchedApi.csproj -- db seed
```

Seeding is idempotent: re-running resolves the same deterministic ids and
updates in place. The two Aurelia steps are registered after the platform's
standard steps and before `ValidationAndReport`, so the report is complete.

### Refreshing the demo data

Just run the command again. `AureliaLuxeActivitySeedStep` starts by deleting the
rows it previously created for this tenant (`ExecuteDelete` on deterministic
ids) and then regenerates them, so appointment/attendance dates always straddle
"today". Rows created by a human through the app have different ids and are
never touched.

To seed only this tenant (faster, and leaves the other demo businesses alone):

```powershell
$env:Seed__BusinessCount='1'
dotnet run --project .\PunchedApi\PunchedApi.csproj -- db seed
```

### Analytics aggregates

The dashboard reads raw appointments/payments/stamps, so it is meaningful
immediately. The daily aggregate tables are filled with the platform's own
backfill command:

```powershell
dotnet run --project .\PunchedApi\PunchedApi.csproj -- db backfill `
  --from 2026-09-16 --to 2026-10-30 --business 1bcf4cf4-aacc-4e30-8245-d18ba25a79e2
```

## Development credentials

All accounts are development/demo only. Passwords are BCrypt-hashed through the
platform's own auth path — nothing is stored in plaintext.

| Role | Email | Password |
|------|-------|----------|
| Owner (Elena Njeri) | `elena.njeri@aurelialuxe.co.ke` | `Owner@1234!` |
| Salon Manager (Ivy Wambui) | `ivy.wambui@aurelialuxe.co.ke` | `Staff@1234!` |
| Senior Hairstylist (Chris Mwangi) | `chris.mwangi@aurelialuxe.co.ke` | `Staff@1234!` |
| Braiding Specialist (Zawadi Omondi) | `zawadi.omondi@aurelialuxe.co.ke` | `Staff@1234!` |
| Front Desk (Ruth Kendi) | `ruth.kendi@aurelialuxe.co.ke` | `Staff@1234!` |
| Customer (mid-card) | `diana.achieng.b101@demo.punched.app` | `Customer@1234!` |
| Customer (brand new, 0 stamps) | `hassan.achieng.b111@demo.punched.app` | `Cust@1234!` |
| Customer (2 stamps) | `james.achieng.b103@demo.punched.app` | `Cust@1234!` |
| Customer (card complete, unclaimed) | `terry.achieng.b115@demo.punched.app` | `Cust@1234!` |
| Customer (2 rewards redeemed, ready again) | `yvonne.maina.b108@demo.punched.app` | `Cust@1234!` |

Every seeded customer uses `Cust@1234!` except the first (`Customer@1234!`).
The other eight team members (`staff-1-barber`, `staff-1-nail-tech`,
`staff-1-natural-hair`, `staff-1-treatment`, `staff-1-junior`, plus the three
above) all share `Staff@1234!`.

## Deliberate safety choices

- **No external delivery.** In-app notifications are internal rows; the
  notification ledger is written only in terminal states (`sent` / `failed`) so
  the outbox worker can never pick one up, and no reminder job is left pending.
  Automation rules are seeded **disabled**.
- **No real payments.** Payments are internal records; `business_payment_configs`
  is stored with cash + M-PESA enabled but **without Daraja credentials**, so
  even a manual retry cannot raise a real M-PESA request.
- **No fabricated storage.** Media rows are upload-backed, so the demo uses
  hosted logo/cover URLs rather than inventing object-storage state.
- **Subscription-driven entitlements.** The tenant is moved onto the Enterprise
  plan; stale `ADMIN` module overrides that *denied* modules the plan grants are
  removed so entitlements resolve from the plan, exactly as for a paying tenant.
