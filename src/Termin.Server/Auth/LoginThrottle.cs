using System.Collections.Concurrent;

namespace Termin.Server.Auth;

/// <summary>At most 10 failed logins per 15 minutes per IP.</summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();
    private readonly TimeProvider _time;

    public LoginThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsBlocked(string ip)
    {
        if (!_failures.TryGetValue(ip, out var q)) return false;
        lock (q)
        {
            Trim(q);
            return q.Count >= MaxFailures;
        }
    }

    public void Fail(string ip)
    {
        var q = _failures.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            Trim(q);
            q.Enqueue(_time.GetUtcNow());
        }
    }

    public void Succeed(string ip) => _failures.TryRemove(ip, out _);

    private void Trim(Queue<DateTimeOffset> q)
    {
        var cutoff = _time.GetUtcNow() - Window;
        while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
    }
}
