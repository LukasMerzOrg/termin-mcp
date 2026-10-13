using System.Text.Json;
using Termin.Calendar;

namespace Termin.Integration;

/// <summary>
/// Every tool against a real CalDAV server, through both expansion paths
/// (server-side &lt;expand&gt; and forced client-side expansion).
/// </summary>
public abstract class CalDavSuite<TServer>(TServer server) : IClassFixture<TServer> where TServer : CalDavServer
{
    private const string Account = "it";
    private const string Tz = "Europe/Zurich";

    private bool Skip => server.SkipReason is not null;

    private async Task<(Harness H, string Cal)> SetupAsync(bool clientExpand, params string[] components)
    {
        var id = "it-" + Guid.NewGuid().ToString("N")[..10];
        await server.CreateCalendarAsync(id, "Termin " + id, components.Length == 0 ? ["VEVENT"] : components);
        var h = new Harness(server, clientExpand);
        return (h, id);
    }

    private static string[] Starts(JsonElement page) =>
        page.GetProperty("events").EnumerateArray().Select(e => e.TryGetProperty("start", out var s) ? s.GetString()! : e.GetProperty("date").GetString()!).ToArray();

    private static JsonElement Event(JsonElement page, string start) =>
        page.GetProperty("events").EnumerateArray().First(e => e.TryGetProperty("start", out var s) && s.GetString() == start);

    [Fact]
    public async Task List_calendars_reports_components()
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(false, "VEVENT");
        var (_, todoCal) = await SetupAsync(false, "VTODO");
        using var _h = h;

