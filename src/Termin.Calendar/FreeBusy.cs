using System.Globalization;

namespace Termin.Calendar;

public sealed record BusyBlock(DateTimeOffset Start, DateTimeOffset End);

public sealed class FreeBusyResult
{
    public string Tz { get; set; } = "";
    public List<Slot> Busy { get; set; } = [];
    public List<Slot> Free { get; set; } = [];
    public string? WorkingHours { get; set; }

    public sealed class Slot
    {
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
        public int? Minutes { get; set; }
    }
}

/// <summary>Working hours like "09:00-17:00" or "Mon-Fri 08:00-18:00".</summary>
public sealed record WorkingHours(HashSet<DayOfWeek> Days, TimeOnly From, TimeOnly To)
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static WorkingHours Parse(string text)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) throw Bad(text);
        var days = parts.Length == 2 ? ParseDays(parts[0], text) : Week.ToHashSet();
        var times = parts[^1].Split('-');
        if (times.Length != 2
            || !TimeOnly.TryParseExact(times[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !TimeOnly.TryParseExact(times[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            || to <= from)
            throw Bad(text);
        return new WorkingHours(days, from, to);
    }

    private static HashSet<DayOfWeek> ParseDays(string spec, string text)
    {
        var result = new HashSet<DayOfWeek>();
        foreach (var piece in spec.Split(','))
        {
            var range = piece.Split('-');
            var a = Day(range[0], text);
            var b = range.Length == 2 ? Day(range[1], text) : a;
            var i = Array.IndexOf(Week, a);
            while (true)
            {
                result.Add(Week[i]);
                if (Week[i] == b) break;
                i = (i + 1) % 7;
            }
        }
        return result;
    }

    private static DayOfWeek Day(string s, string text) => s.Trim().ToLowerInvariant()[..Math.Min(2, s.Trim().Length)] switch
    {
        "mo" => DayOfWeek.Monday, "tu" or "di" => DayOfWeek.Tuesday, "we" or "mi" => DayOfWeek.Wednesday,
        "th" or "do" => DayOfWeek.Thursday, "fr" => DayOfWeek.Friday, "sa" => DayOfWeek.Saturday,
        "su" or "so" => DayOfWeek.Sunday, _ => throw Bad(text),
    };

    private static TerminException Bad(string text) => TerminException.Invalid(
        $"Invalid working_hours '{text}'.", "Use '09:00-17:00' or 'Mon-Fri 08:00-18:00'.");
}

public static class FreeBusyCalculator
{
    /// <summary>Merges overlapping or touching busy blocks.</summary>
    public static List<BusyBlock> Merge(IEnumerable<BusyBlock> blocks)
    {
        var merged = new List<BusyBlock>();
        foreach (var b in blocks.Where(b => b.End > b.Start).OrderBy(b => b.Start))
        {
            if (merged.Count > 0 && b.Start <= merged[^1].End)
            {
                if (b.End > merged[^1].End) merged[^1] = merged[^1] with { End = b.End };
            }
            else merged.Add(b);
        }
        return merged;
    }

    /// <summary>Free windows of at least <paramref name="minMinutes"/> within [from, to), optionally limited to working hours.</summary>
    public static List<BusyBlock> FreeSlots(
        IReadOnlyList<BusyBlock> busy, DateTimeOffset from, DateTimeOffset to, int minMinutes, WorkingHours? hours, TimeZoneInfo zone)
    {
        var windows = new List<BusyBlock>();
        if (hours is null) windows.Add(new BusyBlock(from, to));
        else
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
            for (; day <= lastDay; day = day.AddDays(1))
            {
                if (!hours.Days.Contains(day.DayOfWeek)) continue;
                var ws = TimeRules.FromLocal(day.ToDateTime(hours.From), zone);
                var we = TimeRules.FromLocal(day.ToDateTime(hours.To), zone);
                if (ws < from) ws = from;
                if (we > to) we = to;
                if (we > ws) windows.Add(new BusyBlock(ws, we));
            }
        }

        var free = new List<BusyBlock>();
        var min = TimeSpan.FromMinutes(Math.Max(1, minMinutes));
        foreach (var w in windows)
        {
            var cursor = w.Start;
            foreach (var b in busy)
            {
                if (b.End <= cursor) continue;
                if (b.Start >= w.End) break;
                if (b.Start > cursor && b.Start - cursor >= min) free.Add(new BusyBlock(cursor, b.Start));
                if (b.End > cursor) cursor = b.End;
                if (cursor >= w.End) break;
            }
            if (cursor < w.End && w.End - cursor >= min) free.Add(new BusyBlock(cursor, w.End));
        }
        return free;
    }
}
