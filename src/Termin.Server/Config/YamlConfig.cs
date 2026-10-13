using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Termin.Server.Config;

/// <summary>Optional termin.yaml: CalDAV accounts and defaults.</summary>
public sealed class YamlConfig
{
    public string? DefaultTimezone { get; set; }
    public List<YamlAccount> Accounts { get; set; } = [];

    public sealed class YamlAccount
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
        public string Username { get; set; } = "";
        public string? Secret { get; set; }
        public string? SecretEnv { get; set; }
        public string? SecretFile { get; set; }
        public string? Provider { get; set; }
        public bool Default { get; set; }
    }

    public static YamlConfig Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new YamlConfig();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<YamlConfig>(File.ReadAllText(path)) ?? new YamlConfig();
    }

    public static string? ResolveSecret(YamlAccount a)
    {
        if (!string.IsNullOrEmpty(a.Secret)) return a.Secret;
        if (!string.IsNullOrEmpty(a.SecretEnv)) return Environment.GetEnvironmentVariable(a.SecretEnv);
        if (!string.IsNullOrEmpty(a.SecretFile) && File.Exists(a.SecretFile)) return File.ReadAllText(a.SecretFile).Trim();
        return null;
    }
}
