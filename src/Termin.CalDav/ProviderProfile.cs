namespace Termin.CalDav;

/// <summary>
/// Provider quirks as flags. Tool code never branches on the provider name;
/// it reads these flags.
/// </summary>
public sealed record ProviderProfile
{
    public string Name { get; init; } = "generic";

    /// <summary>Ask the server to expand recurrences (CalDAV &lt;expand&gt;). Unexpanded answers are still expanded client-side.</summary>
    public bool ServerExpand { get; init; } = true;

    /// <summary>Whether the backend stores VTODOs at all.</summary>
    public bool SupportsTodos { get; init; } = true;

    /// <summary>Always embed VTIMEZONE in written resources.</summary>
    public bool EmbedTimezones { get; init; } = true;

    /// <summary>Skip REPORT responses without calendar-data (Nextcloud trashbin).</summary>
    public bool RequireCalendarData { get; init; } = true;

    /// <summary>Follow principal redirects to other hosts of the same provider (iCloud partitions).</summary>
    public bool FollowCrossHostRedirects { get; init; }

    /// <summary>Hosts a redirect may lead to, as domain suffixes.</summary>
    public string[] RedirectDomains { get; init; } = [];

    public static readonly ProviderProfile Generic = new();

    public static readonly ProviderProfile ICloud = new()
    {
        Name = "icloud",
        FollowCrossHostRedirects = true,
        RedirectDomains = ["icloud.com"],
    };

    public static readonly ProviderProfile Google = new()
    {
        Name = "google",
        ServerExpand = false,
        SupportsTodos = false,
    };

    public static readonly ProviderProfile Nextcloud = new() { Name = "nextcloud" };

    public static readonly ProviderProfile Radicale = new() { Name = "radicale" };

    public static readonly ProviderProfile Baikal = new() { Name = "baikal" };

    public static readonly ProviderProfile Fastmail = new()
    {
        Name = "fastmail",
        EmbedTimezones = true,
        FollowCrossHostRedirects = true,
        RedirectDomains = ["fastmail.com"],
    };

    public static ProviderProfile For(string? provider, Uri baseUrl)
    {
        var name = provider?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name == "auto")
        {
            var host = baseUrl.Host.ToLowerInvariant();
            name = host switch
            {
                _ when host.EndsWith("icloud.com") => "icloud",
                _ when host.EndsWith("google.com") || host.EndsWith("googleapis.com") => "google",
                _ when host.EndsWith("fastmail.com") => "fastmail",
                _ when baseUrl.AbsolutePath.Contains("remote.php", StringComparison.OrdinalIgnoreCase) => "nextcloud",
                _ when baseUrl.AbsolutePath.Contains("dav.php", StringComparison.OrdinalIgnoreCase) => "baikal",
                _ => "generic",
            };
        }

        return name switch
        {
            "icloud" => ICloud,
            "google" => Google,
            "nextcloud" => Nextcloud,
            "radicale" => Radicale,
            "baikal" => Baikal,
            "fastmail" => Fastmail,
            _ => Generic,
        };
    }
}