        var result = await Harness.Json(h.Service.ListCalendarsAsync(Account, default));
        var cals = result.GetProperty("calendars").EnumerateArray().ToList();
        var ev = cals.Single(c => c.GetProperty("id").GetString() == cal);
        Assert.Equal("Termin " + cal, ev.GetProperty("name").GetString());
        Assert.Contains("VEVENT", ev.GetProperty("components").EnumerateArray().Select(x => x.GetString()));
        var td = cals.Single(c => c.GetProperty("id").GetString() == todoCal);
        Assert.Contains("VTODO", td.GetProperty("components").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recurring_series_lifecycle_across_dst(bool clientExpand)
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(clientExpand);
        using var _h = h;
        const string from = "2026-10-01T00:00:00+02:00", to = "2027-01-01T00:00:00+01:00";

        var created = await Harness.Json(h.Service.CreateEventAsync(Account, cal, new NewEvent
        {
            Summary = "Standup", Start = "2026-10-19T09:00:00", End = "2026-10-19T09:30:00", Timezone = Tz,
            Rrule = "FREQ=WEEKLY;COUNT=6", Location = "Boniswil", Reminders = [10],
        }, default));
        var uid = created.GetProperty("event").GetProperty("uid").GetString()!;

        var page = await Harness.Json(h.Service.GetEventsAsync(Account, from, to, [cal], null, null, default));
        Assert.Equal(
            ["2026-10-19T09:00:00+02:00", "2026-10-26T09:00:00+01:00", "2026-11-02T09:00:00+01:00", "2026-11-09T09:00:00+01:00", "2026-11-16T09:00:00+01:00", "2026-11-23T09:00:00+01:00"],
            Starts(page));
        Assert.All(page.GetProperty("events").EnumerateArray(), e => Assert.True(e.GetProperty("recurring").GetBoolean()));

        // Delete one occurrence (EXDATE).
        var etag = Event(page, "2026-11-02T09:00:00+01:00").GetProperty("etag").GetString()!;
        var del = await Harness.Json(h.Service.DeleteEventAsync(Account, cal, uid, etag, "this", "2026-11-02T09:00:00+01:00", default));
        etag = del.GetProperty("etag").GetString() ?? await FreshEtag(h, cal, uid);

        // Move one occurrence to another day (override in the same resource).
        await h.Service.UpdateEventAsync(Account, cal, uid, etag,
            new EventPatch { Summary = "Standup (moved)", Start = "2026-10-27T14:00:00+01:00", End = "2026-10-27T15:00:00+01:00" },
            "this", "2026-10-26T09:00:00+01:00", default);

        page = await Harness.Json(h.Service.GetEventsAsync(Account, from, to, [cal], null, null, default));
        Assert.Equal(
            ["2026-10-19T09:00:00+02:00", "2026-10-27T14:00:00+01:00", "2026-11-09T09:00:00+01:00", "2026-11-16T09:00:00+01:00", "2026-11-23T09:00:00+01:00"],
            Starts(page));
        var moved = Event(page, "2026-10-27T14:00:00+01:00");
        Assert.Equal("Standup (moved)", moved.GetProperty("summary").GetString());
        Assert.Equal("2026-10-26T09:00:00+01:00", moved.GetProperty("recurrence_id").GetString());

        var detail = await Harness.Json(h.Service.GetEventAsync(Account, cal, uid, "2026-10-26T09:00:00+01:00", false, false, default));
        Assert.Equal("Standup (moved)", detail.GetProperty("summary").GetString());
        var series = await Harness.Json(h.Service.GetEventAsync(Account, cal, uid, null, true, false, default));
        Assert.StartsWith("FREQ=WEEKLY", series.GetProperty("rrule").GetString());
        Assert.Equal(10, series.GetProperty("reminders")[0].GetProperty("minutes_before").GetInt32());
        Assert.Contains("BEGIN:VTIMEZONE", series.GetProperty("ical").GetString());

        // Split: this and future get a new location.
        etag = series.GetProperty("etag").GetString()!;
        var split = await Harness.Json(h.Service.UpdateEventAsync(Account, cal, uid, etag,
            new EventPatch { Location = "Zürich" }, "this_and_future", "2026-11-16T09:00:00+01:00", default));
        var newUid = split.GetProperty("new_series").GetProperty("uid").GetString()!;
        Assert.NotEqual(uid, newUid);

        page = await Harness.Json(h.Service.GetEventsAsync(Account, from, to, [cal], null, null, default));
        Assert.Equal(
            ["2026-10-19T09:00:00+02:00", "2026-10-27T14:00:00+01:00", "2026-11-09T09:00:00+01:00", "2026-11-16T09:00:00+01:00", "2026-11-23T09:00:00+01:00"],
            Starts(page));
        Assert.Equal(newUid, Event(page, "2026-11-23T09:00:00+01:00").GetProperty("uid").GetString());
        Assert.Equal("Zürich", Event(page, "2026-11-16T09:00:00+01:00").GetProperty("location").GetString());
        Assert.Equal("Boniswil", Event(page, "2026-11-09T09:00:00+01:00").GetProperty("location").GetString());

        // Delete the new series entirely.
        var newEtag = Event(page, "2026-11-16T09:00:00+01:00").GetProperty("etag").GetString()!;
        await h.Service.DeleteEventAsync(Account, cal, newUid, newEtag, "all", null, default);
        page = await Harness.Json(h.Service.GetEventsAsync(Account, from, to, [cal], null, null, default));
        Assert.Equal(3, page.GetProperty("events").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_etag_returns_conflict_and_writes_nothing(bool clientExpand)
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(clientExpand);
        using var _h = h;
        var created = await Harness.Json(h.Service.CreateEventAsync(Account, cal, new NewEvent
        {
            Summary = "Dentist", Start = "2026-10-20T15:00:00+02:00", End = "2026-10-20T16:00:00+02:00",
        }, default));
        var uid = created.GetProperty("event").GetProperty("uid").GetString()!;
        var etag1 = created.GetProperty("event").GetProperty("etag").GetString() ?? await FreshEtag(h, cal, uid);

        await h.Service.UpdateEventAsync(Account, cal, uid, etag1, new EventPatch { Summary = "Dentist (Dr. Muster)" }, "all", null, default);

        var ex = await Assert.ThrowsAsync<TerminException>(() =>
            h.Service.UpdateEventAsync(Account, cal, uid, etag1, new EventPatch { Summary = "Overwritten?" }, "all", null, default));
        Assert.Equal(ErrorCodes.Conflict, ex.Code);
        var current = JsonDocument.Parse(TerminJson.Serialize(ex.Current)).RootElement;
        Assert.Equal("Dentist (Dr. Muster)", current.GetProperty("summary").GetString());

        var stillThere = await Harness.Json(h.Service.GetEventAsync(Account, cal, uid, null, false, false, default));
        Assert.Equal("Dentist (Dr. Muster)", stillThere.GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_day_search_and_free_busy(bool clientExpand)
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(clientExpand);
        using var _h = h;
        await h.Service.CreateEventAsync(Account, cal, new NewEvent { Summary = "Herbstferien Kita", AllDayDate = "2026-10-14", AllDayEndDate = "2026-10-16" }, default);
        await h.Service.CreateEventAsync(Account, cal, new NewEvent { Summary = "Elterngespräch", Start = "2026-10-15T10:00:00+02:00", End = "2026-10-15T11:00:00+02:00", Location = "Kita Boniswil" }, default);
        await h.Service.CreateEventAsync(Account, cal, new NewEvent { Summary = "Focus", Start = "2026-10-15T13:00:00+02:00", End = "2026-10-15T14:00:00+02:00", Transparency = "TRANSPARENT" }, default);

        var day = await Harness.Json(h.Service.GetEventsAsync(Account, "2026-10-15T00:00:00+02:00", "2026-10-16T00:00:00+02:00", [cal], null, null, default));
        var events = day.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        var allDay = events.Single(e => e.TryGetProperty("all_day", out _));
        Assert.Equal("2026-10-14", allDay.GetProperty("start_date").GetString());
        Assert.Equal("2026-10-16", allDay.GetProperty("end_date").GetString());

        // The day after the inclusive end is free of the all-day event.
        var after = await Harness.Json(h.Service.GetEventsAsync(Account, "2026-10-17T00:00:00+02:00", "2026-10-18T00:00:00+02:00", [cal], null, null, default));
        Assert.Equal(0, after.GetProperty("events").GetArrayLength());

        var search = await Harness.Json(h.Service.SearchEventsAsync(Account, "kita", "2026-10-01T00:00:00+02:00", "2026-11-01T00:00:00+01:00", [cal], null, null, default));
        Assert.Equal(2, search.GetProperty("events").GetArrayLength());

        var fb = await Harness.Json(h.Service.FreeBusyAsync(Account, "2026-10-15T00:00:00+02:00", "2026-10-16T00:00:00+02:00", [cal], 30, "08:00-17:00", default));
        Assert.Equal(["2026-10-15T10:00:00+02:00"], fb.GetProperty("busy").EnumerateArray().Select(b => b.GetProperty("start").GetString()!).ToArray());
        Assert.Equal(
            [("2026-10-15T08:00:00+02:00", "2026-10-15T10:00:00+02:00"), ("2026-10-15T11:00:00+02:00", "2026-10-15T17:00:00+02:00")],
            fb.GetProperty("free").EnumerateArray().Select(b => (b.GetProperty("start").GetString()!, b.GetProperty("end").GetString()!)).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paging_with_cursor(bool clientExpand)
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(clientExpand);
        using var _h = h;
        await h.Service.CreateEventAsync(Account, cal, new NewEvent { Summary = "Daily", Start = "2026-10-01T07:00:00+02:00", End = "2026-10-01T07:15:00+02:00", Rrule = "FREQ=DAILY" }, default);

        var first = await Harness.Json(h.Service.GetEventsAsync(Account, "2026-10-01T00:00:00+02:00", "2026-10-11T00:00:00+02:00", [cal], 4, null, default));
        Assert.Equal(4, first.GetProperty("events").GetArrayLength());
        Assert.Equal(10, first.GetProperty("total").GetInt32());
        var cursor = first.GetProperty("next_cursor").GetString();
        var second = await Harness.Json(h.Service.GetEventsAsync(Account, "2026-10-01T00:00:00+02:00", "2026-10-11T00:00:00+02:00", [cal], 4, cursor, default));
        Assert.Equal("2026-10-05T07:00:00+02:00", Starts(second)[0]);
    }

    [Fact]
    public async Task Todos_create_complete_and_filter()
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(false, "VTODO");
        using var _h = h;
        var created = await Harness.Json(h.Service.UpsertTodoAsync(Account, cal, null, null, new TodoInput { Summary = "Steuererklärung", DueDate = "2026-10-31" }, default));
        var uid = created.GetProperty("todo").GetProperty("uid").GetString()!;
        await h.Service.UpsertTodoAsync(Account, cal, null, null, new TodoInput { Summary = "Velo service", Due = "2026-11-20T18:00:00+01:00" }, default);

        var open = await Harness.Json(h.Service.ListTodosAsync(Account, [cal], null, null, null, null, default));
        Assert.Equal(["Steuererklärung", "Velo service"], open.GetProperty("todos").EnumerateArray().Select(t => t.GetProperty("summary").GetString()!).ToArray());

        var dueSoon = await Harness.Json(h.Service.ListTodosAsync(Account, [cal], null, "2026-11-01", null, null, default));
        Assert.Equal(1, dueSoon.GetProperty("todos").GetArrayLength());

        var etag = open.GetProperty("todos")[0].GetProperty("etag").GetString()!;
        await h.Service.UpsertTodoAsync(Account, cal, uid, etag, new TodoInput { Status = "COMPLETED" }, default);

        open = await Harness.Json(h.Service.ListTodosAsync(Account, [cal], null, null, null, null, default));
        Assert.Equal(1, open.GetProperty("todos").GetArrayLength());
        var done = await Harness.Json(h.Service.ListTodosAsync(Account, [cal], "completed", null, null, null, default));
        Assert.Equal("Steuererklärung", done.GetProperty("todos")[0].GetProperty("summary").GetString());
        Assert.True(done.GetProperty("todos")[0].TryGetProperty("completed", out _));
    }

    [Fact]
    public async Task Writes_are_rejected_on_naive_times_and_unknown_calendars()
    {
        if (Skip) return;
        var (h, cal) = await SetupAsync(false);
        using var _h = h;
        var naive = await Assert.ThrowsAsync<TerminException>(() =>
            h.Service.CreateEventAsync(Account, cal, new NewEvent { Summary = "x", Start = "2026-10-20T15:00:00" }, default));
        Assert.Equal(ErrorCodes.InvalidTime, naive.Code);
        var unknown = await Assert.ThrowsAsync<TerminException>(() =>
            h.Service.GetEventsAsync(Account, "2026-10-01T00:00:00Z", "2026-10-02T00:00:00Z", ["does-not-exist"], null, null, default));
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);
    }

    private static async Task<string> FreshEtag(Harness h, string cal, string uid)
    {
        var e = await Harness.Json(h.Service.GetEventAsync(Account, cal, uid, null, false, false, default));
        return e.GetProperty("etag").GetString()!;
    }
}

public sealed class RadicaleTests(RadicaleServer server) : CalDavSuite<RadicaleServer>(server);

public sealed class NextcloudTests(NextcloudServer server) : CalDavSuite<NextcloudServer>(server);

public sealed class BaikalTests(BaikalServer server) : CalDavSuite<BaikalServer>(server);
