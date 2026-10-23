# Punched — Canonical Authentication & Authorization Architecture

This document is the single source of truth for **who exists**, **how they come into
existence**, **what proves who they are**, and **what they may touch**. Every other
description of the auth flows in the codebase (comments, UI copy, older plans) is
subordinate to this file.

Scope: API (`PunchedApi`) + UI (`punched-ui`). Tokens are issued by the API only.

---

## 1. Identity model

| Entity | Table | Meaning | Authority |
| --- | --- | --- | --- |
| `UserAuth` | `user_auths` | The **credential** record. One per email. Owns password hash, OTP state, lockout counters, `LastLoginAt`. Its `Id` is the JWT `sub`. | Global |
| `User` | `users` | The **profile + persona** record. One per email, `1:1` with `UserAuth` (joined by email). Holds `FullName` and `Role` (routing hint only — never a tenant grant). Has **no** tenant pointer. | Global |
| `Business` | `businesses` | The **tenant**. `OwnerId` is a denormalized pointer to `Users.Id`, maintained by `BusinessMembershipService`; it is **not** an authorization input. | Tenant |
| `BusinessMembership` | `business_memberships` | **The authoritative user ↔ business relationship.** One row per `(user, business)`, `role ∈ {Owner, Staff, Customer}`, `status ∈ {Active, Invited, Suspended, Left}`. Every tenant decision reads this table. | Tenant |
| `StaffInvitation` | `staff_invitations` | Owner-issued, single-use, hashed-token grant that converts an identity into staff of one business (writes a `Staff` membership on accept). | Tenant |
| `CustomerBusinessEnrollment` | `customer_business_enrollments` | Enrollment/lifecycle detail for customers (pending, inactive, source attribution). Mirrored into a `Customer` membership by `CustomerEnrollmentService`. | Tenant |
| `RefreshToken` | `refresh_tokens` | Server-side session. Rotated on refresh, revocable, one row per issued refresh token. | Identity |

`business_memberships` is the only place a tenant relationship is recorded. The legacy
`users."StaffBusinessId"` column was backfilled into Staff rows and then dropped
(migration `RemoveLegacyStaffBusinessId`); `businesses.owner_id` survives only as a
denormalized pointer that `BusinessMembershipService` keeps in sync with the Owner row.
Invariants the schema enforces itself:

* unique `(user_id, business_id)` — joining twice can never create two rows;
* partial unique index `ux_business_memberships_single_owner` on `business_id`
  `WHERE role = 'Owner' AND status = 'Active'` — exactly one active owner per business;
* `CHECK` constraints on `role` and `status` — no free-text roles;
* FKs are `RESTRICT` on user/business delete, so a membership can never outlive its
  tenant or identity silently.


### 1.1 What the token carries

`JwtTokenService.GenerateAccessToken(UserAuth, User, Guid? businessId, string? bizRole)`

| Claim | Source | Meaning |
| --- | --- | --- |
| `sub` | `UserAuth.Id` | Credential id — the only value accepted as "who is calling" for self-service actions. |
| `userId` | `User.Id` | Profile id — used by controllers that need `Users.Id` (owner lookups, staff link). |
| `email`, `name`, `jti`, `exp`, `iss`, `aud` | `UserAuth` / `User` | Standard. |
| `role` (`ClaimTypes.Role`) | `User.Role` | Global persona: `Customer`, `Business`, `Staff`, `Admin`. Drives `[Authorize(Roles = …)]`. |
| `biz` | `IBusinessMembershipResolver` | Business id the session was authenticated in. **Optional/additive.** |
| `bizRole` | `IBusinessMembershipResolver` | `Owner` \| `Staff` \| `Customer` within `biz` — copied from the **active membership row**. **Optional/additive.** |

`biz`/`bizRole` are additive: tokens without them remain valid, and no authorization
decision depends on them alone — the membership row is always the authority (§4).

Refresh tokens never carry claims. They are rotated on every `POST /v1/auth/refresh-token`
and mirrored into the shared HttpOnly session cookie (`ISessionCookieService`), which
carries **only** the refresh token.

