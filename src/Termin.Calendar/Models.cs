using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Termin.Calendar;

/// <summary>Compact event shape shared by all read tools. Null fields are omitted.</summary>
public class EventDto
{
    public string Uid { get; set; } = "";
    public string? RecurrenceId { get; set; }
    public string? Account { get; set; }
    public string? Calendar { get; set; }
    public string? Summary { get; set; }

    // Timed events
    public string? Start { get; set; }
    public string? End { get; set; }

    // All-day events (inclusive dates)
    public string? Date { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }

    public bool? AllDay { get; set; }
    public string? Tz { get; set; }
    public bool? Floating { get; set; }
    public string? Location { get; set; }
    public bool? Recurring { get; set; }
    public string? Status { get; set; }
    public string? Etag { get; set; }

    [JsonIgnore] public DateTimeOffset SortKey { get; set; }
    [JsonIgnore] public DateTimeOffset EndKey { get; set; }
    [JsonIgnore] public bool Transparent { get; set; }
    [JsonIgnore] public string? SearchText { get; set; }
}

public sealed class EventDetailDto : EventDto
{
    public string? Description { get; set; }
    public bool? DescriptionTruncated { get; set; }
    public string? Organizer { get; set; }
    public List<AttendeeDto>? Attendees { get; set; }
    public string? Rrule { get; set; }
    public List<string>? Exdates { get; set; }
    public List<ReminderDto>? Reminders { get; set; }
    public List<string>? Categories { get; set; }
    public string? Transparency { get; set; }
    public string? Url { get; set; }
    public int? Sequence { get; set; }
    public string? Ical { get; set; }
}

public sealed class AttendeeDto
{
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? Role { get; set; }
}

public sealed class ReminderDto
{
    /// <summary>Minutes before the start (relative trigger).</summary>
    public int? MinutesBefore { get; set; }

    /// <summary>Absolute trigger time.</summary>
    public string? At { get; set; }

    public string? Action { get; set; }
}

public sealed class TodoDto
{
    public string Uid { get; set; } = "";
    public string? Account { get; set; }
    public string? Calendar { get; set; }
    public string? Summary { get; set; }
    public string? Due { get; set; }
    public string? DueDate { get; set; }
    public string? Status { get; set; }
    public string? Completed { get; set; }
    public int? Priority { get; set; }
    public int? PercentComplete { get; set; }
    public string? Description { get; set; }
    public string? Etag { get; set; }

    [JsonIgnore] public DateTimeOffset? DueKey { get; set; }
}

public sealed class CalendarDto
{
    public string Id { get; set; } = "";
    public string? Account { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string? Tz { get; set; }
    public List<string> Components { get; set; } = [];
    public bool? ReadOnly { get; set; }
}

/// <summary>Fields an update may change. A field that is not set stays unchanged.</summary>
public sealed class EventPatch
{
    [Description("New title; '' clears it.")]
    public string? Summary { get; set; }

    [Description("New location; '' clears it.")]
    public string? Location { get; set; }

    [Description("New description; '' clears it.")]
    public string? Description { get; set; }

    [Description("New start (ISO 8601 with offset). Keeps the duration unless end is given.")]
    public string? Start { get; set; }

    [Description("New end (ISO 8601 with offset).")]
    public string? End { get; set; }

    [Description("Make it an all-day event on this date (2026-10-14).")]
    public string? AllDayDate { get; set; }

    [Description("Last day (inclusive) of a multi-day all-day event.")]
    public string? AllDayEndDate { get; set; }

    [Description("IANA timezone for the event times, e.g. Europe/Zurich.")]
    public string? Timezone { get; set; }

    [Description("New RRULE without the 'RRULE:' prefix; '' removes recurrence.")]
    public string? Rrule { get; set; }

    [Description("CONFIRMED, TENTATIVE or CANCELLED.")]
    public string? Status { get; set; }

    [Description("OPAQUE (busy) or TRANSPARENT (free).")]
    public string? Transparency { get; set; }

    [Description("Replaces all reminders: minutes before start. [] removes them.")]
    public List<int>? Reminders { get; set; }

    [JsonIgnore]
    public bool ChangesTiming => Start is not null || End is not null || AllDayDate is not null || AllDayEndDate is not null || Timezone is not null;

    [JsonIgnore]
    public bool IsEmpty =>
        Summary is null && Location is null && Description is null && !ChangesTiming &&
        Rrule is null && Status is null && Transparency is null && Reminders is null;
}

public static class TerminJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
