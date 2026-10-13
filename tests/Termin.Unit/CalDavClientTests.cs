using System.Net;
using System.Text;
using Termin.CalDav;
using Termin.Calendar;

namespace Termin.Unit;

/// <summary>CalDAV client behaviour that needs no real server: retries, redirects, error mapping.</summary>
public class CalDavClientTests
{
    private static readonly Uri Href = new("https://dav.example/cal/e1.ics");

    private const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1\r\nLAST-MODIFIED:20261013T080000Z\r\nSUMMARY:x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private sealed class Script(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Calls++));
        }
    }

    private static CalDavClient Client(Script script, ProviderProfile? profile = null) =>
        new(new HttpClient(script), new CalDavAccount("t", new Uri("https://dav.example/"), "u", "p", profile ?? ProviderProfile.Generic));

    private static HttpResponseMessage Status(HttpStatusCode code, string? body = null, string? etag = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body ?? "", Encoding.UTF8) };
        if (etag is not null) r.Headers.TryAddWithoutValidation("ETag", etag);
        return r;
    }

    [Fact]
    public async Task Dropped_put_that_landed_is_reported_as_success()
    {
        var script = new Script((req, n) => n switch
        {
            0 => throw new HttpRequestException("response ended prematurely"), // server applied it, then dropped the connection
            1 => Status(HttpStatusCode.PreconditionFailed),                     // replay fails If-None-Match
            _ => Status(HttpStatusCode.OK, Ics, "\"v2\""),                    // GET shows exactly what we wrote
        });
        var etag = await Client(script).PutAsync(Href, Ics, null, default);
        Assert.Equal("\"v2\"", etag);
        Assert.Equal(HttpMethod.Get, script.Requests[2].Method);
    }

    [Fact]
    public async Task Real_precondition_failure_stays_a_conflict()
    {
        var other = Ics.Replace("LAST-MODIFIED:20261013T080000Z", "LAST-MODIFIED:20261013T090000Z");
        var script = new Script((req, n) => n switch
        {
            0 => throw new HttpRequestException("reset"),
            1 => Status(HttpStatusCode.PreconditionFailed),
            _ => Status(HttpStatusCode.OK, other, "\"v3\""), // someone else's version
        });
        var ex = await Assert.ThrowsAsync<TerminException>(() => Client(script).PutAsync(Href, Ics, "\"v1\"", default));
        Assert.Equal(ErrorCodes.Conflict, ex.Code);
    }

    [Fact]
    public async Task Precondition_failure_without_retry_is_a_conflict()
    {
        var script = new Script((_, _) => Status(HttpStatusCode.PreconditionFailed));
        var ex = await Assert.ThrowsAsync<TerminException>(() => Client(script).PutAsync(Href, Ics, "\"v1\"", default));
        Assert.Equal(ErrorCodes.Conflict, ex.Code);
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task Retried_delete_that_finds_nothing_succeeded()
    {
        var script = new Script((_, n) => n == 0 ? throw new HttpRequestException("reset") : Status(HttpStatusCode.NotFound));
        await Client(script).DeleteAsync(Href, "\"v1\"", default);
        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task Persistent_network_failure_is_backend_unavailable_without_details()
    {
        var script = new Script((_, _) => throw new HttpRequestException("connect to 10.0.0.5 failed for user lukas"));
        var ex = await Assert.ThrowsAsync<TerminException>(() => Client(script).PutAsync(Href, Ics, null, default));
        Assert.Equal(ErrorCodes.BackendUnavailable, ex.Code);
        Assert.DoesNotContain("10.0.0.5", ex.Message);
        Assert.DoesNotContain("lukas", ex.Message);
        Assert.Equal(3, script.Calls);
    }

    [Fact]
    public async Task Credentials_follow_same_host_redirects_but_not_foreign_ones()
    {
        var script = new Script((req, n) => n switch
        {
            0 => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://evil.example/steal") } },
            _ => Status(HttpStatusCode.OK),
        });
        var ex = await Assert.ThrowsAsync<TerminException>(() => Client(script).GetAsync(Href, default));
        Assert.Equal(ErrorCodes.BackendUnavailable, ex.Code);
        Assert.Single(script.Requests);

        var icloud = new Script((req, n) => n switch
        {
            0 => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://p42-caldav.icloud.com/cal/e1.ics") } },
            _ => Status(HttpStatusCode.OK, Ics),
        });
        var client = new CalDavClient(new HttpClient(icloud), new CalDavAccount("i", new Uri("https://caldav.icloud.com/"), "u", "p", ProviderProfile.ICloud));
        var res = await client.GetAsync(new Uri("https://caldav.icloud.com/cal/e1.ics"), default);
        Assert.NotNull(res);
        Assert.Equal("p42-caldav.icloud.com", icloud.Requests[1].RequestUri!.Host);
        Assert.NotNull(icloud.Requests[1].Headers.Authorization);
    }

    [Theory]
    [InlineData(401, ErrorCodes.Forbidden)]
    [InlineData(403, ErrorCodes.Forbidden)]
    [InlineData(404, ErrorCodes.NotFound)]
    [InlineData(412, ErrorCodes.Conflict)]
    [InlineData(503, ErrorCodes.BackendUnavailable)]
    public async Task Status_codes_map_to_the_error_set_and_never_leak_bodies(int status, string code)
    {
        var script = new Script((_, _) => Status((HttpStatusCode)status, "<error>secret path /home/lukas</error>"));
        var ex = await Assert.ThrowsAsync<TerminException>(() => Client(script).DeleteAsync(Href, "\"x\"", default));
        Assert.Equal(code, ex.Code);
        Assert.DoesNotContain("lukas", ex.Message);
    }

    [Theory]
    [InlineData("https://caldav.icloud.com/", "icloud")]
    [InlineData("https://cloud.example.com/remote.php/dav", "nextcloud")]
    [InlineData("https://dav.example.com/dav.php/", "baikal")]
    [InlineData("https://caldav.fastmail.com/", "fastmail")]
    [InlineData("https://dav.example.com/", "generic")]
    public void Provider_profile_detection(string url, string expected) =>
        Assert.Equal(expected, ProviderProfile.For("auto", new Uri(url)).Name);
}
