using FluentValidation;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.Application.Validators;

/// <summary>
/// Validates the Storefront Builder draft payload. Bounds text so a runaway value
/// can never bloat the JSON column or the rendered page, and rejects unknown
/// template ids. Section keys are normalised (unknown ones dropped) by
/// <see cref="StorefrontBuilderDefaults.Merge"/>, so they are not hard-failed here.
/// </summary>
public class SaveStorefrontDraftRequestValidator : AbstractValidator<SaveStorefrontDraftRequest>
{
    public SaveStorefrontDraftRequestValidator()
    {
        RuleFor(x => x.Config).NotNull().WithMessage("Configuration is required.");
        When(x => x.Config != null, () =>
        {
            RuleFor(x => x.Config.TemplateId)
                .Must(id => StorefrontTemplates.Find(id) != null)
                .WithMessage("Unknown storefront template.");

            RuleFor(x => x.Config.Hero.Heading).MaximumLength(120);
            RuleFor(x => x.Config.Hero.Subheading).MaximumLength(160);
            RuleFor(x => x.Config.Hero.Description).MaximumLength(600);
            RuleFor(x => x.Config.Hero.PrimaryCtaLabel).MaximumLength(40);
            RuleFor(x => x.Config.Hero.SecondaryCtaLabel).MaximumLength(40);
            RuleFor(x => x.Config.Hero.OverlayOpacity).InclusiveBetween(0, 100);

            RuleFor(x => x.Config.Header.CtaLabel).MaximumLength(40);
            RuleFor(x => x.Config.Footer.Copyright).MaximumLength(200);
            RuleFor(x => x.Config.Footer.Description).MaximumLength(400);

            RuleFor(x => x.Config.Seo.Title).MaximumLength(70);
            RuleFor(x => x.Config.Seo.Description).MaximumLength(200);
            RuleFor(x => x.Config.Seo.OgTitle).MaximumLength(70);
            RuleFor(x => x.Config.Seo.OgDescription).MaximumLength(200);

            RuleFor(x => x.Config.Sections).NotNull();
            RuleForEach(x => x.Config.Sections).ChildRules(section =>
            {
                section.RuleFor(s => s.Title).MaximumLength(120);
                section.RuleFor(s => s.Description).MaximumLength(600);
                section.RuleFor(s => s.Key).NotEmpty();
                section.RuleFor(s => s.Order).InclusiveBetween(0, 999);
            });

            RuleForEach(x => x.Config.Pages).ChildRules(page =>
            {
                page.RuleFor(p => p.Title).NotEmpty().MaximumLength(80);
                page.RuleFor(p => p.Slug).MaximumLength(80);
                page.RuleFor(p => p.SeoTitle).MaximumLength(70);
                page.RuleFor(p => p.SeoDescription).MaximumLength(200);
            });
        });
    }
}
