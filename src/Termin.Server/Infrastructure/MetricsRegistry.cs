using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Termin.Server.Infrastructure;

/// <summary>Minimal Prometheus text exposition: tool latency and backend status counters.</summary>
public sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<(string Tool, string Result), long> _calls = new();
    private readonly ConcurrentDictionary<string, (double Sum, long Count)> _latency = new();
    private readonly ConcurrentDictionary<(string Account, string Status), long> _backend = new();

    public void ToolCall(string tool, string result, TimeSpan duration)
    {
        _calls.AddOrUpdate((tool, result), 1, (_, v) => v + 1);
        _latency.AddOrUpdate(tool, (duration.TotalSeconds, 1), (_, v) => (v.Sum + duration.TotalSeconds, v.Count + 1));
    }

    public void Backend(string account, int status)
    {
        var bucket = status == 0 ? "error" : $"{status / 100}xx";
        _backend.AddOrUpdate((account, bucket), 1, (_, v) => v + 1);
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP termin_tool_calls_total Tool calls by result code.");
        sb.AppendLine("# TYPE termin_tool_calls_total counter");
        foreach (var ((tool, result), v) in _calls.OrderBy(k => k.Key.Tool))
            sb.AppendLine(CultureInfo.InvariantCulture, $"termin_tool_calls_total{{tool=\"{tool}\",result=\"{result}\"}} {v}");

        sb.AppendLine("# HELP termin_tool_duration_seconds Tool call latency.");
        sb.AppendLine("# TYPE termin_tool_duration_seconds summary");
        foreach (var (tool, (sum, count)) in _latency.OrderBy(k => k.Key))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"termin_tool_duration_seconds_sum{{tool=\"{tool}\"}} {sum:0.######}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"termin_tool_duration_seconds_count{{tool=\"{tool}\"}} {count}");
        }

        sb.AppendLine("# HELP termin_backend_responses_total CalDAV responses by status class.");
        sb.AppendLine("# TYPE termin_backend_responses_total counter");
        foreach (var ((account, status), v) in _backend.OrderBy(k => k.Key.Account))
            sb.AppendLine(CultureInfo.InvariantCulture, $"termin_backend_responses_total{{account=\"{account}\",status=\"{status}\"}} {v}");
        return sb.ToString();
    }
}