---

## 2. Personas (the four, and only four, ways to be someone)

| Persona | `User.Role` | Created by | Tenant scope | May call |
| --- | --- | --- | --- | --- |
| **Customer** | `Customer` | `POST /v1/auth/register` (self-service, the default) | `business_memberships` rows with `role='Customer'` (0..n businesses) | customer surfaces; `POST /v1/businesses` (promotes to owner) |
| **Owner** | `Business` | `POST /v1/businesses` by an authenticated identity | the `Active` `Owner` membership (`Business.OwnerId` mirrors it) | owner surfaces for **that** business |
| **Staff** | `Staff` | accepting an owner-issued invitation (new or existing identity) | `Active` `Staff` membership rows (0..n businesses) | staff surfaces for **those** businesses |
| **Admin** | `Admin` | out-of-band provisioning only | platform | admin surfaces |

`User.Role` is a **routing hint and `[Authorize(Roles=…)]` input only**; it never grants a
tenant. Tenant authority is always the membership row, resolved per request by
`IBusinessScopeResolver` / `IBusinessMembershipResolver`. A `Staff` user with two memberships
acts in whichever tenant the caller supplies and the membership verifies (§4).

**Persona boundary rule:** a persona is never accepted from a request body. `role` is
still present on `POST /v1/auth/register` for wire compatibility, but any value other
than `Customer` is rejected (`ROLE_NOT_SELF_ASSIGNABLE`) in the validator *and* in
`AuthService.RegisterAsync` — never silently coerced, so a caller can never believe it
created a privileged identity.

**Role precedence:** a membership is only ever *upgraded* — `Owner > Staff > Customer`
(`BusinessMembershipService.EnsureAsync`). Buying from your own business keeps you an
Owner; being invited as staff upgrades a Customer row to Staff; nobody is silently
downgraded by a later association.

---


## 3. Canonical flows

### 3.1 Customer sign-up (self-service)

```
POST /v1/auth/register {email, password, fullName, role:"customer", sourceProvider?, sourceCampaign?}
  → 201 {message}                      UserAuth(unverified) + User(Customer) + OTP email
POST /v1/auth/verify-email {email, code}
  → 200 {accessToken, refreshToken, user}   + shared session cookie
```

If the request arrived on a tenant host (`java-house.punched.app`), the same unit of work
also writes a `CustomerBusinessEnrollment` for that business (`TenantContext`), so the
customer belongs to the tenant from the first sign-in.

### 3.2 Owner provisioning (canonical)

```
POST /v1/auth/register             → Customer identity
POST /v1/auth/verify-email         → signed in (Customer token)
POST /v1/businesses {name, category, location, mpesaNumber, …}
  → 201 {business}                   User.Role Customer → Business + Business insert
                                     + Active Owner membership (ONE SaveChanges)
POST /v1/auth/refresh-token        → access token now carries role=Business
```

`POST /v1/businesses` is the **only** way a Business persona is created. It accepts any
authenticated identity and refuses, by persona:

| Caller state | Result |
| --- | --- |
| `Customer` (no owned business) | 201 — promoted to `Business`, business inserted and `EnsureOwnerAsync` writes the Owner membership + `OwnerId` pointer in the same transaction |
| `Staff` | 400 `ROLE_NOT_ELIGIBLE` |
| `Admin` | 400 `ROLE_NOT_ELIGIBLE` |
| already owns a business | 409 `BUSINESS_EXISTS` |
| deleted identity | 400 `ACCOUNT_DISABLED` |

Because promotion, the business insert and the membership write share one
`SaveChangesAsync`, an identity can never be an owner without a business, a business
without an owner, or an owner without the membership row authorization reads.

### 3.3 Staff onboarding — invitee has **no** account (canonical)

```
Owner:   POST /v1/businesses/me/staff/invitations {email}       → 201 + emailed link
Invitee: GET  /v1/invitations/{token}                           → validation (200; valid=false + code when unusable)
Invitee: POST /v1/invitations/{token}/accept {fullName, password, emailConfirmation}
  → 200 {accessToken(biz, bizRole=Staff), refreshToken, user}   + shared session cookie
```

