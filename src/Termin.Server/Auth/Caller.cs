namespace Termin.Server.Auth;

public static class Scopes
{
    public const string Read = "calendar.read";
    public const string Write = "calendar.write";
    public static readonly string[] All = [Read, Write];

    /// <summary>Normalizes a requested scope string; empty means everything.</summary>
    public static string Normalize(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return string.Join(' ', All);
        var asked = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(All.Contains).Distinct().ToList();
        if (asked.Contains(Write) && !asked.Contains(Read)) asked.Insert(0, Read);
        return asked.Count == 0 ? Read : string.Join(' ', asked);
    }
}

/// <summary>The authenticated caller of an MCP request.</summary>
public sealed record Caller(string ClientId, string Subject, IReadOnlySet<string> Scopes)
{
    public bool CanRead => Scopes.Contains(Auth.Scopes.Read);
    public bool CanWrite => Scopes.Contains(Auth.Scopes.Write);
}

public static class CallerExtensions
{
    private const string Key = "termin.caller";

    public static Caller? GetCaller(this HttpContext ctx) => ctx.Items.TryGetValue(Key, out var c) ? c as Caller : null;

    public static void SetCaller(this HttpContext ctx, Caller caller) => ctx.Items[Key] = caller;
}
