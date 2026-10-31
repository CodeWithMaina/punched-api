using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

/// <summary>
/// Populates the existing "Aurelia Luxe Hair Atelier" tenant with a complete
/// salon setup: business profile, subscription/entitlements, payment + messaging
/// + attendance configuration, storefront configuration, the service catalogue,
/// the team roster with shifts and service eligibility, and the loyalty program
/// templates.
///
/// Idempotent by construction: every row is addressed by a deterministic id
/// (<see cref="AureliaLuxeDemoIds"/>) or a stable natural key, so re-running
/// updates in place instead of inserting duplicates. The step never creates the
/// business and never touches another tenant.
/// </summary>
public sealed class AureliaLuxeSetupSeedStep : ISeedStep
{
    public string Name => "AureliaLuxeSetup";

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;

        var business = await AureliaLuxeDemoResolver.ResolveBusinessAsync(db, cancellationToken);
        if (business is null)
        {
            context.Logger.LogWarning(
                "Aurelia Luxe demo setup skipped: no business with slug '{Slug}' exists.",
                AureliaLuxeDemoData.Slug);
            context.Report.Warnings.Add("AureliaLuxeSetup skipped: demo business not found.");
            return;
        }

        var demo = await AureliaLuxeDemoResolver.ResolveContextAsync(context, business, cancellationToken);
        if (demo is null)
        {
            context.Logger.LogWarning("Aurelia Luxe demo setup skipped: identities could not be resolved.");
            context.Report.Warnings.Add("AureliaLuxeSetup skipped: identities not resolved.");
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(string key, int value) =>
            counts[key] = counts.TryGetValue(key, out var existing) ? existing + value : value;

        Count("BusinessProfileUpdated", await EnsureBusinessProfileAsync(db, demo, cancellationToken));
        Count("OwnerCredentialsEnsured", await EnsureOwnerAsync(db, demo, cancellationToken));
        Count("StaffMembershipsEnsured", await EnsureStaffAsync(db, demo, cancellationToken));
        Count("CustomerMembershipsEnsured", await EnsureCustomersAsync(db, demo, cancellationToken));
        Count("SubscriptionChanged", await EnsureSubscriptionAsync(db, demo, cancellationToken));
        Count("StaleModuleOverridesRemoved", await EnsureModuleEntitlementsAsync(db, demo, cancellationToken));
        Count("PaymentConfigEnsured", await EnsurePaymentConfigAsync(db, demo, cancellationToken));
        Count("MessagingProfileEnsured", await EnsureMessagingProfileAsync(db, demo, cancellationToken));
        Count("ReminderPolicyEnsured", await EnsureReminderPolicyAsync(db, demo, cancellationToken));
        Count("AttendanceConfigured", await EnsureAttendanceAsync(db, demo, cancellationToken));
        Count("LandingPageConfigured", await EnsureLandingPageAsync(db, demo, cancellationToken));
        Count("AutomationRulesEnsured", await EnsureAutomationRulesAsync(db, demo, cancellationToken));
        Count("ServicesEnsured", await EnsureServiceCatalogAsync(db, demo, cancellationToken));
        Count("StaffServiceAssignmentsEnsured", await EnsureStaffServiceAssignmentsAsync(db, demo, cancellationToken));
        Count("StaffShiftsEnsured", await EnsureStaffShiftsAsync(db, demo, cancellationToken));
        Count("LoyaltyTemplatesEnsured", await EnsureLoyaltyTemplatesAsync(db, demo, cancellationToken));
        Count("LoyaltyEarningRulesEnsured", await EnsureEarningRulesAsync(db, demo, cancellationToken));
        Count("LoyaltyCardsBound", await BindCardsAsync(db, demo, cancellationToken));
        Count("StaffInvitationsEnsured", await EnsureStaffInvitationAsync(db, demo, cancellationToken));

        foreach (var (key, value) in counts)
        {
            context.Report.Counts[$"Aurelia_{key}"] = value;
            if (value > 0)
            {
                context.Logger.LogInformation("Aurelia demo setup · {Key}: {Value}", key, value);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  BUSINESS PROFILE
    // ═══════════════════════════════════════════════════════════

    private static async Task<int> EnsureBusinessProfileAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var business = demo.Business;
        var changed = false;

        if (!string.Equals(business.Category, AureliaLuxeDemoData.Category, StringComparison.Ordinal))
        {
            business.Category = AureliaLuxeDemoData.Category;
            changed = true;
        }

        if (!string.Equals(business.Location, AureliaLuxeDemoData.Location, StringComparison.Ordinal))
        {
            business.Location = AureliaLuxeDemoData.Location;
            changed = true;
        }

        if (!string.Equals(business.PhoneNumber, AureliaLuxeDemoData.PhoneNumber, StringComparison.Ordinal))
        {
            business.PhoneNumber = AureliaLuxeDemoData.PhoneNumber;
            changed = true;
        }

        if (!string.Equals(business.Email, AureliaLuxeDemoData.Email, StringComparison.Ordinal))
        {
            business.Email = AureliaLuxeDemoData.Email;
            changed = true;
        }

        if (!string.Equals(business.Description, AureliaLuxeDemoData.Description, StringComparison.Ordinal))
        {
            business.Description = AureliaLuxeDemoData.Description;
            changed = true;
        }

        if (!string.Equals(business.LogoUrl, AureliaLuxeDemoData.LogoUrl, StringComparison.Ordinal))
        {
            business.LogoUrl = AureliaLuxeDemoData.LogoUrl;
            changed = true;
        }

        if (!string.Equals(business.MpesaNumber, AureliaLuxeDemoData.MpesaNumber, StringComparison.Ordinal))
        {
            business.MpesaNumber = AureliaLuxeDemoData.MpesaNumber;
            changed = true;
        }

        if (!string.Equals(business.TimeZoneId, AureliaLuxeDemoData.TimeZoneId, StringComparison.Ordinal))
        {
            business.TimeZoneId = AureliaLuxeDemoData.TimeZoneId;
            changed = true;
        }

        if (!string.Equals(business.Slug, AureliaLuxeDemoData.Slug, StringComparison.Ordinal))
        {
            business.Slug = AureliaLuxeDemoData.Slug;
            changed = true;
        }

        if (business.BookingOpenHour != AureliaLuxeDemoData.WeekdayOpenHour)
        {
            business.BookingOpenHour = AureliaLuxeDemoData.WeekdayOpenHour;
            changed = true;
        }

        if (business.BookingCloseHour != AureliaLuxeDemoData.WeekdayCloseHour)
        {
            business.BookingCloseHour = AureliaLuxeDemoData.WeekdayCloseHour;
            changed = true;
        }

        if (business.BookingSlotIntervalMinutes != AureliaLuxeDemoData.BookingSlotIntervalMinutes)
        {
            business.BookingSlotIntervalMinutes = AureliaLuxeDemoData.BookingSlotIntervalMinutes;
            changed = true;
        }

        if (business.BookingLeadTimeMinutes != AureliaLuxeDemoData.BookingLeadTimeMinutes)
        {
            business.BookingLeadTimeMinutes = AureliaLuxeDemoData.BookingLeadTimeMinutes;
            changed = true;
        }

        if (business.DefaultDailyGoal != AureliaLuxeDemoData.DefaultDailyStampGoal)
        {
            business.DefaultDailyGoal = AureliaLuxeDemoData.DefaultDailyStampGoal;
            changed = true;
        }

        if (business.DefaultAppointmentDailyGoal != AureliaLuxeDemoData.DefaultAppointmentDailyGoal)
        {
            business.DefaultAppointmentDailyGoal = AureliaLuxeDemoData.DefaultAppointmentDailyGoal;
            changed = true;
        }

        if (!string.Equals(business.DailyGoalType, "stamps", StringComparison.Ordinal))
        {
            business.DailyGoalType = "stamps";
            changed = true;
        }

        if (business.OwnerId != demo.Owner.Id)
        {
            business.OwnerId = demo.Owner.Id;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed ? 1 : 0;
    }

    // ═══════════════════════════════════════════════════════════
    //  IDENTITY: OWNER, STAFF, CUSTOMERS
    // ═══════════════════════════════════════════════════════════

    private static async Task<int> EnsureOwnerAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var created = await AureliaLuxeDemoResolver.EnsureCredentialAsync(
            db, demo.Owner, AureliaLuxeDemoData.OwnerPassword,
            DateTime.UtcNow.AddHours(-3), cancellationToken);

        var membership = await db.BusinessMemberships
            .FirstOrDefaultAsync(m => m.UserId == demo.Owner.Id && m.BusinessId == demo.BusinessId, cancellationToken);

        if (membership is null)
        {
            db.BusinessMemberships.Add(new BusinessMembership
            {
                Id = AureliaLuxeDemoIds.Membership(AureliaLuxeDemoData.OwnerUserKey, "Owner"),
                UserId = demo.Owner.Id,
                BusinessId = demo.BusinessId,
                Role = BusinessMembershipRole.Owner,
                Status = BusinessMembershipStatus.Active,
                JoinedAt = demo.Business.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                CreatedAt = demo.Business.CreatedAt,
            });
            created = true;
        }
        else if (membership.Role != BusinessMembershipRole.Owner ||
                 membership.Status != BusinessMembershipStatus.Active)
        {
            membership.Role = BusinessMembershipRole.Owner;
            membership.Status = BusinessMembershipStatus.Active;
            membership.EndedAt = null;
            membership.UpdatedAt = DateTime.UtcNow;
            created = true;
        }

        if (created)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return created ? 1 : 0;
    }

    private static async Task<int> EnsureStaffAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var staffIds = demo.Staff.Select(s => s.User.Id).ToList();
        var memberships = await db.BusinessMemberships
            .Where(m => m.BusinessId == demo.BusinessId && staffIds.Contains(m.UserId))
            .ToListAsync(cancellationToken);

        var changed = 0;
        var joinedAt = demo.Business.CreatedAt.AddDays(30);

        foreach (var staff in demo.Staff)
        {
            var credentialChanged = await AureliaLuxeDemoResolver.EnsureCredentialAsync(
                db, staff.User, AureliaLuxeDemoData.StaffPassword,
                DateTime.UtcNow.AddDays(-1), cancellationToken);

            if (credentialChanged)
            {
                changed++;
            }

            var membership = memberships.FirstOrDefault(m => m.UserId == staff.User.Id);
            if (membership is null)
            {
                db.BusinessMemberships.Add(new BusinessMembership
                {
                    Id = AureliaLuxeDemoIds.Membership(staff.Key, "Staff"),
                    UserId = staff.User.Id,
                    BusinessId = demo.BusinessId,
                    Role = BusinessMembershipRole.Staff,
                    Status = BusinessMembershipStatus.Active,
                    InvitedByUserId = demo.Owner.Id,
                    JoinedAt = joinedAt,
                    UpdatedAt = DateTime.UtcNow,
                    CreatedAt = joinedAt,
                });
                changed++;
                continue;
            }

            // Owner precedence is never downgraded: only a Customer row is upgraded.
            if (membership.Role == BusinessMembershipRole.Customer)
            {
                membership.Role = BusinessMembershipRole.Staff;
                membership.Status = BusinessMembershipStatus.Active;
                membership.EndedAt = null;
                membership.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
            else if (membership.Status != BusinessMembershipStatus.Active)
            {
                membership.Status = BusinessMembershipStatus.Active;
                membership.EndedAt = null;
                membership.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private static async Task<int> EnsureCustomersAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var customerIds = demo.Customers.Select(c => c.User.Id).ToList();

        var memberships = await db.BusinessMemberships
            .Where(m => m.BusinessId == demo.BusinessId && customerIds.Contains(m.UserId))
            .ToListAsync(cancellationToken);

        var enrollments = await db.CustomerBusinessEnrollments
            .Where(e => e.BusinessId == demo.BusinessId && customerIds.Contains(e.CustomerId))
            .ToListAsync(cancellationToken);

        var program = await db.LoyaltyPrograms
            .Where(p => p.BusinessId == demo.BusinessId && p.Name == AureliaLuxeDemoData.ProgramName)
            .FirstOrDefaultAsync(cancellationToken);

        var cards = await db.LoyaltyCards
            .Where(c => c.BusinessId == demo.BusinessId && customerIds.Contains(c.CustomerId))
            .ToListAsync(cancellationToken);

        var changed = 0;

        foreach (var (index, customer) in demo.Customers.Select((c, i) => (i, c)))
        {
            var enrolledAt = demo.Business.CreatedAt.AddDays(3 + (index * 2));

            var credentialChanged = await AureliaLuxeDemoResolver.EnsureCredentialAsync(
                db, customer.User, index == 0
                    ? AureliaLuxeDemoData.PrimaryCustomerPassword
                    : AureliaLuxeDemoData.CustomerPassword,
                DateTime.UtcNow.AddDays(-(index % 20) - 1), cancellationToken);

            if (credentialChanged)
            {
                changed++;
            }

            // Customer membership is the authoritative grant the customer
            // dashboard resolves; the enrollment row is the loyalty-domain detail.
            if (!memberships.Any(m => m.UserId == customer.User.Id))
            {
                db.BusinessMemberships.Add(new BusinessMembership
                {
                    Id = AureliaLuxeDemoIds.Membership(customer.Key, "Customer"),
                    UserId = customer.User.Id,
                    BusinessId = demo.BusinessId,
                    Role = BusinessMembershipRole.Customer,
                    Status = BusinessMembershipStatus.Active,
                    JoinedAt = enrolledAt,
                    UpdatedAt = DateTime.UtcNow,
                    CreatedAt = enrolledAt,
                });
                changed++;
            }

            if (!enrollments.Any(e => e.CustomerId == customer.User.Id))
            {
                db.CustomerBusinessEnrollments.Add(new CustomerBusinessEnrollment
                {
                    Id = DeterministicSeed.GuidFor("enrollment", $"{AureliaLuxeDemoData.BusinessKey}:{customer.User.Id}"),
                    CustomerId = customer.User.Id,
                    BusinessId = demo.BusinessId,
                    Status = CustomerBusinessEnrollmentStatus.Active,
                    Source = index % 4 == 0 ? "qr" : index % 4 == 1 ? "booking" : index % 4 == 2 ? "referral" : "loyalty",
                    EnrolledAt = enrolledAt,
                    UpdatedAt = enrolledAt,
                    CreatedAt = enrolledAt,
                });
                changed++;
            }

            if (program is not null && !cards.Any(c => c.CustomerId == customer.User.Id))
            {
                db.LoyaltyCards.Add(new LoyaltyCard
                {
                    Id = DeterministicSeed.GuidFor("card", $"{AureliaLuxeDemoData.BusinessKey}:{customer.User.Id}"),
                    CustomerId = customer.User.Id,
                    BusinessId = demo.BusinessId,
                    ProgramId = program.Id,
                    EnrolledAt = enrolledAt,
                    CreatedAt = enrolledAt,
                });
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    // ═══════════════════════════════════════════════════════════
    //  SUBSCRIPTION & ENTITLEMENTS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Moves the tenant onto the plan that grants every module the platform
    /// ships (Enterprise). This is the real entitlement mechanism: no arbitrary
    /// <c>business_modules</c> flag is written, so module gating keeps working
    /// exactly as it does for a paying customer.
    /// </summary>
    private static async Task<int> EnsureSubscriptionAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var plan = await db.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Key == "enterprise", cancellationToken);

        if (plan is null)
        {
            return 0;
        }

        var subscription = await db.BusinessSubscriptions
            .FirstOrDefaultAsync(s => s.BusinessId == demo.BusinessId, cancellationToken);

        var now = DateTime.UtcNow;

        if (subscription is null)
        {
            db.BusinessSubscriptions.Add(new BusinessSubscription
            {
                Id = DeterministicSeed.GuidFor("business-subscription", demo.BusinessId.ToString("N")),
                BusinessId = demo.BusinessId,
                PlanId = plan.Id,
                Status = "active",
                StartsAt = now.AddMonths(-6),
                EndsAt = now.AddMonths(6),
                CreatedAt = now.AddMonths(-6),
            });

            db.SubscriptionAuditLogs.Add(BuildSubscriptionAudit(demo, plan.Id, null, "Enterprise", now));
            await db.SaveChangesAsync(cancellationToken);
            return 1;
        }

        if (subscription.PlanId == plan.Id && subscription.Status == "active")
        {
            return 0;
        }

        var previousPlanName = await db.SubscriptionPlans
            .Where(p => p.Id == subscription.PlanId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(cancellationToken);

        subscription.PlanId = plan.Id;
        subscription.Status = "active";
        subscription.CanceledAt = null;
        subscription.EndsAt = now.AddMonths(6);

        db.SubscriptionAuditLogs.Add(BuildSubscriptionAudit(
            demo, plan.Id, previousPlanName ?? "unknown", "Enterprise", now));

        await db.SaveChangesAsync(cancellationToken);
        return 1;
    }

    /// <summary>
    /// Reconciles the tenant's module entitlements with its subscription.
    ///
    /// A business's plan grants a set of modules, but an administrator can lay a
    /// per-business override on top of it. This tenant carries stale
    /// <c>ADMIN</c> overrides that *deny* modules the Enterprise plan grants
    /// (staff, stamps, rewards, analytics, reviews, gallery), which would leave
    /// half the platform unreachable in the demo. The overrides that contradict
    /// the subscription are removed so entitlement resolution falls back to the
    /// plan — the mechanism the platform uses for every paying customer. Owner
    /// overrides (<c>OVERRIDE</c>) and enabling overrides are left untouched.
    /// </summary>
    private static async Task<int> EnsureModuleEntitlementsAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var planId = await db.BusinessSubscriptions
            .Where(s => s.BusinessId == demo.BusinessId)
            .Select(s => s.PlanId)
            .FirstOrDefaultAsync(cancellationToken);

        if (planId == Guid.Empty)
        {
            return 0;
        }

        var planModuleIds = await db.PlanModules
            .Where(pm => pm.PlanId == planId)
            .Select(pm => pm.ModuleId)
            .ToListAsync(cancellationToken);

        if (planModuleIds.Count == 0)
        {
            return 0;
        }

        var contradictory = await db.BusinessModules
            .Where(bm => bm.BusinessId == demo.BusinessId &&
                         bm.Source == "ADMIN" &&
                         !bm.IsEnabled &&
                         planModuleIds.Contains(bm.ModuleId))
            .ToListAsync(cancellationToken);

        if (contradictory.Count == 0)
        {
            return 0;
        }

        db.BusinessModules.RemoveRange(contradictory);
        await db.SaveChangesAsync(cancellationToken);
        return contradictory.Count;
    }

    private static SubscriptionAuditLog BuildSubscriptionAudit(
        AureliaLuxeDemoContext demo, Guid planId, string? from, string to, DateTime at) =>
        new()
        {
            Id = AureliaLuxeDemoIds.SubscriptionAudit($"{demo.BusinessId:N}:{to}"),
            Action = "BUSINESS_SUBSCRIPTION_CHANGED",
            ActorUserId = demo.Owner.Id,
            TargetBusinessId = demo.BusinessId,
            TargetPlanId = planId,
            PayloadJson = $"{{\"from\":\"{from ?? "none"}\",\"to\":\"{to}\",\"source\":\"demo-seed\"}}",
            Reason = "Aurelia Luxe demo tenant provisioned on the full-feature tier so every module is demonstrable.",
            CreatedAt = at,
        };

    private static async Task<int> EnsurePaymentConfigAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var config = await db.BusinessPaymentConfigs
            .FirstOrDefaultAsync(c => c.BusinessId == demo.BusinessId, cancellationToken);

        var changed = false;

        if (config is null)
        {
            config = new BusinessPaymentConfig
            {
                Id = AureliaLuxeDemoIds.PaymentConfig(),
                BusinessId = demo.BusinessId,
                CreatedAt = demo.Business.CreatedAt,
            };
            db.BusinessPaymentConfigs.Add(config);
            changed = true;
        }

        if (!config.CashEnabled)
        {
            config.CashEnabled = true;
            changed = true;
        }

        if (!config.MpesaEnabled)
        {
            config.MpesaEnabled = true;
            changed = true;
        }

        if (!string.Equals(config.MpesaAccountType, "paybill", StringComparison.Ordinal))
        {
            config.MpesaAccountType = "paybill";
            changed = true;
        }

        if (!string.Equals(config.MpesaShortCode, AureliaLuxeDemoData.MpesaNumber, StringComparison.Ordinal))
        {
            config.MpesaShortCode = AureliaLuxeDemoData.MpesaNumber;
            changed = true;
        }

        // Deliberately NO Daraja credentials: payments stay on the internal
        // demo records and no real M-PESA request can ever be raised.
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed ? 1 : 0;
    }

    // ═══════════════════════════════════════════════════════════
    //  MESSAGING, REMINDERS, ATTENDANCE
    // ═══════════════════════════════════════════════════════════

    /// <summary>Assigns a value only when it differs, reporting whether anything changed.</summary>
    /// <remarks>
    /// Comparison ignores whitespace because PostgreSQL normalises <c>jsonb</c>
    /// on write (<c>[1440,60]</c> round-trips as <c>[1440, 60]</c>); a raw string
    /// comparison would report a change on every run.
    /// </remarks>
    private static bool Assign(string? current, string value, Action<string> assign)
    {
        if (string.Equals(WithoutWhitespace(current), WithoutWhitespace(value), StringComparison.Ordinal))
        {
            return false;
        }

        assign(value);
        return true;
    }

    private static string? WithoutWhitespace(string? value) =>
        value is null ? null : new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static async Task<int> EnsureMessagingProfileAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var profile = await db.BusinessMessagingProfiles
            .FirstOrDefaultAsync(p => p.BusinessId == demo.BusinessId, cancellationToken);

        var changed = false;
        if (profile is null)
        {
            profile = new BusinessMessagingProfile
            {
                Id = AureliaLuxeDemoIds.MessagingProfile(),
                BusinessId = demo.BusinessId,
                CreatedAt = demo.Business.CreatedAt,
            };
            db.BusinessMessagingProfiles.Add(profile);
            changed = true;
        }

        changed |= Assign(profile.DisplayName, AureliaLuxeDemoData.Name, v => profile.DisplayName = v);
        changed |= Assign(profile.PrimaryColor, AureliaLuxeDemoData.BrandPrimaryColor, v => profile.PrimaryColor = v);
        changed |= Assign(profile.ReplyTo, AureliaLuxeDemoData.Email, v => profile.ReplyTo = v);
        changed |= Assign(profile.Timezone, AureliaLuxeDemoData.TimeZoneId, v => profile.Timezone = v);
        changed |= Assign(profile.FooterNote,
            "Aurelia Luxe Hair Atelier · Westlands, Nairobi · hello@aurelialuxe.co.ke · +254712440001",
            v => profile.FooterNote = v);
        changed |= Assign(profile.ReminderPolicyJson, "[1440,60]", v => profile.ReminderPolicyJson = v);

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed ? 1 : 0;
    }

    private static async Task<int> EnsureReminderPolicyAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var policy = await db.BusinessReminderPolicies
            .FirstOrDefaultAsync(p => p.BusinessId == demo.BusinessId, cancellationToken);

        var changed = false;
        if (policy is null)
        {
            policy = new BusinessReminderPolicy { BusinessId = demo.BusinessId };
            db.BusinessReminderPolicies.Add(policy);
            changed = true;
        }

        if (!policy.Enabled)
        {
            policy.Enabled = true;
            changed = true;
        }

        changed |= Assign(policy.OffsetMinutesJson, "[1440,120]", v => policy.OffsetMinutesJson = v);
        changed |= Assign(policy.ChannelsJson, "[\"in_app\",\"email\",\"push\"]", v => policy.ChannelsJson = v);

        if (changed)
        {
            policy.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed ? 1 : 0;
    }

    private static async Task<int> EnsureAttendanceAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var changed = 0;

        var policy = await db.AttendancePolicies
            .FirstOrDefaultAsync(p => p.BusinessId == demo.BusinessId, cancellationToken);

        if (policy is null)
        {
            db.AttendancePolicies.Add(new AttendancePolicy
            {
                Id = DeterministicSeed.GuidFor("attendance-policy", demo.BusinessId.ToString("N")),
                BusinessId = demo.BusinessId,
                Mode = AttendanceMode.Standard,
                RequiredVerificationsJson = "[\"AUTHENTICATED_USER\",\"QR\"]",
                IsActive = true,
                UpdatedAt = DateTime.UtcNow,
                CreatedAt = demo.Business.CreatedAt,
            });
            changed++;
        }
        else if (!policy.IsActive)
        {
            policy.IsActive = true;
            policy.UpdatedAt = DateTime.UtcNow;
            changed++;
        }

        const string locationName = "Westlands Atelier — Front Desk";
        var location = await db.AttendanceLocations
            .FirstOrDefaultAsync(l => l.BusinessId == demo.BusinessId && l.Name == locationName, cancellationToken);

        if (location is null)
        {
            location = new AttendanceLocation
            {
                Id = AureliaLuxeDemoIds.AttendanceLocation(locationName),
                BusinessId = demo.BusinessId,
                Name = locationName,
                Description = "Reception counter where the printed clock-in QR code is displayed.",
                IsActive = true,
                CreatedByUserId = demo.Owner.Id,
                CreatedAt = demo.Business.CreatedAt.AddDays(14),
            };
            db.AttendanceLocations.Add(location);
            changed++;
        }

        var credentialExists = await db.AttendanceQrCredentials
            .AnyAsync(c => c.BusinessId == demo.BusinessId && c.AttendanceLocationId == location.Id, cancellationToken);

        if (!credentialExists)
        {
            db.AttendanceQrCredentials.Add(new AttendanceQrCredential
            {
                Id = AureliaLuxeDemoIds.AttendanceCredential(locationName),
                BusinessId = demo.BusinessId,
                AttendanceLocationId = location.Id,
                TokenHash = AureliaLuxeDemoIds.Token("attendance-qr", locationName),
                Status = AttendanceCredentialStatus.Active,
                CreatedByUserId = demo.Owner.Id,
                CreatedAt = demo.Business.CreatedAt.AddDays(14),
            });
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    // ═══════════════════════════════════════════════════════════
    //  STOREFRONT (landing page configuration)
    // ═══════════════════════════════════════════════════════════

    private static async Task<int> EnsureLandingPageAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var config = LandingPageDefaults.Create();
        config.Hero.ShowLogo = true;
        config.Hero.TitleOverride = AureliaLuxeDemoData.Name;
        config.Hero.TaglineOverride = "Premium hair rituals in the heart of Westlands";
        config.Hero.DescriptionOverride = AureliaLuxeDemoData.Description;
        config.PrimaryCta.Enabled = true;
        config.PrimaryCta.Type = "book";
        config.PrimaryCta.Label = "Book your appointment";
        config.SecondaryCta.Enabled = true;
        config.SecondaryCta.Type = "services";
        config.SecondaryCta.Label = "Explore the menu";

        SetSection(config, "about", "About the atelier",
            $"{AureliaLuxeDemoData.Description} {AureliaLuxeDemoData.BookingPolicy}");
        SetSection(config, "services", "The Aurelia menu",
            "Styling, braiding, treatments, extensions and natural-hair care — priced in KES.");
        SetSection(config, "loyalty", "Aurelia Gold loyalty",
            $"Collect {AureliaLuxeDemoData.StampCardStampsRequired} stamps and earn "
            + $"{AureliaLuxeDemoData.StampCardRewardDescription}.");
        SetSection(config, "booking", "Book your chair", AureliaLuxeDemoData.CancellationPolicy);
        SetSection(config, "contact", "Visit us",
            $"{AureliaLuxeDemoData.AddressLine} · {AureliaLuxeDemoData.PhoneNumber}");
        SetSection(config, "gallery", "Inside the atelier", "A look at the Aurelia chair and studio.");
        SetSection(config, "reviews", "What our guests say", "Verified reviews from completed appointments.");

        var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

        var row = await db.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(c => c.BusinessId == demo.BusinessId, cancellationToken);

        if (row is null)
        {
            db.BusinessLandingPageConfigs.Add(new BusinessLandingPageConfig
            {
                Id = AureliaLuxeDemoIds.LandingPage(),
                BusinessId = demo.BusinessId,
                Version = 1,
                UpdatedAt = DateTime.UtcNow,
                ConfigJson = json,
                CreatedAt = demo.Business.CreatedAt,
            });
            await db.SaveChangesAsync(cancellationToken);
            return 1;
        }

        if (string.Equals(row.ConfigJson, json, StringComparison.Ordinal))
        {
            return 0;
        }

        row.ConfigJson = json;
        row.Version += 1;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return 1;
    }

    private static void SetSection(
        PunchedApi.Application.DTOs.LandingPageConfigDto config,
        string key, string title, string description)
    {
        if (!config.Sections.TryGetValue(key, out var section) || section is null)
        {
            return;
        }

        section.Enabled = true;
        section.Title = title;
        section.Description = description;
    }

    // ═══════════════════════════════════════════════════════════
    //  AUTOMATION RULES
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Adds the atelier's own automation rule alongside the platform defaults.
    /// It is created DISABLED, exactly like every default rule: enabling it is
    /// an owner decision, and nothing is delivered until they do.
    /// </summary>
    private static async Task<int> EnsureAutomationRulesAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        const string ruleKey = "aurelia:payment-failed-nudge";
        var id = AureliaLuxeDemoIds.AutomationRule(ruleKey);

        var exists = await db.AutomationRules.AnyAsync(r => r.Id == id, cancellationToken);
        if (exists)
        {
            return 0;
        }

        db.AutomationRules.Add(new AutomationRule
        {
            Id = id,
            BusinessId = demo.BusinessId,
            Name = "Failed payment follow-up",
            Description = "Invites the guest to settle an unpaid atelier appointment after a failed payment.",
            Trigger = "payment.failed",
            ConditionsJson = "[]",
            ActionsJson = "[{\"type\":\"send_notification\",\"payload\":{\"type\":\"payment.failed_nudge\"}}]",
            RecipientsJson = "[{\"category\":\"customer\",\"refId\":\"$customer_id\"}]",
            Enabled = false,
            Version = 1,
            DailyActionCap = 5,
            FrequencyCapMinutes = 1440,
            QuietHoursStart = new TimeSpan(20, 0, 0),
            QuietHoursEnd = new TimeSpan(8, 0, 0),
            CreatedAt = demo.Business.CreatedAt.AddDays(20),
            UpdatedAt = demo.Business.CreatedAt.AddDays(20),
        });

        await db.SaveChangesAsync(cancellationToken);
        return 1;
    }

    // ═══════════════════════════════════════════════════════════
    //  SERVICE CATALOGUE
    // ═══════════════════════════════════════════════════════════

    private static async Task<int> EnsureServiceCatalogAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var existing = await db.ServiceCatalogItems
            .Where(s => s.BusinessId == demo.BusinessId)
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var changed = 0;
        var index = 0;

        foreach (var definition in AureliaLuxeDemoData.Services)
        {
            var id = AureliaLuxeDemoIds.Service(definition.Key);
            var createdAt = demo.Business.CreatedAt.AddDays(10 + index);
            index++;

            if (!existing.TryGetValue(id, out var service))
            {
                service = new ServiceCatalogItem
                {
                    Id = id,
                    BusinessId = demo.BusinessId,
                    CreatedAt = createdAt,
                };
                db.ServiceCatalogItems.Add(service);
                existing[id] = service;
                changed++;
            }

            // Category has no column of its own; it is the catalogue's own
            // grouping label and travels with the customer-facing description.
            var description = $"{definition.Category} · {definition.Description}";

            if (!string.Equals(service.Name, definition.Name, StringComparison.Ordinal) ||
                !string.Equals(service.Description, description, StringComparison.Ordinal) ||
                service.DurationMinutes != definition.DurationMinutes ||
                service.Price != definition.Price ||
                service.IsActive != definition.IsActive ||
                service.Showcase != definition.Showcase)
            {
                service.Name = definition.Name;
                service.Description = description;
                service.DurationMinutes = definition.DurationMinutes;
                service.Price = definition.Price;
                service.IsActive = definition.IsActive;
                service.Showcase = definition.Showcase;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    // ═══════════════════════════════════════════════════════════
    //  STAFF ELIGIBILITY & ROSTER
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Records which bookable services each team member may perform. This is the
    /// platform's own eligibility table, so it drives the booking wizard's staff
    /// step rather than being decorative data.
    /// </summary>
    private static async Task<int> EnsureStaffServiceAssignmentsAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var staffIds = demo.Staff.Select(s => s.User.Id).ToList();

        var existing = await db.StaffServiceAssignments
            .Where(a => a.BusinessId == demo.BusinessId && staffIds.Contains(a.StaffUserId))
            .Select(a => new { a.StaffUserId, a.ServiceCatalogItemId })
            .ToListAsync(cancellationToken);

        var pairs = existing
            .Select(a => (a.StaffUserId, a.ServiceCatalogItemId))
            .ToHashSet();

        var changed = 0;
        var createdAt = demo.Business.CreatedAt.AddDays(21);

        foreach (var staff in demo.Staff)
        {
            foreach (var serviceKey in staff.Definition.ServiceKeys)
            {
                var serviceId = AureliaLuxeDemoIds.Service(serviceKey);
                if (!pairs.Add((staff.User.Id, serviceId)))
                {
                    continue;
                }

                db.StaffServiceAssignments.Add(new StaffServiceAssignment
                {
                    Id = AureliaLuxeDemoIds.StaffAssignment(staff.Key, serviceKey),
                    StaffUserId = staff.User.Id,
                    ServiceCatalogItemId = serviceId,
                    BusinessId = demo.BusinessId,
                    CreatedAt = createdAt,
                });
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    /// <summary>
    /// Publishes the rota for a window around today: Monday–Saturday working,
    /// Sunday explicitly not working (so the availability engine closes the day),
    /// with each specialist on their own shift.
    /// </summary>
    private static async Task<int> EnsureStaffShiftsAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-AureliaLuxeDemoData.ShiftWindowDays);
        var to = today.AddDays(AureliaLuxeDemoData.ShiftWindowDays);

        var staffIds = demo.Staff.Select(s => s.User.Id).ToList();
        var existing = await db.StaffShifts
            .Where(s => s.BusinessId == demo.BusinessId && staffIds.Contains(s.StaffUserId) &&
                        s.Date >= from && s.Date <= to)
            .Select(s => new { s.StaffUserId, s.Date })
            .ToListAsync(cancellationToken);

        var published = existing
            .Select(s => (s.StaffUserId, s.Date))
            .ToHashSet();

        var changed = 0;

        foreach (var staff in demo.Staff)
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (!published.Add((staff.User.Id, date)))
                {
                    continue;
                }

                var isSunday = date.DayOfWeek == DayOfWeek.Sunday;
                var isSaturday = date.DayOfWeek == DayOfWeek.Saturday;

                var start = staff.Definition.ShiftStartHour;
                var end = staff.Definition.ShiftEndHour;

                if (isSaturday)
                {
                    start = Math.Min(start, AureliaLuxeDemoData.SaturdayOpenHour);
                    end = Math.Min(end, AureliaLuxeDemoData.SaturdayCloseHour);
                }

                db.StaffShifts.Add(new StaffShift
                {
                    Id = AureliaLuxeDemoIds.Shift(staff.Key, date),
                    StaffUserId = staff.User.Id,
                    BusinessId = demo.BusinessId,
                    Date = date,
                    StartHour = start,
                    EndHour = end,
                    IsWorking = !isSunday,
                    CreatedAt = demo.Business.CreatedAt.AddDays(28),
                });
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    // ═══════════════════════════════════════════════════════════
    //  LOYALTY TEMPLATES
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the stamp-card template, the reward it unlocks and the earning
    /// rules that award stamps — then binds every enrolled card to the template
    /// with a frozen rules snapshot (the platform's own snapshot columns).
    /// </summary>
    private static async Task<int> EnsureLoyaltyTemplatesAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var program = await db.LoyaltyPrograms
            .Where(p => p.BusinessId == demo.BusinessId && p.Name == AureliaLuxeDemoData.ProgramName)
            .FirstOrDefaultAsync(cancellationToken);

        if (program is null)
        {
            return 0;
        }

        var changed = 0;
        var createdAt = program.CreatedAt;

        if (program.IsActive != true ||
            program.Status != ProgramStatus.Active ||
            program.StampsRequired != AureliaLuxeDemoData.StampCardStampsRequired ||
            program.RewardValue != AureliaLuxeDemoData.StampCardRewardValue ||
            program.RewardExpirationHours != AureliaLuxeDemoData.RewardExpirationHours ||
            !string.Equals(program.RewardDescription, AureliaLuxeDemoData.StampCardRewardDescription, StringComparison.Ordinal) ||
            !string.Equals(program.ProgramType, ProgramTypes.Stamp, StringComparison.Ordinal))
        {
            program.IsActive = true;
            program.Status = ProgramStatus.Active;
            program.StampsRequired = AureliaLuxeDemoData.StampCardStampsRequired;
            program.RewardValue = AureliaLuxeDemoData.StampCardRewardValue;
            program.RewardDescription = AureliaLuxeDemoData.StampCardRewardDescription;
            program.RewardExpirationHours = AureliaLuxeDemoData.RewardExpirationHours;
            program.ProgramType = ProgramTypes.Stamp;
            changed++;
        }

        // ── Stamp card template ─────────────────────────────────
        var cardId = AureliaLuxeDemoIds.StampCard(program.Name, "atelier-gold-card");
        var card = await db.StampCards.FirstOrDefaultAsync(c => c.Id == cardId, cancellationToken);

        if (card is null)
        {
            card = new StampCard
            {
                Id = cardId,
                ProgramId = program.Id,
                BusinessId = demo.BusinessId,
                CreatedAt = createdAt,
            };
            db.StampCards.Add(card);
            changed++;
        }

        var cardDescription = $"Complete {AureliaLuxeDemoData.StampCardStampsRequired} atelier visits to unlock "
            + $"{AureliaLuxeDemoData.StampCardRewardDescription}.";

        if (!string.Equals(card.Name, "Aurelia Gold Card", StringComparison.Ordinal) ||
            !string.Equals(card.Description, cardDescription, StringComparison.Ordinal) ||
            card.StampsRequired != AureliaLuxeDemoData.StampCardStampsRequired ||
            !string.Equals(card.RewardDescription, AureliaLuxeDemoData.StampCardRewardDescription, StringComparison.Ordinal) ||
            card.RewardValue != AureliaLuxeDemoData.StampCardRewardValue ||
            card.Status != StampCardStatus.Active)
        {
            card.Name = "Aurelia Gold Card";
            card.Description = cardDescription;
            card.StampsRequired = AureliaLuxeDemoData.StampCardStampsRequired;
            card.RewardDescription = AureliaLuxeDemoData.StampCardRewardDescription;
            card.RewardValue = AureliaLuxeDemoData.StampCardRewardValue;
            card.Status = StampCardStatus.Active;
            card.UpdatedAt = DateTime.UtcNow;
            changed++;
        }

        // ── Reward ──────────────────────────────────────────────
        var rewardId = AureliaLuxeDemoIds.Reward("atelier-service-credit");
        var reward = await db.Rewards.FirstOrDefaultAsync(r => r.Id == rewardId, cancellationToken);

        if (reward is null)
        {
            reward = new Reward
            {
                Id = rewardId,
                ProgramId = program.Id,
                BusinessId = demo.BusinessId,
                CreatedAt = createdAt,
            };
            db.Rewards.Add(reward);
            changed++;
        }

        if (!string.Equals(reward.Name, "KES 1,800 service credit", StringComparison.Ordinal) ||
            reward.RequiredStamps != AureliaLuxeDemoData.StampCardStampsRequired ||
            reward.Value != AureliaLuxeDemoData.StampCardRewardValue ||
            reward.StampsToConsume != AureliaLuxeDemoData.StampCardStampsRequired ||
            reward.Status != RewardStatus.Active ||
            reward.Type != RewardType.FixedDiscount ||
            reward.ExpirationHours != AureliaLuxeDemoData.RewardExpirationHours)
        {
            reward.Name = "KES 1,800 service credit";
            reward.Description = "Redeem against any single service at the atelier. One credit per completed card.";
            reward.RequiredStamps = AureliaLuxeDemoData.StampCardStampsRequired;
            reward.Value = AureliaLuxeDemoData.StampCardRewardValue;
            reward.StampsToConsume = AureliaLuxeDemoData.StampCardStampsRequired;
            reward.Type = RewardType.FixedDiscount;
            reward.Status = RewardStatus.Active;
            reward.ExpirationHours = AureliaLuxeDemoData.RewardExpirationHours;
            changed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Binds every enrolled card to the stamp-card template with a frozen rules
    /// snapshot, and records the customer's ownership of the template.
    /// </summary>
    private static async Task<int> BindCardsAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var card = await db.StampCards
            .FirstOrDefaultAsync(c => c.BusinessId == demo.BusinessId && c.Status == StampCardStatus.Active, cancellationToken);

        if (card is null)
        {
            return 0;
        }

        var cards = await db.LoyaltyCards
            .Where(c => c.BusinessId == demo.BusinessId)
            .ToListAsync(cancellationToken);

        var bound = 0;
        foreach (var loyaltyCard in cards)
        {
            if (loyaltyCard.StampCardId == card.Id &&
                loyaltyCard.RequiredStamps == card.StampsRequired &&
                loyaltyCard.RulesVersion == card.RulesVersion)
            {
                continue;
            }

            loyaltyCard.StampCardId = card.Id;
            loyaltyCard.RequiredStamps = card.StampsRequired;
            loyaltyCard.RulesVersion = card.RulesVersion;
            bound++;
        }

        var owned = (await db.CustomerStampCards
            .Where(c => c.StampCardId == card.Id)
            .Select(c => c.CustomerId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var ownershipCreated = 0;
        foreach (var customer in demo.Customers)
        {
            if (!owned.Add(customer.User.Id))
            {
                continue;
            }

            db.CustomerStampCards.Add(new CustomerStampCard
            {
                Id = DeterministicSeed.GuidFor("customer-stamp-card", $"{card.Id}:{customer.User.Id}"),
                CustomerId = customer.User.Id,
                StampCardId = card.Id,
                Status = CustomerStampCardStatus.Active,
                JoinedAt = card.CreatedAt.AddDays(7),
                UpdatedAt = card.CreatedAt.AddDays(7),
                CreatedAt = card.CreatedAt.AddDays(7),
            });
            ownershipCreated++;
        }

        if (bound > 0 || ownershipCreated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return bound + ownershipCreated;
    }

    private static async Task<int> EnsureEarningRuleAsync(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        Guid programId,
        string ruleKey,
        EarningSource source,
        StampingMode mode,
        int stampAmount,
        string description,
        CancellationToken cancellationToken)
    {
        var id = AureliaLuxeDemoIds.EarningRule(ruleKey);
        var rule = await db.LoyaltyEarningRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        var createdAt = demo.Business.CreatedAt.AddDays(9);

        if (rule is null)
        {
            rule = new LoyaltyEarningRule
            {
                Id = id,
                ProgramId = programId,
                BusinessId = demo.BusinessId,
                CreatedAt = createdAt,
            };
            db.LoyaltyEarningRules.Add(rule);
        }

        if (rule.Source == source && rule.StampingMode == mode && rule.StampAmount == stampAmount &&
            rule.Status == EarningRuleStatus.Active && rule.ActivatedAt is not null &&
            string.Equals(rule.Description, description, StringComparison.Ordinal))
        {
            return 0;
        }

        rule.ProgramId = programId;
        rule.BusinessId = demo.BusinessId;
        rule.Source = source;
        rule.StampingMode = mode;
        rule.StampAmount = stampAmount;
        rule.Status = EarningRuleStatus.Active;
        rule.Description = description;
        rule.ActivatedAt ??= createdAt;
        return 1;
    }

    // ═══════════════════════════════════════════════════════════
    //  LOYALTY EARNING RULES
    // ═══════════════════════════════════════════════════════════

    private static async Task<int> EnsureEarningRulesAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var program = await db.LoyaltyPrograms
            .Where(p => p.BusinessId == demo.BusinessId && p.Name == AureliaLuxeDemoData.ProgramName)
            .FirstOrDefaultAsync(cancellationToken);

        if (program is null)
        {
            return 0;
        }

        var changed = 0;
        changed += await EnsureEarningRuleAsync(db, demo, program.Id, "appointment", EarningSource.Appointment,
            StampingMode.Automatic, 1, "One stamp for every completed atelier appointment.", cancellationToken);
        changed += await EnsureEarningRuleAsync(db, demo, program.Id, "referral", EarningSource.Referral,
            StampingMode.Automatic, 2, "Two bonus stamps when an invited guest completes their first visit.", cancellationToken);
        changed += await EnsureEarningRuleAsync(db, demo, program.Id, "service-signature", EarningSource.Service,
            StampingMode.Manual, 1, "Staff-awarded stamp for a signature atelier service.", cancellationToken);

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    // ═══════════════════════════════════════════════════════════
    //  STAFF ONBOARDING
    // ═══════════════════════════════════════════════════════════
    /// <summary>
    /// Leaves one pending invitation in the atelier's inbox so the
    /// invitation-only staff onboarding flow is demonstrable. No user account is
    /// created — accepting the invitation is what would create one.
    /// </summary>
    private static async Task<int> EnsureStaffInvitationAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        const string invitedEmail = "wanjiku.kamau@aurelialuxe.co.ke";
        var id = AureliaLuxeDemoIds.StaffInvitation(invitedEmail);

        var exists = await db.StaffInvitations.AnyAsync(i => i.Id == id, cancellationToken);
        if (exists)
        {
            return 0;
        }

        db.StaffInvitations.Add(new StaffInvitation
        {
            Id = id,
            BusinessId = demo.BusinessId,
            InvitedEmail = invitedEmail,
            InvitingUserId = demo.Owner.Id,
            TokenHash = AureliaLuxeDemoIds.Token("staff-invite", invitedEmail),
            Status = InvitationStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ResendCount = 0,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });

        await db.SaveChangesAsync(cancellationToken);
        return 1;
    }
}