Creates `UserAuth(verified) + User(Staff)` and an `Active` `Staff` membership in
`business_memberships`, marks the invitation `Accepted`, and issues tenant-aware tokens in
one `SaveChanges`. The invitation *is* the membership grant — the account, the membership and
the consumed token commit together.

If an identity already exists for the invited email, this endpoint returns
`EMAIL_IN_USE` (HTTP 400) — the token alone does not prove the caller controls the
mailbox. The invitee must use §3.4.

### 3.4 Staff onboarding — invitee **has** an account (canonical)

```
Invitee: POST /v1/auth/login                     → session
Invitee: GET  /v1/invitations/{token}            → validation (invited email is shown)
Invitee: POST /v1/invitations/{token}/accept-existing
  → 200 {accessToken(biz, bizRole=Staff), refreshToken, user}   + shared session cookie
```

Authorization is the **session**, not the body: the signed-in identity's email must equal
`StaffInvitation.InvitedEmail` (`EMAIL_MISMATCH` → 403). Refusals:

| Condition | Code | HTTP |
| --- | --- | --- |
| token unknown | `INVALID_TOKEN` | 400 |
| already accepted / revoked / expired | `ALREADY_USED` / `REVOKED` / `EXPIRED` | 409 |
| issuing business deleted | `BUSINESS_NOT_FOUND` | 409 |
| session email ≠ invited email | `EMAIL_MISMATCH` | 403 |
| already an active Staff member of this business | `ALREADY_STAFF` | 409 |
| already an active member of this business with another role | `ALREADY_MEMBER` | 409 |
| identity owns a business | `OWNS_BUSINESS` | 409 |
| identity is `Admin` | `ROLE_NOT_ELIGIBLE` | 409 |
| identity deleted | `ACCOUNT_DISABLED` | 400 |

Staff membership is **not** single-business any more: a staff member may hold `Staff`
memberships in several businesses and switches tenant per request. Only ownership is
exclusive (`OWNS_BUSINESS`), matching the one-owned-business product rule (§7).

### 3.5 Customer enrollment (tenant-aware)

`CustomerEnrollmentService` owns all customer↔business enrollment writes. Invariants:
idempotent per `(userId, businessId)` (an existing row is returned/reactivated, never
duplicated), it never creates a `User`, and it mirrors the enrollment into an `Active`
`Customer` membership through `IBusinessMembershipService.EnsureAsync` in the same
`SaveChanges` — the enrollment row keeps lifecycle/source detail, the membership row is what
authorization reads. Covered by `OnboardingIdempotencyTests` and
`CustomerEnrollmentServiceTests`.

### 3.6 Sessions

| Action | Endpoint | Effect |
| --- | --- | --- |
| Sign in | `POST /v1/auth/login` | Issues access + refresh token; refresh token written to the shared cookie. Tenant claims resolved from the request host. |
| Hydrate another origin | `POST /v1/auth/refresh-token` (body token optional) | Reads the cookie when the body token is absent, rotates the session, returns a token carrying the **current** persona (`role`) and the resolved tenant. |
| Sign out | `POST /v1/auth/logout` | Revokes all refresh tokens for the credential, clears the cookie. |

Persona changes (`Customer → Business`, `Customer → Staff`) take effect on the **next
refresh**: the current access token keeps the persona it was minted with. Clients must
call `refresh-token` immediately after `POST /v1/businesses`; invitation acceptance
already returns fresh tokens, so only business creation needs the extra round trip.

---


## 4. Authorization rules

1. **Deny by default.** `DenyByDefaultAuthorization` + the fallback policy in `Program.cs`:
   every endpoint must declare `[Authorize]`/`[Authorize(Roles=…)]`, `[AllowAnonymous]`, or be
   listed explicitly. Enforced by `AuthorizationPostureTests`.
