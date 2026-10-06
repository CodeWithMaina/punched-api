using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Validators;

/// <summary>
/// Validates the controlled landing-page configuration payload.
/// Presentation/visibility/ordering/overrides only — domain data is never stored here.
/// Mirrors the frontend zod schema (lib/validations/landingPage.ts).
/// </summary>
public class UpdateLandingPageRequestValidator : AbstractValidator<UpdateLandingPageRequest>
{
    public UpdateLandingPageRequestValidator()
    {
        RuleFor(x => x.Version).GreaterThanOrEqualTo(0);

        RuleFor(x => x.Config).NotNull().WithMessage("Configuration is required.");
        When(x => x.Config != null, () =>
        {
            RuleFor(x => x.Config.Hero.TitleOverride)
                .MaximumLength(100).WithMessage("Hero title must not exceed 100 characters.");
            RuleFor(x => x.Config.Hero.TaglineOverride)
                .MaximumLength(140).WithMessage("Hero tagline must not exceed 140 characters.");
            RuleFor(x => x.Config.Hero.DescriptionOverride)
                .MaximumLength(500).WithMessage("Hero description must not exceed 500 characters.");

            RuleFor(x => x.Config.Sections)
                .NotNull().WithMessage("Sections are required.")
                .Must(s => LandingPageDefaults.SectionKeys.All(k => s.ContainsKey(k)))
                .WithMessage("All sections must be present.")
                .Must(s => s == null || s.Values.All(v => v == null || (v.Order >= 0 && v.Order <= 50)))
                .WithMessage("Section order must be between 0 and 50.");
            RuleForEach(x => x.Config.Sections.Values)
                .ChildRules(section =>
                {
                    section.RuleFor(s => s!.Title).MaximumLength(100)
                        .WithMessage("Section title must not exceed 100 characters.");
                    section.RuleFor(s => s!.Description).MaximumLength(500)
                        .WithMessage("Section description must not exceed 500 characters.");
                });

            RuleFor(x => x.Config.PrimaryCta.Type)
                .Must(t => LandingPageDefaults.CtaTypes.Contains(t ?? string.Empty))
                .WithMessage("Primary CTA type is invalid.");
            RuleFor(x => x.Config.PrimaryCta.Label)
                .NotEmpty().WithMessage("Primary CTA label is required.")
                .MaximumLength(40).WithMessage("Primary CTA label must not exceed 40 characters.");
            RuleFor(x => x.Config.SecondaryCta.Type)
                .Must(t => LandingPageDefaults.CtaTypes.Contains(t ?? string.Empty))
                .WithMessage("Secondary CTA type is invalid.");
            RuleFor(x => x.Config.SecondaryCta.Label)
                .NotEmpty().WithMessage("Secondary CTA label is required.")
                .MaximumLength(40).WithMessage("Secondary CTA label must not exceed 40 characters.");
        });
    }
}
