namespace Termin.Server.Config;

/// <summary>Runtime configuration. Secrets and the public URL come from environment variables.</summary>
public sealed class TerminOptions
{
    public required Uri PublicUrl { get; init; }
    public required string SecretKey { get; init; }
    public string? AdminPasswordHash { get; init; }
    public string? StaticToken { get; init; }
    public required string DefaultTimeZone { get; init; }
    public required string DatabasePath { get; init; }
    public string? ConfigPath { get; init; }
    public bool Dev { get; init; }
    public bool Metrics { get; init; }
    public bool ForceClientExpand { get; init; }
    public int RateLimitPerMinute { get; init; } = 60;
    public IReadOnlyList<string> ExtraRedirectHosts { get; init; } = [];
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    public string? OidcIssuer { get; init; }

    /// <summary>The canonical MCP resource URL tokens are bound to (RFC 8707).</summary>
    public string ResourceUrl => PublicUrl.ToString().TrimEnd('/') + "/mcp";

    public string Issuer => PublicUrl.ToString().TrimEnd('/');

    public string Url(string path) => Issuer + path;

    public static TerminOptions FromEnvironment(Func<string, string?> env)
    {
        var dev = Flag(env("TERMIN_DEV"));
        var publicUrlText = env("TERMIN_PUBLIC_URL") ?? (dev ? "http://localhost:8080" : null)
            ?? throw new InvalidOperationException("TERMIN_PUBLIC_URL is required (the public HTTPS URL of this server).");
        if (!Uri.TryCreate(publicUrlText.TrimEnd('/'), UriKind.Absolute, out var publicUrl))
            throw new InvalidOperationException("TERMIN_PUBLIC_URL is not a valid absolute URL.");
        if (publicUrl.Scheme != Uri.UriSchemeHttps && !dev)
            throw new InvalidOperationException("TERMIN_PUBLIC_URL must use https. Set TERMIN_DEV=1 for local testing over http.");

        var secretKey = Secret(env, "TERMIN_SECRET_KEY")
            ?? throw new InvalidOperationException("TERMIN_SECRET_KEY (or TERMIN_SECRET_KEY_FILE) is required. Generate one with: termin gen-key");

        string? adminHash = Secret(env, "TERMIN_ADMIN_PASSWORD_HASH");
        var adminPlain = Secret(env, "TERMIN_ADMIN_PASSWORD");
        if (adminHash is null && adminPlain is not null)
            adminHash = Storage.PasswordHasher.Hash(adminPlain);

        var dataDir = env("TERMIN_DATA_DIR") ?? (Directory.Exists("/data") ? "/data" : Path.Combine(AppContext.BaseDirectory, "data"));
        var configPath = env("TERMIN_CONFIG");
        if (configPath is null)
        {
            foreach (var candidate in new[] { Path.Combine(dataDir, "termin.yaml"), "termin.yaml" })
                if (File.Exists(candidate)) { configPath = candidate; break; }
        }

        return new TerminOptions
        {
            PublicUrl = publicUrl,
            SecretKey = secretKey,
            AdminPasswordHash = adminHash,
            StaticToken = Secret(env, "TERMIN_STATIC_TOKEN"),
            DefaultTimeZone = env("TERMIN_DEFAULT_TZ") ?? env("TZ") ?? "UTC",
            DatabasePath = env("TERMIN_DB") ?? Path.Combine(dataDir, "termin.db"),
            ConfigPath = configPath,
            Dev = dev,
            Metrics = Flag(env("TERMIN_METRICS")),
            ForceClientExpand = Flag(env("TERMIN_FORCE_CLIENT_EXPAND")),
            RateLimitPerMinute = int.TryParse(env("TERMIN_RATE_LIMIT"), out var rl) && rl > 0 ? rl : 60,
            ExtraRedirectHosts = List(env("TERMIN_REDIRECT_HOSTS")),
            AllowedOrigins = List(env("TERMIN_ALLOWED_ORIGINS")),
            OidcIssuer = env("TERMIN_OIDC_ISSUER"),
        };
    }

    private static string? Secret(Func<string, string?> env, string name)
    {
        var value = env(name);
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        var file = env(name + "_FILE");
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) return File.ReadAllText(file).Trim();
        return null;
    }

    private static bool Flag(string? v) => v is "1" or "true" or "TRUE" or "yes";

    private static IReadOnlyList<string> List(string? v) =>
        string.IsNullOrWhiteSpace(v) ? [] : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
