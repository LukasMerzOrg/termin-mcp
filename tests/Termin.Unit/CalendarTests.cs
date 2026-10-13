using Termin.Calendar;

namespace Termin.Unit;

public class CalendarTests
{
    private static readonly TimeZoneInfo Zurich = TimeRules.ResolveZone("Europe/Zurich");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static List<EventDto> Read(string ics, string from, string to)
    {
        var cal = Ics.Parse(ics);
        return EventReader.Expand(cal, T(from), T(to), Zurich)
            .Select(o => EventReader.ToDto(o, Zurich, "cal", "\"e1\""))
            .OrderBy(e => e.SortKey).ToList();
    }

    [Fact]
    public void Weekly_series_keeps_wall_time_across_dst_switch()
    {
        var events = Read(Fixture("weekly-dst-override.ics"), "2026-10-01T00:00:00Z", "2026-12-31T00:00:00Z");

        // 19 Oct (CEST), 26 Oct moved to 27 Oct (CET), 2 Nov excluded, 9, 16, 23 Nov.
        Assert.Equal(
            ["2026-10-19T09:00:00+02:00", "2026-10-27T14:00:00+01:00", "2026-11-09T09:00:00+01:00", "2026-11-16T09:00:00+01:00", "2026-11-23T09:00:00+01:00"],
            events.Select(e => e.Start!).ToArray());
        Assert.All(events, e => Assert.True(e.Recurring));
        Assert.All(events, e => Assert.Equal("Europe/Zurich", e.Tz));
    }

    [Fact]
    public void Moved_override_keeps_original_recurrence_id()
    {
        var events = Read(Fixture("weekly-dst-override.ics"), "2026-10-26T00:00:00+01:00", "2026-10-28T00:00:00+01:00");
        var moved = Assert.Single(events);
        Assert.Equal("Standup (moved)", moved.Summary);
        Assert.Equal("2026-10-26T09:00:00+01:00", moved.RecurrenceId);
        Assert.Equal("2026-10-27T15:00:00+01:00", moved.End);
    }

    [Fact]
    public void Exdate_removes_occurrence()
    {
        var events = Read(Fixture("weekly-dst-override.ics"), "2026-11-02T00:00:00+01:00", "2026-11-03T00:00:00+01:00");
        Assert.Empty(events);
    }

    [Fact]
    public void Count_and_until_bound_the_series()
    {
        // COUNT=6 from 19 Oct: last generated instance is 23 Nov.
        Assert.Empty(Read(Fixture("weekly-dst-override.ics"), "2026-11-24T00:00:00+01:00", "2027-06-01T00:00:00+02:00"));

        // UNTIL=20261023T170000Z is 18:00 CEST on 23 Oct: 20, 21, 22, 23 Oct.
        var daily = Read(Fixture("until-utc.ics"), "2026-10-01T00:00:00Z", "2026-11-30T00:00:00Z");
        Assert.Equal(4, daily.Count);
        Assert.Equal("2026-10-23T18:00:00+02:00", daily[^1].Start);
    }

    [Fact]
    public void All_day_uses_inclusive_dates_in_the_api()
    {
        var events = Read(Fixture("allday-multiday.ics"), "2026-10-14T00:00:00+02:00", "2026-10-15T00:00:00+02:00");
        var e = Assert.Single(events);
        Assert.True(e.AllDay);
        Assert.Equal("2026-10-14", e.Date);
        Assert.Null(e.Start);
        Assert.True(e.Transparent);

        // The day after is not part of the event (DTEND is exclusive).
        Assert.Empty(Read(Fixture("allday-multiday.ics"), "2026-10-15T00:00:00+02:00", "2026-10-16T00:00:00+02:00"));
    }

    [Fact]
    public void Multi_day_all_day_create_converts_to_exclusive_end()
    {
        var cal = EventWriter.BuildEvent(new NewEvent { Summary = "Holiday", AllDayDate = "2026-10-14", AllDayEndDate = "2026-10-16" }, Zurich, out _);
        var text = Ics.Serialize(cal);
        Assert.Contains("DTSTART;VALUE=DATE:20261014", text);
        Assert.Contains("DTEND;VALUE=DATE:20261017", text);

        var e = Assert.Single(Read(text, "2026-10-01T00:00:00Z", "2026-10-31T00:00:00Z"));
        Assert.Equal("2026-10-14", e.StartDate);
        Assert.Equal("2026-10-16", e.EndDate);
    }

    [Fact]
    public void Floating_times_are_flagged_and_use_the_default_zone()
    {
        var e = Assert.Single(Read(Fixture("floating.ics"), "2026-10-14T00:00:00+02:00", "2026-10-15T00:00:00+02:00"));
        Assert.True(e.Floating);
        Assert.Equal("2026-10-14T12:00:00+02:00", e.Start);
    }