2. **Tenant from the membership row, never from the client.** Business-scoped
   owner/staff endpoints derive the business from the caller's `Active` membership
   (`IBusinessScopeResolver.GetOwnedBusinessIdAsync` / `GetMemberBusinessIdAsync`) or verify
   membership explicitly (`IBusinessMembershipService.IsActiveMemberAsync`,
   `IsStaffOfAsync`) before touching a client-supplied `businessId`.
   `Businesses.OwnerId` and the removed `Users.StaffBusinessId` are never read for
   authorization; owner-addressed notifications/automation resolve recipients from
   `business_memberships` (`role = 'Owner' AND status = 'Active'`) too.
3. **Module entitlement on top of role.** `[RequireModule("…")]` gates paid features; it is
   never a substitute for the role check.
4. **Self-service acts on `sub`.** Password change, logout and invitation acceptance resolve
   the caller from the JWT `sub`; `userId` is only used for profile/ownership lookups.
5. **Invitations are the only staff entry point.** No endpoint creates a `Staff` user except
   the two acceptance paths, and `POST /v1/businesses` refuses `Staff`/`Admin`.
6. **One writer per relationship.** `IBusinessMembershipService` is the only writer of
   `business_memberships`; it upgrades roles (`Owner > Staff > Customer`), reactivates
   suspended rows, keeps `Business.OwnerId` in sync for Owners and never calls `SaveChanges`
   itself, so provisioning/invitation acceptance stay one transaction.
7. **Membership changes invalidate the scope cache.** `BusinessScopeResolver.InvalidateOwner`
   evicts both the owner and member keys (60 s positive / 2 s negative TTL), so a revoked
   membership cannot keep acting on a cached tenant.

---

## 5. Single-use invitations (how it is actually guaranteed)

* **Token:** 32 random bytes, hex; only the SHA-256 hash is stored (`TokenHash`). Lookup is by
  hash → no plaintext at rest, and `GET /v1/invitations/{token}` always returns 200 with
  `valid=false` + an error code, so tokens cannot be enumerated.
* **Expiry:** `ExpiresAt`, checked by the shared `LifecycleError` gate used by validation and
  **both** acceptance paths, so the accept page and the endpoints can never disagree.
* **Single use under concurrency:** `StaffInvitation.Status` is a `[ConcurrencyCheck]` token,
  so EF emits `UPDATE … WHERE status = @original`. The loser of a race gets
  `DbUpdateConcurrencyException` and is answered `ALREADY_USED`; its new account rows were
  never committed. No application-level read-then-write window decides this.
* **Tenant still exists:** `GetInvitableBusinessAsync` rejects invitations whose business was
  deleted, so a dead tenant cannot grant staff access.
* **Revocation / resend** go through the same status transition, so the same concurrency
  token protects them.

---

## 6. Deprecated compatibility shim

`POST /v1/auth/register-business` remains available **only** so existing clients keep
working. It is a shim, not a second architecture:

* It writes the same rows as the canonical flow — `UserAuth` + `Business`-role `User` +
  `Business` + default subscription — atomically, and grants no capability the canonical
  flow cannot grant.
* Responses carry `Deprecation: true` and `Link: </v1/businesses>; rel="successor-version"`
  (see `AuthController.MarkDeprecated`).
* It cannot create a `Customer`, `Staff` or `Admin` identity, and it cannot add a business to
  an existing identity (`EMAIL_ALREADY_REGISTERED`).
* No new client may adopt it.

### Removal plan (Phase 2 — tracked, not yet executed)

Already migrated (nothing left to build here):

1. **UI — owner wizard.** `useOnboarding.registerBusiness` calls `authApi.register` (Customer)
   and parks the business payload in `sessionStorage`
   (`lib/onboarding/pendingBusiness.ts`, keyed to the e-mail it was entered for).
   `POST /auth/verify-email` then signs that identity in, `provisionPendingBusiness` calls
   `businessesApi.create`, and `useAuthStore.refreshSession()` rotates the token before the
   redirect — so the dashboard never renders a stale Customer persona. A Customer whose
   provisioning failed (or who navigates back) sees a one-click finish banner on
   `/business-register`, which is what keeps a network failure from losing the payload.
