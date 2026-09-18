namespace PunchedApi.Domain.Entities;

/// <summary>
/// Attendance operating mode for a business (V1: Standard only).
/// Persisted as a string via <c>HasConversion&lt;string&gt;()</c>; wire
/// values are SCREAMING_SNAKE.
/// </summary>
public enum AttendanceMode
{
    Standard = 0,
}

/// <summary>
/// Ledger event direction. <c>ClockIn</c>/<c>ClockOut</c> are V1;
/// <c>BreakStart</c>, <c>BreakEnd</c> and <c>ManualAdjustment</c> are
/// reserved for future phases (types exist so no breaking schema change
/// is needed later — no endpoints use them yet).
/// Persisted as a string via <c>HasConversion&lt;string&gt;()</c>.
/// </summary>
public enum AttendanceEventType
{
    ClockIn = 0,
    ClockOut = 1,
    BreakStart = 2,
    BreakEnd = 3,
    ManualAdjustment = 4,
}

/// <summary>
/// Source of an attendance event row (V1: Standard only; Manual reserved).
/// Persisted as a string via <c>HasConversion&lt;string&gt;()</c>.
/// </summary>
public enum AttendanceEventSource
{
    Standard = 0,
    Manual = 1,
}

/// <summary>
/// Verification methods. <c>AuthenticatedUser</c> and <c>Qr</c> are V1;
/// the rest are reserved extension points for the verification engine.
/// Persisted as a string via <c>HasConversion&lt;string&gt;()</c>;
/// wire values are SCREAMING_SNAKE (e.g. "AUTHENTICATED_USER").
/// </summary>
public enum AttendanceVerificationMethod
{
    AuthenticatedUser = 0,
    Qr = 1,
    Gps = 2,
    Nfc = 3,
    Ble = 4,
    TrustedDevice = 5,
    Biometric = 6,
    Wifi = 7,
}

/// <summary>
/// Lifecycle of a printed, business-owned QR credential.
/// Persisted as a string via <c>HasConversion&lt;string&gt;()</c>.
/// </summary>
public enum AttendanceCredentialStatus
{
    Active = 0,
    Revoked = 1,
}

/// <summary>
/// Lifecycle of an attendance session. <c>Open</c>/<c>Closed</c> are V1;
/// <c>Abandoned</c> is reserved. Persisted as a string via
/// <c>HasConversion&lt;string&gt;()</c>.
/// </summary>
public enum AttendanceSessionStatus
{
    Open = 0,
    Closed = 1,
    Abandoned = 2,
}
