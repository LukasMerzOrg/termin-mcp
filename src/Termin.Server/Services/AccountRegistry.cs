using Termin.CalDav;
using Termin.Calendar;
using Termin.Server.Config;
using Termin.Storage;

namespace Termin.Server.Services;

public sealed record AccountEntry(CalDavAccount Account, CalDavClient Client, bool IsDefault, string Source);

/// <summary>
/// All configured CalDAV accounts (termin.yaml plus setup page), each with a
/// long-lived client that caches discovery.
/// </summary>
public sealed class AccountRegistry
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AccountStore _store;
    private readonly TerminOptions _options;
    private readonly ILogger<AccountRegistry> _log;
    private readonly object _gate = new();
    private IReadOnlyList<AccountEntry> _entries = [];

    public AccountRegistry(IHttpClientFactory httpFactory, AccountStore store, TerminOptions options, ILogger<AccountRegistry> log)
    {
        _httpFactory = httpFactory;
        _store = store;
        _options = options;
        _log = log;
        Reload();
    }

    public IReadOnlyList<AccountEntry> All => _entries;

    public void Reload()
    {
        var list = new List<AccountEntry>();
        var yaml = YamlConfig.Load(_options.ConfigPath);
        foreach (var a in yaml.Accounts)
        {
            var secret = YamlConfig.ResolveSecret(a);
            if (string.IsNullOrWhiteSpace(a.Label) || string.IsNullOrWhiteSpace(a.Url) || secret is null)
            {
                _log.LogWarning("Skipping account {Label} from termin.yaml: label, url and a secret are required.", a.Label);
                continue;
            }
            Add(list, a.Label, a.Url, a.Username, secret, a.Provider, a.Default, "config");
        }

        foreach (var a in _store.List())
        {
            if (list.Any(e => e.Account.Label.Equals(a.Label, StringComparison.OrdinalIgnoreCase)))
            {
                _log.LogWarning("Account {Label} exists in termin.yaml and in the database; the termin.yaml entry wins.", a.Label);
                continue;
            }
            Add(list, a.Label, a.BaseUrl, a.Username, a.Secret, a.Provider, a.IsDefault, "setup");
        }

        if (list.Count > 0 && !list.Any(e => e.IsDefault))
            list[0] = list[0] with { IsDefault = true };
        else if (list.Count(e => e.IsDefault) > 1)
        {
            var first = list.FindIndex(e => e.IsDefault);
            for (var i = 0; i < list.Count; i++) if (i != first && list[i].IsDefault) list[i] = list[i] with { IsDefault = false };
        }

        lock (_gate) _entries = list;
        _log.LogInformation("Loaded {Count} CalDAV account(s).", list.Count);
    }

    private void Add(List<AccountEntry> list, string label, string url, string username, string secret, string? provider, bool isDefault, string source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            _log.LogWarning("Skipping account {Label}: invalid URL.", label);
            return;
        }
        if (baseUrl.Scheme == Uri.UriSchemeHttp && !_options.Dev && !IsPrivateHost(baseUrl))
            _log.LogWarning("Account {Label} uses plain http to a public host; credentials travel unencrypted.", label);

        var account = new CalDavAccount(label, baseUrl, username, secret, ProviderProfile.For(provider, baseUrl), _options.ForceClientExpand);
        list.Add(new AccountEntry(account, new CalDavClient(_httpFactory.CreateClient("caldav"), account), isDefault, source));
    }

    public AccountEntry Resolve(string? label)
    {
        var entries = _entries;
        if (entries.Count == 0)
            throw new TerminException(ErrorCodes.NotFound, "No calendar account is configured.", "Add one on the server's /setup page.");
        if (string.IsNullOrWhiteSpace(label)) return entries.First(e => e.IsDefault);
        return entries.FirstOrDefault(e => e.Account.Label.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw TerminException.NotFound($"Unknown account '{label}'.", "Known accounts: " + string.Join(", ", entries.Select(e => e.Account.Label)));
    }

    private static bool IsPrivateHost(Uri u) =>
        u.IsLoopback || u.Host.EndsWith(".local") || u.Host.EndsWith(".internal") || !u.Host.Contains('.') ||
        (System.Net.IPAddress.TryParse(u.Host, out var ip) && (ip.ToString().StartsWith("10.") || ip.ToString().StartsWith("192.168.") || ip.ToString().StartsWith("172.")));
}
