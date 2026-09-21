using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Concurrency and lifecycle guards for the loyalty stamping pipeline.
/// </summary>
public partial class LoyaltyStampingService
{
    /// <summary>PostgreSQL row lock (FOR UPDATE). Skipped for non-relational providers.</summary>
    private async Task LockCardForUpdateAsync(Guid cardId)
    {
        if (!_context.Database.IsRelational()) return;
        await _context.Database.ExecuteSqlRawAsync(
            "SELECT id FROM loyalty_cards WHERE id = {0} FOR UPDATE", cardId);
    }

    /// <summary>
    /// Lifecycle prerequisite: only an Active program awards stamps. Draft,
    /// Paused and Archived award nothing while retaining all existing data.
    /// </summary>
    private static (string Code, string Message)? ValidateProgramCanEarn(LoyaltyProgram program) =>
        program.Status switch
        {
            ProgramStatus.Active => null,
            ProgramStatus.Draft =>
                ("PROGRAM_NOT_ACTIVE", "This loyalty program is still a draft and cannot award stamps."),
            ProgramStatus.Paused =>
                ("PROGRAM_PAUSED", "This loyalty program is paused. Existing progress is unchanged."),
            ProgramStatus.Archived =>
                ("PROGRAM_ARCHIVED", "This loyalty program is archived and no longer awards stamps."),
            _ => ("PROGRAM_NOT_ACTIVE", "This loyalty program cannot award stamps.")
        };
}