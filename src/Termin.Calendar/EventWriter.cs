using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using IcalCalendar = Ical.Net.Calendar;

namespace Termin.Calendar;

public sealed class NewEvent
{
    public string Summary { get; set; } = "";
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? AllDayDate { get; set; }
    public string? AllDayEndDate { get; set; }
    public string? Timezone { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string? Rrule { get; set; }
    public List<int>? Reminders { get; set; }
    public string? Status { get; set; }
    public string? Transparency { get; set; }
}

public enum EditScope { This, ThisAndFuture, All }

/// <summary>Result of a series split: the truncated original and the new series.</summary>
public sealed record SplitResult(IcalCalendar Original, IcalCalendar? NewSeries, string? NewUid);

/// <summary>
/// Builds and edits VCALENDAR resources. Updates always parse, patch and
/// reserialize the stored resource so unknown and X- properties survive.
/// </summary>
public static class EventWriter
{
    public const string ProductId = "-//Termin//termin-mcp//EN";

    public static EditScope ParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => EditScope.All,
        "this" => EditScope.This,
        "this_and_future" => EditScope.ThisAndFuture,
        _ => throw TerminException.Invalid($"Unknown scope '{scope}'.", "Use this, this_and_future or all."),
    };

    public static IcalCalendar NewCalendar() => new() { ProductId = ProductId };

    public static IcalCalendar BuildEvent(NewEvent input, TimeZoneInfo defaultZone, out string uid)
    {
        if (string.IsNullOrWhiteSpace(input.Summary))
            throw TerminException.Invalid("'summary' is required.");

        uid = Guid.NewGuid().ToString();
        var ev = new CalendarEvent { Uid = uid, Summary = input.Summary.Trim() };
        var (start, end) = ResolveTiming(input.Start, input.End, input.AllDayDate, input.AllDayEndDate, input.Timezone, defaultZone, null);
        ev.DtStart = start;
        ev.DtEnd = end;
        if (!string.IsNullOrWhiteSpace(input.Location)) ev.Location = input.Location;
        if (!string.IsNullOrWhiteSpace(input.Description)) ev.Description = input.Description;
        if (!string.IsNullOrWhiteSpace(input.Rrule)) ev.RecurrenceRule = ParseRrule(input.Rrule);
        if (!string.IsNullOrWhiteSpace(input.Status)) ev.Status = NormalizeStatus(input.Status);
        if (!string.IsNullOrWhiteSpace(input.Transparency)) ev.Transparency = NormalizeTransparency(input.Transparency);
        if (input.Reminders is { Count: > 0 }) SetReminders(ev, input.Reminders, ev.Summary);

        var now = CalDateTime.UtcNow;
        ev.DtStamp = now;
        ev.Created = now;
        ev.LastModified = now;
        ev.Sequence = 0;

        var cal = NewCalendar();
        cal.Events.Add(ev);
        Ics.EnsureTimeZones(cal);
        return cal;
    }

    /// <summary>Applies a patch to one event series or occurrence of a stored resource.</summary>
    public static void Update(IcalCalendar cal, EventPatch patch, EditScope scope, string? recurrenceId, TimeZoneInfo defaultZone)
    {
        if (patch.IsEmpty) throw TerminException.Invalid("'patch' has no fields to change.");
        var master = Ics.Master(cal) ?? throw TerminException.NotFound("The resource contains no event.");
        var recurring = Ics.IsRecurring(master);

        if (!recurring || scope == EditScope.All)
        {
            if (scope != EditScope.All && recurrenceId is not null && !recurring)
                throw TerminException.Invalid("The event is not recurring; use scope 'all'.");
            PatchSeries(cal, master, patch, defaultZone);
            Ics.EnsureTimeZones(cal);
            return;
        }

        if (recurrenceId is null)
            throw TerminException.Invalid("'recurrence_id' is required for scope 'this'.");
        if (patch.Rrule is not null)
            throw TerminException.Invalid("'rrule' can only change with scope 'all' or 'this_and_future'.");

        var originalStart = FindOccurrence(cal, master, recurrenceId, defaultZone);
        var target = Ics.Overrides(cal).FirstOrDefault(o => SameInstant(o.RecurrenceIdentifier!.StartTime, originalStart, defaultZone));
        if (target is null)
        {
            target = Ics.CloneForOverride(master);
            var duration = Ics.EffectiveEnd(master).Subtract(master.DtStart!);
            target.RecurrenceIdentifier = new RecurrenceIdentifier(Align(master.DtStart!, originalStart, defaultZone), null);
            if (target.Duration is not null) target.Duration = null;
            target.DtStart = Align(master.DtStart!, originalStart, defaultZone);
            target.DtEnd = target.DtStart.Add(duration);
            target.Sequence = master.Sequence;
            cal.Events.Add(target);
        }
        ApplyPatch(target, patch, defaultZone);
        Ics.Touch(target);
        Ics.EnsureTimeZones(cal);
    }

