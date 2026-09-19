namespace PunchedApi.Application.Services;

/// <summary>
/// The platform's built-in **Default Card Design**.
///
/// This is part of the core Loyalty experience: every business with the loyalty
/// module gets a working stamp card whether or not it has the optional
/// <c>customCardDesign</c> entitlement, and whether or not any
/// <see cref="PunchedApi.Domain.Entities.CardDesign"/> row exists for it.
///
/// The constant is therefore BOTH:
///  • the payload of the seeded platform-default <c>card_designs</c> row
///    (<see cref="Id"/>, <see cref="Name"/>, business_id = NULL, is_default = true), and
///  • the ultimate in-code fallback used by <see cref="CardDesignResolver"/>
///    when that row is missing (fresh database, wiped seed data, tests).
///
/// It is authored with only whitelisted tags/attributes/CSS properties so the
/// same template body works for saved, previewed and rendered cards.
/// </summary>
public static class DefaultCardTemplate
{
    /// <summary>Stable id of the platform-default <c>card_designs</c> row.</summary>
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-00000000d001");

    /// <summary>Display name of the platform-default design.</summary>
    public const string Name = "Default Card";

    /// <summary>
    /// The default card HTML fragment. Rendered exclusively through
    /// <see cref="CardTemplateRenderer"/>.
    /// </summary>
    public const string Html = """
<div class="punched-card" style="border-radius:20px;overflow:hidden;background:#0f172a;color:#ffffff;font-family:system-ui,-apple-system,'Segoe UI',sans-serif;box-shadow:0 18px 40px rgba(15,23,42,.35)">
<div style="padding:20px;display:flex;align-items:center;gap:12px;background:#1e293b">
<img src="{{business.logo}}" alt="{{business.name}}" width="44" height="44" style="border-radius:50%;background:#334155;object-fit:cover;width:44px;height:44px" />
<div style="flex:1;min-width:0">
<p style="margin:0;font-size:16px;font-weight:700;line-height:1.2">{{business.name}}</p>
<p style="margin:2px 0 0;font-size:12px;color:#94a3b8">{{campaign.name}}</p>
</div>
</div>
<div style="padding:20px">
<div style="display:flex;align-items:baseline;gap:6px">
<span style="font-size:32px;font-weight:800;letter-spacing:-.02em;line-height:1">{{card.completedStamps}}</span>
<span style="font-size:15px;font-weight:600;color:#94a3b8">/ {{card.totalStamps}}</span>
</div>
<p style="margin:8px 0 0;font-size:12px;color:#cbd5e1">{{card.name}}</p>
<div style="margin-top:16px;display:flex;flex-wrap:wrap;gap:8px">{{#each stamps}}<span class="stamp {{status}}" title="{{position}}"></span>{{/each}}</div>
<p style="margin:18px 0 0;font-size:12px;color:#94a3b8">{{reward.name}}</p>
</div>
</div>
""";
}
