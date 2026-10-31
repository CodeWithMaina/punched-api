namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

/// <summary>
/// Static, deterministic definitions for the "Aurelia Luxe Hair Atelier"
/// demonstration tenant. Everything the demo seeder writes is derived from
/// these tables plus <see cref="AureliaLuxeDemoIds"/>, so repeated runs
/// produce identical records and never duplicate rows.
///
/// Nothing here invents platform entities: every value maps onto a column or
/// enum that already exists in the Punched domain model.
/// </summary>
internal static class AureliaLuxeDemoData
{
    /// <summary>The existing tenant's subdomain slug (stable identity).</summary>
    public const string Slug = "aurelia-luxe-hair-atelier";

    /// <summary>Fallback lookup when a legacy row has no slug yet.</summary>
    public const string Name = "Aurelia Luxe Hair Atelier";

    /// <summary>Business definition key inside <c>SeedCatalog</c>.</summary>
    public const string BusinessKey = "business-1";

    /// <summary>Owner definition key inside <c>SeedCatalog</c>.</summary>
    public const string OwnerUserKey = "owner-1";

    /// <summary>Customer definition-key prefix inside <c>SeedCatalog</c>.</summary>
    public const string CustomerKeyPrefix = "customer-b1-";

    /// <summary>Development-only password for the seeded owner account.</summary>
    public const string OwnerPassword = "Owner@1234!";

    /// <summary>Development-only password for the seeded staff accounts.</summary>
    public const string StaffPassword = "Staff@1234!";

    /// <summary>Development-only password for the first seeded customer.</summary>
    public const string PrimaryCustomerPassword = "Customer@1234!";

    /// <summary>Development-only password for every other seeded customer.</summary>
    public const string CustomerPassword = "Cust@1234!";

    // ── Business profile ────────────────────────────────────────
    public const string Category = "Hair & Beauty Salon";
    public const string Location = "Westlands, Nairobi";
    public const string PhoneNumber = "+254712440001";
    public const string Email = "hello@aurelialuxe.co.ke";
    public const string Description =
        "A premium hair atelier offering professional hairstyling, hair treatments, braiding, extensions, and beauty services.";
    public const string LogoUrl = "https://images.unsplash.com/photo-1524504388940-b1c1722653e1";
    public const string CoverUrl = "https://images.unsplash.com/photo-1560066984-138dadb4c035";
    public const string MpesaNumber = "4021001";
    public const string TimeZoneId = "Africa/Nairobi";
    public const string AddressLine = "The Mirage, 3rd Floor, Ring Road Westlands, Nairobi, Kenya";
    public const string WebsiteUrl = "https://aurelialuxe.co.ke";
    public const string InstagramUrl = "https://instagram.com/aurelialuxesalon";
    public const string FacebookUrl = "https://facebook.com/aurelialuxesalon";
    public const string TikTokUrl = "https://tiktok.com/@aurelialuxesalon";
    public const string BrandPrimaryColor = "#B08D57";

    public const string BookingPolicy =
        "A 30% deposit secures bridal and braiding appointments. Please arrive 10 minutes early; "
        + "arrivals more than 20 minutes late may be shortened or rescheduled.";

    public const string CancellationPolicy =
        "Free cancellation or reschedule up to 24 hours before your appointment. "
        + "Cancellations inside 24 hours forfeit the booking deposit.";

    public const string PaymentPolicy =
        "We accept cash, M-PESA PayBill (Business No. 4021001) and card at the atelier. "
        + "A deposit is required for appointments longer than three hours.";

    // ── Opening hours (business-local, 24h) ─────────────────────
    public const int WeekdayOpenHour = 9;
    public const int WeekdayCloseHour = 20;
    public const int SaturdayOpenHour = 8;
    public const int SaturdayCloseHour = 18;
    public const int BookingSlotIntervalMinutes = 15;
    public const int BookingLeadTimeMinutes = 120;

    /// <summary>Default per-staff daily stamp goal configured on the business.</summary>
    public const int DefaultDailyStampGoal = 6;

    /// <summary>Default per-staff daily appointment goal configured on the business.</summary>
    public const int DefaultAppointmentDailyGoal = 6;

    // ── Service catalogue ───────────────────────────────────────

    /// <summary>
    /// One bookable service. <c>Category</c> has no dedicated column in the
    /// platform schema, so it is carried as a description prefix — the
    /// catalogue's own grouping label, not a new entity.
    /// </summary>
    internal sealed record ServiceDefinition(
        string Key,
        string Name,
        string Category,
        string Description,
        int DurationMinutes,
        decimal Price,
        bool IsActive = true,
        bool Showcase = true);

