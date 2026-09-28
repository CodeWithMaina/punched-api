using FluentValidation;
using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Validators;

public sealed class CreateMediaUploadRequestValidator : AbstractValidator<CreateMediaUploadRequest>
{
    public CreateMediaUploadRequestValidator()
    {
        RuleFor(x => x.Purpose).NotEmpty().MaximumLength(40);
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.DeclaredMimeType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.SizeBytes).GreaterThan(0);
    }
}

