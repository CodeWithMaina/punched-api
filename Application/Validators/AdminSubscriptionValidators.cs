using FluentValidation;
using PunchedApi.Application.DTOs;
using static PunchedApi.Application.DTOs.TierModuleView;

namespace PunchedApi.Application.Validators;

/// <summary>
/// Validates AdminTierCreateRequest field shape. Business/lifecycle rules
/// (duplicate keys, dependency closure, core-module requirement, lifecycle
/// transitions) are enforced authoritatively in <c>AdminTierService</c>.
/// </summary>
public class AdminTierCreateRequestValidator : AbstractValidator<AdminTierCreateRequest>
{
    public AdminTierCreateRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tier name is required.")
            .MaximumLength(100).WithMessage("Tier name must not exceed 100 characters.");

        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Tier key is required.")
            .Matches("^[a-z][a-z0-9-]*$").WithMessage("Tier key must be lowercase and match ^[a-z][a-z0-9-]*$.")
            .MaximumLength(50).WithMessage("Tier key must not exceed 50 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters.");

        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price must be greater than or equal to 0.")
            .LessThanOrEqualTo(1_000_000).WithMessage("Price must not exceed 1,000,000.");

        RuleFor(x => x.BillingInterval)
            .NotEmpty().WithMessage("Billing interval is required.")
            .Must(x => x == "monthly" || x == "yearly")
            .WithMessage("Billing interval must be 'monthly' or 'yearly'.");
    }
}

/// <summary>Validates AdminTierUpdateRequest field shape.</summary>
public class AdminTierUpdateRequestValidator : AbstractValidator<AdminTierUpdateRequest>
{
    public AdminTierUpdateRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tier name is required.")
            .MaximumLength(100).WithMessage("Tier name must not exceed 100 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters.");

        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price must be greater than or equal to 0.")
            .LessThanOrEqualTo(1_000_000).WithMessage("Price must not exceed 1,000,000.");

        RuleFor(x => x.BillingInterval)
            .NotEmpty().WithMessage("Billing interval is required.")
            .Must(x => x == "monthly" || x == "yearly")
            .WithMessage("Billing interval must be 'monthly' or 'yearly'.");
    }
}

/// <summary>
/// Validates AdminTierModulesRequest: non-empty, no duplicate module keys.
/// Module existence + dependency closure are enforced in the service.
/// </summary>
public class AdminTierModulesRequestValidator : AbstractValidator<AdminTierModulesRequest>
{
    public AdminTierModulesRequestValidator()
    {
        RuleFor(x => x.ModuleKeys)
            .NotNull().WithMessage("moduleKeys is required.")
            .Must(keys => keys == null || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Count)
            .WithMessage("moduleKeys must not contain duplicate entries.");

        RuleFor(x => x.Reason)
            .MaximumLength(1000).WithMessage("Reason must not exceed 1000 characters.");
    }
}