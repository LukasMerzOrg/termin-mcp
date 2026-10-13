using System.Text;
using Ical.Net.CalendarComponents;
using Termin.CalDav;
using Termin.Calendar;
using Termin.Server.Auth;
using Termin.Server.Config;
using Termin.Storage;
using IcalCalendar = Ical.Net.Calendar;

namespace Termin.Server.Services;

/// <summary>
/// Orchestrates the tool operations: resolves accounts and calendars, queries
/// CalDAV, expands, maps and pages results, and performs ETag-guarded writes.
/// </summary>
public sealed class CalendarService(
    AccountRegistry accounts,
    AuditStore audit,
    TerminOptions options,
    IHttpContextAccessor http,
    ILogger<CalendarService> log)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    public const int DefaultSearchLimit = 50;
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(732);

    public TimeZoneInfo DefaultZone => TimeRules.ResolveZone(options.DefaultTimeZone);

    // ================================================================ calendars

    public async Task<object> ListCalendarsAsync(string? account, CancellationToken ct)
    {
        var entries = account is null ? accounts.All : [accounts.Resolve(account)];
        if (entries.Count == 0) accounts.Resolve(null); // throws the "no account configured" error

        var results = await Task.WhenAll(entries.Select(async e =>
        {
            try
            {
                var cals = await e.Client.GetCalendarsAsync(ct);
                return (Entry: e, Calendars: cals, Error: (TerminException?)null);
            }
            catch (TerminException ex) when (entries.Count > 1)
            {
                return (Entry: e, Calendars: (IReadOnlyList<CalendarInfo>)[], Error: ex);
            }
        }));

        var multi = entries.Count > 1;
        var list = results.SelectMany(r => r.Calendars.Select(c => new CalendarDto
        {
            Id = c.Id,
            Account = multi ? r.Entry.Account.Label : null,
            Name = c.Name,
            Color = c.Color,
            Tz = c.TimeZone,
            Components = c.Components.ToList(),
            ReadOnly = c.ReadOnly ? true : null,
        })).ToList();

        var errors = results.Where(r => r.Error is not null)
            .Select(r => new { account = r.Entry.Account.Label, error = r.Error!.Code, message = r.Error.Message })
            .ToList();

        return new
        {
            default_account = multi ? entries.First(e => e.IsDefault).Account.Label : null,
            calendars = list,
            errors = errors.Count > 0 ? errors : null,
        };
    }

    // ================================================================ reads

    public async Task<object> GetEventsAsync(string? account, string start, string end, string[]? calendars, int? limit, string? cursor, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        var zone = DefaultZone;
        var (from, to) = ParseRange(start, end);
        var cals = await ResolveCalendarsAsync(entry, calendars, "VEVENT", ct);
        var (events, capped) = await CollectEventsAsync(entry, cals, from, to, ct);
        return Page(events, from, to, zone, limit ?? DefaultLimit, cursor, capped);
    }

    public async Task<object> SearchEventsAsync(string? account, string query, string? start, string? end, string[]? calendars, int? limit, string? cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) throw TerminException.Invalid("'query' is required.");
        var entry = accounts.Resolve(account);
        var zone = DefaultZone;
        var now = DateTimeOffset.UtcNow;
        var from = start is null ? now.AddDays(-90) : TimeRules.ParseInstant(start, null, "start");
        var to = end is null ? now.AddDays(90) : TimeRules.ParseInstant(end, null, "end");
        CheckRange(from, to);

        var cals = await ResolveCalendarsAsync(entry, calendars, "VEVENT", ct);
        var (events, capped) = await CollectEventsAsync(entry, cals, from, to, ct);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = events
            .Where(e => e.SearchText is { } text && terms.All(t => text.Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        return Page(matches, from, to, zone, limit ?? DefaultSearchLimit, cursor, capped);
    }

    public async Task<object> GetEventAsync(string? account, string calendar, string uid, string? recurrenceId, bool includeIcal, bool fullText, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        var cal = await ResolveCalendarAsync(entry, calendar, ct);
        var res = await FindAsync(entry, cal, "VEVENT", uid, ct);
        var parsed = Ics.Parse(res.Data);
        return BuildDetail(parsed, recurrenceId, cal.Id, res.Etag, includeIcal, fullText);
    }

    public async Task<object> FreeBusyAsync(string? account, string start, string end, string[]? calendars, int? minSlotMinutes, string? workingHours, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        var zone = DefaultZone;
        var (from, to) = ParseRange(start, end);
        var hours = string.IsNullOrWhiteSpace(workingHours) ? null : WorkingHours.Parse(workingHours);
        var cals = await ResolveCalendarsAsync(entry, calendars, "VEVENT", ct);
        var (events, _) = await CollectEventsAsync(entry, cals, from, to, ct);

        var busy = FreeBusyCalculator.Merge(events
            .Where(e => !e.Transparent && !string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .Select(e => new BusyBlock(e.SortKey < from ? from : e.SortKey, e.EndKey > to ? to : e.EndKey)));
        var free = FreeBusyCalculator.FreeSlots(busy, from, to, minSlotMinutes ?? 30, hours, zone);

        return new FreeBusyResult
        {
            Tz = TimeRules.IanaId(zone),
            WorkingHours = workingHours,
            Busy = busy.Select(b => Slot(b, zone, false)).ToList(),
            Free = free.Select(b => Slot(b, zone, true)).ToList(),
        };
    }

    public async Task<object> ListTodosAsync(string? account, string[]? calendars, string? status, string? dueBefore, int? limit, string? cursor, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        RequireTodos(entry);
        var zone = DefaultZone;
        var cals = await ResolveCalendarsAsync(entry, calendars, "VTODO", ct);

        DateTimeOffset? dueLimit = null;
        if (!string.IsNullOrWhiteSpace(dueBefore))
            dueLimit = dueBefore.Trim().Length == 10
                ? TimeRules.FromLocal(TimeRules.ParseDate(dueBefore, "due_before").ToDateTime(TimeOnly.MinValue), zone)
                : TimeRules.ParseInstant(dueBefore, null, "due_before");

        var fetched = await Task.WhenAll(cals.Select(async c => (Cal: c, Items: await entry.Client.QueryAsync(c, "VTODO", null, null, false, ct))));
        var todos = new List<TodoDto>();
        foreach (var (c, items) in fetched)
            foreach (var res in items)
            {
                var parsed = Ics.TryParse(res.Data);
                var todo = parsed?.Todos.FirstOrDefault(t => t.RecurrenceIdentifier is null);
                if (todo is null) continue;
                todos.Add(TodoMapper.ToDto(todo, zone, c.Id, res.Etag));
            }

        var filter = status?.Trim().ToUpperInvariant().Replace('_', '-');
        IEnumerable<TodoDto> q = filter switch
        {
            null or "" or "OPEN" => todos.Where(TodoMapper.IsOpen),
            "ALL" => todos,
            "DONE" => todos.Where(t => t.Status == "COMPLETED"),
            _ => todos.Where(t => t.Status == filter),
        };
        if (dueLimit is not null) q = q.Where(t => t.DueKey is { } d && d < dueLimit);
        var sorted = q.OrderBy(t => t.DueKey ?? DateTimeOffset.MaxValue).ThenBy(t => t.Summary, StringComparer.CurrentCultureIgnoreCase).ToList();

        var (offset, size) = Paging(cursor, limit ?? DefaultLimit);
        var page = sorted.Skip(offset).Take(size).ToList();
        return new
        {
            tz = TimeRules.IanaId(zone),
            todos = page,
            next_cursor = offset + size < sorted.Count ? EncodeCursor(offset + size) : null,
        };
    }

    // ================================================================ writes

    public async Task<object> CreateEventAsync(string? account, string calendar, NewEvent input, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        var cal = await ResolveWritableAsync(entry, calendar, "VEVENT", ct);
        var zone = DefaultZone;
        var ics = EventWriter.BuildEvent(input, zone, out var uid);
        var href = CalDavClient.NewResourceUrl(cal, uid);
        return await Audited("create_event", entry, cal, uid, null, async () =>
        {
            var etag = await entry.Client.PutAsync(href, Ics.Serialize(ics), null, ct);
            return new { created = true, @event = Summarize(ics, null, cal.Id, etag) };
        });
    }

    public async Task<object> UpdateEventAsync(string? account, string calendar, string uid, string etag, EventPatch patch, string? scope, string? recurrenceId, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        var cal = await ResolveWritableAsync(entry, calendar, "VEVENT", ct);
        var editScope = EventWriter.ParseScope(scope);
        var zone = DefaultZone;
        RequireEtag(etag);

        return await Audited("update_event", entry, cal, uid, editScope.ToString(), async () =>
        {
            var res = await FindAsync(entry, cal, "VEVENT", uid, ct);
            var parsed = Ics.Parse(res.Data);
            EnsureFresh(res, etag, () => BuildDetail(parsed, null, cal.Id, res.Etag, false, false));

            if (editScope == EditScope.ThisAndFuture)
            {
                if (recurrenceId is null) throw TerminException.Invalid("'recurrence_id' is required for scope 'this_and_future'.");
                var split = EventWriter.Split(parsed, recurrenceId, patch, zone);
                if (split.NewSeries is not null)
                {
                    var newHref = CalDavClient.NewResourceUrl(cal, split.NewUid!);
                    var newEtag = await entry.Client.PutAsync(newHref, Ics.Serialize(split.NewSeries), null, ct);
                    string? origEtag;
                    try
                    {
                        origEtag = await PutGuarded(entry, cal, res, split.Original, etag, ct);
                    }
                    catch
                    {
                        // Keep the calendar consistent: undo the new series if the original could not be truncated.
                        if (newEtag is not null)
                            try { await entry.Client.DeleteAsync(newHref, newEtag, ct); } catch (Exception ex) { log.LogWarning(ex, "Rollback of split series failed."); }
                        throw;
                    }
                    return new
                    {
                        updated = true,
                        scope = "this_and_future",
                        @event = Summarize(split.Original, null, cal.Id, origEtag),
                        new_series = Summarize(split.NewSeries, null, cal.Id, newEtag),
                    };
                }
                editScope = EditScope.All; // split at the first occurrence is the whole series
            }

            EventWriter.Update(parsed, patch, editScope, recurrenceId, zone);
            var newTag = await PutGuarded(entry, cal, res, parsed, etag, ct);
            return new
            {
                updated = true,
                scope = editScope switch { EditScope.This => "this", _ => "all" },
                @event = Summarize(parsed, editScope == EditScope.This ? recurrenceId : null, cal.Id, newTag),
            };
        });
    }

    public async Task<object> DeleteEventAsync(string? account, string calendar, string uid, string etag, string? scope, string? recurrenceId, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        var cal = await ResolveWritableAsync(entry, calendar, "VEVENT", ct);
        var editScope = EventWriter.ParseScope(scope);
        var zone = DefaultZone;
        RequireEtag(etag);

        return await Audited("delete_event", entry, cal, uid, editScope.ToString(), async () =>
        {
            var res = await FindAsync(entry, cal, "VEVENT", uid, ct);
            var parsed = Ics.Parse(res.Data);
            EnsureFresh(res, etag, () => BuildDetail(parsed, null, cal.Id, res.Etag, false, false));

            if (editScope != EditScope.All)
            {
                if (recurrenceId is null) throw TerminException.Invalid($"'recurrence_id' is required for scope '{scope}'.");
                var keep = editScope == EditScope.This
                    ? EventWriter.DeleteOccurrence(parsed, recurrenceId, zone)
                    : EventWriter.DeleteFrom(parsed, recurrenceId, zone);
                if (keep)
                {
                    var newTag = await PutGuarded(entry, cal, res, parsed, etag, ct);
                    return new { deleted = true, scope = scope, uid, recurrence_id = recurrenceId, etag = newTag };
                }
            }

            await DeleteGuarded(entry, cal, res, etag, ct);
            return (object)new { deleted = true, scope = "all", uid };
        });
    }

    public async Task<object> UpsertTodoAsync(string? account, string calendar, string? uid, string? etag, TodoInput input, CancellationToken ct)
    {
        var entry = accounts.Resolve(account);
        RequireTodos(entry);
        var cal = await ResolveWritableAsync(entry, calendar, "VTODO", ct);
        var zone = DefaultZone;

        if (string.IsNullOrWhiteSpace(uid))
        {
            var ics = TodoMapper.Create(input, zone, out var newUid);
            return await Audited("upsert_todo", entry, cal, newUid, "create", async () =>
            {
                var tag = await entry.Client.PutAsync(CalDavClient.NewResourceUrl(cal, newUid), Ics.Serialize(ics), null, ct);
                return new { created = true, todo = TodoMapper.ToDto(ics.Todos.First(), zone, cal.Id, tag) };
            });
        }

        RequireEtag(etag);
        return await Audited("upsert_todo", entry, cal, uid, "update", async () =>
        {
            var res = await FindAsync(entry, cal, "VTODO", uid, ct);
            var parsed = Ics.Parse(res.Data);
            EnsureFresh(res, etag!, () => TodoMapper.ToDto(parsed.Todos.First(), zone, cal.Id, res.Etag));
            TodoMapper.Update(parsed, input, zone);
            var tag = await PutGuarded(entry, cal, res, parsed, etag!, ct);
            return new { updated = true, todo = TodoMapper.ToDto(parsed.Todos.First(t => t.RecurrenceIdentifier is null), zone, cal.Id, tag) };
        });
    }

    // ================================================================ helpers

    public async Task<IReadOnlyList<CalendarInfo>> ResolveCalendarsAsync(AccountEntry entry, string[]? ids, string component, CancellationToken ct)
    {
        if (ids is { Length: > 0 })
        {
            var list = new List<CalendarInfo>();
            foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
                list.Add(await ResolveCalendarAsync(entry, id, ct));
            return list;
        }
        var all = await entry.Client.GetCalendarsAsync(ct);
        return all.Where(c => c.Supports(component)).ToList();
    }

    public async Task<CalendarInfo> ResolveCalendarAsync(AccountEntry entry, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw TerminException.Invalid("'calendar' is required.");
        var all = await entry.Client.GetCalendarsAsync(ct);
        var key = id.Trim();
        return all.FirstOrDefault(c => c.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? throw TerminException.NotFound($"Unknown calendar '{id}' in account '{entry.Account.Label}'.",
                "Known calendars: " + string.Join(", ", all.Select(c => c.Name is null ? c.Id : $"{c.Id} ({c.Name})")));
    }

    private async Task<CalendarInfo> ResolveWritableAsync(AccountEntry entry, string id, string component, CancellationToken ct)
    {
        var cal = await ResolveCalendarAsync(entry, id, ct);
        if (cal.ReadOnly) throw new TerminException(ErrorCodes.Forbidden, $"Calendar '{cal.Id}' is read-only.");
        if (!cal.Supports(component))
            throw TerminException.Invalid($"Calendar '{cal.Id}' does not store {(component == "VTODO" ? "todos" : "events")}.",
                "Pick a calendar whose components include " + component + " (see list_calendars).");
        return cal;
    }

    private async Task<(List<EventDto> Events, bool Capped)> CollectEventsAsync(
        AccountEntry entry, IReadOnlyList<CalendarInfo> cals, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var zone = DefaultZone;
        var fetched = await Task.WhenAll(cals.Select(async c => (Cal: c, Items: await entry.Client.QueryAsync(c, "VEVENT", from, to, expand: true, ct))));

        var events = new List<EventDto>();
        var budget = EventReader.MaxOccurrencesPerQuery;
        var capped = false;
        foreach (var (c, items) in fetched)
        {
            foreach (var res in items)
            {
                var parsed = Ics.TryParse(res.Data);
                if (parsed is null)
                {
                    log.LogDebug("Skipping unparsable resource in calendar {Calendar}.", c.Id);
                    continue;
                }
                foreach (var occ in EventReader.Expand(parsed, from, to, zone, budget + 1))
                {
                    if (budget-- <= 0) { capped = true; break; }
                    events.Add(EventReader.ToDto(occ, zone, c.Id, res.Etag));
                }
                if (capped) break;
            }
            if (capped) break;
        }

        events.Sort((a, b) =>
        {
            var cmp = a.SortKey.CompareTo(b.SortKey);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Uid, b.Uid);
        });
        return (events, capped);
    }

    private object Page(List<EventDto> events, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, int limit, string? cursor, bool capped)
    {
        var tz = TimeRules.IanaId(zone);
        var (offset, size) = Paging(cursor, limit);
        var page = events.Skip(offset).Take(size).ToList();
        foreach (var e in page)
            if (e.Tz == tz) e.Tz = null;

        return new
        {
            range = new { start = TimeRules.Format(TimeZoneInfo.ConvertTime(from, zone)), end = TimeRules.Format(TimeZoneInfo.ConvertTime(to, zone)) },
            tz,
            events = page,
            total = events.Count,
            next_cursor = offset + size < events.Count ? EncodeCursor(offset + size) : null,
            hint = capped ? $"More than {EventReader.MaxOccurrencesPerQuery} occurrences in range; results are cut there. Narrow the range or the calendars." : null,
        };
    }

    private EventDetailDto BuildDetail(IcalCalendar parsed, string? recurrenceId, string calendarId, string? etag, bool includeIcal, bool fullText)
    {
        var zone = DefaultZone;
        var master = Ics.Master(parsed) ?? throw TerminException.NotFound("The resource contains no event.");
        if (recurrenceId is null)
            return EventReader.ToDetail(master, parsed, null, zone, calendarId, etag, fullText, includeIcal);

        var original = EventWriter.FindOccurrence(parsed, master, recurrenceId, zone);
        var over = Ics.Overrides(parsed).FirstOrDefault(o =>
            o.RecurrenceIdentifier!.StartTime.HasTime == original.HasTime &&
            TimeRules.Instant(o.RecurrenceIdentifier.StartTime, zone) == TimeRules.Instant(original, zone));
        if (over is not null)
            return EventReader.ToDetail(over, parsed, new EventOccurrence(over, over.DtStart!, Ics.EffectiveEnd(over), original, true),
                zone, calendarId, etag, fullText, includeIcal);

        var duration = Ics.EffectiveEnd(master).Subtract(master.DtStart!);
        var occ = new EventOccurrence(master, original, original.Add(duration), original, true);
        return EventReader.ToDetail(master, parsed, occ, zone, calendarId, etag, fullText, includeIcal);
    }

    /// <summary>A compact view of a written event: the master, or one occurrence.</summary>
    private EventDto Summarize(IcalCalendar cal, string? recurrenceId, string calendarId, string? etag)
    {
        var zone = DefaultZone;
        if (recurrenceId is not null)
        {
            var detail = BuildDetail(cal, recurrenceId, calendarId, etag, false, false);
            return new EventDto
            {
                Uid = detail.Uid, RecurrenceId = detail.RecurrenceId, Calendar = detail.Calendar, Summary = detail.Summary,
                Start = detail.Start, End = detail.End, Date = detail.Date, StartDate = detail.StartDate, EndDate = detail.EndDate,
                AllDay = detail.AllDay, Tz = detail.Tz, Floating = detail.Floating, Location = detail.Location,
                Recurring = detail.Recurring, Status = detail.Status, Etag = etag,
            };
        }
        var master = Ics.Master(cal)!;
        var recurring = Ics.IsRecurring(master);
        return EventReader.ToDto(new EventOccurrence(master, master.DtStart!, Ics.EffectiveEnd(master), null, recurring), zone, calendarId, etag);
    }

    private async Task<CalendarResource> FindAsync(AccountEntry entry, CalendarInfo cal, string component, string uid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid)) throw TerminException.Invalid("'uid' is required.");
        return await entry.Client.FindByUidAsync(cal, component, uid.Trim(), ct)
            ?? throw TerminException.NotFound($"No {(component == "VTODO" ? "todo" : "event")} with uid '{uid}' in calendar '{cal.Id}'.");
    }

    private async Task<string?> PutGuarded(AccountEntry entry, CalendarInfo cal, CalendarResource res, IcalCalendar parsed, string etag, CancellationToken ct)
    {
        try
        {
            return await entry.Client.PutAsync(res.Href, Ics.Serialize(parsed), res.Etag ?? etag, ct);
        }
        catch (TerminException ex) when (ex.Code == ErrorCodes.Conflict)
        {
            throw await ConflictWithCurrent(entry, cal, res, ct);
        }
    }

    private async Task DeleteGuarded(AccountEntry entry, CalendarInfo cal, CalendarResource res, string etag, CancellationToken ct)
    {
        try
        {
            await entry.Client.DeleteAsync(res.Href, res.Etag ?? etag, ct);
        }
        catch (TerminException ex) when (ex.Code == ErrorCodes.Conflict)
        {
            throw await ConflictWithCurrent(entry, cal, res, ct);
        }
    }

    private async Task<TerminException> ConflictWithCurrent(AccountEntry entry, CalendarInfo cal, CalendarResource res, CancellationToken ct)
    {
        object? current = null;
        try
        {
            var uid = Ics.TryParse(res.Data)?.Children.OfType<UniqueComponent>().FirstOrDefault()?.Uid;
            if (uid is not null)
            {
                var fresh = await entry.Client.FindByUidAsync(cal, "VEVENT", uid, ct) ?? await entry.Client.FindByUidAsync(cal, "VTODO", uid, ct);
                if (fresh is not null && Ics.TryParse(fresh.Data) is { } parsed)
                    current = parsed.Events.Count > 0
                        ? BuildDetail(parsed, null, cal.Id, fresh.Etag, false, false)
                        : TodoMapper.ToDto(parsed.Todos.First(), DefaultZone, cal.Id, fresh.Etag);
            }
        }
        catch (TerminException)
        {
        }
        return new TerminException(ErrorCodes.Conflict, "The item changed on the server since it was read; nothing was written.",
            "Review the current version and retry with its etag.", current);
    }

    private static void EnsureFresh(CalendarResource res, string etag, Func<object> current)
    {
        if (res.Etag is null || EtagEquals(res.Etag, etag)) return;
        throw new TerminException(ErrorCodes.Conflict, "The etag is stale: the item changed since it was read; nothing was written.",
            "Review the current version and retry with its etag.", current());
    }

    public static bool EtagEquals(string a, string b)
    {
        static string Norm(string s)
        {
            s = s.Trim();
            if (s.StartsWith("W/", StringComparison.Ordinal)) s = s[2..];
            return s.Trim('"');
        }
        return Norm(a) == Norm(b);
    }

    private static void RequireEtag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            throw TerminException.Invalid("'etag' is required.", "Read the item first (get_events or get_event) and pass its etag.");
    }

    private static void RequireTodos(AccountEntry entry)
    {
        if (!entry.Account.Profile.SupportsTodos)
            throw TerminException.Invalid($"Account '{entry.Account.Label}' does not support todos.");
    }

    private async Task<object> Audited(string tool, AccountEntry entry, CalendarInfo cal, string? uid, string? scope, Func<Task<object>> action)
    {
        var clientId = http.HttpContext?.GetCaller()?.ClientId;
        try
        {
            var result = await action();
            TryAudit(new AuditEntry(tool, entry.Account.Label, cal.Id, uid, scope?.ToLowerInvariant(), "ok", clientId));
            return result;
        }
        catch (TerminException ex)
        {
            TryAudit(new AuditEntry(tool, entry.Account.Label, cal.Id, uid, scope?.ToLowerInvariant(), ex.Code, clientId));
            throw;
        }
    }

    private void TryAudit(AuditEntry entry)
    {
        try { audit.Write(entry); }
        catch (Exception ex) { log.LogError(ex, "Audit write failed."); }
    }

    private static (DateTimeOffset From, DateTimeOffset To) ParseRange(string start, string end)
    {
        var from = TimeRules.ParseInstant(start, null, "start");
        var to = TimeRules.ParseInstant(end, null, "end");
        CheckRange(from, to);
        return (from, to);
    }

    private static void CheckRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) throw TerminException.InvalidTime("'end' must be after 'start'.");
        if (to - from > MaxRange) throw TerminException.InvalidTime("The range is longer than two years.", "Query a shorter range.");
    }

    private static FreeBusyResult.Slot Slot(BusyBlock b, TimeZoneInfo zone, bool withMinutes) => new()
    {
        Start = TimeRules.Format(TimeZoneInfo.ConvertTime(b.Start, zone)),
        End = TimeRules.Format(TimeZoneInfo.ConvertTime(b.End, zone)),
        Minutes = withMinutes ? (int)(b.End - b.Start).TotalMinutes : null,
    };

    private static (int Offset, int Size) Paging(string? cursor, int limit)
    {
        var size = Math.Clamp(limit, 1, MaxLimit);
        if (string.IsNullOrWhiteSpace(cursor)) return (0, size);
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight(cursor.Length + (4 - cursor.Length % 4) % 4, '=')));
            if (text.StartsWith("o:") && int.TryParse(text[2..], out var o) && o >= 0) return (o, size);
        }
        catch (FormatException)
        {
        }
        throw TerminException.Invalid("Invalid cursor.", "Pass next_cursor exactly as returned, with the same other arguments.");
    }

    private static string EncodeCursor(int offset) => Tokens.Base64Url(Encoding.UTF8.GetBytes("o:" + offset));
}
