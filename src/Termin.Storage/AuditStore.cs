namespace Termin.Storage;

public sealed record AuditEntry(string Tool, string? Account, string? Calendar, string? Uid, string? Scope, string Result, string? ClientId);

/// <summary>Write-tool audit log with 90-day retention.</summary>
public sealed class AuditStore(Database db)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public void Write(AuditEntry e)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit (at, tool, account, calendar, uid, scope, result, client_id)
            VALUES ($at, $tool, $account, $calendar, $uid, $scope, $result, $client)
            """;
        cmd.Parameters.AddWithValue("$at", Time.Now());
        cmd.Parameters.AddWithValue("$tool", e.Tool);
        cmd.Parameters.AddWithValue("$account", (object?)e.Account ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$calendar", (object?)e.Calendar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", (object?)e.Uid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", (object?)e.Scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$result", e.Result);
        cmd.Parameters.AddWithValue("$client", (object?)e.ClientId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(DateTimeOffset At, AuditEntry Entry)> Recent(int limit = 50)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, tool, account, calendar, uid, scope, result, client_id FROM audit ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTimeOffset, AuditEntry)>();
        while (r.Read())
            list.Add((Time.Parse(r.GetString(0)), new AuditEntry(r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), r.GetString(6), Str(r, 7))));
        return list;

        static string? Str(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    }

    public int Prune()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM audit WHERE at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", Time.Format(DateTimeOffset.UtcNow - Retention));
        return cmd.ExecuteNonQuery();
    }
}
