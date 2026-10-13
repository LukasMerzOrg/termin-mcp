using System.Diagnostics;
using ModelContextProtocol.Protocol;
using Termin.Calendar;
using Termin.Server.Auth;
using Termin.Server.Infrastructure;

namespace Termin.Server.Tools;

/// <summary>
/// Runs a tool body: checks the scope, maps errors to the structured error set,
/// logs one JSON line per call (no event content) and records metrics.
/// </summary>
public sealed class ToolRunner(IHttpContextAccessor http, MetricsRegistry metrics, ILogger<ToolRunner> log)
{
    public async Task<CallToolResult> Run(string tool, string? account, bool write, Func<Task<object>> body)
    {
        var sw = Stopwatch.StartNew();
        var code = "ok";
        try
        {
            var caller = http.HttpContext?.GetCaller();
            if (caller is null || !caller.CanRead || (write && !caller.CanWrite))
                throw new TerminException(ErrorCodes.Forbidden,
                    write ? "This connection is read-only (missing scope calendar.write)." : "Missing scope calendar.read.",
                    write ? "Reconnect the connector and allow write access." : null);

            var result = await body();
            return new CallToolResult { Content = [new TextContentBlock { Text = TerminJson.Serialize(result) }] };
        }
        catch (TerminException ex)
        {
            code = ex.Code;
            return Error(ex.Code, ex.Message, ex.Hint, ex.Current);
        }
        catch (OperationCanceledException)
        {
            code = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            code = "internal";
            log.LogError(ex, "Tool {Tool} failed.", tool);
            return Error(ErrorCodes.BackendUnavailable, "Unexpected server error.", "Try again; if it persists, check the Termin logs.", null);
        }
        finally
        {
            sw.Stop();
            metrics.ToolCall(tool, code, sw.Elapsed);
            log.LogInformation("tool={Tool} account={Account} result={Result} duration_ms={DurationMs}",
                tool, account ?? "(default)", code, (long)sw.Elapsed.TotalMilliseconds);
        }
    }

    private static CallToolResult Error(string code, string message, string? hint, object? current) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = TerminJson.Serialize(new { error = code, message, hint, current }) }],
    };
}
