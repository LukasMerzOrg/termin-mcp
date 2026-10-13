using System.Net.Http.Headers;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Termin.Integration;

/// <summary>
/// A real CalDAV server for the integration suite. By default each one runs in
/// Docker via Testcontainers. Set TERMIN_IT_&lt;NAME&gt;_URL (plus _USER/_PASSWORD)
/// to use an already running server instead, and TERMIN_IT_&lt;NAME&gt;_IMAGE to
/// pull from a mirror.
/// </summary>
public abstract class CalDavServer : IAsyncLifetime
{
    protected IContainer? Container;

    public abstract string Name { get; }
    public abstract string Provider { get; }
    public Uri BaseUrl { get; protected set; } = null!;
    public string User { get; protected set; } = "test";
    public string Password { get; protected set; } = "test";
    public string? SkipReason { get; protected set; }

    /// <summary>The collection under which calendars are created with MKCALENDAR.</summary>
    protected abstract Uri CalendarHome { get; }

    protected abstract Task StartContainerAsync();

    public async Task InitializeAsync()
    {
        var prefix = "TERMIN_IT_" + Name.ToUpperInvariant();
        if (Environment.GetEnvironmentVariable(prefix + "_URL") is { Length: > 0 } url)
        {
            BaseUrl = new Uri(url);
            User = Environment.GetEnvironmentVariable(prefix + "_USER") ?? User;
            Password = Environment.GetEnvironmentVariable(prefix + "_PASSWORD") ?? Password;
            return;
        }
        if (Environment.GetEnvironmentVariable(prefix + "_SKIP") == "1")
        {
            SkipReason = $"{Name} disabled via {prefix}_SKIP";
            return;
        }
        await StartContainerAsync();
    }

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
    }

    protected static string Image(string name, string fallback) =>
        Environment.GetEnvironmentVariable("TERMIN_IT_" + name.ToUpperInvariant() + "_IMAGE") ?? fallback;

    public HttpClient Http()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));
        return http;
    }

    /// <summary>Creates a calendar collection and returns its id.</summary>
    public async Task<string> CreateCalendarAsync(string id, string displayName, params string[] components)
    {
        using var http = Http();
        var comps = string.Join("", components.Select(c => $"<C:comp name=\"{c}\"/>"));
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:set><D:prop>
                <D:displayname>{displayName}</D:displayname>
                <C:supported-calendar-component-set>{comps}</C:supported-calendar-component-set>
              </D:prop></D:set>
            </C:mkcalendar>
            """;
        var req = new HttpRequestMessage(new HttpMethod("MKCALENDAR"), new Uri(CalendarHome, id + "/"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"MKCALENDAR on {Name} failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return id;
    }
}

public sealed class RadicaleServer : CalDavServer
{
    public override string Name => "radicale";
    public override string Provider => "radicale";
    protected override Uri CalendarHome => new(BaseUrl, $"/{User}/");

    protected override async Task StartContainerAsync()
    {
        const string config = """
            [server]
            hosts = 0.0.0.0:5232
            [auth]
            type = htpasswd
            htpasswd_filename = /config/users
            htpasswd_encryption = plain
            [storage]
            filesystem_folder = /data/collections
            """;
        Container = new ContainerBuilder(Image(Name, "tomsquest/docker-radicale:3.5.4.0"))
            .WithPortBinding(5232, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/config/config")
            .WithResourceMapping(Encoding.UTF8.GetBytes("test:test\n"), "/config/users")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(5232).ForPath("/.web/")))
            .Build();
        await Container.StartAsync();
        BaseUrl = new Uri($"http://{Container.Hostname}:{Container.GetMappedPublicPort(5232)}/");
    }
}

public sealed class NextcloudServer : CalDavServer
{
    public override string Name => "nextcloud";
    public override string Provider => "nextcloud";
    protected override Uri CalendarHome => new(BaseUrl, $"/remote.php/dav/calendars/{User}/");

    protected override async Task StartContainerAsync()
    {
        User = "admin";
        Password = "termin-it-Passw0rd!";
        Container = new ContainerBuilder(Image(Name, "nextcloud:31-apache"))
            .WithPortBinding(80, true)
            .WithEnvironment("SQLITE_DATABASE", "nextcloud")
            .WithEnvironment("NEXTCLOUD_ADMIN_USER", User)
            .WithEnvironment("NEXTCLOUD_ADMIN_PASSWORD", Password)
            .WithEnvironment("NEXTCLOUD_TRUSTED_DOMAINS", "localhost 127.0.0.1 *")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(80).ForPath("/status.php")
                .ForResponseMessageMatching(async m => (await m.Content.ReadAsStringAsync()).Contains("\"installed\":true")),
                o => o.WithTimeout(TimeSpan.FromMinutes(5))))
            .Build();
        await Container.StartAsync();
        // Brute-force protection would slow down the suite after the first 401s.
        await Container.ExecAsync(["su", "-s", "/bin/sh", "www-data", "-c", "php occ config:system:set auth.bruteforce.protection.enabled --value=false --type=boolean"]);
        BaseUrl = new Uri($"http://{Container.Hostname}:{Container.GetMappedPublicPort(80)}/remote.php/dav/");
    }
}

public sealed class BaikalServer : CalDavServer
{
    public override string Name => "baikal";
    public override string Provider => "baikal";
    protected override Uri CalendarHome => new(BaseUrl, $"calendars/{User}/");

    protected override async Task StartContainerAsync()
    {
        // Pre-configured instance: Basic auth, SQLite, one user test/test.
        var config = """
            system:
              configured_version: '0.10.1'
              timezone: 'Europe/Zurich'
              card_enabled: true
              cal_enabled: true
              invite_from: 'noreply@example.com'
              dav_auth_type: 'Basic'
              admin_passwordhash: 'x'
              failed_access_message: 'user %u authentication failure for Baikal'
              auth_realm: BaikalDAV
              base_uri: ''
            database:
              encryption_key: 'termin-integration-tests'
              backend: 'sqlite'
              sqlite_file: '/var/www/baikal/Specific/db/db.sqlite'
              mysql_host: ''
              mysql_dbname: ''
              mysql_username: ''
              mysql_password: ''
            """;
        Container = new ContainerBuilder(Image(Name, "ckulka/baikal:0.10.1-nginx"))
            .WithPortBinding(80, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/var/www/baikal/config/baikal.yaml")
            // Unconfigured, Baikal answers with a redirect to its installer; any answer means nginx and PHP are up.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/dav.php/").ForStatusCodeMatching(_ => true)))
            .Build();
        await Container.StartAsync();

        const string init = """
            $db = new PDO('sqlite:/var/www/baikal/Specific/db/db.sqlite');
            $db->exec(file_get_contents('/var/www/baikal/Core/Resources/Db/SQLite/db.sql'));
            $digest = md5('test:BaikalDAV:test');
            $db->exec("INSERT INTO users (username, digesta1) VALUES ('test', '$digest')");
            $db->exec("INSERT INTO principals (uri, email, displayname) VALUES ('principals/test', 'test@example.com', 'Test')");
            """;
        await Container.CopyAsync(Encoding.UTF8.GetBytes("<?php\n" + init), "/tmp/init.php");
        var result = await Container.ExecAsync(["sh", "-c",
            "mkdir -p /var/www/baikal/Specific/db && php /tmp/init.php && chown -R nginx:nginx /var/www/baikal/Specific /var/www/baikal/config"]);
        if (result.ExitCode != 0) throw new InvalidOperationException("Baikal init failed: " + result.Stderr + result.Stdout);
        BaseUrl = new Uri($"http://{Container.Hostname}:{Container.GetMappedPublicPort(80)}/dav.php/");

        using var probe = new HttpClient();
        for (var i = 0; i < 60; i++)
        {
            var res = await probe.GetAsync(BaseUrl);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) return;
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Baikal did not become ready.");
    }
}