    public static readonly IReadOnlyList<ServiceDefinition> Services =
    [
        // ── Hair Styling ────────────────────────────────────────
        new("wash-blow-dry", "Wash & Blow Dry", "Hair Styling",
            "Signature cleanse, deep steam and a smooth salon blow-out finished with heat protection.",
            45, 1200m),
        new("silk-press", "Silk Press", "Hair Styling",
            "Glass-finish silk press on natural hair with a protective heat routine and trim check.",
            120, 3500m),
        new("basic-styling", "Basic Styling", "Hair Styling",
            "Everyday styling — curls, waves or a sleek updo for work and weekend.",
            60, 1800m),
        new("bridal-hairstyling", "Bridal Hairstyling", "Hair Styling",
            "Trial-informed bridal styling with veil placement, pinning and all-day hold.",
            180, 12000m),
        new("hair-consultation", "Hair Consultation", "Hair Styling",
            "Scalp and strand diagnosis with a personalised atelier treatment plan.",
            30, 0m),
        new("mens-cut-lineup", "Men's Cut & Line-Up", "Hair Styling",
            "Precision clipper and scissor cut with a razor-sharp line-up and beard tidy.",
            45, 1000m),
        new("kids-styling", "Kids Styling", "Hair Styling",
            "Gentle wash, detangle and styling for our youngest guests (under 12).",
            45, 900m),

        // ── Braiding ────────────────────────────────────────────
        new("knotless-braids", "Knotless Braids", "Braiding",
            "Featherweight knotless braids in your chosen length, density and colour blend.",
            300, 6500m),
        new("box-braids", "Box Braids", "Braiding",
            "Classic box braids with a neat square-part grid and sealed ends.",
            270, 5500m),
        new("cornrows", "Cornrows", "Braiding",
            "Straight-back, side or feed-in cornrows with clean defined parts.",
            90, 2000m),
        new("fulani-braids", "Fulani Braids", "Braiding",
            "Fulani-inspired braids with beading and a centre-part detail.",
            240, 6000m),
        new("goddess-braids", "Goddess Braids", "Braiding",
            "Sculpted goddess braids with a soft curly finish and grip-friendly hold.",
            240, 6500m),
        new("passion-twists", "Passion Twists", "Braiding",
            "Lightweight water-wave passion twists with a natural, romantic fall.",
            210, 5000m),

        // ── Hair Treatments ─────────────────────────────────────
        new("deep-conditioning", "Deep Conditioning Treatment", "Hair Treatments",
            "Intensive moisture mask under steam to restore softness and shine.",
            60, 2000m),
        new("protein-treatment", "Protein Treatment", "Hair Treatments",
            "Bond-building protein therapy for over-processed, heat-stressed hair.",
            75, 2800m),
        new("scalp-treatment", "Scalp Treatment", "Hair Treatments",
            "Detoxifying scalp exfoliation and massage to calm irritation and flaking.",
            45, 1800m),
        new("hair-repair-treatment", "Hair Repair Treatment", "Hair Treatments",
            "Multi-stage repair ritual for breakage, thinning edges and colour damage.",
            90, 3500m),

        // ── Extensions ──────────────────────────────────────────
        new("wig-installation", "Wig Installation", "Extensions",
            "Lace melt, customisation and a secure, natural-looking wig install.",
            90, 3000m),
        new("wig-revamp", "Wig Revamp", "Extensions",
            "Deep clean, re-condition, re-style and re-lace your wig or unit.",
            120, 3500m),
        new("sew-in-weave", "Sew-In Weave", "Extensions",
            "Braided-base sew-in with leave-out or closure finish.",
            210, 8000m),
        new("extension-installation", "Hair Extension Installation", "Extensions",
            "Tape-in, clip-in or bonded extension installation with blending.",
            180, 7000m),

        // ── Natural Hair ────────────────────────────────────────
        new("natural-hair-styling", "Natural Hair Styling", "Natural Hair",
            "Wash-and-go, braid-out or roller-set styling for natural textures.",
            75, 2200m),
        new("twist-out", "Twist Out", "Natural Hair",
            "Two-strand twist set with a defined, hydrated twist-out finish.",
            90, 2500m),
        new("loc-maintenance", "Loc Maintenance", "Natural Hair",
            "Root cleanse, palm-roll and oiling to keep locs healthy and neat.",
            120, 3000m),
        new("loc-retwist", "Loc Retwist", "Natural Hair",
            "Precision retwist with part refresh and scalp conditioning.",
            150, 3800m),

        // ── Nails & Beauty ──────────────────────────────────────
        new("gel-manicure", "Gel Manicure", "Nails & Beauty",
            "Shape, cuticle care and long-wear gel colour with a hand massage.",
            60, 1800m),
        new("spa-pedicure", "Spa Pedicure", "Nails & Beauty",
            "Soak, exfoliation, callus work, massage and polish.",
            75, 2200m),

        // ── Lifecycle flags (IsActive vs Showcase) ──────────────
        new("hair-gloss-treatment", "Hair Gloss Treatment", "Hair Treatments",
            "Discontinued tinted gloss service retained for historical appointments.",
            60, 2600m, IsActive: false, Showcase: false),
        new("cornrow-weekday-special", "Weekday Cornrow Special", "Braiding",
            "Live service that is not advertised on the storefront (unlisted special).",
            90, 1500m, IsActive: true, Showcase: false),
    ];

