using System.Collections.Concurrent;
using System.Globalization;
using Ical.Net.DataTypes;

namespace Termin.Calendar;

/// <summary>
/// Time handling rules: inputs must carry an offset or an IANA zone, all-day
/// values are DATEs with an exclusive DTEND, floating times are interpreted in
/// the default zone.
/// </summary>
public static class TimeRules
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> ZoneCache = new(StringComparer.OrdinalIgnoreCase);

    public const string OffsetFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    public static TimeZoneInfo ResolveZone(string tzId)
    {
        return TryResolveZone(tzId)
            ?? throw TerminException.InvalidTime($"Unknown timezone '{tzId}'.", "Use an IANA zone such as Europe/Zurich.");
    }

    public static TimeZoneInfo? TryResolveZone(string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId)) return null;
        return ZoneCache.GetOrAdd(tzId, static id =>
        {
            if (id is "UTC" or "Z" or "Etc/UTC" or "GMT") return TimeZoneInfo.Utc;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
        });
    }

    /// <summary>The IANA id for a zone (Windows ids are converted where possible).</summary>
    public static string IanaId(TimeZoneInfo zone)
    {
        if (zone == TimeZoneInfo.Utc) return "UTC";
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    /// <summary>
    /// Parses an instant. Accepted: ISO 8601 with offset or Z, ISO 8601 with an
    /// IANA zone suffix in brackets (<c>2026-10-14T09:00[Europe/Zurich]</c>), or a
    /// local time together with an explicit <paramref name="tzId"/>. Naive times
    /// without any zone are rejected.
    /// </summary>
    public static DateTimeOffset ParseInstant(string input, string? tzId, string field = "time")
    {
        if (string.IsNullOrWhiteSpace(input))
            throw TerminException.InvalidTime($"'{field}' is empty.");

        var s = input.Trim();
        string? bracketZone = null;
        var lb = s.IndexOf('[');
        if (lb > 0 && s.EndsWith(']'))
        {
            bracketZone = s[(lb + 1)..^1];
            s = s[..lb];
        }

        if (!s.Contains('T') && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw TerminException.InvalidTime(
                $"'{field}' is a date without a time: '{input}'.",
                "Give a full date-time with an offset, e.g. 2026-10-14T00:00:00+02:00, or use the all-day fields.");

        if (HasExplicitOffset(s))
        {
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                return dto;
            throw TerminException.InvalidTime($"'{field}' is not a valid ISO 8601 date-time: '{input}'.");
        }

        var zoneId = bracketZone ?? tzId;
        if (zoneId is null)
            throw TerminException.InvalidTime(
                $"'{field}' has no offset or timezone: '{input}'.",
                "Add an offset (2026-10-14T09:00:00+02:00) or pass a timezone.");

        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw TerminException.InvalidTime($"'{field}' is not a valid ISO 8601 date-time: '{input}'.");

        return FromLocal(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ResolveZone(zoneId));
    }

    /// <summary>Local wall time in a zone to an instant. Gaps move forward, overlaps take the first occurrence.</summary>
    public static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    public static DateOnly ParseDate(string input, string field = "date")
    {
        if (DateOnly.TryParseExact(input?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw TerminException.InvalidTime($"'{field}' must be a date like 2026-10-14, got '{input}'.");
    }

    public static string Format(DateTimeOffset value) => value.ToString(OffsetFormat, CultureInfo.InvariantCulture);

    public static string Format(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An instant as a CalDateTime with a TZID (or UTC).</summary>
    public static CalDateTime ToCal(DateTimeOffset value, TimeZoneInfo zone)
    {
        if (zone == TimeZoneInfo.Utc)
            return new CalDateTime(value.UtcDateTime, CalDateTime.UtcTzId, true);
        var local = TimeZoneInfo.ConvertTime(value, zone).DateTime;
        return new CalDateTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), IanaId(zone), true);
    }

    public static CalDateTime ToCalUtc(DateTimeOffset value) =>
        new(value.UtcDateTime, CalDateTime.UtcTzId, true);

    public static CalDateTime ToCal(DateOnly date) => new(date);

    /// <summary>A resolved timed value: the instant, the zone it is shown in, and whether it was floating.</summary>
    public readonly record struct Resolved(DateTimeOffset Value, TimeZoneInfo Zone, bool Floating);

    /// <summary>
    /// Resolves a timed CalDateTime. Values with a known TZID stay in that zone,
    /// UTC and unknown zones are shown in the default zone, floating values are
    /// interpreted in the default zone.
    /// </summary>
    public static Resolved Resolve(CalDateTime value, TimeZoneInfo defaultZone)
    {
        if (value.IsFloating)
            return new Resolved(FromLocal(value.Value, defaultZone), defaultZone, true);

        var zone = value.IsUtc ? defaultZone : TryResolveZone(value.TzId) ?? defaultZone;
        var utc = DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc);
        return new Resolved(TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), zone), zone, false);
    }

    public static DateTimeOffset Instant(CalDateTime value, TimeZoneInfo defaultZone)
    {
        if (!value.HasTime)
            return FromLocal(value.Date.ToDateTime(TimeOnly.MinValue), defaultZone);
        return Resolve(value, defaultZone).Value;
    }

    /// <summary>Converts an exclusive DATE end to the inclusive last day.</summary>
    public static DateOnly InclusiveEnd(DateOnly start, DateOnly? exclusiveEnd)
    {
        if (exclusiveEnd is null || exclusiveEnd.Value <= start) return start;
        return exclusiveEnd.Value.AddDays(-1);
    }

    /// <summary>Converts an inclusive last day to the exclusive DATE end.</summary>
    public static DateOnly ExclusiveEnd(DateOnly inclusiveEnd) => inclusiveEnd.AddDays(1);

    private static bool HasExplicitOffset(string s)
    {
        if (s.EndsWith('Z') || s.EndsWith('z')) return true;
        var t = s.IndexOf('T');
        if (t < 0) return false;
        var timePart = s[t..];
        return timePart.Contains('+') || timePart.Contains('-');
    }
}
