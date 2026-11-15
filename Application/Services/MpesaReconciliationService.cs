using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// M-Pesa statement reconciliation: comparing what the business recorded in Punched with the
/// statement it downloaded from its own M-Pesa account.
///
/// <para><b>No Safaricom integration is assumed.</b> Punched never claims a live link to M-Pesa, so
/// the business supplies the file. What the module guarantees is that the comparison is honest: a
/// verified payment stays <c>unreconciled</c> until it is actually compared, and every line keeps its
/// own identity so a later import cannot silently rewrite an earlier conclusion.</para>
///
/// <para><b>Reconciliation never verifies a payment.</b> Finding a receipt on the statement is
/// evidence, not a decision: the owner still confirms the money in the payments module. Keeping the
/// two apart is what stops a bookkeeper's import from changing what the business believes it
/// earned.</para>
/// </summary>
public partial class MpesaReconciliationService : IMpesaReconciliationService
{
    /// <summary>How many lines the workspace shows at once — enough to work through, bounded.</summary>
    private const int WorkspaceLineLimit = 200;

    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<MpesaReconciliationService> _logger;

    public MpesaReconciliationService(
        ApplicationDbContext context,
        IUnitOfWork uow,
        ILogger<MpesaReconciliationService> logger)
    {
        _context = context;
        _uow = uow;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════
    //  WORKSPACE
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResponse<ReconciliationWorkspaceResponse>> GetWorkspaceAsync(Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<ReconciliationWorkspaceResponse>.Fail("FORBIDDEN", "No business scope for user.");

        return ApiResponse<ReconciliationWorkspaceResponse>.Ok(await BuildWorkspaceAsync(business));
    }

    private static MpesaStatementImportResponse ToImportResponse(MpesaStatementImport import) => new()
    {
        Id = import.Id,
        FileName = import.FileName,
        Format = import.Format,
        ImportedAt = import.ImportedAt,
        ImportedByUserId = import.ImportedByUserId,
        LineCount = import.LineCount,
        MatchedCount = import.MatchedCount,
        DiscrepancyCount = import.DiscrepancyCount,
        UnmatchedCount = import.UnmatchedCount,
        ExcludedCount = import.ExcludedCount,
        Notes = import.Notes
    };

    private static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
