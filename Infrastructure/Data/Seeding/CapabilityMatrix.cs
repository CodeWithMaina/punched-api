namespace PunchedApi.Infrastructure.Data.Seeding;

public static class CapabilityMatrix
{
    public static IReadOnlyList<SeedModuleCapability> Build() =>
    [
        new SeedModuleCapability { Module = "Businesses", Status = "Implemented", Notes = "Business profile, contact details, location, opening hours, booking window, timezone, daily goals." },
        new SeedModuleCapability { Module = "Users and Authentication", Status = "Implemented", Notes = "users, user_auth (BCrypt), refresh_tokens; demo accounts are verified and unlocked." },
        new SeedModuleCapability { Module = "Staff Linking", Status = "Implemented", Notes = "business_memberships (role=Staff, status=Active) is the only tenant staff mapping." },
        new SeedModuleCapability { Module = "Customer Enrollment", Status = "Implemented", Notes = "business_memberships (Customer) + customer_business_enrollments; both are required for the customer dashboard." },
        new SeedModuleCapability { Module = "Services Catalog", Status = "Implemented", Notes = "service_catalog_items incl. IsActive/Showcase, plus staff_services eligibility and staff_shifts rota." },
        new SeedModuleCapability { Module = "Appointments", Status = "Implemented", Notes = "appointments + appointment_resources snapshots + appointment_status_history + reschedule_requests; generated without violating the no-staff-overlap exclusion constraint." },
        new SeedModuleCapability { Module = "Payments", Status = "Implemented", Notes = "payments + payment_attempts in every state (success, deposit+balance, created, awaiting customer, failed, refunded). No provider is contacted and no Daraja credentials are stored." },
        new SeedModuleCapability { Module = "Loyalty", Status = "Implemented", Notes = "loyalty_programs, stamp_cards, rewards, loyalty_earning_rules, loyalty_cards, customer_stamp_cards, stamps, stamp_transactions, stamp_adjustments, reward_entitlements, redemptions." },
        new SeedModuleCapability { Module = "Referrals", Status = "Implemented", Notes = "referral_programs, referral_links, referrals in every lifecycle state; rewarded referrals credit bonus stamps." },
        new SeedModuleCapability { Module = "Attendance", Status = "Implemented", Notes = "attendance_policies, attendance_locations, attendance_qr_credentials, attendance_sessions, attendance_events (late arrivals, absences, one open shift)." },
        new SeedModuleCapability { Module = "Reviews", Status = "Implemented", Notes = "reviews against real completed appointments, mostly published with some hidden." },
        new SeedModuleCapability { Module = "Notifications", Status = "Implemented", Notes = "in-app notifications plus the notifications ledger. Ledger rows are written terminal (sent/failed) so no real email/SMS/push is ever delivered." },
        new SeedModuleCapability { Module = "Analytics", Status = "Implemented", Notes = "Raw appointments/payments/stamps feed the dashboard; business_daily_analytics and staff_daily_analytics are populated by the supported backfill CLI (db backfill)." },
        new SeedModuleCapability { Module = "Insights and Segments", Status = "Implemented", Notes = "insights + customer_segments computed from real loyalty cards." },
        new SeedModuleCapability { Module = "Subscriptions and Modules", Status = "Implemented", Notes = "business_subscriptions on the full-feature tier (no arbitrary business_modules flags); subscription_audit_logs records the change." },
        new SeedModuleCapability { Module = "Storefront", Status = "Implemented", Notes = "business_landing_page_configs (sections, hero, CTAs) + business_messaging_profiles; the public page itself is served from live domain data." },
        new SeedModuleCapability { Module = "Automation Rules", Status = "Implemented (disabled)", Notes = "Per-business default rules plus an atelier-specific rule, all created disabled so nothing is delivered until an owner enables them." },
        new SeedModuleCapability { Module = "Media", Status = "Not seeded", Notes = "Media rows are upload-backed (R2 object keys + worker processing); the demo uses hosted logo/cover URLs instead of fabricating object storage state." },
        new SeedModuleCapability { Module = "Inventory", Status = "Not implemented in platform", Notes = "No product/stock/supplier tables exist." },
        new SeedModuleCapability { Module = "Commissions", Status = "Not implemented in platform", Notes = "No commission ledger exists; performance is derived from appointments/stamps/redemptions." },
        new SeedModuleCapability { Module = "Roles and Permissions", Status = "Partially Implemented", Notes = "Authority is the membership role enum plus the static PermissionMatrix; there are no role/permission tables to seed." },
    ];
}
