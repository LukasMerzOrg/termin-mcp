using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using IcalCalendar = Ical.Net.Calendar;

namespace Termin.Calendar;

/// <summary>One occurrence of an event, before mapping to the JSON model.</summary>
public sealed record EventOccurrence(
    CalendarEvent Source,
    CalDateTime Start,
    CalDateTime End,
    CalDateTime? RecurrenceId,
    bool Recurring);

/// <summary>Range expansion and mapping of VEVENTs to the compact JSON model.</summary>
public static class EventReader
{
    public const int MaxOccurrencesPerQuery = 1000;
    public const int DescriptionCap = 2000;

    /// <summary>
    /// Returns the occurrences of a calendar resource that overlap [from, to).
    /// Resources that still carry RRULE/RDATE are expanded client-side, honoring
    /// EXDATE and RECURRENCE-ID overrides. Resources the server already expanded
    /// (instances only) are passed through.
    /// </summary>
    public static List<EventOccurrence> Expand(IcalCalendar cal, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo defaultZone, int max = MaxOccurrencesPerQuery)
    {
        var result = new List<EventOccurrence>();
        var events = cal.Events.Where(e => e.DtStart is not null).ToList();
        if (events.Count == 0) return result;

        var master = events.FirstOrDefault(e => e.RecurrenceIdentifier is null);
        var seriesRecurring = master is not null && Ics.IsRecurring(master);

        if (!events.Any(Ics.IsRecurring))
        {
            // Single events or server-expanded instances.
            foreach (var e in events)
            {
                if (e.RecurrenceIdentifier is not null && IsCancelled(e)) continue;
                var start = e.DtStart!;
                var end = Ics.EffectiveEnd(e);
                if (!Overlaps(start, end, from, to, defaultZone)) continue;
                var rid = e.RecurrenceIdentifier?.StartTime;
                result.Add(new EventOccurrence(e, start, end, rid, rid is not null || seriesRecurring));
                if (result.Count >= max) break;
            }
            return result;
        }

        // Client-side expansion. Start early enough to catch long occurrences that began before the range.
        var lookback = events.Max(e => SafeDuration(e, defaultZone));
        var evalStart = TimeRules.ToCalUtc(from - lookback - TimeSpan.FromDays(1));
        var evalEnd = TimeRules.ToCalUtc(to);
        var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = 10_000 };

