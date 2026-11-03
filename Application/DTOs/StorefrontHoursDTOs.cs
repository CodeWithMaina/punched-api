using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  PUBLIC STOREFRONT OPENING HOURS
//
//  A business-wide presentation of the SAME scheduling data the
//  availability engine reads (staff working patterns, dated overrides
//  and the business's own booking window). This is deliberately a
//  presentation of existing domain data — there is no second schedule.
//
//  Times are business-local wall-clock ("09:00"), because the underlying
//  minutes are already stored as minutes-from-local-midnight in the
//  business's time zone. `timeZoneId` is reported so a client can label
//  them correctly.
// ═══════════════════════════════════════════════════════════════

/// <summary>One opening period in a day, with the staff break inside it (if any).</summary>
public sealed class OpeningHoursPeriodResponse
{
    /// <summary>Local opening time, `HH:mm`.</summary>
    [JsonPropertyName("opensAt")] public string OpensAt { get; set; } = string.Empty;

    /// <summary>Local closing time, `HH:mm`.</summary>
    [JsonPropertyName("closesAt")] public string ClosesAt { get; set; } = string.Empty;

    /// <summary>Local start of the break taken inside this period, when declared.</summary>
    [JsonPropertyName("breakStartsAt")] public string? BreakStartsAt { get; set; }

    /// <summary>Local end of the break taken inside this period, when declared.</summary>
    [JsonPropertyName("breakEndsAt")] public string? BreakEndsAt { get; set; }
}

/// <summary>One weekday's opening hours.</summary>
public sealed class OpeningHoursDayResponse
{
    /// <summary>Lower-case English day name (`monday` … `sunday`).</summary>
    [JsonPropertyName("day")] public string Day { get; set; } = string.Empty;

    /// <summary>0 = Sunday … 6 = Saturday (mirrors <see cref="System.DayOfWeek"/>).</summary>
    [JsonPropertyName("dayOfWeek")] public int DayOfWeek { get; set; }

    /// <summary>True when the business is closed all day.</summary>
    [JsonPropertyName("isClosed")] public bool IsClosed { get; set; }

    /// <summary>Opening periods, in ascending order. Empty when closed.</summary>
    [JsonPropertyName("periods")] public IReadOnlyList<OpeningHoursPeriodResponse> Periods { get; set; } = [];
}

/// <summary>A dated exception to the weekly pattern (a holiday or a special day).</summary>
public sealed class SpecialOpeningHoursResponse
{
    /// <summary>Business-local calendar date, `yyyy-MM-dd`.</summary>
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;

    [JsonPropertyName("isClosed")] public bool IsClosed { get; set; }

    /// <summary>The note the business recorded for the date, when present.</summary>
    [JsonPropertyName("label")] public string? Label { get; set; }

    [JsonPropertyName("periods")] public IReadOnlyList<OpeningHoursPeriodResponse> Periods { get; set; } = [];
}

/// <summary>
/// `GET /v1/businesses/{businessId}/opening-hours` — the business's
/// opening hours, derived from the availability domain.
/// </summary>
public sealed class PublicOpeningHoursResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }

    /// <summary>IANA time zone the times are expressed in.</summary>
    [JsonPropertyName("timeZoneId")] public string TimeZoneId { get; set; } = "Africa/Nairobi";

    /// <summary>
    /// False when the business has no staff schedules at all, in which case the
    /// weekly hours fall back to the business's own booking window. The client
    /// uses this to label the hours honestly rather than implying a roster.
    /// </summary>
    [JsonPropertyName("hasStaffSchedule")] public bool HasStaffSchedule { get; set; }

    /// <summary>Seven entries, Sunday first.</summary>
    [JsonPropertyName("days")] public IReadOnlyList<OpeningHoursDayResponse> Days { get; set; } = [];

    /// <summary>Dated exceptions within the published horizon (soonest first).</summary>
    [JsonPropertyName("specialHours")] public IReadOnlyList<SpecialOpeningHoursResponse> SpecialHours { get; set; } = [];
}