    /// <summary>
    /// Splits a series at an occurrence: the original RRULE is truncated with UNTIL
    /// and a new series with a new UID starts at the occurrence (RFC 5545 practice
    /// used by Apple and Google). The patch applies to the new series.
    /// </summary>
    public static SplitResult Split(IcalCalendar cal, string recurrenceId, EventPatch? patch, TimeZoneInfo defaultZone)
    {
        var master = Ics.Master(cal) ?? throw TerminException.NotFound("The resource contains no event.");
        if (!Ics.IsRecurring(master))
            throw TerminException.Invalid("The event is not recurring; use scope 'all'.");

        var splitAt = FindOccurrence(cal, master, recurrenceId, defaultZone);
        var splitInstant = TimeRules.Instant(splitAt, defaultZone);
        if (splitInstant <= TimeRules.Instant(master.DtStart!, defaultZone))
            return new SplitResult(cal, null, null); // First occurrence: caller treats it as scope 'all'.

        var rule = master.RecurrenceRule;
        int? remainingCount = null;
        if (rule?.Count is { } count)
            remainingCount = Math.Max(1, count - CountBefore(master, splitAt, defaultZone));

        // New series.
        var newUid = Guid.NewGuid().ToString();
        var next = Ics.CloneEvent(master);
        next.Uid = newUid;
        var duration = Ics.EffectiveEnd(master).Subtract(master.DtStart!);
        if (next.Duration is not null) next.Duration = null;
        next.DtStart = Align(master.DtStart!, splitAt, defaultZone);
        next.DtEnd = next.DtStart.Add(duration);
        next.RecurrenceRule = null;
        if (rule is not null)
        {
            var copy = new RecurrencePattern(rule.ToString()!);
            if (remainingCount is not null) copy.Count = remainingCount;
            next.RecurrenceRule = copy;
        }
        next.ExceptionDates.Clear();
        foreach (var ex in master.ExceptionDates.GetAllDates().Where(d => TimeRules.Instant(d, defaultZone) >= splitInstant))
            next.ExceptionDates.Add(ex);
        next.Sequence = 0;
        next.Created = CalDateTime.UtcNow;

        var newCal = NewCalendar();
        newCal.Events.Add(next);
        foreach (var o in Ics.Overrides(cal).Where(o => TimeRules.Instant(o.RecurrenceIdentifier!.StartTime, defaultZone) >= splitInstant).ToList())
        {
            Ics.RemoveComponent(cal, o);
            o.Uid = newUid;
            newCal.Events.Add(o);
        }

        if (patch is not null && !patch.IsEmpty) PatchSeries(newCal, next, patch, defaultZone);
        else Ics.Touch(next);

        TruncateBefore(cal, master, splitAt, defaultZone);
        Ics.EnsureTimeZones(cal);
        Ics.EnsureTimeZones(newCal);
        return new SplitResult(cal, newCal, newUid);
    }

