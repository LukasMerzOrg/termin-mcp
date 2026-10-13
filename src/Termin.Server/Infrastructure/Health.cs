using Termin.Calendar;
using Termin.Server.Services;
using Termin.Storage;

namespace Termin.Server.Infrastructure;

/// <summary>/healthz (process) and /readyz (DB plus one PROPFIND per account, cached 60 s).</summary>
public sealed class ReadinessProbe(Database db, AccountRegistry accounts)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset At, bool Ok, object Body)? _cached;

    public async Task<(bool Ok, object Body)> CheckAsync(CancellationToken ct)
    {
        if (_cached is { } c && DateTimeOffset.UtcNow - c.At < CacheFor) return (c.Ok, c.Body);
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } again && DateTimeOffset.UtcNow - again.At < CacheFor) return (again.Ok, again.Body);
            var dbOk = db.Ping();
            var checks = await Task.WhenAll(accounts.All.Select(async a =>
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await a.Client.PingAsync(timeout.Token);
                    return new { account = a.Account.Label, status = "ok" };
                }
                catch (TerminException ex)
                {
                    return new { account = a.Account.Label, status = ex.Code };
                }
                catch (OperationCanceledException)
                {
                    return new { account = a.Account.Label, status = "timeout" };
                }
            }));
            var ok = dbOk && checks.All(x => x.status == "ok");
            var body = new { status = ok ? "ready" : "degraded", database = dbOk ? "ok" : "error", accounts = checks };
            _cached = (DateTimeOffset.UtcNow, ok, body);
            return (ok, body);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Hourly cleanup: expired OAuth codes and tokens, audit entries past retention.</summary>
public sealed class MaintenanceService(OAuthStore oauth, AuditStore audit, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                oauth.Cleanup();
                var pruned = audit.Prune();
                if (pruned > 0) log.LogInformation("Pruned {Count} audit entries older than 90 days.", pruned);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Maintenance failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
