using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;

namespace Termin.Calendar;

/// <summary>Parsing and serialization helpers around Ical.Net.</summary>
public static class Ics
{
    public static IcalCalendar Parse(string ics)
    {
        try
        {
            return IcalCalendar.Load(ics) ?? throw new FormatException("empty calendar");
        }
        catch (Exception ex) when (ex is not TerminException)
        {
            throw new TerminException(ErrorCodes.BackendUnavailable, "The server returned calendar data that could not be parsed.");
        }
    }

    public static IcalCalendar? TryParse(string? ics)
    {
        if (string.IsNullOrWhiteSpace(ics)) return null;
        try { return IcalCalendar.Load(ics); }
        catch { return null; }
    }

    public static string Serialize(IcalCalendar calendar) => new CalendarSerializer().SerializeToString(calendar)!;

    public static bool IsRecurring(RecurringComponent c) =>
        c.Properties.Any(p => p.Name is "RRULE" or "RDATE");

    /// <summary>The master VEVENT of a resource (the one without RECURRENCE-ID).</summary>
    public static CalendarEvent? Master(IcalCalendar cal) =>
        cal.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? cal.Events.FirstOrDefault();

    public static IEnumerable<CalendarEvent> Overrides(IcalCalendar cal) =>
        cal.Events.Where(e => e.RecurrenceIdentifier is not null);

    /// <summary>Removes exactly this component. Ical.Net's Remove compares by value and can hit a sibling with the same UID.</summary>
    public static void RemoveComponent(IcalCalendar cal, Ical.Net.ICalendarObject component)
    {
        var keep = cal.Children.Where(c => !ReferenceEquals(c, component)).ToList();
        if (keep.Count == cal.Children.Count) return;
        cal.Children.Clear();
        foreach (var c in keep) cal.Children.Add(c);
    }

    public static CalDateTime EffectiveEnd(CalendarEvent e)
    {
        if (e.DtEnd is not null) return e.DtEnd;
        if (e.Duration is { } d) return e.DtStart!.Add(d);
        // RFC 5545: a DATE start without end lasts one day, a DATE-TIME start lasts zero.
        return e.DtStart!.HasTime ? e.DtStart : e.DtStart.AddDays(1);
    }

    /// <summary>Sets DTSTAMP, LAST-MODIFIED and bumps SEQUENCE on a changed component.</summary>
    public static void Touch(RecurringComponent c)
    {
        var now = CalDateTime.UtcNow;
        c.DtStamp = now;
        c.LastModified = now;
        c.Sequence += 1;
    }

    /// <summary>Embeds VTIMEZONE components for every TZID used, as strict servers require.</summary>
    public static void EnsureTimeZones(IcalCalendar cal)
    {
        var existing = new HashSet<string>(cal.TimeZones.Select(t => t.TzId).Where(t => t is not null)!, StringComparer.OrdinalIgnoreCase);
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cal.Children.OfType<RecurringComponent>())
        {
            Add(c.DtStart);
            Add(c.RecurrenceIdentifier?.StartTime);
            if (c is CalendarEvent e) Add(e.DtEnd);
            if (c is Todo t) Add(t.Due);
            foreach (var d in c.ExceptionDates.GetAllDates()) Add(d);
        }

        foreach (var tz in needed.Where(t => !existing.Contains(t)))
        {
            try
            {
                cal.AddTimeZone(VTimeZone.FromDateTimeZone(tz, new DateTime(DateTime.UtcNow.Year - 1, 1, 1), false));
            }
            catch
            {
                // Unknown to the tz database: leave it to the server.
            }
        }

        void Add(CalDateTime? d)
        {
            if (d is { HasTime: true, IsFloating: false, IsUtc: false, TzId: { } id }) needed.Add(id);
        }
    }

    /// <summary>Copies all properties and sub-components of a VEVENT except recurrence definitions.</summary>
    public static CalendarEvent CloneForOverride(CalendarEvent master)
    {
        var copy = CloneEvent(master);
        foreach (var name in new[] { "RRULE", "RDATE", "EXDATE", "EXRULE", "RECURRENCE-ID" })
            while (copy.Properties.FirstOrDefault(p => p.Name == name) is { } p)
                copy.Properties.Remove(p);
        return copy;
    }

    /// <summary>
    /// A faithful copy of a VEVENT (parameters, X- properties and alarms included),
    /// made by serializing and re-parsing the component.
    /// </summary>
    public static CalendarEvent CloneEvent(CalendarEvent source)
    {
        var text = new ComponentSerializer().SerializeToString(source);
        var wrapper = IcalCalendar.Load("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Termin//clone//EN\r\n" + text + "END:VCALENDAR\r\n")!;
        var copy = wrapper.Events.First();
        RemoveComponent(wrapper, copy);
        return copy;
    }
}
