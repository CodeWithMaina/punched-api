using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owner-side customer creation for the booking flow (appointment wizard Step 1).
///
/// <para>Two rules shape this service:</para>
/// <list type="bullet">
///   <item>a person is a single platform identity, so an existing Customer matching
///   the phone or email is reused and simply enrolled — never duplicated;</item>
///   <item>enrollment and the Customer membership are written by
///   <see cref="ICustomerEnrollmentService"/>, so a quick-added customer is
///   indistinguishable from one enrolled any other way.</item>
/// </list>
/// </summary>
public class CustomerDirectoryService : ICustomerDirectoryService
{
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICustomerEnrollmentService _enrollments;
    private readonly ILogger<CustomerDirectoryService> _logger;

    public CustomerDirectoryService(
        ApplicationDbContext context,
        IUnitOfWork unitOfWork,
        ICustomerEnrollmentService enrollments,
        ILogger<CustomerDirectoryService> logger)
    {
        _context = context;
        _unitOfWork = unitOfWork;
        _enrollments = enrollments;
        _logger = logger;
    }

    public async Task<ApiResponse<CustomerOptionResponse>> QuickAddAsync(
        Guid ownerUserId, QuickAddCustomerRequest request)
    {
        var business = await _unitOfWork.Businesses
            .FirstOrDefaultAsync(b => b.OwnerId == ownerUserId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<CustomerOptionResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var name = request.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ApiResponse<CustomerOptionResponse>.Fail("VALIDATION_ERROR", "A customer name is required.");
        if (name.Length > 100)
            return ApiResponse<CustomerOptionResponse>.Fail("VALIDATION_ERROR", "The customer name is too long (max 100 characters).");

        var phone = NormalizePhone(request.PhoneNumber);
        var email = request.Email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(email) && !EmailPattern.IsMatch(email))
            return ApiResponse<CustomerOptionResponse>.Fail("VALIDATION_ERROR", "Enter a valid email address.");
        if (phone == null && string.IsNullOrWhiteSpace(email))
            return ApiResponse<CustomerOptionResponse>.Fail(
                "VALIDATION_ERROR", "Provide a phone number or an email so the customer can be identified.");

        // Reuse an existing Customer identity where one matches. This is deliberately
        // platform-wide (a person is one identity) rather than tenant-scoped.
        User? customer = null;
        if (!string.IsNullOrWhiteSpace(email))
            customer = await _context.Users.FirstOrDefaultAsync(u => u.Email == email && u.Role == UserRole.Customer);
        if (customer == null && phone != null)
            customer = await _context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phone && u.Role == UserRole.Customer);

        if (customer == null)
        {
            customer = new User
            {
                Id = Guid.NewGuid(),
                Email = string.IsNullOrWhiteSpace(email) ? WalkInEmail(business.Id, phone!) : email!,
                PhoneNumber = phone,
                FullName = name,
                Role = UserRole.Customer,
                SourceProvider = "business",
                CreatedAt = DateTime.UtcNow
            };
            await _context.Users.AddAsync(customer);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another add raced us to the same email/phone. Re-resolve rather than fail.
                _context.Entry(customer).State = EntityState.Detached;
                customer = await _context.Users
                    .FirstOrDefaultAsync(u => (email != null && u.Email == email) || (phone != null && u.PhoneNumber == phone));
                if (customer == null)
                    return ApiResponse<CustomerOptionResponse>.Fail(
                        "CONFLICT", "A customer with these details already exists. Search for them instead.");
            }
        }
        else if (string.IsNullOrWhiteSpace(customer.FullName))
        {
            // Never overwrite an existing name, but fill a blank one.
            customer.FullName = name;
        }

        // Authoritative, idempotent enrollment (+ Customer membership).
        var enrolled = await _enrollments.EnrollAsync(customer.Id, business.Id, "business");
        if (!enrolled.Success)
        {
            _logger.LogWarning("Quick-add enrollment failed for business {BusinessId}: {Code} {Message}",
                business.Id, enrolled.Error?.Code, enrolled.Error?.Message);
            return ApiResponse<CustomerOptionResponse>.Fail(
                enrolled.Error?.Code ?? "VALIDATION_ERROR",
                enrolled.Error?.Message ?? "The customer could not be added to this business.");
        }

        return ApiResponse<CustomerOptionResponse>.Ok(new CustomerOptionResponse
        {
            UserId = customer.Id,
            FullName = customer.FullName,
            Email = customer.Email,
            PhoneNumber = customer.PhoneNumber,
            AvatarUrl = customer.AvatarUrl,
            EnrollmentStatus = "active"
        });
    }

    /// <summary>
    /// A non-deliverable placeholder address for a walk-in customer who gave only a
    /// phone number. Deterministic per (business, phone), so re-adding the same
    /// walk-in resolves to the same (uniquely-indexed) email instead of colliding.
    /// </summary>
    private static string WalkInEmail(Guid businessId, string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        var businessKey = businessId.ToString("N")[..12];
        return $"walkin-{digits}-{businessKey}@walkin.punched.local";
    }

    /// <summary>Normalises a Kenyan MSISDN to 2547XXXXXXXX / 2541XXXXXXXX, or null when invalid.</summary>
    private static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0") && digits.Length == 10) return "254" + digits[1..];
        if ((digits.StartsWith("7") || digits.StartsWith("1")) && digits.Length == 9) return "254" + digits;
        if (digits.StartsWith("254") && digits.Length == 12) return digits;
        // Fall back to storing what was given (bounded) rather than rejecting outright.
        return digits.Length is > 0 and <= 20 ? digits : null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true
        || ex.Message.Contains("23505");
}
