using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Termin.Calendar;
using Termin.Server.Services;

namespace Termin.Server.Tools;

/// <summary>
/// The ten Termin tools. Parameter names are snake_case on purpose: they are the
/// wire names. Today's date and the default timezone are appended to every
/// description at list time.
/// </summary>
[McpServerToolType]
public sealed class CalendarTools(CalendarService service, ToolRunner runner)
{
    private const string AccountDoc = "Account label; omit for the default account.";
    private const string TimeDoc = "ISO 8601 date-time with offset, e.g. 2026-10-14T09:00:00+02:00.";

    [McpServerTool(Name = "list_calendars", Title = "List calendars", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List calendars with id, name, color, timezone, components (VEVENT for events, VTODO for todos) and read-only flag. Without 'account', lists calendars of all accounts. Use the returned id as 'calendar' in other tools.")]
    public Task<CallToolResult> ListCalendars(
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("list_calendars", account, false, () => service.ListCalendarsAsync(account, ct));

    [McpServerTool(Name = "get_events", Title = "Get events", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Events overlapping a time range, recurrences expanded, sorted by start. Each occurrence of a recurring event carries uid + recurrence_id. All-day events use 'date' (or inclusive start_date/end_date). Times are shown in the event's own zone; 'tz' is omitted when it equals the response tz. If next_cursor is present, call again with it to get more.")]
    public Task<CallToolResult> GetEvents(
        [Description("Range start. " + TimeDoc)] string start,
        [Description("Range end (exclusive). " + TimeDoc)] string end,
        [Description("Calendar ids or names; omit for all event calendars.")] string[]? calendars = null,
        [Description("Max events per page (default 100, max 500).")] int? limit = null,
        [Description("next_cursor from a previous call with the same arguments.")] string? cursor = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("get_events", account, false, () => service.GetEventsAsync(account, start, end, calendars, limit, cursor, ct));

    [McpServerTool(Name = "get_event", Title = "Get event", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One event in full: description (capped at 2000 characters unless full_text), attendees, organizer, rrule, exdates, reminders. Pass recurrence_id to get one occurrence of a recurring event. include_ical returns the raw iCalendar text.")]
    public Task<CallToolResult> GetEvent(
        [Description("Calendar id or name.")] string calendar,
        [Description("Event uid.")] string uid,
        [Description("Occurrence to show, exactly as returned by get_events.")] string? recurrence_id = null,
        [Description("Also return the raw iCalendar.")] bool include_ical = false,
        [Description("Return the full description instead of the first 2000 characters.")] bool full_text = false,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("get_event", account, false, () => service.GetEventAsync(account, calendar, uid, recurrence_id, include_ical, full_text, ct));

    [McpServerTool(Name = "search_events", Title = "Search events", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Case-insensitive text search over summary, location and description. All words must match. Searches 90 days back and 90 days ahead unless start/end are given.")]
    public Task<CallToolResult> SearchEvents(
        [Description("Words to search for.")] string query,
        [Description("Range start (default: 90 days ago). " + TimeDoc)] string? start = null,
        [Description("Range end (default: 90 days ahead). " + TimeDoc)] string? end = null,
        [Description("Calendar ids or names; omit for all event calendars.")] string[]? calendars = null,
        [Description("Max results per page (default 50).")] int? limit = null,
        [Description("next_cursor from a previous call with the same arguments.")] string? cursor = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("search_events", account, false, () => service.SearchEventsAsync(account, query, start, end, calendars, limit, cursor, ct));

    [McpServerTool(Name = "free_busy", Title = "Free/busy", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Merged busy blocks and free slots of at least min_slot_minutes in a range. Transparent (free) and cancelled events are ignored; all-day events count as free unless marked busy. Optionally restrict free slots to working hours like '09:00-17:00' or 'Mon-Fri 08:00-18:00' (in the default timezone).")]
    public Task<CallToolResult> FreeBusy(
        [Description("Range start. " + TimeDoc)] string start,
        [Description("Range end. " + TimeDoc)] string end,
        [Description("Calendar ids or names; omit for all event calendars.")] string[]? calendars = null,
        [Description("Minimum free slot length in minutes (default 30).")] int? min_slot_minutes = null,
        [Description("Working hours, e.g. 'Mon-Fri 08:00-18:00'.")] string? working_hours = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("free_busy", account, false, () => service.FreeBusyAsync(account, start, end, calendars, min_slot_minutes, working_hours, ct));

    [McpServerTool(Name = "create_event", Title = "Create event", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an event, optionally recurring. Timed: start (+ end, default 1 hour). All-day: all_day_date (+ inclusive all_day_end_date). Times without offset need 'timezone'; the event is stored in 'timezone' or the default zone. rrule is an RFC 5545 rule without the 'RRULE:' prefix, e.g. FREQ=WEEKLY;BYDAY=MO;COUNT=10. reminders are minutes before start.")]
    public Task<CallToolResult> CreateEvent(
        [Description("Calendar id or name.")] string calendar,
        [Description("Title.")] string summary,
        [Description("Start. " + TimeDoc)] string? start = null,
        [Description("End. " + TimeDoc)] string? end = null,
        [Description("All-day event date, e.g. 2026-10-14.")] string? all_day_date = null,
        [Description("Last day (inclusive) of a multi-day all-day event.")] string? all_day_end_date = null,
        [Description("IANA timezone, e.g. Europe/Zurich.")] string? timezone = null,
        [Description("Location.")] string? location = null,
        [Description("Description / notes.")] string? description = null,
        [Description("Recurrence rule, e.g. FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261231T230000Z.")] string? rrule = null,
        [Description("Reminders in minutes before start, e.g. [15, 60].")] int[]? reminders = null,
        [Description("CONFIRMED, TENTATIVE or CANCELLED.")] string? status = null,
        [Description("OPAQUE (busy, default for timed events) or TRANSPARENT (free).")] string? transparency = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("create_event", account, true, () => service.CreateEventAsync(account, calendar, new NewEvent
        {
            Summary = summary, Start = start, End = end, AllDayDate = all_day_date, AllDayEndDate = all_day_end_date,
            Timezone = timezone, Location = location, Description = description, Rrule = rrule,
            Reminders = reminders?.ToList(), Status = status, Transparency = transparency,
        }, ct));

    [McpServerTool(Name = "update_event", Title = "Update event", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Patch fields of an event. Requires the etag from a prior read; a stale etag returns 'conflict' with the current version and writes nothing. scope: 'all' (whole series, default), 'this' (one occurrence, needs recurrence_id), 'this_and_future' (splits the series at recurrence_id: the original ends before it, a new series with a new uid starts there). Unset patch fields stay unchanged; an empty string clears a text field; rrule '' removes recurrence. Changing start keeps the duration unless end is given.")]
    public Task<CallToolResult> UpdateEvent(
        [Description("Calendar id or name.")] string calendar,
        [Description("Event uid.")] string uid,
        [Description("etag from get_events/get_event.")] string etag,
        [Description("Fields to change.")] EventPatch patch,
        [Description("this, this_and_future or all.")] string scope = "all",
        [Description("Occurrence for scope this / this_and_future, as returned by get_events.")] string? recurrence_id = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("update_event", account, true, () => service.UpdateEventAsync(account, calendar, uid, etag, patch, scope, recurrence_id, ct));

    [McpServerTool(Name = "delete_event", Title = "Delete event", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Delete an event. Requires the etag from a prior read. scope 'all' deletes the event or whole series; 'this' removes one occurrence (adds an EXDATE); 'this_and_future' ends the series before recurrence_id.")]
    public Task<CallToolResult> DeleteEvent(
        [Description("Calendar id or name.")] string calendar,
        [Description("Event uid.")] string uid,
        [Description("etag from get_events/get_event.")] string etag,
        [Description("this, this_and_future or all.")] string scope = "all",
        [Description("Occurrence for scope this / this_and_future, as returned by get_events.")] string? recurrence_id = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("delete_event", account, true, () => service.DeleteEventAsync(account, calendar, uid, etag, scope, recurrence_id, ct));

    [McpServerTool(Name = "list_todos", Title = "List todos", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Todos (VTODO), sorted by due date. status: 'open' (default: NEEDS-ACTION and IN-PROCESS), 'all', 'completed', 'cancelled', 'needs-action' or 'in-process'. due_before limits to todos due before a date or date-time.")]
    public Task<CallToolResult> ListTodos(
        [Description("Calendar ids or names; omit for all todo calendars.")] string[]? calendars = null,
        [Description("Status filter (default open).")] string? status = null,
        [Description("Only todos due before this date (2026-10-14) or date-time.")] string? due_before = null,
        [Description("Max todos per page (default 100).")] int? limit = null,
        [Description("next_cursor from a previous call with the same arguments.")] string? cursor = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("list_todos", account, false, () => service.ListTodosAsync(account, calendars, status, due_before, limit, cursor, ct));

    [McpServerTool(Name = "upsert_todo", Title = "Create or update todo", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a todo (omit uid) or update/complete one (uid + etag from list_todos). Set status COMPLETED to complete it. Unset fields stay unchanged; an empty string clears due/due_date/description.")]
    public Task<CallToolResult> UpsertTodo(
        [Description("Calendar id or name (must support VTODO).")] string calendar,
        [Description("Title (required when creating).")] string? summary = null,
        [Description("uid of an existing todo to update.")] string? uid = null,
        [Description("etag of the existing todo (required with uid).")] string? etag = null,
        [Description("Due date-time. " + TimeDoc)] string? due = null,
        [Description("Due date without time, e.g. 2026-10-14.")] string? due_date = null,
        [Description("NEEDS-ACTION, IN-PROCESS, COMPLETED or CANCELLED.")] string? status = null,
        [Description("Notes.")] string? description = null,
        [Description("1 (highest) to 9 (lowest), 0 for none.")] int? priority = null,
        [Description("IANA timezone for a due time without offset.")] string? timezone = null,
        [Description(AccountDoc)] string? account = null,
        CancellationToken ct = default) =>
        runner.Run("upsert_todo", account, true, () => service.UpsertTodoAsync(account, calendar, uid, etag, new TodoInput
        {
            Summary = summary, Due = due, DueDate = due_date, Status = status, Description = description, Priority = priority, Timezone = timezone,
        }, ct));
}