    [Theory]
    [InlineData("2026-10-14T09:00:00")]
    [InlineData("2026-10-14")]
    [InlineData("tomorrow")]
    public void Naive_times_are_rejected(string input)
    {
        var ex = Assert.Throws<TerminException>(() => TimeRules.ParseInstant(input, null));
        Assert.Equal(ErrorCodes.InvalidTime, ex.Code);
    }

    [Fact]
    public void Times_with_offset_zone_suffix_or_explicit_zone_are_accepted()
    {
        Assert.Equal(T("2026-10-14T09:00:00+02:00"), TimeRules.ParseInstant("2026-10-14T09:00:00+02:00", null));
        Assert.Equal(T("2026-10-14T07:00:00Z"), TimeRules.ParseInstant("2026-10-14T07:00:00Z", null));
        Assert.Equal(T("2026-10-14T09:00:00+02:00"), TimeRules.ParseInstant("2026-10-14T09:00[Europe/Zurich]", null));
        Assert.Equal(T("2026-11-14T09:00:00+01:00"), TimeRules.ParseInstant("2026-11-14T09:00:00", "Europe/Zurich"));
    }

    [Fact]
    public void Create_embeds_vtimezone_and_uses_tzid()
    {
        var cal = EventWriter.BuildEvent(new NewEvent
        {
            Summary = "Elterngespräch", Start = "2026-10-14T09:00:00+02:00", End = "2026-10-14T09:30:00+02:00",
            Rrule = "RRULE:FREQ=WEEKLY;COUNT=3", Reminders = [15],
        }, Zurich, out var uid);
        var text = Ics.Serialize(cal);
        Assert.True(Guid.TryParse(uid, out _));
        Assert.Contains("DTSTART;TZID=Europe/Zurich:20261014T090000", text);
        Assert.Contains("BEGIN:VTIMEZONE", text);
        Assert.Contains("RRULE:FREQ=WEEKLY;COUNT=3", text);
        Assert.Contains("TRIGGER:-PT15M", text);
        Assert.Contains("SEQUENCE:0", text);
    }

    [Fact]
    public void Update_preserves_unknown_and_x_properties_and_bumps_sequence()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        EventWriter.Update(cal, new EventPatch { Location = "Zürich" }, EditScope.All, null, Zurich);
        var text = Ics.Serialize(cal);

        Assert.Contains("X-CUSTOM-PROP;X-PARAM=keep:keep me", text);
        Assert.Contains("X-APPLE-TRAVEL-ADVISORY-BEHAVIOR:AUTOMATIC", text);
        Assert.Contains("X-WR-ALARMUID:2F0A-ALARM", text);
        Assert.Contains("LOCATION:Zürich", text);
        var master = Ics.Master(Ics.Parse(text))!;
        Assert.Equal(1, master.Sequence);
        Assert.True(master.DtStamp!.AsUtc > new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Update_this_occurrence_writes_override_into_same_resource()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        EventWriter.Update(cal, new EventPatch { Summary = "Standup (remote)" }, EditScope.This, "2026-11-09T09:00:00+01:00", Zurich);
        var text = Ics.Serialize(cal);

        var events = Read(text, "2026-11-01T00:00:00+01:00", "2026-11-30T00:00:00+01:00");
        Assert.Equal(["Standup (remote)", "Standup", "Standup"], events.Select(e => e.Summary!).ToArray());
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Zurich:20261109T090000", text);
        Assert.Contains("X-CUSTOM-PROP", text.Split("RECURRENCE-ID;TZID=Europe/Zurich:20261109")[1]); // override copies master props
    }

    [Fact]
    public void Update_this_and_future_splits_series_with_until_and_new_uid()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        var split = EventWriter.Split(cal, "2026-11-16T09:00:00+01:00", new EventPatch { Start = "2026-11-16T10:00:00+01:00" }, Zurich);

        Assert.NotNull(split.NewSeries);
        var original = Read(Ics.Serialize(split.Original), "2026-10-01T00:00:00Z", "2027-01-01T00:00:00Z");
        var next = Read(Ics.Serialize(split.NewSeries!), "2026-10-01T00:00:00Z", "2027-01-01T00:00:00Z");