    /// <summary>Removes one occurrence: drops its override and adds an EXDATE.</summary>
    /// <returns>false when the event is not recurring (the caller deletes the resource).</returns>
    public static bool DeleteOccurrence(IcalCalendar cal, string recurrenceId, TimeZoneInfo defaultZone)
    {
        var master = Ics.Master(cal) ?? throw TerminException.NotFound("The resource contains no event.");
        if (!Ics.IsRecurring(master)) return false;
        var occurrence = FindOccurrence(cal, master, recurrenceId, defaultZone);
        foreach (var o in Ics.Overrides(cal).Where(o => SameInstant(o.RecurrenceIdentifier!.StartTime, occurrence, defaultZone)).ToList())
            Ics.RemoveComponent(cal, o);
        master.ExceptionDates.Add(Align(master.DtStart!, occurrence, defaultZone));
        Ics.Touch(master);
        Ics.EnsureTimeZones(cal);
        return true;
    }

    /// <summary>Ends the series before an occurrence.</summary>
    /// <returns>false when the occurrence is the first one (the caller deletes the resource).</returns>
    public static bool DeleteFrom(IcalCalendar cal, string recurrenceId, TimeZoneInfo defaultZone)
    {
        var master = Ics.Master(cal) ?? throw TerminException.NotFound("The resource contains no event.");
        if (!Ics.IsRecurring(master)) return false;
        var at = FindOccurrence(cal, master, recurrenceId, defaultZone);
        if (TimeRules.Instant(at, defaultZone) <= TimeRules.Instant(master.DtStart!, defaultZone)) return false;
        TruncateBefore(cal, master, at, defaultZone);
        return true;
    }

    /// <summary>Finds the original start of an occurrence by its recurrence id.</summary>
    public static CalDateTime FindOccurrence(IcalCalendar cal, CalendarEvent master, string recurrenceId, TimeZoneInfo defaultZone)
    {
        var target = ParseRecurrenceId(recurrenceId, defaultZone);

        foreach (var o in Ics.Overrides(cal))
            if (Matches(o.RecurrenceIdentifier!.StartTime, target, defaultZone))
                return o.RecurrenceIdentifier.StartTime;

        var probe = Ics.CloneEvent(master);
        var probeCal = NewCalendar();
        probeCal.Events.Add(probe);
        foreach (var tz in cal.TimeZones) probeCal.TimeZones.Add(tz.Copy<VTimeZone>()!);

        var from = target.Instant - TimeSpan.FromDays(2);
        var to = target.Instant + TimeSpan.FromDays(2);
        foreach (var occ in probeCal.GetOccurrences<CalendarEvent>(TimeRules.ToCalUtc(from), new EvaluationOptions { MaxUnmatchedIncrementsLimit = 10_000 })
                     .TakeWhileBefore(TimeRules.ToCalUtc(to)))
        {
            if (Matches(occ.Period.StartTime, target, defaultZone))
                return occ.Period.StartTime;
        }

        throw TerminException.NotFound(
            $"No occurrence with recurrence_id '{recurrenceId}'.",
            "Use the recurrence_id exactly as returned by get_events.");
    }

    public static void SetReminders(RecurringComponent c, IEnumerable<int> minutesBefore, string? summary)
    {
        c.Alarms.Clear();
        foreach (var m in minutesBefore.Distinct())
        {
            if (m < 0 || m > 40_320) throw TerminException.Invalid("Reminders must be between 0 and 40320 minutes before the start.");
            c.Alarms.Add(new Alarm
            {
                Action = "DISPLAY",
                Description = string.IsNullOrWhiteSpace(summary) ? "Reminder" : summary,
                Trigger = new Trigger(Duration.FromMinutes(-m)),
            });
        }
    }

    public static RecurrencePattern ParseRrule(string rrule)
    {
        var text = rrule.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        if (!text.Contains("FREQ=", StringComparison.OrdinalIgnoreCase))
            throw TerminException.Invalid($"Invalid RRULE '{rrule}'.", "Example: FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10");
        try { return new RecurrencePattern(text.ToUpperInvariant()); }
        catch (Exception) { throw TerminException.Invalid($"Invalid RRULE '{rrule}'.", "Example: FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10"); }
    }

