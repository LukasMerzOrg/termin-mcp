using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Termin.Server.Config;
using Termin.Server.Infrastructure;
using Termin.Storage;
using static Termin.Server.Infrastructure.Html;

namespace Termin.Server.Auth;

/// <summary>
/// A minimal OAuth 2.1 authorization server for MCP clients: RFC 9728 and RFC 8414
/// metadata, RFC 7591 dynamic client registration, authorization code with PKCE
/// (S256 only), refresh token rotation and RFC 8707 audience binding.
/// </summary>
public static class OAuthEndpoints
{
    public sealed class RegistrationRequest
    {
        [JsonPropertyName("redirect_uris")] public List<string>? RedirectUris { get; set; }
        [JsonPropertyName("client_name")] public string? ClientName { get; set; }
        [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; set; }
        [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; set; }
        [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; set; }
    }

    public static void MapOAuth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<TerminOptions>();

        IResult ProtectedResource() => Results.Json(new Dictionary<string, object?>
        {
            ["resource"] = options.ResourceUrl,
            ["authorization_servers"] = new[] { options.Issuer },
            ["scopes_supported"] = Scopes.All,
            ["bearer_methods_supported"] = new[] { "header" },
            ["resource_name"] = "Termin",
            ["resource_documentation"] = "https://github.com/LukasMerzOrg/termin-mcp",
        });
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResource);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResource);

        IResult AuthServer() => Results.Json(new Dictionary<string, object?>
        {
            ["issuer"] = options.Issuer,
            ["authorization_endpoint"] = options.Url("/authorize"),
            ["token_endpoint"] = options.Url("/token"),
            ["registration_endpoint"] = options.Url("/register"),
            ["revocation_endpoint"] = options.Url("/revoke"),
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["revocation_endpoint_auth_methods_supported"] = new[] { "none" },
            ["scopes_supported"] = Scopes.All,
            ["authorization_response_iss_parameter_supported"] = true,
        });
        app.MapGet("/.well-known/oauth-authorization-server", AuthServer);
        app.MapGet("/.well-known/oauth-authorization-server/mcp", AuthServer);
        app.MapGet("/.well-known/openid-configuration", AuthServer);

        app.MapPost("/register", Register).DisableAntiforgery();
        app.MapGet("/authorize", AuthorizeGet);
        app.MapPost("/authorize", AuthorizePost).DisableAntiforgery();
        app.MapPost("/token", Token).DisableAntiforgery();
        app.MapPost("/revoke", Revoke).DisableAntiforgery();
    }

    // ------------------------------------------------------------------ registration

    private static IResult Register(RegistrationRequest req, OAuthStore store, TerminOptions options, ILoggerFactory lf)
    {
        var uris = req.RedirectUris ?? [];
        if (uris.Count == 0)
            return RegistrationError("invalid_redirect_uri", "redirect_uris is required.");
        foreach (var uri in uris)
            if (!RedirectAllowed(uri, options))
                return RegistrationError("invalid_redirect_uri", $"Redirect URI not allowed: {uri}");
        if (req.TokenEndpointAuthMethod is not (null or "none"))
            return RegistrationError("invalid_client_metadata", "Only public clients (token_endpoint_auth_method=none) are supported.");

        var name = req.ClientName is { Length: > 100 } n ? n[..100] : req.ClientName;
        var client = store.RegisterClient(name, uris);
        lf.CreateLogger("Termin.OAuth").LogInformation("Registered OAuth client {ClientId} ({ClientName}).", client.ClientId, name);
        return Results.Json(new Dictionary<string, object?>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = client.CreatedAt.ToUnixTimeSeconds(),
            ["client_name"] = client.ClientName,
            ["redirect_uris"] = client.RedirectUris,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["scope"] = string.Join(' ', Scopes.All),
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: 400);

    /// <summary>claude.ai / claude.com callbacks over HTTPS, loopback over HTTP (Desktop, Code), plus configured hosts.</summary>
    public static bool RedirectAllowed(string uri, TerminOptions options)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment)) return false;
        var host = u.Host.ToLowerInvariant();
        if (u.Scheme == Uri.UriSchemeHttp)
            return host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        var allowed = new List<string> { "claude.ai", "claude.com" };
        allowed.AddRange(options.ExtraRedirectHosts);
        return allowed.Any(h => host == h.ToLowerInvariant());
    }

    private static bool RedirectMatches(string requested, IReadOnlyList<string> registered)
    {
        if (registered.Contains(requested)) return true;
        // RFC 8252 §7.3: loopback redirects may use any port.
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var r) || !r.IsLoopback) return false;
        return registered.Any(x => Uri.TryCreate(x, UriKind.Absolute, out var u) && u.IsLoopback &&
                                   u.Scheme == r.Scheme && u.Host == r.Host && u.AbsolutePath == r.AbsolutePath);
    }

    // ------------------------------------------------------------------ authorize

    private sealed record AuthRequest(
        string ClientId, string RedirectUri, string CodeChallenge, string Scope, string? State, string? Resource, string? ClientName);

    private static (AuthRequest? Request, IResult? Error) Validate(IQueryCollection? q, IFormCollection? f, OAuthStore store, TerminOptions options)
    {
        string? Get(string key) => (f is not null ? f[key].ToString() : q![key].ToString()) is { Length: > 0 } v ? v : null;

        var clientId = Get("client_id");
        var redirectUri = Get("redirect_uri");
        var client = clientId is null ? null : store.FindClient(clientId);
        if (client is null)
            return (null, ErrorPage("Unknown client", "This application is not registered with Termin. Remove and re-add the connector."));
        if (redirectUri is null || !RedirectMatches(redirectUri, client.RedirectUris))
            return (null, ErrorPage("Invalid redirect", "The redirect URI does not match the registered client."));

        var state = Get("state");
        IResult Fail(string error, string description) => Results.Redirect(AppendQuery(redirectUri, new()
        {
            ["error"] = error, ["error_description"] = description, ["state"] = state, ["iss"] = options.Issuer,
        }));

        if (Get("response_type") != "code") return (null, Fail("unsupported_response_type", "Only response_type=code is supported."));
        var challenge = Get("code_challenge");
        if (challenge is null || challenge.Length < 43) return (null, Fail("invalid_request", "PKCE code_challenge is required."));
        if (Get("code_challenge_method") != "S256") return (null, Fail("invalid_request", "code_challenge_method must be S256."));

        var resource = Get("resource");
        if (resource is not null && resource.TrimEnd('/') != options.ResourceUrl && resource.TrimEnd('/') != options.Issuer)
            return (null, Fail("invalid_target", "Unknown resource."));

        return (new AuthRequest(client.ClientId, redirectUri, challenge, Scopes.Normalize(Get("scope")), state, resource, client.ClientName), null);
    }

    private static IResult AuthorizeGet(HttpContext ctx, OAuthStore store, TerminOptions options, IAntiforgery af)
    {
        var (req, error) = Validate(ctx.Request.Query, null, store, options);
        if (error is not null) return error;
        return ConsentPage(ctx, req!, options, af, null);
    }

    private static async Task<IResult> AuthorizePost(HttpContext ctx, OAuthStore store, TerminOptions options, IAntiforgery af, LoginThrottle throttle, ILoggerFactory lf)
    {
        var log = lf.CreateLogger("Termin.OAuth");
        if (!await af.IsRequestValidAsync(ctx)) return ErrorPage("Session expired", "Reload the page and try again.", 400);
        var form = await ctx.Request.ReadFormAsync();
        var (req, error) = Validate(null, form, store, options);
        if (error is not null) return error;

        if (form["action"] == "deny")
            return Results.Redirect(AppendQuery(req!.RedirectUri, new()
            {
                ["error"] = "access_denied", ["error_description"] = "The user denied access.", ["state"] = req.State, ["iss"] = options.Issuer,
            }));

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsBlocked(ip))
            return ConsentPage(ctx, req!, options, af, "Too many failed attempts. Try again in 15 minutes.", 429);

        var password = form["password"].ToString();
        if (options.AdminPasswordHash is null || !PasswordHasher.Verify(password, options.AdminPasswordHash))
        {
            throttle.Fail(ip);
            log.LogWarning("Failed consent login from {Ip}.", ip);
            return ConsentPage(ctx, req!, options, af, "Wrong password.", 401);
        }
        throttle.Succeed(ip);

        var scope = req!.Scope;
        if (form["allow_write"] != "on") scope = Scopes.Read;
        var code = store.CreateCode(new AuthCode(req.ClientId, req.RedirectUri, req.CodeChallenge, scope, req.Resource ?? options.ResourceUrl, "admin"));
        log.LogInformation("Authorized client {ClientId} with scope {Scope}.", req.ClientId, scope);
        return Results.Redirect(AppendQuery(req.RedirectUri, new() { ["code"] = code, ["state"] = req.State, ["iss"] = options.Issuer }));
    }

    private static IResult ConsentPage(HttpContext ctx, AuthRequest req, TerminOptions options, IAntiforgery af, string? error, int status = 200)
    {
        var tokens = af.GetAndStoreTokens(ctx);
        var wantsWrite = req.Scope.Contains(Scopes.Write);
        string Hidden(string name, string? value) => value is null ? "" : $"<input type=\"hidden\" name=\"{name}\" value=\"{E(value)}\">";

        var configured = options.AdminPasswordHash is not null;
        var body = $"""
            <div class="card">
              <h1>Connect to Termin</h1>
              <p class="muted"><strong>{E(req.ClientName ?? "An MCP client")}</strong> wants access to your calendars on <code>{E(options.PublicUrl.Host)}</code>.</p>
              {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
              {(configured ? "" : "<p class=\"err\">No admin password is configured. Set TERMIN_ADMIN_PASSWORD and restart Termin.</p>")}
              <form method="post" action="/authorize">
                <input type="hidden" name="{tokens.FormFieldName}" value="{E(tokens.RequestToken)}">
                {Hidden("client_id", req.ClientId)}{Hidden("redirect_uri", req.RedirectUri)}{Hidden("response_type", "code")}
                {Hidden("code_challenge", req.CodeChallenge)}{Hidden("code_challenge_method", "S256")}{Hidden("scope", req.Scope)}
                {Hidden("state", req.State)}{Hidden("resource", req.Resource)}
                <p>It will be able to:</p>
                <ul>
                  <li>Read calendars, events and todos</li>
                  {(wantsWrite ? "<li><label style=\"display:inline\"><input type=\"checkbox\" name=\"allow_write\" checked> Create, change and delete events and todos</label></li>" : "")}
                </ul>
                <label for="password">Termin admin password</label>
                <input id="password" type="password" name="password" autocomplete="current-password" autofocus required>
                <div class="row">
                  <button type="submit" name="action" value="allow">Allow</button>
                  <button type="submit" name="action" value="deny" class="secondary" formnovalidate>Deny</button>
                </div>
              </form>
            </div>
            <p class="muted">Redirects to {E(new Uri(req.RedirectUri).Host)} after you allow access.</p>
            """;
        return Page("Authorize", body, status);
    }

    private static IResult ErrorPage(string title, string message, int status = 400) =>
        Page(title, $"<div class=\"card\"><h1>{E(title)}</h1><p>{E(message)}</p></div>", status);

    // ------------------------------------------------------------------ token

    private static async Task<IResult> Token(HttpContext ctx, OAuthStore store, TerminOptions options, ILoggerFactory lf)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
        if (!ctx.Request.HasFormContentType) return TokenError("invalid_request", "Use application/x-www-form-urlencoded.");
        var form = await ctx.Request.ReadFormAsync();
        string? Get(string k) => form[k].ToString() is { Length: > 0 } v ? v : null;

        var clientId = Get("client_id");
        if (clientId is null || store.FindClient(clientId) is null) return TokenError("invalid_client", "Unknown client.", 401);

        switch (Get("grant_type"))
        {
            case "authorization_code":
            {
                var code = Get("code");
                var verifier = Get("code_verifier");
                var redirect = Get("redirect_uri");
                if (code is null || verifier is null) return TokenError("invalid_request", "code and code_verifier are required.");
                var grant = store.ConsumeCode(code);
                if (grant is null || grant.ClientId != clientId) return TokenError("invalid_grant", "The code is invalid, expired or already used.");
                if (redirect is not null && redirect != grant.RedirectUri) return TokenError("invalid_grant", "redirect_uri does not match.");
                if (!Tokens.VerifyPkce(verifier, grant.CodeChallenge)) return TokenError("invalid_grant", "PKCE verification failed.");
                var resource = Get("resource");
                if (resource is not null && resource.TrimEnd('/') != options.ResourceUrl && resource.TrimEnd('/') != options.Issuer)
                    return TokenError("invalid_target", "Unknown resource.");
                var issued = store.IssueTokens(clientId, grant.Scope, grant.Resource ?? options.ResourceUrl, grant.Subject);
                return TokenResponse(issued);
            }
            case "refresh_token":
            {
                var refresh = Get("refresh_token");
                if (refresh is null) return TokenError("invalid_request", "refresh_token is required.");
                var (outcome, issued) = store.Refresh(refresh, clientId, Get("scope"));
                if (outcome == OAuthStore.RefreshOutcome.Reused)
                    lf.CreateLogger("Termin.OAuth").LogWarning("Refresh token reuse detected for client {ClientId}; token family revoked.", clientId);
                return issued is null ? TokenError("invalid_grant", "The refresh token is invalid or expired.") : TokenResponse(issued);
            }
            default:
                return TokenError("unsupported_grant_type", "Use authorization_code or refresh_token.");
        }
    }

    private static IResult TokenResponse(IssuedTokens t) => Results.Json(new Dictionary<string, object>
    {
        ["access_token"] = t.AccessToken,
        ["token_type"] = "Bearer",
        ["expires_in"] = t.ExpiresIn,
        ["refresh_token"] = t.RefreshToken,
        ["scope"] = t.Scope,
    });

    private static IResult TokenError(string error, string description, int status = 400) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    private static async Task<IResult> Revoke(HttpContext ctx, OAuthStore store)
    {
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync();
            if (form["token"].ToString() is { Length: > 0 } token) store.Revoke(token);
        }
        return Results.Ok(); // RFC 7009: always 200
    }

    private static string AppendQuery(string uri, Dictionary<string, string?> values) =>
        QueryHelpers.AddQueryString(uri, values.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));
}
