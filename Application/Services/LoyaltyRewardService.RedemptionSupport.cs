using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace PunchedApi.Application.Services;

/// <summary>
/// Hashing, fulfilment-code generation and row-lock helpers for redemption.
/// </summary>
public partial class LoyaltyRewardService
{
    /// <summary>Row lock for the entitlement (relational providers only).</summary>
    private async Task LockEntitlementForUpdateAsync(Guid entitlementId)
    {
        if (!_context.Database.IsRelational()) return;
        await _context.Database.ExecuteSqlRawAsync(
            "SELECT id FROM reward_entitlements WHERE id = {0} FOR UPDATE", entitlementId);
    }

    /// <summary>SHA256 hex, matching <c>RedemptionService.HashToken</c> so codes verify.</summary>
    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    /// <summary>6-character code, excluding visually ambiguous characters.</summary>
    private static string GenerateFulfilmentCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var buffer = new byte[6];
        RandomNumberGenerator.Fill(buffer);
        return new string(buffer.Select(b => chars[b % chars.Length]).ToArray());
    }
}