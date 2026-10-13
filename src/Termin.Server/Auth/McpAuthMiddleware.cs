using Termin.Server.Config;
using Termin.Storage;

namespace Termin.Server.Auth;

/// <summary>
/// Guards the MCP endpoint: validates the Origin header (DNS-rebinding guard)
/// and the bearer token. Unauthenticated requests get a 401 with the RFC 9728
/// resource_metadata pointer so clients can start the OAuth flow.
/// </summary>
public sealed class McpAuthMiddleware(RequestDelegate next, TerminOptions options, OAuthStore store, ILogger<McpAuthMiddleware> log)
{
    private readonly HashSet<string> _origins = BuildOrigins(options);

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Headers.Origin is { Count: > 0 } origin && !_origins.Contains(origin.ToString().TrimEnd('/')))
        {
            log.LogWarning("Rejected MCP request from origin {Origin}.", origin.ToString());
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "forbidden", message = "Origin not allowed." });
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await Challenge(ctx, null);
            return;
        }
        var token = header[7..].Trim();

        if (options.StaticToken is { Length: > 0 } st && Tokens.FixedEquals(token, st))
        {
            ctx.SetCaller(new Caller("static-token", "admin", Scopes.All.ToHashSet()));
            await next(ctx);
            return;
        }

        var grant = store.ValidateAccessToken(token);
        if (grant is null || !ResourceMatches(grant.Resource))
        {
            await Challenge(ctx, "invalid_token");
            return;
        }

        ctx.SetCaller(new Caller(grant.ClientId, grant.Subject, grant.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet()));
        await next(ctx);
    }

    private bool ResourceMatches(string? resource) =>
        resource is null
        || resource.TrimEnd('/') == options.ResourceUrl.TrimEnd('/')
        || resource.TrimEnd('/') == options.Issuer;

    private Task Challenge(HttpContext ctx, string? error)
    {
        var metadata = options.Url("/.well-known/oauth-protected-resource");
        var value = $"Bearer resource_metadata=\"{metadata}\", scope=\"{string.Join(' ', Scopes.All)}\"";
        if (error is not null) value += $", error=\"{error}\"";
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = value;
        return ctx.Response.WriteAsJsonAsync(new { error = error ?? "unauthorized", message = "A valid bearer token is required." });
    }

    private static HashSet<string> BuildOrigins(TerminOptions o)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            o.PublicUrl.GetLeftPart(UriPartial.Authority),
            "https://claude.ai",
            "https://claude.com",
            "https://www.claude.ai",
        };
        foreach (var x in o.AllowedOrigins) set.Add(x.TrimEnd('/'));
        if (o.Dev)
        {
            set.Add("http://localhost:6274"); // MCP Inspector
            set.Add("http://127.0.0.1:6274");
        }
        return set;
    }
}