2. **UI — invitations.** `InvitationAcceptForm` renders the `accept-existing` path when the
   signed-in e-mail equals the invited address, and otherwise the create-account form with a
   sign-in escape hatch plus `EMAIL_IN_USE` recovery. `mapOnboardingErrorCode` maps the
   persona-conflict codes (`EMAIL_MISMATCH`, `ALREADY_STAFF`, `ALREADY_MEMBER`,
   `OWNS_BUSINESS`) to actionable copy.
3. **E2E.** `registerBusinessOwner` and the wizard helpers now drive
   `register → verify → POST /businesses`, and `05-staff-invitation.spec.ts` covers the
   existing-account (linking) branch end to end.

Still to do when the shim is deleted:

4. **Tests:** `BusinessRegistrationTests` currently assert the shim's atomicity; re-point them
   at the canonical pair (register + create) before deletion.
5. **Delete:** `AuthService.RegisterBusinessAsync`, `RegisterBusinessRequest/Response`,
   `RegisterBusinessRequestValidator`, the controller action, the `MarkDeprecated` helper and
   the shim tests.
6. Keep the `Deprecation` header for one release before the route is removed entirely.

**Rollback for Phase 2:** the shim is additive and self-contained, so restoring the controller
action + service method restores the old contract without any data change — rows created by
either flow are identical.


## 7. Known limitations (deliberate)

1. **One global persona per identity.** `User.Role` is single-valued, so it stays a routing
   hint: a customer who becomes staff of a business keeps a `Staff` token and reaches the
   tenant surfaces, but the *global* `Role` still cannot express "customer here, staff there".
   Tenant authority is unaffected (it comes from the membership row); only UI/`[Authorize]`
   persona routing is coarse.
2. **Ownership is exclusive.** One owned business per identity (`BUSINESS_EXISTS`) and an
   owner cannot also be staff of another business (`OWNS_BUSINESS`) — the `OwnerId` pointer is
   single-valued and the product rule is one owner, one business.
3. **Staff membership is multi-business.** `business_memberships` allows `Staff` rows in
   several businesses; the caller picks the tenant per request and every request re-verifies
   the membership.
4. **No social sign-in.** No provider credentials are configured and no endpoint exists;
   `User.SourceProvider` only records referral attribution today. Deferred.
5. **`customer_business_enrollments` still exists.** It is no longer read for authorization;
   it carries enrollment lifecycle/attribution and is kept in sync with the `Customer`
   membership. Dropping it is a later, purely mechanical contract step.

---

## 8. Delivered: `BusinessMembership` (Phase 3)

The membership table is shipped and authoritative. What landed:

```sql
business_memberships (
  id uuid primary key,
  user_id uuid not null references users(id) on delete restrict,
  business_id uuid not null references businesses(id) on delete restrict,
  role text not null check (role in ('Owner','Staff','Customer')),
  status text not null check (status in ('Active','Invited','Suspended','Left')),
  invited_by_user_id uuid null references users(id) on delete set null,
  joined_at, ended_at, created_at, updated_at,
  unique (user_id, business_id),
  unique (business_id) where role = 'Owner' and status = 'Active'
);
```

| Concern | Shipped implementation |
| --- | --- |
| Only writer | `BusinessMembershipService.EnsureAsync` / `EnsureOwnerAsync` / `EndAsync` (shares `IUnitOfWork`, never saves on its own) |
| Write paths | `BusinessService.CreateBusinessAsync` (Owner), both invitation accept paths (Staff), `CustomerEnrollmentService` (Customer), `AuthService` tenant association, seeding (`BusinessSeedStep` Owner, `StaffLinkSeedStep` Staff) |
| Read paths | `BusinessMembershipResolver` (token `biz`/`bizRole`), `BusinessScopeResolver` (per-request tenant, cached), `BusinessService`/`AppointmentService`/`LoyaltyService`/`ReferralService`/`StampCardService`/`CardDesignService`/`PaymentService`/`NotificationService`/`AutomationRuleEvaluator`/`ScheduledJobSchedulerWorker` |
| Denormalized pointer | `Business.OwnerId` is written only by `EnsureOwnerAsync`, and read only for display/reporting — never to authorize |

