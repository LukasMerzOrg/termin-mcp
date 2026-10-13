using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Termin.Calendar;

namespace Termin.CalDav;

public sealed record CalDavAccount(
    string Label,
    Uri BaseUrl,
    string Username,
    string Secret,
    ProviderProfile Profile,
    bool ForceClientExpand = false);

public sealed record CalendarInfo(
    string Id,
    Uri Url,
    string? Name,
    string? Color,
    string? TimeZone,
    IReadOnlyList<string> Components,
    bool ReadOnly)
{
    public bool Supports(string component) => Components.Contains(component, StringComparer.OrdinalIgnoreCase);
}

public sealed record CalendarResource(Uri Href, string? Etag, string Data);

/// <summary>
/// A thin CalDAV client on HttpClient: discovery (RFC 6764 / RFC 4791),
/// calendar-query REPORT, PUT and DELETE with ETag preconditions.
/// </summary>
public sealed partial class CalDavClient
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace Cal = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace Apple = "http://apple.com/ns/ical/";
    public static readonly XNamespace Cs = "http://calendarserver.org/ns/";

    public static readonly TimeSpan DiscoveryTtl = TimeSpan.FromMinutes(10);
    private const int MaxRedirects = 5;
    private const int NetworkRetries = 2;

    /// <summary>Observer for backend status codes (metrics). Arguments: account label, HTTP status (0 = network error).</summary>
    public static event Action<string, int>? ResponseObserved;

    private readonly HttpClient _http;
    private readonly CalDavAccount _account;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private IReadOnlyList<CalendarInfo>? _calendars;
    private DateTimeOffset _discoveredAt;
    private Uri? _home;
    private bool _serverExpandBroken;

    public CalDavClient(HttpClient http, CalDavAccount account)
    {
        _http = http;
        _account = account;
    }

    public CalDavAccount Account => _account;

    /// <summary>Whether this client asks the server to expand recurrences.</summary>
    public bool UsesServerExpand => _account.Profile.ServerExpand && !_account.ForceClientExpand && !_serverExpandBroken;

    public void InvalidateDiscovery()
    {
        _calendars = null;
    }

    // ---------------------------------------------------------------- discovery

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct)
    {
        if (_calendars is { } cached && DateTimeOffset.UtcNow - _discoveredAt < DiscoveryTtl) return cached;
        await _discoveryLock.WaitAsync(ct);
        try
        {
            if (_calendars is { } again && DateTimeOffset.UtcNow - _discoveredAt < DiscoveryTtl) return again;
            var home = _home ?? await FindCalendarHomeAsync(ct);
            _home = home;
            var calendars = await ListCalendarsAsync(home, ct);
            _calendars = calendars;
            _discoveredAt = DateTimeOffset.UtcNow;
            return calendars;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    /// <summary>A cheap liveness check: PROPFIND Depth 0 on the calendar home.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        var home = _home ?? await FindCalendarHomeAsync(ct);
        _home = home;
        await PropfindAsync(home, 0, Props(Dav + "resourcetype"), ct);
    }

    private async Task<Uri> FindCalendarHomeAsync(CancellationToken ct)
    {
        var principal = await FindPrincipalAsync(ct);
        var doc = await PropfindAsync(principal, 0, Props(Cal + "calendar-home-set"), ct);
        var homeHref = Responses(doc).SelectMany(r => r.Props)
            .FirstOrDefault(p => p.Name == Cal + "calendar-home-set")?.Element(Dav + "href")?.Value;
        return homeHref is null ? principal : Resolve(principal, homeHref);
    }

    private async Task<Uri> FindPrincipalAsync(CancellationToken ct)
    {
        var candidates = new List<Uri>();
        var baseUrl = _account.BaseUrl;
        if (baseUrl.AbsolutePath is "/" or "")
            candidates.Add(new Uri(baseUrl, "/.well-known/caldav"));
        candidates.Add(baseUrl);
        if (baseUrl.AbsolutePath is not ("/" or ""))
            candidates.Add(new Uri(baseUrl, "/.well-known/caldav"));

        TerminException? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var (doc, finalUri) = await PropfindWithUriAsync(candidate, 0, Props(Dav + "current-user-principal"), ct);
                var href = Responses(doc).SelectMany(r => r.Props)
                    .FirstOrDefault(p => p.Name == Dav + "current-user-principal")?.Element(Dav + "href")?.Value;
                if (href is not null) return Resolve(finalUri, href);
                if (candidate == baseUrl) return finalUri;
            }
            catch (TerminException ex) when (ex.Code is ErrorCodes.NotFound or ErrorCodes.BackendUnavailable)
            {
                last = ex;
            }
        }
        throw last ?? new TerminException(ErrorCodes.BackendUnavailable, $"Calendar discovery failed for account '{_account.Label}'.");
    }

    private async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(Uri home, CancellationToken ct)
    {
        var doc = await PropfindAsync(home, 1, Props(
            Dav + "resourcetype", Dav + "displayname", Apple + "calendar-color",
            Cal + "supported-calendar-component-set", Dav + "current-user-privilege-set",
            Cs + "getctag", Dav + "sync-token", Cal + "calendar-timezone"), ct);

        var result = new List<CalendarInfo>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Responses(doc))
        {
            var props = r.Props.ToDictionary(p => p.Name, p => p);
            if (!props.TryGetValue(Dav + "resourcetype", out var rt) || rt.Element(Cal + "calendar") is null) continue;

            var url = Resolve(home, r.Href);
            var components = props.TryGetValue(Cal + "supported-calendar-component-set", out var comps)
                ? comps.Elements(Cal + "comp").Select(c => (string?)c.Attribute("name")).OfType<string>().Select(s => s.ToUpperInvariant()).ToList()
                : ["VEVENT", "VTODO"];
            if (!components.Contains("VEVENT") && !components.Contains("VTODO")) continue;
            if (!_account.Profile.SupportsTodos) components.Remove("VTODO");
            if (components.Count == 0) continue;

            var readOnly = false;
            if (props.TryGetValue(Dav + "current-user-privilege-set", out var privs) && privs.HasElements)
            {
                var names = privs.Elements(Dav + "privilege").SelectMany(p => p.Elements()).Select(e => e.Name.LocalName).ToHashSet();
                readOnly = !(names.Contains("all") || names.Contains("write") || names.Contains("write-content"));
            }

            string? tz = null;
            if (props.TryGetValue(Cal + "calendar-timezone", out var tzEl) && TzidRegex().Match(tzEl.Value) is { Success: true } m)
                tz = m.Groups[1].Value.Trim();

            var color = props.TryGetValue(Apple + "calendar-color", out var c) && c.Value.Length > 0 ? NormalizeColor(c.Value) : null;
            var name = props.TryGetValue(Dav + "displayname", out var dn) && dn.Value.Length > 0 ? dn.Value : null;

            var id = Uri.UnescapeDataString(url.AbsolutePath.TrimEnd('/').Split('/').Last());
            var unique = id;
            for (var i = 2; !usedIds.Add(unique); i++) unique = $"{id}-{i}";

            result.Add(new CalendarInfo(unique, EnsureTrailingSlash(url), name, color, tz, components, readOnly));
        }
        return result;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// calendar-query REPORT for a component type, optionally limited to a time range
    /// and optionally asking the server to expand recurrences.
    /// </summary>
    public async Task<List<CalendarResource>> QueryAsync(
        CalendarInfo calendar, string component, DateTimeOffset? from, DateTimeOffset? to, bool expand, CancellationToken ct)
    {
        expand = expand && from is not null && to is not null && UsesServerExpand;
        try
        {
            return await ReportAsync(calendar, BuildQuery(component, from, to, expand, uid: null), ct);
        }
        catch (TerminException ex) when (expand && ex.Code is ErrorCodes.BackendUnavailable or ErrorCodes.Forbidden or ErrorCodes.InvalidInput)
        {
            // The server rejected <expand>: remember and fall back to client-side expansion.
            _serverExpandBroken = true;
            return await ReportAsync(calendar, BuildQuery(component, from, to, false, uid: null), ct);
        }
    }

    public async Task<CalendarResource?> FindByUidAsync(CalendarInfo calendar, string component, string uid, CancellationToken ct)
    {
        var found = await ReportAsync(calendar, BuildQuery(component, null, null, false, uid), ct);
        return found.FirstOrDefault(r => Ics.TryParse(r.Data)?.Children
            .OfType<Ical.Net.CalendarComponents.UniqueComponent>().Any(c => c.Uid == uid) == true);
    }

    private async Task<List<CalendarResource>> ReportAsync(CalendarInfo calendar, XDocument body, CancellationToken ct)
    {
        var doc = await SendXmlAsync(new HttpMethod("REPORT"), calendar.Url, 1, body, ct);
        var list = new List<CalendarResource>();
        foreach (var r in Responses(doc))
        {
            var data = r.Props.FirstOrDefault(p => p.Name == Cal + "calendar-data")?.Value;
            if (string.IsNullOrWhiteSpace(data))
            {
                if (_account.Profile.RequireCalendarData) continue;
                data = "";
            }
            var etag = r.Props.FirstOrDefault(p => p.Name == Dav + "getetag")?.Value;
            list.Add(new CalendarResource(Resolve(calendar.Url, r.Href), NullIfEmpty(etag), data));
        }
        return list;
    }

    private static XDocument BuildQuery(string component, DateTimeOffset? from, DateTimeOffset? to, bool expand, string? uid)
    {
        var calendarData = new XElement(Cal + "calendar-data");
        if (expand)
            calendarData.Add(new XElement(Cal + "expand",
                new XAttribute("start", CalDavTime(from!.Value)), new XAttribute("end", CalDavTime(to!.Value))));

        var compFilter = new XElement(Cal + "comp-filter", new XAttribute("name", component));
        if (from is not null || to is not null)
        {
            var range = new XElement(Cal + "time-range");
            if (from is not null) range.Add(new XAttribute("start", CalDavTime(from.Value)));
            if (to is not null) range.Add(new XAttribute("end", CalDavTime(to.Value)));
            compFilter.Add(range);
        }
        if (uid is not null)
            compFilter.Add(new XElement(Cal + "prop-filter", new XAttribute("name", "UID"),
                new XElement(Cal + "text-match", new XAttribute("collation", "i;octet"), uid)));

        return new XDocument(new XElement(Cal + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "d", Dav), new XAttribute(XNamespace.Xmlns + "c", Cal),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), calendarData),
            new XElement(Cal + "filter",
                new XElement(Cal + "comp-filter", new XAttribute("name", "VCALENDAR"), compFilter))));
    }

    // ---------------------------------------------------------------- writes

    /// <summary>PUT a resource. New resources use If-None-Match: *, updates If-Match.</summary>
    /// <returns>The new ETag, if the server reports one.</returns>
    public async Task<string?> PutAsync(Uri href, string ics, string? ifMatch, CancellationToken ct)
    {
        var (response, retried) = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, href)
            {
                Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
            };
            if (ifMatch is null) req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            else req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return req;
        }, href, ct);
        using (response)
        {
            // After a dropped connection the first PUT may have landed; then the retry fails its
            // precondition. Only treat that as success if the server holds exactly what we wrote.
            if (retried && response.StatusCode == HttpStatusCode.PreconditionFailed && await LandedAsync(href, ics, ct) is { } landed)
                return landed.Etag ?? await GetEtagAsync(href, ct);

            await EnsureSuccessAsync(response);
            var etag = response.Headers.ETag?.ToString();
            if (etag is null && response.Headers.TryGetValues("ETag", out var values)) etag = values.FirstOrDefault();
            return etag ?? await GetEtagAsync(href, ct);
        }
    }

    public async Task DeleteAsync(Uri href, string ifMatch, CancellationToken ct)
    {
        var (response, retried) = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, href);
            req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return req;
        }, href, ct);
        using (response)
        {
            // A retried DELETE that finds nothing means the first attempt succeeded.
            if (retried && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return;
            await EnsureSuccessAsync(response);
        }
    }

    /// <summary>GET a single resource.</summary>
    public async Task<CalendarResource?> GetAsync(Uri href, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, href), href, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        await EnsureSuccessAsync(response);
        var etag = response.Headers.ETag?.ToString();
        return new CalendarResource(href, etag, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<CalendarResource?> LandedAsync(Uri href, string written, CancellationToken ct)
    {
        try
        {
            var current = await GetAsync(href, ct);
            if (current is null) return null;
            var mine = Unfold(written);
            var theirs = Unfold(current.Data);
            var uids = Lines(mine, "UID:");
            var stamps = Lines(mine, "LAST-MODIFIED:");
            return uids.Count > 0 && uids.All(theirs.Contains) && stamps.Count > 0 && stamps.Any(theirs.Contains) ? current : null;
        }
        catch (TerminException)
        {
            return null;
        }

        static string Unfold(string s) => s.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");
        static List<string> Lines(string s, string prefix) =>
            s.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Distinct().ToList();
    }

    public async Task<string?> GetEtagAsync(Uri href, CancellationToken ct)
    {
        try
        {
            var doc = await PropfindAsync(href, 0, Props(Dav + "getetag"), ct);
            return NullIfEmpty(Responses(doc).SelectMany(r => r.Props).FirstOrDefault(p => p.Name == Dav + "getetag")?.Value);
        }
        catch (TerminException)
        {
            return null;
        }
    }

    public static Uri NewResourceUrl(CalendarInfo calendar, string uid) =>
        new(calendar.Url, Uri.EscapeDataString(uid) + ".ics");

    // ---------------------------------------------------------------- HTTP plumbing

    private Task<XDocument> PropfindAsync(Uri url, int depth, XElement prop, CancellationToken ct) =>
        SendXmlAsync(new HttpMethod("PROPFIND"), url, depth,
            new XDocument(new XElement(Dav + "propfind", new XAttribute(XNamespace.Xmlns + "d", Dav), prop)), ct);

    private async Task<(XDocument Doc, Uri FinalUri)> PropfindWithUriAsync(Uri url, int depth, XElement prop, CancellationToken ct)
    {
        var body = new XDocument(new XElement(Dav + "propfind", new XAttribute(XNamespace.Xmlns + "d", Dav), prop));
        using var response = await SendAsync(() => XmlRequest(new HttpMethod("PROPFIND"), url, depth, body), url, ct);
        await EnsureSuccessAsync(response);
        var final = response.RequestMessage?.RequestUri ?? url;
        return (await ReadXmlAsync(response, ct), final);
    }

    private async Task<XDocument> SendXmlAsync(HttpMethod method, Uri url, int depth, XDocument body, CancellationToken ct)
    {
        using var response = await SendAsync(() => XmlRequest(method, url, depth, body), url, ct);
        await EnsureSuccessAsync(response);
        return await ReadXmlAsync(response, ct);
    }

    private static HttpRequestMessage XmlRequest(HttpMethod method, Uri url, int depth, XDocument body)
    {
        var req = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.Declaration + body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml"),
        };
        req.Headers.TryAddWithoutValidation("Depth", depth.ToString(CultureInfo.InvariantCulture));
        return req;
    }

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            throw new TerminException(ErrorCodes.BackendUnavailable, "The calendar server returned an unreadable response.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, Uri url, CancellationToken ct) =>
        (await SendWithRetryAsync(factory, url, ct)).Response;

    /// <summary>
    /// Sends with Basic auth and follows redirects manually, since HttpClient drops
    /// the Authorization header on redirects. Cross-host redirects are followed only
    /// over HTTPS and only to hosts the provider profile allows.
    /// Some servers (Radicale's built-in WSGI server, notably) occasionally close a
    /// connection without answering; such requests are retried. Callers of writes get
    /// <c>Retried</c> so they can tell a replayed precondition failure from a real one.
    /// </summary>
    private async Task<(HttpResponseMessage Response, bool Retried)> SendWithRetryAsync(Func<HttpRequestMessage> factory, Uri url, CancellationToken ct)
    {
        var current = url;
        var attempts = 0;
        for (var hop = 0; ; )
        {
            var request = factory();
            request.RequestUri = current;
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_account.Username}:{_account.Secret}")));
            request.Headers.UserAgent.ParseAdd("Termin/0.3 (+https://github.com/LukasMerzOrg/termin-mcp)");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested && attempts < NetworkRetries)
            {
                attempts++;
                await Task.Delay(150 * attempts, ct);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                ResponseObserved?.Invoke(_account.Label, 0);
                throw new TerminException(ErrorCodes.BackendUnavailable,
                    $"The calendar server for account '{_account.Label}' is not reachable.", "Try again later.");
            }
            ResponseObserved?.Invoke(_account.Label, (int)response.StatusCode);

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                if (++hop > MaxRedirects || !RedirectAllowed(current, next))
                    throw new TerminException(ErrorCodes.BackendUnavailable,
                        $"The calendar server for account '{_account.Label}' redirected to a location Termin does not follow.");
                current = next;
                continue;
            }
            return (response, attempts > 0);
        }
    }

    private bool RedirectAllowed(Uri from, Uri to)
    {
        if (string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase))
            return to.Scheme == from.Scheme || to.Scheme == Uri.UriSchemeHttps;
        if (to.Scheme != Uri.UriSchemeHttps || !_account.Profile.FollowCrossHostRedirects) return false;
        return _account.Profile.RedirectDomains.Any(d =>
            to.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || to.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var label = _account.Label;
        var status = (int)response.StatusCode;
        // Response bodies are never surfaced: they can contain URLs and usernames.
        await Task.CompletedTask;
        throw status switch
        {
            401 => new TerminException(ErrorCodes.Forbidden, $"The calendar server rejected the credentials of account '{label}'.",
                "Check the account's username and app password on the setup page."),
            403 => new TerminException(ErrorCodes.Forbidden, $"Account '{label}' has no permission for this operation."),
            404 or 410 => Invalidate(new TerminException(ErrorCodes.NotFound, "The calendar item or collection was not found.",
                "It may have been deleted. Read the calendar again.")),
            412 => new TerminException(ErrorCodes.Conflict, "The item changed on the server since it was read.",
                "Read it again and retry with the new etag."),
            400 or 415 or 422 => new TerminException(ErrorCodes.InvalidInput, "The calendar server rejected the request as invalid."),
            507 => new TerminException(ErrorCodes.BackendUnavailable, "The calendar server is out of storage."),
            _ => new TerminException(ErrorCodes.BackendUnavailable, $"The calendar server for account '{label}' returned an error ({status})."),
        };
    }

    private TerminException Invalidate(TerminException ex)
    {
        InvalidateDiscovery();
        return ex;
    }

    // ---------------------------------------------------------------- XML helpers

    private sealed record DavResponse(string Href, IReadOnlyList<XElement> Props);

    private static IEnumerable<DavResponse> Responses(XDocument doc)
    {
        foreach (var r in doc.Root?.Elements(Dav + "response") ?? [])
        {
            var href = r.Element(Dav + "href")?.Value;
            if (href is null) continue;
            var props = r.Elements(Dav + "propstat")
                .Where(ps => ps.Element(Dav + "status")?.Value.Contains(" 200") ?? false)
                .SelectMany(ps => ps.Element(Dav + "prop")?.Elements() ?? [])
                .ToList();
            yield return new DavResponse(href.Trim(), props);
        }
    }

    private static XElement Props(params XName[] names) => new(Dav + "prop", names.Select(n => new XElement(n)));

    private static Uri Resolve(Uri baseUri, string href) => new(baseUri, href.Trim());

    private static Uri EnsureTrailingSlash(Uri u) =>
        u.AbsolutePath.EndsWith('/') ? u : new UriBuilder(u) { Path = u.AbsolutePath + "/" }.Uri;

    private static string CalDavTime(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string NormalizeColor(string c)
    {
        c = c.Trim();
        // Apple stores #RRGGBBAA; drop the alpha channel.
        return c.Length == 9 && c[0] == '#' ? c[..7] : c;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"TZID:([^\r\n]+)")]
    private static partial Regex TzidRegex();
}