    /// <summary>
    /// Resolves the timing inputs of a create or patch into DTSTART/DTEND. With a
    /// <paramref name="current"/> event, missing parts are taken from it.
    /// </summary>
    public static (CalDateTime Start, CalDateTime End) ResolveTiming(
        string? start, string? end, string? allDayDate, string? allDayEndDate, string? timezone,
        TimeZoneInfo defaultZone, CalendarEvent? current)
    {
        if (allDayDate is not null || (allDayEndDate is not null && current is { DtStart.HasTime: false }))
        {
            if (start is not null || end is not null)
                throw TerminException.InvalidTime("Use either start/end or all_day_date(s), not both.");
            var first = allDayDate is not null ? TimeRules.ParseDate(allDayDate, "all_day_date") : current!.DtStart!.Date;
            DateOnly last;
            if (allDayEndDate is not null) last = TimeRules.ParseDate(allDayEndDate, "all_day_end_date");
            else if (current is { DtStart.HasTime: false } && allDayDate is not null)
            {
                var span = TimeRules.InclusiveEnd(current.DtStart.Date, Ics.EffectiveEnd(current).Date).DayNumber - current.DtStart.Date.DayNumber;
                last = first.AddDays(span);
            }
            else last = first;
            if (last < first) throw TerminException.InvalidTime("all_day_end_date is before all_day_date.");
            return (TimeRules.ToCal(first), TimeRules.ToCal(TimeRules.ExclusiveEnd(last)));
        }

        TimeZoneInfo zone = timezone is not null
            ? TimeRules.ResolveZone(timezone)
            : current?.DtStart is { HasTime: true, IsFloating: false } cs
                ? (cs.IsUtc ? TimeZoneInfo.Utc : TimeRules.TryResolveZone(cs.TzId) ?? defaultZone)
                : defaultZone;
        // Naive times are only accepted together with an explicit timezone.
        var tzForParsing = timezone;

        DateTimeOffset s;
        TimeSpan length;
        if (current is { DtStart.HasTime: true })
        {
            var curStart = TimeRules.Instant(current.DtStart, defaultZone);
            var curEnd = TimeRules.Instant(Ics.EffectiveEnd(current), defaultZone);
            s = start is not null ? TimeRules.ParseInstant(start, tzForParsing, "start") : curStart;
            length = curEnd - curStart;
        }
        else
        {
            if (start is null)
                throw TerminException.InvalidTime("'start' is required (or all_day_date for an all-day event).");
            s = TimeRules.ParseInstant(start, tzForParsing, "start");
            length = TimeSpan.FromHours(1);
        }

        var e = end is not null ? TimeRules.ParseInstant(end, tzForParsing, "end") : s + length;
        if (e < s) throw TerminException.InvalidTime("'end' is before 'start'.");
        return (TimeRules.ToCal(s, zone), TimeRules.ToCal(e, zone));
    }

    private static void PatchSeries(IcalCalendar cal, CalendarEvent master, EventPatch patch, TimeZoneInfo defaultZone)
    {
        var oldStart = master.DtStart!;
        var oldSummary = master.Summary;
        var oldLocation = master.Location;
        var oldDescription = master.Description;

        ApplyPatch(master, patch, defaultZone);
        Ics.Touch(master);

        var overrides = Ics.Overrides(cal).Where(o => o.Uid == master.Uid).ToList();
        if (patch.ChangesTiming && Ics.IsRecurring(master))
        {
            var newStart = master.DtStart!;
            if (oldStart.HasTime != newStart.HasTime)
            {
                if (overrides.Count > 0 || master.ExceptionDates.GetAllDates().Any())
                    throw TerminException.Invalid(
                        "Switching a recurring series with exceptions between all-day and timed is not supported.",
                        "Delete the series and create a new one.");
            }
            else
            {
                // Keep RECURRENCE-IDs and EXDATEs pointing at the moved occurrences.
                var delta = newStart.HasTime
                    ? TimeRules.Instant(newStart, defaultZone) - TimeRules.Instant(oldStart, defaultZone)
                    : TimeSpan.FromDays(newStart.Date.DayNumber - oldStart.Date.DayNumber);
                if (delta != TimeSpan.Zero)
                {
                    var exdates = master.ExceptionDates.GetAllDates().ToList();
                    master.ExceptionDates.Clear();
                    foreach (var d in exdates) master.ExceptionDates.Add(Shift(newStart, d, delta, defaultZone));
                    foreach (var o in overrides)
                        o.RecurrenceIdentifier = new RecurrenceIdentifier(Shift(newStart, o.RecurrenceIdentifier!.StartTime, delta, defaultZone), null);
                }
            }
        }

        // Text changes to the series reach overrides that still carried the old text.
        foreach (var o in overrides)
        {
            var changed = false;
            if (patch.Summary is not null && o.Summary == oldSummary) { o.Summary = NullIfEmpty(patch.Summary); changed = true; }
            if (patch.Location is not null && o.Location == oldLocation) { o.Location = NullIfEmpty(patch.Location); changed = true; }
            if (patch.Description is not null && o.Description == oldDescription) { o.Description = NullIfEmpty(patch.Description); changed = true; }
            if (changed) Ics.Touch(o);
        }
    }

