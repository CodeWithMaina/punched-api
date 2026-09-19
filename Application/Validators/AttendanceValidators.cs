using FluentValidation;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Validators;

/// <summary>
/// Validates the attendance clock request (plan §13.2). The token is the ONLY
/// field, and the namespace prefix check rejects foreign QR codes with
/// INVALID_QR before any database work (§8.2) — auto-validation runs before
/// the service, so a malformed payload never reaches the verification engine.
/// </summary>
public class AttendanceClockRequestValidator : AbstractValidator<AttendanceClockRequest>
{
    public AttendanceClockRequestValidator()
    {
        RuleFor(r => r.Token)
            .NotEmpty()
            .MaximumLength(512)
            .Must(t => AttendanceTokenFactory.HasValidPrefix(t))
            .WithMessage("token must be a Punched attendance QR code.");
    }
}

/// <summary>Validates the history query window and paging bounds.</summary>
public class AttendanceHistoryQueryValidator : AbstractValidator<AttendanceHistoryQuery>
{
    public AttendanceHistoryQueryValidator()
    {
        RuleFor(q => q.From)
            .LessThanOrEqualTo(q => q.To)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("from must not be after to.");
        RuleFor(q => q.Page).InclusiveBetween(1, 10_000);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}