### Migrations (expand → migrate → contract)

1. **Expand + backfill:** `AddBusinessMemberships` creates the table, indexes, checks and FKs,
   then backfills in one idempotent statement set (`ON CONFLICT (user_id, business_id) DO NOTHING`)
   with upgrade-only precedence `Owner > Staff > Customer`:
   `businesses.owner_id → Owner/Active`, `users."StaffBusinessId" → Staff/Active`,
   `customer_business_enrollments → Customer/Active` (status mapped to active/inactive).
2. **Contract:** `RemoveLegacyStaffBusinessId` drops `users."StaffBusinessId"` and its index.
   No code path read it any more, so the drop costs nothing and loses nothing (the backfill
   already copied every non-null value).
3. **Snapshot:** `ApplicationDbContextModelSnapshot` matches the model —
   `dotnet ef migrations has-pending-model-changes` reports *“No changes have been made to the
   model since the last migration.”*

### Rollback

`RemoveLegacyStaffBusinessId.Down` re-adds the empty column (no data — the memberships keep
working), and `AddBusinessMemberships.Down` drops the table. Rolling back only the contract
step is safe at any time; rolling back the expand step requires the previous build, because
that build is the last one that could run without memberships.

---

## 9. Enforcement map (where each rule lives)

| Rule | Enforced in |
| --- | --- |
| Registration is Customer-only | `AuthValidators.RegisterRequestValidator`, `AuthService.RegisterAsync` |
| Business creation promotes atomically | `BusinessService.CreateBusinessAsync` (business insert + `EnsureOwnerAsync` in one `SaveChangesAsync`) |
| Membership is canonical | `BusinessMembershipService` (unique writer), `BusinessMembershipConfiguration` (unique index, single-owner partial index, checks, restrict FKs) |
| Tenant scope from membership | `BusinessScopeResolver`, `BusinessMembershipResolver`, `TenantConsistencyMiddleware`, `BusinessMembershipService.IsActiveMemberAsync` / `IsStaffOfAsync` |
| Owner-addressed side effects | `NotificationService`, `AutomationRuleEvaluator`, `ScheduledJobSchedulerWorker`, appointment/loyalty/referral/stamp/payment/card-design services (Owner membership lookup) |
| Only invitations create staff | `InvitationService.CreateStaffInvitationAsync` + both `Accept*` methods |
| Invitation single-use | `StaffInvitation.Status` `[ConcurrencyCheck]` + `DbUpdateConcurrencyException` → `ALREADY_USED` |
| Invitation email ownership | `AcceptStaffInvitationForExistingAccountAsync` (`EMAIL_MISMATCH`) |
| Lifecycle (expired/revoked/used/business-deleted) | `InvitationService.LifecycleError`, `GetInvitableBusinessAsync` |
| Session/token shape | `JwtTokenService.GenerateAccessToken`, `SessionCookieService` |
| Seeded data matches production | `BusinessSeedStep` (Owner membership), `StaffLinkSeedStep` (Staff membership, idempotent) |
| Deny-by-default | `DenyByDefaultAuthorization` + `AuthorizationPostureTests` |

### Verification

```bash
cd punched-api
dotnet build
dotnet ef migrations has-pending-model-changes      # → no pending changes
dotnet test --filter "FullyQualifiedName~AuthModelInvariantsTests|FullyQualifiedName~InvitationServiceTests|FullyQualifiedName~BusinessRegistrationTests|FullyQualifiedName~OnboardingIdempotencyTests|FullyQualifiedName~AuthorizationPostureTests"
```

`AuthModelInvariantsTests` pins the identity model: `User` carries no tenant pointer,
`BusinessMembership` is the only user↔business relation, and `Business.OwnerId` is a
denormalized mirror of the Owner membership.

---