        foreach (var occ in cal.GetOccurrences<CalendarEvent>(evalStart, options).TakeWhileBefore(evalEnd))
        {
            if (occ.Source is not CalendarEvent src) continue;
            var start = occ.Period.StartTime;
            var end = occ.Period.EffectiveEndTime ?? start;
            if (!Overlaps(start, end, from, to, defaultZone)) continue;

            var isOverride = src.RecurrenceIdentifier is not null;
            if (isOverride && IsCancelled(src)) continue;
            var recurring = isOverride || Ics.IsRecurring(src);
            var rid = isOverride ? src.RecurrenceIdentifier!.StartTime : recurring ? start : null;
            result.Add(new EventOccurrence(src, start, end, rid, recurring));
            if (result.Count >= max) break;
        }
        return result;
    }

    public static bool Overlaps(CalDateTime start, CalDateTime end, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var s = TimeRules.Instant(start, zone);
        var e = TimeRules.Instant(end, zone);
        if (e <= s) return s >= from && s < to;
        return s < to && e > from;
    }

    public static EventDto ToDto(EventOccurrence occ, TimeZoneInfo defaultZone, string? calendar, string? etag)
    {
        var dto = new EventDto();
        Fill(dto, occ.Source, occ.Start, occ.End, occ.RecurrenceId, occ.Recurring, defaultZone, calendar, etag);
        return dto;
    }

    /// <summary>The full shape for get_event.</summary>
    public static EventDetailDto ToDetail(
        CalendarEvent ev, IcalCalendar cal, EventOccurrence? occ, TimeZoneInfo defaultZone,
        string? calendar, string? etag, bool fullText, bool includeIcal)
    {
        var master = Ics.Master(cal);
        var dto = new EventDetailDto();
        var recurring = occ?.Recurring ?? (Ics.IsRecurring(ev) || ev.RecurrenceIdentifier is not null);
        Fill(dto, ev, occ?.Start ?? ev.DtStart!, occ?.End ?? Ics.EffectiveEnd(ev),
            occ?.RecurrenceId ?? ev.RecurrenceIdentifier?.StartTime, recurring, defaultZone, calendar, etag);

        if (!string.IsNullOrEmpty(ev.Description))
        {
            if (!fullText && ev.Description.Length > DescriptionCap)
            {
                dto.Description = ev.Description[..DescriptionCap];
                dto.DescriptionTruncated = true;
            }
            else dto.Description = ev.Description;
        }

        if (ev.Organizer?.Value is { } org)
            dto.Organizer = MailOf(org) ?? ev.Organizer.CommonName;

        if (ev.Attendees.Count > 0)
            dto.Attendees = ev.Attendees.Select(a => new AttendeeDto
            {
                Email = a.Value is null ? null : MailOf(a.Value),
                Name = a.CommonName,
                Status = a.ParticipationStatus,
                Role = a.Role,
            }).ToList();

        var ruleSource = master ?? ev;
        if (ruleSource.RecurrenceRule is { } rrule)
            dto.Rrule = rrule.ToString();

        var exdates = ruleSource.ExceptionDates.GetAllDates().ToList();
        if (exdates.Count > 0)
            dto.Exdates = exdates.Select(d => FormatCal(d, defaultZone)).ToList();

        var alarms = ev.Alarms.Where(a => a.Trigger is not null).ToList();
        if (alarms.Count > 0)
            dto.Reminders = alarms.Select(a => ToReminder(a, defaultZone)).ToList();

        if (ev.Categories.Count > 0) dto.Categories = ev.Categories.ToList();
        if (!string.IsNullOrEmpty(ev.Transparency)) dto.Transparency = ev.Transparency;
        if (ev.Url is not null) dto.Url = ev.Url.ToString();
        if (ev.Sequence > 0) dto.Sequence = ev.Sequence;
        if (includeIcal) dto.Ical = Ics.Serialize(cal);
        return dto;
    }

    public static string FormatCal(CalDateTime value, TimeZoneInfo defaultZone) =>
        value.HasTime ? TimeRules.Format(TimeRules.Resolve(value, defaultZone).Value) : TimeRules.Format(value.Date);

    public static bool IsCancelled(RecurringComponent c) =>
        c is CalendarEvent { Status: { } s } && s.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static void Fill(
        EventDto dto, CalendarEvent ev, CalDateTime start, CalDateTime end, CalDateTime? rid, bool recurring,
        TimeZoneInfo defaultZone, string? calendar, string? etag)
    {
        dto.Uid = ev.Uid ?? "";
        dto.Calendar = calendar;
        dto.Summary = NullIfEmpty(ev.Summary);
        dto.Location = NullIfEmpty(ev.Location);
        dto.Status = NullIfEmpty(ev.Status);
        dto.Etag = etag;
        dto.Recurring = recurring ? true : null;
        dto.RecurrenceId = rid is null ? null : FormatCal(rid, defaultZone);
        dto.Transparent = IsTransparent(ev, start.HasTime);
        dto.SearchText = string.Join('\n', ev.Summary, ev.Location, ev.Description);

        if (!start.HasTime)
        {
            var first = start.Date;
            var last = TimeRules.InclusiveEnd(first, end.Date);
            dto.AllDay = true;
            if (last == first) dto.Date = TimeRules.Format(first);
            else
            {
                dto.StartDate = TimeRules.Format(first);
                dto.EndDate = TimeRules.Format(last);
            }
            dto.SortKey = TimeRules.FromLocal(first.ToDateTime(TimeOnly.MinValue), defaultZone);
            dto.EndKey = TimeRules.FromLocal(last.AddDays(1).ToDateTime(TimeOnly.MinValue), defaultZone);
            return;
        }

        var s = TimeRules.Resolve(start, defaultZone);
        var e = end.HasTime ? TimeRules.Resolve(end, defaultZone) : s;
        // Show the end in the start's zone so both read the same way.
        var endValue = TimeZoneInfo.ConvertTime(e.Value, s.Zone);
        dto.Start = TimeRules.Format(s.Value);
        dto.End = TimeRules.Format(endValue);
        dto.Tz = TimeRules.IanaId(s.Zone);
        dto.Floating = s.Floating ? true : null;
        dto.SortKey = s.Value;
        dto.EndKey = endValue;
    }

    /// <summary>TRANSP when set; otherwise timed events are busy and all-day events are free.</summary>
    public static bool IsTransparent(CalendarEvent ev, bool timed)
    {
        if (!string.IsNullOrEmpty(ev.Transparency))
            return ev.Transparency.Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase);
        return !timed;
    }

    private static ReminderDto ToReminder(Alarm a, TimeZoneInfo zone)
    {
        var r = new ReminderDto { Action = a.Action };
        if (a.Trigger!.IsRelative && a.Trigger.Duration is { } d)
        {
            var minutes = -(int)Math.Round(d.ToTimeSpanUnspecified().TotalMinutes);
            r.MinutesBefore = minutes;
        }
        else if (a.Trigger.DateTime is { } at)
        {
            r.At = FormatCal(at, zone);
        }
        return r;
    }

    private static TimeSpan SafeDuration(CalendarEvent e, TimeZoneInfo zone)
    {
        try
        {
            var span = TimeRules.Instant(Ics.EffectiveEnd(e), zone) - TimeRules.Instant(e.DtStart!, zone);
            return span < TimeSpan.Zero ? TimeSpan.Zero : span > TimeSpan.FromDays(366) ? TimeSpan.FromDays(366) : span;
        }
        catch { return TimeSpan.FromDays(1); }
    }

    private static string? MailOf(Uri uri)
    {
        var s = uri.OriginalString;
        return s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? s[7..] : s;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
