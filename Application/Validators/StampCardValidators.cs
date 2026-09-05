using FluentValidation;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.Application.Validators;

/// <summary>Validates stamp card create payloads.</summary>
public class CreateStampCardRequestValidator : AbstractValidator<CreateStampCardRequest>
{
    public CreateStampCardRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Description).MaximumLength(500);
        RuleFor(r => r.StampsRequired).InclusiveBetween(1, 100);
        RuleFor(r => r.RewardDescription).NotEmpty().MaximumLength(200);
        RuleFor(r => r.RewardValue).GreaterThan(0);
        RuleFor(r => r.Status)
            .Must(s => s is null or "draft" or "active" or "inactive")
            .WithMessage("status must be one of: draft, active, inactive.");
    }
}

/// <summary>Validates stamp card update payloads (at least one field required).</summary>
public class UpdateStampCardRequestValidator : AbstractValidator<UpdateStampCardRequest>
{
    public UpdateStampCardRequestValidator()
    {
        RuleFor(r => r.Name).MaximumLength(100).When(r => r.Name != null);
        RuleFor(r => r.Description).MaximumLength(500).When(r => r.Description != null);
        RuleFor(r => r.StampsRequired).InclusiveBetween(1, 100).When(r => r.StampsRequired.HasValue);
        RuleFor(r => r.RewardDescription).MaximumLength(200).When(r => r.RewardDescription != null);
        RuleFor(r => r.RewardValue).GreaterThan(0).When(r => r.RewardValue.HasValue);
        RuleFor(r => r).Must(r =>
                r.Name != null || r.Description != null || r.StampsRequired.HasValue ||
                r.RewardDescription != null || r.RewardValue.HasValue || r.CardDesignId.HasValue || r.ClearCardDesign)
            .WithMessage("At least one field must be provided.");
    }
}

/// <summary>Validates stamp card status transitions.</summary>
public class UpdateStampCardStatusRequestValidator : AbstractValidator<UpdateStampCardStatusRequest>
{
    public UpdateStampCardStatusRequestValidator()
    {
        RuleFor(r => r.Status)
            .NotEmpty()
            .Must(s => s is "draft" or "active" or "inactive" or "archived")
            .WithMessage("status must be one of: draft, active, inactive, archived.");
    }
}

/// <summary>Validates card design create payloads.</summary>
public class CreateCardDesignRequestValidator : AbstractValidator<CreateCardDesignRequest>
{
    public CreateCardDesignRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.HtmlTemplate)
            .NotEmpty()
            .Must(html => CardTemplateSanitizer.Validate(html).IsValid)
            .WithMessage(r => CardTemplateSanitizer.Validate(r.HtmlTemplate).Error ?? "Invalid template.");
    }
}

/// <summary>Validates card design update payloads.</summary>
public class UpdateCardDesignRequestValidator : AbstractValidator<UpdateCardDesignRequest>
{
    public UpdateCardDesignRequestValidator()
    {
        RuleFor(r => r.Name).MaximumLength(100).When(r => r.Name != null);
        RuleFor(r => r)
            .Must(r => r.Name != null || r.HtmlTemplate != null || r.IsActive.HasValue)
            .WithMessage("At least one field must be provided.");
        RuleFor(r => r.HtmlTemplate)
            .Must(html => html == null || CardTemplateSanitizer.Validate(html).IsValid)
            .WithMessage(r => CardTemplateSanitizer.Validate(r.HtmlTemplate!).Error ?? "Invalid template.");
    }
}