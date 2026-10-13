using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;

namespace Termin.Calendar;

public sealed class TodoInput
{
    public string? Summary { get; set; }
    public string? Due { get; set; }
    public string? DueDate { get; set; }
    public string? Timezone { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public int? Priority { get; set; }
}

/// <summary>VTODO mapping and editing.</summary>
public static class TodoMapper
{
    public static readonly string[] OpenStatuses = ["NEEDS-ACTION", "IN-PROCESS"];

    public static TodoDto ToDto(Todo t, TimeZoneInfo zone, string? calendar, string? etag)
    {
        var dto = new TodoDto
        {
            Uid = t.Uid ?? "",
            Calendar = calendar,
            Summary = string.IsNullOrWhiteSpace(t.Summary) ? null : t.Summary,
            Status = string.IsNullOrWhiteSpace(t.Status) ? "NEEDS-ACTION" : t.Status.ToUpperInvariant(),
            Priority = t.Priority > 0 ? t.Priority : null,
            PercentComplete = t.PercentComplete > 0 ? t.PercentComplete : null,
            Etag = etag,
        };
        if (!string.IsNullOrEmpty(t.Description))
            dto.Description = t.Description.Length > EventReader.DescriptionCap ? t.Description[..EventReader.DescriptionCap] : t.Description;

        if (t.Due is { } due)
        {
            if (due.HasTime) dto.Due = TimeRules.Format(TimeRules.Resolve(due, zone).Value);
            else dto.DueDate = TimeRules.Format(due.Date);
            dto.DueKey = TimeRules.Instant(due, zone);
        }
        if (t.Completed is { } c) dto.Completed = EventReader.FormatCal(c, zone);
        return dto;
    }

    public static bool IsOpen(TodoDto t) => OpenStatuses.Contains(t.Status);

    public static IcalCalendar Create(TodoInput input, TimeZoneInfo defaultZone, out string uid)
    {
        if (string.IsNullOrWhiteSpace(input.Summary))
            throw TerminException.Invalid("'summary' is required for a new todo.");
        uid = Guid.NewGuid().ToString();
        var todo = new Todo { Uid = uid };
        var now = CalDateTime.UtcNow;
        todo.Created = now;
        todo.DtStamp = now;
        todo.LastModified = now;
        Apply(todo, input, defaultZone);
        if (string.IsNullOrEmpty(todo.Status)) todo.Status = "NEEDS-ACTION";
        var cal = EventWriter.NewCalendar();
        cal.Todos.Add(todo);
        Ics.EnsureTimeZones(cal);
        return cal;
    }

    public static void Update(IcalCalendar cal, TodoInput input, TimeZoneInfo defaultZone)
    {
        var todo = cal.Todos.FirstOrDefault(t => t.RecurrenceIdentifier is null) ?? cal.Todos.FirstOrDefault()
            ?? throw TerminException.NotFound("The resource contains no todo.");
        Apply(todo, input, defaultZone);
        Ics.Touch(todo);
        Ics.EnsureTimeZones(cal);
    }

    private static void Apply(Todo todo, TodoInput input, TimeZoneInfo defaultZone)
    {
        if (input.Summary is not null) todo.Summary = input.Summary;
        if (input.Description is not null) todo.Description = input.Description.Length == 0 ? null : input.Description;
        if (input.Priority is { } p)
        {
            if (p is < 0 or > 9) throw TerminException.Invalid("'priority' must be 0 (none) to 9 (lowest); 1 is highest.");
            todo.Priority = p;
        }

        if (input.Due is not null && input.DueDate is not null)
            throw TerminException.InvalidTime("Use either 'due' or 'due_date', not both.");
        if (input.DueDate is not null)
            todo.Due = input.DueDate.Length == 0 ? null : TimeRules.ToCal(TimeRules.ParseDate(input.DueDate, "due_date"));
        else if (input.Due is not null)
        {
            if (input.Due.Length == 0) todo.Due = null;
            else
            {
                var zone = input.Timezone is not null ? TimeRules.ResolveZone(input.Timezone) : defaultZone;
                todo.Due = TimeRules.ToCal(TimeRules.ParseInstant(input.Due, input.Timezone, "due"), zone);
            }
        }

        if (input.Status is not null)
        {
            var status = input.Status.Trim().ToUpperInvariant().Replace('_', '-');
            if (status is "DONE" or "COMPLETE") status = "COMPLETED";
            if (status is "OPEN" or "TODO") status = "NEEDS-ACTION";
            if (status is not ("NEEDS-ACTION" or "IN-PROCESS" or "COMPLETED" or "CANCELLED"))
                throw TerminException.Invalid($"Unknown status '{input.Status}'.", "Use NEEDS-ACTION, IN-PROCESS, COMPLETED or CANCELLED.");
            todo.Status = status;
            if (status == "COMPLETED")
            {
                todo.Completed ??= CalDateTime.UtcNow;
                todo.PercentComplete = 100;
            }
            else
            {
                todo.Completed = null;
                if (todo.PercentComplete == 100) todo.PercentComplete = 0;
            }
        }
    }
}