    private static void ApplyPatch(CalendarEvent ev, EventPatch patch, TimeZoneInfo defaultZone)
    {
        if (patch.Summary is not null) ev.Summary = NullIfEmpty(patch.Summary);
        if (patch.Location is not null) ev.Location = NullIfEmpty(patch.Location);
        if (patch.Description is not null) ev.Description = NullIfEmpty(patch.Description);
        if (patch.Status is not null) ev.Status = patch.Status == "" ? null : NormalizeStatus(patch.Status);
        if (patch.Transparency is not null) ev.Transparency = patch.Transparency == "" ? null : NormalizeTransparency(patch.Transparency);

        if (patch.ChangesTiming)
        {
            var timingSource = patch.AllDayDate is null && patch.AllDayEndDate is null && patch.Start is null && patch.End is null && patch.Timezone is not null
                ? (start: (string?)TimeRules.Format(TimeRules.Instant(ev.DtStart!, defaultZone)), end: (string?)TimeRules.Format(TimeRules.Instant(Ics.EffectiveEnd(ev), defaultZone)))
                : (start: patch.Start, end: patch.End);
            var (s, e) = ResolveTiming(timingSource.start, timingSource.end, patch.AllDayDate, patch.AllDayEndDate, patch.Timezone, defaultZone, ev);
            if (ev.Duration is not null) ev.Duration = null;
            ev.DtStart = s;
            ev.DtEnd = e;
        }

        if (patch.Rrule is not null)
        {
            if (ev.RecurrenceIdentifier is not null)
                throw TerminException.Invalid("An occurrence cannot carry its own RRULE.");
            ev.RecurrenceRule = patch.Rrule.Length > 0 ? ParseRrule(patch.Rrule) : null;
        }

        if (patch.Reminders is not null) SetReminders(ev, patch.Reminders, ev.Summary);
    }

    private static void TruncateBefore(IcalCalendar cal, CalendarEvent master, CalDateTime at, TimeZoneInfo defaultZone)
    {
        var atInstant = TimeRules.Instant(at, defaultZone);
        var rule = master.RecurrenceRule;
        if (rule is not null)
        {
            var truncated = new RecurrencePattern(rule.ToString()!) { Count = null };
            truncated.Until = master.DtStart!.HasTime
                ? TimeRules.ToCalUtc(atInstant - TimeSpan.FromSeconds(1))
                : new CalDateTime(at.Date.AddDays(-1));
            master.RecurrenceRule = truncated;
        }

        // RDATEs, EXDATEs and overrides after the cut belong to the new series (or nobody).
        var exdates = master.ExceptionDates.GetAllDates().Where(d => TimeRules.Instant(d, defaultZone) < atInstant).ToList();
        master.ExceptionDates.Clear();
        foreach (var d in exdates) master.ExceptionDates.Add(d);
        var rdates = master.RecurrenceDates.GetAllDates().Where(d => TimeRules.Instant(d, defaultZone) < atInstant).ToList();
        master.RecurrenceDates.Clear();
        foreach (var d in rdates) master.RecurrenceDates.Add(d);
        foreach (var o in Ics.Overrides(cal).Where(o => TimeRules.Instant(o.RecurrenceIdentifier!.StartTime, defaultZone) >= atInstant).ToList())
            Ics.RemoveComponent(cal, o);
        Ics.Touch(master);
    }

