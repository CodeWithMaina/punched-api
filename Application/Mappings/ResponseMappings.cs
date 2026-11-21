using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Mappings;

/// <summary>
/// Explicit entity → response projections.
///
/// <para><b>Why explicit and not a mapping library.</b> The API maps entities to
/// DTOs in exactly four shapes, all of them flat and all of them one-way. They
/// used to be configured as AutoMapper profiles; the mapping engine was removed
/// because every patched release of it is only available under a commercial
/// licence, while the four mappings below are a handful of property assignments.
/// Keeping them as plain code also means the compiler checks each assignment:
/// renaming a DTO property can no longer silently drop a field from an API
/// response.</para>
///
/// <para>Each method reproduces the mapping that the former profile configured —
/// including the only two non-identity rules: <c>User.PhoneNumber → Phone</c> and
/// <c>ServiceCatalogItem.Price ?? 0</c>. Fields that the profile left untouched
/// (for example <c>AppointmentResponse.CustomerName</c>, which callers resolve
/// with a separate query) keep their DTO defaults; they are deliberately not
/// invented here.</para>
/// </summary>
public static class ResponseMappings
{
    /// <summary><see cref="User"/> → <see cref="UserProfileResponse"/>.</summary>
    public static UserProfileResponse ToProfileResponse(this User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        Phone = user.PhoneNumber,
        FullName = user.FullName,
        AvatarUrl = user.AvatarUrl,
        AvatarMediaId = user.AvatarMediaId,
        DateOfBirth = user.DateOfBirth,
        Gender = user.Gender,
        Role = user.Role,
        CreatedAt = user.CreatedAt
    };

    /// <summary>
    /// <see cref="Appointment"/> → <see cref="AppointmentResponse"/>.
    /// <c>UpdatedAt</c> is intentionally not set: the entity has no such column, and
    /// every caller derives it from the appointment's status history (falling back to
    /// <c>CreatedAt</c>) so the response reflects the most recent lifecycle change.
    /// </summary>
    public static AppointmentResponse ToAppointmentResponse(this Appointment appointment) => new()
    {
        Id = appointment.Id,
        BusinessId = appointment.BusinessId,
        CustomerId = appointment.CustomerId,
        StaffUserId = appointment.StaffUserId,
        ScheduledAt = appointment.ScheduledAt,
        EndAt = appointment.EndAt,
        DurationMinutes = AppointmentIntervals.DurationMinutes(appointment.ScheduledAt, appointment.EndAt),
        Status = appointment.Status,
        Services = appointment.Resources
            .OrderBy(resource => resource.SortOrder)
            .Select(resource => resource.ToServiceSnapshot())
            .ToList(),
        CreatedAt = appointment.CreatedAt,
        ReminderOptOut = appointment.ReminderOptOut,
        Note = appointment.Note
    };

    /// <summary><see cref="AppointmentResource"/> → <see cref="AppointmentServiceSnapshot"/>.</summary>
    public static AppointmentServiceSnapshot ToServiceSnapshot(this AppointmentResource resource) => new()
    {
        ServiceCatalogItemId = resource.ServiceCatalogItemId,
        Name = resource.Name,
        DurationMinutes = resource.DurationMinutes,
        Price = resource.Price,
        SortOrder = resource.SortOrder
    };

    /// <summary>
    /// <see cref="ServiceCatalogItem"/> → <see cref="ServiceCatalogItemResponse"/>.
    /// A service without a price is exposed as 0 (the catalogue's "free" value)
    /// rather than null, which is what the former profile configured.
    /// </summary>
    public static ServiceCatalogItemResponse ToResponse(this ServiceCatalogItem item) => new()
    {
        Id = item.Id,
        BusinessId = item.BusinessId,
        Name = item.Name,
        Description = item.Description,
        DurationMinutes = item.DurationMinutes,
        Price = item.Price ?? 0,
        IsActive = item.IsActive,
        Showcase = item.Showcase,
        CategoryId = item.CategoryId,
        IsArchived = item.IsArchived,
        CreatedAt = item.CreatedAt
    };
}
