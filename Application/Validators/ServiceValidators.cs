using FluentValidation;
using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Validators;

/// <summary>
/// Validates the catalog values used by scheduling and customer-facing service listings.
/// </summary>
public class CreateServiceRequestValidator : AbstractValidator<CreateServiceRequest>
{
    public CreateServiceRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Service name is required.")
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Service name is required.")
            .MaximumLength(120).WithMessage("Service name must not exceed 120 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters.");

        RuleFor(x => x.DurationMinutes)
            .InclusiveBetween(5, 1440).WithMessage("Duration must be between 5 and 1,440 minutes.");

        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price must be greater than or equal to 0.");
    }
}

/// <summary>
/// Validates UpdateServiceRequest. Each rule applies only when the field is provided.
/// </summary>
public class UpdateServiceRequestValidator : AbstractValidator<UpdateServiceRequest>
{
    public UpdateServiceRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Service name is required.")
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Service name is required.")
            .MaximumLength(120).WithMessage("Service name must not exceed 120 characters.")
            .When(x => x.Name != null);

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters.");

        RuleFor(x => x.DurationMinutes)
            .InclusiveBetween(5, 1440).WithMessage("Duration must be between 5 and 1,440 minutes.")
            .When(x => x.DurationMinutes.HasValue);

        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price must be greater than or equal to 0.")
            .When(x => x.Price.HasValue);
    }
}