    private static int CountBefore(CalendarEvent master, CalDateTime at, TimeZoneInfo defaultZone)
    {
        // COUNT counts generated instances, EXDATEs included, so count on a copy without them.
        var probe = Ics.CloneEvent(master);
        probe.ExceptionDates.Clear();
        var cal = NewCalendar();
        cal.Events.Add(probe);
        return probe.GetOccurrences(TimeRules.ToCalUtc(TimeRules.Instant(probe.DtStart!, defaultZone)), new EvaluationOptions { MaxUnmatchedIncrementsLimit = 10_000 })
            .TakeWhileBefore(TimeRules.ToCalUtc(TimeRules.Instant(at, defaultZone)))
            .Count();
    }

    private readonly record struct RidTarget(DateTimeOffset Instant, DateOnly? Date);

    private static RidTarget ParseRecurrenceId(string recurrenceId, TimeZoneInfo defaultZone)
    {
        var text = recurrenceId.Trim();
        if (text.Length == 10)
        {
            var d = TimeRules.ParseDate(text, "recurrence_id");
            return new RidTarget(TimeRules.FromLocal(d.ToDateTime(TimeOnly.MinValue), defaultZone), d);
        }
        return new RidTarget(TimeRules.ParseInstant(text, null, "recurrence_id"), null);
    }

    private static bool Matches(CalDateTime candidate, RidTarget target, TimeZoneInfo zone)
    {
        if (target.Date is { } d) return !candidate.HasTime && candidate.Date == d;
        return candidate.HasTime && TimeRules.Instant(candidate, zone) == target.Instant;
    }

    private static bool SameInstant(CalDateTime a, CalDateTime b, TimeZoneInfo zone)
    {
        if (!a.HasTime || !b.HasTime) return a.Date == b.Date && a.HasTime == b.HasTime;
        return TimeRules.Instant(a, zone) == TimeRules.Instant(b, zone);
    }

    /// <summary>Expresses an occurrence start in the same form as the reference DTSTART (DATE, UTC, floating or TZID).</summary>
    public static CalDateTime Align(CalDateTime reference, CalDateTime value, TimeZoneInfo defaultZone)
    {
        if (!reference.HasTime) return new CalDateTime(value.Date);
        var instant = TimeRules.Instant(value, defaultZone);
        if (reference.IsFloating)
            return new CalDateTime(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, defaultZone).DateTime, DateTimeKind.Unspecified), null, true);
        if (reference.IsUtc) return TimeRules.ToCalUtc(instant);
        var zone = TimeRules.TryResolveZone(reference.TzId) ?? defaultZone;
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified);
        return new CalDateTime(local, reference.TzId, true);
    }

    private static CalDateTime Shift(CalDateTime reference, CalDateTime value, TimeSpan delta, TimeZoneInfo zone)
    {
        if (!value.HasTime) return new CalDateTime(value.Date.AddDays((int)delta.TotalDays));
        // Shift by wall-clock in the series' zone so DST does not move occurrences by an hour.
        var refZone = reference.IsUtc || reference.IsFloating ? zone : TimeRules.TryResolveZone(reference.TzId) ?? zone;
        var local = TimeZoneInfo.ConvertTime(TimeRules.Instant(value, zone), refZone).DateTime + delta;
        return Align(reference, TimeRules.ToCal(TimeRules.FromLocal(local, refZone), refZone), zone);
    }

    private static string NormalizeStatus(string status)
    {
        var s = status.Trim().ToUpperInvariant();
        return s is "CONFIRMED" or "TENTATIVE" or "CANCELLED"
            ? s
            : throw TerminException.Invalid($"Unknown status '{status}'.", "Use CONFIRMED, TENTATIVE or CANCELLED.");
    }

    private static string NormalizeTransparency(string value)
    {
        var s = value.Trim().ToUpperInvariant();
        return s switch
        {
            "OPAQUE" or "BUSY" => "OPAQUE",
            "TRANSPARENT" or "FREE" => "TRANSPARENT",
            _ => throw TerminException.Invalid($"Unknown transparency '{value}'.", "Use OPAQUE (busy) or TRANSPARENT (free)."),
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
