using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Termin.Calendar;
using Termin.Server.Auth;
using Termin.Server.Config;
using Termin.Server.Services;
using Termin.Storage;

namespace Termin.Integration;

/// <summary>The real CalendarService wired to one CalDAV server, as the MCP tools use it.</summary>
public sealed class Harness : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "termin-it-" + Guid.NewGuid().ToString("N"));

    public CalendarService Service { get; }
    public AccountRegistry Accounts { get; }

    public Harness(CalDavServer server, bool forceClientExpand)
    {
        Directory.CreateDirectory(_dir);
        var yaml = Path.Combine(_dir, "termin.yaml");
        File.WriteAllText(yaml, $"""
            accounts:
              - label: it
                url: {server.BaseUrl}
                username: {server.User}
                secret: "{server.Password}"
                provider: {server.Provider}
            """);
        var options = new TerminOptions
        {
            PublicUrl = new Uri("http://localhost:8080"),
            SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            DefaultTimeZone = "Europe/Zurich",
            DatabasePath = Path.Combine(_dir, "termin.db"),
            ConfigPath = yaml,
            Dev = true,
            ForceClientExpand = forceClientExpand,
        };
        var db = new Database(options.DatabasePath);
        var box = SecretBox.FromConfig(options.SecretKey, out _);
        Accounts = new AccountRegistry(new Factory(), new AccountStore(db, box), options, NullLogger<AccountRegistry>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.SetCaller(new Caller("it", "admin", Scopes.All.ToHashSet()));
        Service = new CalendarService(Accounts, new AuditStore(db), options, new HttpContextAccessor { HttpContext = ctx }, NullLogger<CalendarService>.Instance);
    }

    public static async Task<JsonElement> Json(Task<object> call) =>
        JsonDocument.Parse(TerminJson.Serialize(await call)).RootElement;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