    // ── Staff ───────────────────────────────────────────────────

    /// <summary>
    /// One team member. <c>Title</c> is a presentation label: the platform
    /// models staff authority as the single <c>Staff</c> membership role, so
    /// the atelier job title is surfaced through the profile while the
    /// service assignments are what actually gate what a member can perform.
    /// </summary>
    internal sealed record StaffDefinition(
        string UserKey,
        string Title,
        string[] ServiceKeys,
        int ShiftStartHour,
        int ShiftEndHour);

    public static readonly IReadOnlyList<StaffDefinition> Staff =
    [
        new("staff-1-manager", "Salon Manager",
            [ "hair-consultation", "bridal-hairstyling", "silk-press", "basic-styling",
              "wash-blow-dry", "deep-conditioning", "protein-treatment", "wig-installation" ],
            9, 18),

        new("staff-1-senior-stylist", "Senior Hairstylist",
            [ "silk-press", "bridal-hairstyling", "basic-styling", "wash-blow-dry",
              "sew-in-weave", "extension-installation", "wig-installation", "wig-revamp",
              "hair-repair-treatment", "deep-conditioning", "hair-consultation" ],
            10, 20),

        new("staff-1-barber", "Grooming Specialist",
            [ "mens-cut-lineup", "wash-blow-dry", "basic-styling", "scalp-treatment",
              "hair-consultation", "kids-styling" ],
            10, 19),

        new("staff-1-nail-tech", "Nail Technician",
            [ "gel-manicure", "spa-pedicure" ],
            10, 18),

        new("staff-1-reception", "Front Desk / Receptionist",
            [ ],
            8, 17),

        new("staff-1-braiding", "Braiding Specialist",
            [ "knotless-braids", "box-braids", "cornrows", "fulani-braids",
              "goddess-braids", "passion-twists", "cornrow-weekday-special",
              "hair-consultation" ],
            9, 19),

        new("staff-1-natural-hair", "Natural Hair Specialist",
            [ "natural-hair-styling", "twist-out", "loc-maintenance", "loc-retwist",
              "wash-blow-dry", "silk-press", "deep-conditioning", "hair-consultation" ],
            9, 18),

        new("staff-1-treatment", "Hair Treatment Specialist",
            [ "deep-conditioning", "protein-treatment", "scalp-treatment",
              "hair-repair-treatment", "wash-blow-dry", "hair-consultation" ],
            9, 17),

        new("staff-1-junior", "Junior Hairstylist",
            [ "wash-blow-dry", "basic-styling", "cornrows", "twist-out",
              "deep-conditioning", "kids-styling" ],
            9, 17),
    ];

    /// <summary>Staff who deliver bookable services (the receptionist does not).</summary>
    public static IEnumerable<StaffDefinition> ServiceStaff =>
        Staff.Where(s => s.ServiceKeys.Length > 0);

    /// <summary>Atelier job title for a staff user key (presentation only).</summary>
    public static string TitleFor(string userKey) =>
        Staff.FirstOrDefault(s => s.UserKey == userKey)?.Title ?? "Staff";

    // ── Loyalty ─────────────────────────────────────────────────

    /// <summary>Name of the active loyalty program the demo cards are bound to.</summary>
    public const string ProgramName = "Aurelia Gold";

    /// <summary>Stamps required to complete the atelier stamp card.</summary>
    public const int StampCardStampsRequired = 8;

    /// <summary>Reward value of a completed stamp card (KES).</summary>
    public const decimal StampCardRewardValue = 1800m;

    /// <summary>Reward description of a completed stamp card.</summary>
    public const string StampCardRewardDescription = "KES 1,800 service credit";

    /// <summary>Reward window after the card completes (hours).</summary>
    public const int RewardExpirationHours = 72;

    /// <summary>Deterministic seed for the demo generator (keeps runs reproducible).</summary>
    public const int RandomSeed = 20260801;

    // ── Activity window ─────────────────────────────────────────

    /// <summary>Days of historical appointments generated before today.</summary>
    public const int HistoryDays = 90;

    /// <summary>Days of upcoming appointments generated after today.</summary>
    public const int UpcomingDays = 30;

    /// <summary>Days of staff shift rows generated either side of today.</summary>
    public const int ShiftWindowDays = 35;

    /// <summary>Days of attendance history generated before today.</summary>
    public const int AttendanceDays = 30;
}
