using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Termin.CalDav;
using Termin.Calendar;
using Termin.Server.Auth;
using Termin.Server.Config;
using Termin.Server.Services;
using Termin.Storage;
using static Termin.Server.Infrastructure.Html;

namespace Termin.Server.Setup;

/// <summary>
/// The minimal setup page: admin login, CalDAV accounts (add, test, remove),
/// connector URL, token revocation and the recent audit log.
/// </summary>
public static class SetupEndpoints
{
    public const string Scheme = "termin-setup";

    // Shared by the HTML pattern attribute (browsers compile it with the v flag, so "-" stays escaped)
    // and the server-side check.
    private const string LabelPattern = @"[A-Za-z0-9_\-]{1,32}";
    private const string LabelRule = "Letters, digits, - and _ only (max 32), e.g. nextcloud";

    public static void MapSetup(this WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/setup"));
        app.MapGet("/setup", (HttpContext ctx, TerminOptions o, AccountRegistry a, AuditStore au, OAuthStore oa, IAntiforgery af) =>
            Dashboard(ctx, o, a, au, oa, af));
        app.MapPost("/setup/login", Login);
        app.MapPost("/setup/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(Scheme);
            return Results.Redirect("/setup");
        });
        app.MapPost("/setup/accounts", AddAccount);
        app.MapPost("/setup/accounts/delete", DeleteAccount);
        app.MapPost("/setup/accounts/test", TestAccount);
        app.MapPost("/setup/revoke-tokens", RevokeTokens);
    }

    private static bool Authenticated(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true;

    private static IResult Dashboard(HttpContext ctx, TerminOptions options, AccountRegistry accounts, AuditStore audit, OAuthStore oauth, IAntiforgery af, string? msg = null, string? err = null)
    {
        var token = af.GetAndStoreTokens(ctx);
        var csrf = $"<input type=\"hidden\" name=\"{token.FormFieldName}\" value=\"{E(token.RequestToken)}\">";

        if (!Authenticated(ctx))
        {
            var note = options.AdminPasswordHash is null
                ? "<p class=\"err\">No admin password is configured. Set TERMIN_ADMIN_PASSWORD (or TERMIN_ADMIN_PASSWORD_HASH) and restart.</p>"
                : "";
            return Page("Setup", $"""
                <div class="card">
                  <h1>Termin setup</h1>
                  <p class="muted">Sign in with the admin password.</p>
                  {note}{(err is null ? "" : $"<p class=\"err\">{E(err)}</p>")}
                  <form method="post" action="/setup/login">{csrf}
                    <label for="password">Admin password</label>
                    <input id="password" type="password" name="password" autocomplete="current-password" autofocus required>
                    <button type="submit">Sign in</button>
                  </form>
                </div>
                """);
        }

        var rows = string.Join("", accounts.All.Select(a => $"""
            <tr>
              <td><strong>{E(a.Account.Label)}</strong>{(a.IsDefault ? " <span class=\"muted\">(default)</span>" : "")}</td>
              <td>{E(a.Account.BaseUrl.Host)}</td>
              <td>{E(a.Account.Username)}</td>
              <td>{E(a.Account.Profile.Name)}</td>
              <td class="muted">{E(a.Source)}</td>
              <td class="row">
                <form method="post" action="/setup/accounts/test">{csrf}<input type="hidden" name="label" value="{E(a.Account.Label)}"><button class="secondary" style="margin:0">Test</button></form>
                {(a.Source == "setup" ? $"<form method=\"post\" action=\"/setup/accounts/delete\" onsubmit=\"return confirm('Remove {E(a.Account.Label)}?')\">{csrf}<input type=\"hidden\" name=\"label\" value=\"{E(a.Account.Label)}\"><button class=\"danger\" style=\"margin:0\">Remove</button></form>" : "")}
              </td>
            </tr>
            """));

        var auditRows = string.Join("", audit.Recent(20).Select(x => $"""
            <tr><td class="muted">{x.At:yyyy-MM-dd HH:mm}</td><td>{E(x.Entry.Tool)}</td><td>{E(x.Entry.Account)}/{E(x.Entry.Calendar)}</td><td>{E(x.Entry.Scope)}</td><td>{E(x.Entry.Result)}</td></tr>
            """));

        var body = $"""
            <div class="card">
              <div class="row" style="justify-content:space-between">
                <h1>Termin</h1>
                <form method="post" action="/setup/logout">{csrf}<button class="secondary" style="margin:0">Sign out</button></form>
              </div>
              {(msg is null ? "" : $"<p>{E(msg)}</p>")}{(err is null ? "" : $"<p class=\"err\">{E(err)}</p>")}
              <p class="muted">Connector URL for claude.ai (Settings → Connectors → Add custom connector):</p>
              <p><code>{E(options.ResourceUrl)}</code></p>
              <p class="muted">Default timezone: {E(options.DefaultTimeZone)} · Active OAuth clients: {oauth.ActiveClientCount()}</p>
            </div>

            <div class="card">
              <h2>Calendar accounts</h2>
              {(accounts.All.Count == 0 ? "<p class=\"muted\">No accounts yet.</p>" : $"<table><tr><th>Label</th><th>Host</th><th>User</th><th>Profile</th><th>Source</th><th></th></tr>{rows}</table>")}
            </div>

            <div class="card">
              <h2>Add account</h2>
              <form method="post" action="/setup/accounts">{csrf}
                <label for="label">Label</label>
                <input id="label" type="text" name="label" placeholder="family" required pattern="{LabelPattern}" title="{LabelRule}">
                <label for="url">CalDAV URL</label>
                <input id="url" type="url" name="url" placeholder="https://cloud.example.com/remote.php/dav" required>
                <label for="username">Username</label>
                <input id="username" type="text" name="username" autocomplete="off" required>
                <label for="secret">Password or app-specific password</label>
                <input id="secret" type="password" name="secret" autocomplete="new-password" required>
                <label for="provider">Provider profile</label>
                <select id="provider" name="provider">
                  <option value="auto">Detect from URL</option><option>generic</option><option>nextcloud</option><option>radicale</option>
                  <option>baikal</option><option>fastmail</option><option>icloud</option>
                </select>
                <label><input type="checkbox" name="default"> Default account</label>
                <button type="submit">Test and save</button>
              </form>
              <p class="muted">iCloud: use https://caldav.icloud.com and an app-specific password. Fastmail: https://caldav.fastmail.com and an app password.</p>
            </div>

            <div class="card">
              <h2>Connections</h2>
              <p class="muted">Revoke every access and refresh token. Connected clients must authorize again.</p>
              <form method="post" action="/setup/revoke-tokens" onsubmit="return confirm('Revoke all tokens?')">{csrf}<button class="danger">Revoke all tokens</button></form>
            </div>

            <div class="card">
              <h2>Recent writes</h2>
              {(auditRows.Length == 0 ? "<p class=\"muted\">None yet.</p>" : $"<table>{auditRows}</table>")}
            </div>
            """;
        return Page("Setup", body);
    }

    private static async Task<IResult> Login(HttpContext ctx, TerminOptions options, LoginThrottle throttle, IAntiforgery af,
        AccountRegistry accounts, AuditStore audit, OAuthStore oauth, ILoggerFactory lf)
    {
        if (!await af.IsRequestValidAsync(ctx)) return Results.Redirect("/setup");
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsBlocked(ip))
            return Dashboard(ctx, options, accounts, audit, oauth, af, err: "Too many failed attempts. Try again in 15 minutes.");

        var form = await ctx.Request.ReadFormAsync();
        if (options.AdminPasswordHash is null || !PasswordHasher.Verify(form["password"].ToString(), options.AdminPasswordHash))
        {
            throttle.Fail(ip);
            lf.CreateLogger("Termin.Setup").LogWarning("Failed setup login from {Ip}.", ip);
            return Dashboard(ctx, options, accounts, audit, oauth, af, err: "Wrong password.");
        }
        throttle.Succeed(ip);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], Scheme);
        await ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity));
        return Results.Redirect("/setup");
    }

    private static async Task<IResult> AddAccount(HttpContext ctx, TerminOptions options, AccountRegistry accounts, AccountStore store,
        AuditStore audit, OAuthStore oauth, IAntiforgery af, IHttpClientFactory httpFactory)
    {
        if (!Authenticated(ctx) || !await af.IsRequestValidAsync(ctx)) return Results.Redirect("/setup");
        var form = await ctx.Request.ReadFormAsync();
        var label = form["label"].ToString().Trim();
        var url = form["url"].ToString().Trim();
        var username = form["username"].ToString().Trim();
        var secret = form["secret"].ToString();
        var provider = form["provider"].ToString();
        var isDefault = form["default"] == "on";

        IResult Fail(string message) => Dashboard(ctx, options, accounts, audit, oauth, af, err: message);
        if (!System.Text.RegularExpressions.Regex.IsMatch(label, $"^{LabelPattern}$")) return Fail(LabelRule + ".");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http")) return Fail("The URL is not valid.");
        if (username.Length == 0 || secret.Length == 0) return Fail("Username and password are required.");
        if (accounts.All.Any(a => a.Account.Label.Equals(label, StringComparison.OrdinalIgnoreCase) && a.Source == "config"))
            return Fail("An account with this label is defined in termin.yaml.");

        var account = new CalDavAccount(label, baseUrl, username, secret, ProviderProfile.For(provider, baseUrl));
        try
        {
            var calendars = await new CalDavClient(httpFactory.CreateClient("caldav"), account).GetCalendarsAsync(ctx.RequestAborted);
            store.Upsert(new StoredAccount(label, url, username, secret, provider is "auto" or "" ? null : provider, isDefault));
            accounts.Reload();
            return Dashboard(ctx, options, accounts, audit, oauth, af, msg: $"Saved '{label}': found {calendars.Count} calendar(s).");
        }
        catch (TerminException ex)
        {
            return Fail($"Connection test failed: {ex.Message}");
        }
    }

    private static async Task<IResult> TestAccount(HttpContext ctx, TerminOptions options, AccountRegistry accounts, AuditStore audit, OAuthStore oauth, IAntiforgery af)
    {
        if (!Authenticated(ctx) || !await af.IsRequestValidAsync(ctx)) return Results.Redirect("/setup");
        var form = await ctx.Request.ReadFormAsync();
        try
        {
            var entry = accounts.Resolve(form["label"].ToString());
            entry.Client.InvalidateDiscovery();
            var calendars = await entry.Client.GetCalendarsAsync(ctx.RequestAborted);
            var names = string.Join(", ", calendars.Select(c => c.Name ?? c.Id));
            return Dashboard(ctx, options, accounts, audit, oauth, af, msg: $"'{entry.Account.Label}' OK: {calendars.Count} calendar(s): {names}. Server-side expansion: {(entry.Client.UsesServerExpand ? "on" : "off")}.");
        }
        catch (TerminException ex)
        {
            return Dashboard(ctx, options, accounts, audit, oauth, af, err: ex.Message);
        }
    }

    private static async Task<IResult> DeleteAccount(HttpContext ctx, TerminOptions options, AccountRegistry accounts, AccountStore store, AuditStore audit, OAuthStore oauth, IAntiforgery af)
    {
        if (!Authenticated(ctx) || !await af.IsRequestValidAsync(ctx)) return Results.Redirect("/setup");
        var form = await ctx.Request.ReadFormAsync();
        var label = form["label"].ToString();
        var removed = store.Delete(label);
        accounts.Reload();
        return Dashboard(ctx, options, accounts, audit, oauth, af, msg: removed ? $"Removed '{label}'." : null, err: removed ? null : "Account not found.");
    }

    private static async Task<IResult> RevokeTokens(HttpContext ctx, TerminOptions options, AccountRegistry accounts, AuditStore audit, OAuthStore oauth, IAntiforgery af)
    {
        if (!Authenticated(ctx) || !await af.IsRequestValidAsync(ctx)) return Results.Redirect("/setup");
        var count = oauth.RevokeAll();
        return Dashboard(ctx, options, accounts, audit, oauth, af, msg: $"Revoked {count} token(s).");
    }
}