        Assert.Equal(["2026-10-19T09:00:00+02:00", "2026-10-27T14:00:00+01:00", "2026-11-09T09:00:00+01:00"], original.Select(e => e.Start!).ToArray());
        // COUNT=6 minus the 4 instances generated before the split (EXDATE included) = 2.
        Assert.Equal(["2026-11-16T10:00:00+01:00", "2026-11-23T10:00:00+01:00"], next.Select(e => e.Start!).ToArray());
        Assert.All(next, e => Assert.Equal(split.NewUid, e.Uid));
        Assert.Contains("UNTIL=20261116T075959Z", Ics.Serialize(split.Original));
    }

    [Fact]
    public void Split_at_first_occurrence_is_whole_series()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        var split = EventWriter.Split(cal, "2026-10-19T09:00:00+02:00", null, Zurich);
        Assert.Null(split.NewSeries);
    }

    [Fact]
    public void Moving_a_series_shifts_overrides_and_exdates()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        EventWriter.Update(cal, new EventPatch { Start = "2026-10-19T10:00:00+02:00" }, EditScope.All, null, Zurich);
        var events = Read(Ics.Serialize(cal), "2026-10-01T00:00:00Z", "2026-12-31T00:00:00Z");

        Assert.Equal(
            ["2026-10-19T10:00:00+02:00", "2026-10-27T14:00:00+01:00", "2026-11-09T10:00:00+01:00", "2026-11-16T10:00:00+01:00", "2026-11-23T10:00:00+01:00"],
            events.Select(e => e.Start!).ToArray());
        Assert.Equal("2026-10-26T10:00:00+01:00", events[1].RecurrenceId);
        Assert.Equal("2026-10-19T10:30:00+02:00", events[0].End); // duration kept
    }

    [Fact]
    public void Delete_single_occurrence_adds_exdate_and_drops_override()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        Assert.True(EventWriter.DeleteOccurrence(cal, "2026-10-26T09:00:00+01:00", Zurich));
        var text = Ics.Serialize(cal);
        Assert.DoesNotContain("RECURRENCE-ID", text);
        Assert.Contains("20261026T090000", text);
        var events = Read(text, "2026-10-01T00:00:00Z", "2026-12-31T00:00:00Z");
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public void Delete_this_and_future_truncates()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        Assert.True(EventWriter.DeleteFrom(cal, "2026-11-09T09:00:00+01:00", Zurich));
        var events = Read(Ics.Serialize(cal), "2026-10-01T00:00:00Z", "2026-12-31T00:00:00Z");
        Assert.Equal(2, events.Count);
        Assert.False(EventWriter.DeleteFrom(Ics.Parse(Fixture("weekly-dst-override.ics")), "2026-10-19T09:00:00+02:00", Zurich));
    }

    [Fact]
    public void Unknown_recurrence_id_is_not_found()
    {
        var cal = Ics.Parse(Fixture("weekly-dst-override.ics"));
        var ex = Assert.Throws<TerminException>(() => EventWriter.DeleteOccurrence(cal, "2026-11-10T09:00:00+01:00", Zurich));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void Infinite_rules_are_clipped_to_the_range_and_capped()
    {
        var cal = EventWriter.BuildEvent(new NewEvent { Summary = "Ping", Start = "2026-01-01T00:00:00Z", End = "2026-01-01T00:05:00Z", Rrule = "FREQ=HOURLY" }, TimeZoneInfo.Utc, out _);
        var occ = EventReader.Expand(cal, T("2026-03-01T00:00:00Z"), T("2026-03-02T00:00:00Z"), Zurich);
        Assert.Equal(24, occ.Count);

        var many = EventReader.Expand(cal, T("2026-01-01T00:00:00Z"), T("2027-01-01T00:00:00Z"), Zurich);
        Assert.Equal(EventReader.MaxOccurrencesPerQuery, many.Count);
    }

    [Fact]
    public void Long_occurrence_starting_before_range_is_included()
    {
        var cal = EventWriter.BuildEvent(new NewEvent { Summary = "Trip", AllDayDate = "2026-10-01", AllDayEndDate = "2026-10-20", Rrule = "FREQ=YEARLY" }, Zurich, out _);
        var occ = EventReader.Expand(cal, T("2026-10-10T00:00:00+02:00"), T("2026-10-11T00:00:00+02:00"), Zurich);
        Assert.Single(occ);
    }

    [Fact]
    public void Server_expanded_instances_pass_through()
    {
        const string expanded = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Server//EN
            BEGIN:VEVENT
            UID:s1
            DTSTART:20261019T070000Z
            DTEND:20261019T073000Z
            RECURRENCE-ID:20261019T070000Z
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:s1
            DTSTART:20261109T080000Z
            DTEND:20261109T083000Z
            RECURRENCE-ID:20261109T080000Z
            SUMMARY:Standup
            END:VEVENT
            END:VCALENDAR
            """;
        var events = Read(expanded, "2026-10-01T00:00:00Z", "2026-12-01T00:00:00Z");
        Assert.Equal(["2026-10-19T09:00:00+02:00", "2026-11-09T09:00:00+01:00"], events.Select(e => e.Start!).ToArray());
        Assert.All(events, e => Assert.True(e.Recurring));
        Assert.Equal("2026-11-09T09:00:00+01:00", events[1].RecurrenceId);
    }

    [Fact]
    public void Detail_caps_description_and_lists_reminders()
    {
        var cal = EventWriter.BuildEvent(new NewEvent { Summary = "x", Start = "2026-10-14T09:00:00+02:00", Description = new string('a', 2500), Reminders = [10, 60] }, Zurich, out _);
        var ev = Ics.Master(cal)!;
        var capped = EventReader.ToDetail(ev, cal, null, Zurich, "c", null, fullText: false, includeIcal: false);
        Assert.Equal(2000, capped.Description!.Length);
        Assert.True(capped.DescriptionTruncated);
        Assert.Equal([10, 60], capped.Reminders!.Select(r => r.MinutesBefore!.Value).Order().ToArray());
        var full = EventReader.ToDetail(ev, cal, null, Zurich, "c", null, fullText: true, includeIcal: true);
        Assert.Equal(2500, full.Description!.Length);
        Assert.Contains("BEGIN:VCALENDAR", full.Ical);
    }

    [Fact]
    public void Json_is_compact_snake_case_and_omits_nulls()
    {
        var e = Read(Fixture("allday-multiday.ics"), "2026-10-14T00:00:00+02:00", "2026-10-15T00:00:00+02:00")[0];
        var json = TerminJson.Serialize(e);
        Assert.Equal("{\"uid\":\"allday-1@example\",\"calendar\":\"cal\",\"summary\":\"Kita closed\",\"date\":\"2026-10-14\",\"all_day\":true,\"etag\":\"\\\"e1\\\"\"}", json);
    }

    [Fact]
    public void Todo_create_complete_and_reopen()
    {
        var cal = TodoMapper.Create(new TodoInput { Summary = "Steuererklärung", DueDate = "2026-10-31" }, Zurich, out var uid);
        var dto = TodoMapper.ToDto(cal.Todos.First(), Zurich, "tasks", null);
        Assert.Equal("NEEDS-ACTION", dto.Status);
        Assert.Equal("2026-10-31", dto.DueDate);

        TodoMapper.Update(cal, new TodoInput { Status = "done" }, Zurich);
        dto = TodoMapper.ToDto(cal.Todos.First(), Zurich, "tasks", null);
        Assert.Equal("COMPLETED", dto.Status);
        Assert.NotNull(dto.Completed);
        Assert.Equal(100, dto.PercentComplete);

        TodoMapper.Update(cal, new TodoInput { Status = "NEEDS-ACTION" }, Zurich);
        dto = TodoMapper.ToDto(cal.Todos.First(), Zurich, "tasks", null);
        Assert.Null(dto.Completed);
        Assert.Equal(uid, dto.Uid);
    }

    [Fact]
    public void Free_busy_merges_and_respects_working_hours()
    {
        var busy = FreeBusyCalculator.Merge([
            new BusyBlock(T("2026-10-14T09:00:00+02:00"), T("2026-10-14T10:00:00+02:00")),
            new BusyBlock(T("2026-10-14T09:30:00+02:00"), T("2026-10-14T11:00:00+02:00")),
            new BusyBlock(T("2026-10-14T13:00:00+02:00"), T("2026-10-14T13:20:00+02:00")),
        ]);
        Assert.Equal(2, busy.Count);
        Assert.Equal(T("2026-10-14T11:00:00+02:00"), busy[0].End);

        var free = FreeBusyCalculator.FreeSlots(busy, T("2026-10-14T00:00:00+02:00"), T("2026-10-16T00:00:00+02:00"), 60,
            WorkingHours.Parse("Mon-Fri 08:00-17:00"), Zurich);
        Assert.Equal(
            [("08:00", "09:00"), ("11:00", "13:00"), ("13:20", "17:00"), ("08:00", "17:00")],
            free.Select(f => (TimeZoneInfo.ConvertTime(f.Start, Zurich).ToString("HH:mm"), TimeZoneInfo.ConvertTime(f.End, Zurich).ToString("HH:mm"))).ToArray());
    }

    [Fact]
    public void Working_hours_parse_day_ranges()
    {
        var wh = WorkingHours.Parse("Sa-Mo 10:00-12:00");
        Assert.Equal(3, wh.Days.Count);
        Assert.Contains(DayOfWeek.Sunday, wh.Days);
        Assert.Throws<TerminException>(() => WorkingHours.Parse("9-5"));
    }
}
