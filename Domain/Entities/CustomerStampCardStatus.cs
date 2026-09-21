namespace PunchedApi.Domain.Entities;

/// <summary>Lifecycle of a <see cref="CustomerStampCard"/>: Active or Removed (history kept).</summary>
public enum CustomerStampCardStatus
{
    Active = 0,
    Removed = 1
}
