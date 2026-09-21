using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Lifecycle status of a <see cref="CustomerBusinessEnrollment"/>.
/// Active = member; Left = customer left (history preserved); Blocked = business-side block.
/// </summary>
public enum CustomerBusinessEnrollmentStatus
{
    Active = 0,
    Left = 1,
    Blocked = 2
}
