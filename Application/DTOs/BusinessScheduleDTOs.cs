using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>One working period: minutes from business-local midnight.</summary>
public class SchedulePeriodDto
{
    [JsonPropertyName("startMinute")]
    public int StartMinute { get; set; }

    [JsonPropertyName("endMinute")]
    public int EndMinute { get; set; }
}

/// <summary>One weekday of the recurring business schedule.</summary>
public class BusinessScheduleDayDto
{
    /// <summary>0 = Sunday ... 6 = Saturday.</summary>
    [JsonPropertyName("dayOfWeek")]
    public int DayOfWeek { get; set; }

    [JsonPropertyName("isClosed")]
    public bool IsClosed { get; set; }

    [JsonPropertyName("periods")]
    public List<SchedulePeriodDto> Periods { get; set; } = new();
}

/// <summary>A dated override (holiday, closure or special hours).</summary>
public class BusinessScheduleExceptionDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Business-local date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("isClosed")]
    public bool IsClosed { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("periods")]
    public List<SchedulePeriodDto> Periods { get; set; } = new();
}

public class BusinessScheduleResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; set; } = "Africa/Nairobi";

    [JsonPropertyName("days")]
    public List<BusinessScheduleDayDto> Days { get; set; } = new();

    [JsonPropertyName("exceptions")]
    public List<BusinessScheduleExceptionDto> Exceptions { get; set; } = new();
}

public class SaveBusinessScheduleRequest
{
    /// <summary>IANA zone id. Omitted leaves the business's zone unchanged.</summary>
    [JsonPropertyName("timeZoneId")]
    public string? TimeZoneId { get; set; }

    [JsonPropertyName("days")]
    public List<BusinessScheduleDayDto> Days { get; set; } = new();
}

public class SaveScheduleExceptionRequest
{
    /// <summary>Business-local date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("isClosed")]
    public bool IsClosed { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("periods")]
    public List<SchedulePeriodDto> Periods { get; set; } = new();
}
