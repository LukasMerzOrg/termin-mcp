using Microsoft.Data.Sqlite;

namespace Termin.Storage;

public sealed record StoredAccount(string Label, string BaseUrl, string Username, string Secret, string? Provider, bool IsDefault);

/// <summary>CalDAV accounts added on the setup page. Secrets are encrypted with AES-256-GCM.</summary>
public sealed class AccountStore(Database db, SecretBox box)
{
    public IReadOnlyList<StoredAccount> List()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT label, base_url, username, secret_enc, provider, is_default FROM accounts ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<StoredAccount>();
        while (r.Read())
        {
            var label = r.GetString(0);
            var secret = box.Decrypt((byte[])r["secret_enc"], "account:" + label);
            list.Add(new StoredAccount(label, r.GetString(1), r.GetString(2), secret, r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) == 1));
        }
        return list;
    }

    public void Upsert(StoredAccount account)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (account.IsDefault)
        {
            using var reset = c.CreateCommand();
            reset.Transaction = tx;
            reset.CommandText = "UPDATE accounts SET is_default = 0";
            reset.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO accounts (label, base_url, username, secret_enc, provider, is_default, created_at)
            VALUES ($label, $url, $user, $secret, $provider, $def, $now)
            ON CONFLICT(label) DO UPDATE SET base_url = $url, username = $user, secret_enc = $secret,
                provider = $provider, is_default = $def
            """;
        cmd.Parameters.AddWithValue("$label", account.Label);
        cmd.Parameters.AddWithValue("$url", account.BaseUrl);
        cmd.Parameters.AddWithValue("$user", account.Username);
        cmd.Parameters.Add("$secret", SqliteType.Blob).Value = box.Encrypt(account.Secret, "account:" + account.Label);
        cmd.Parameters.AddWithValue("$provider", (object?)account.Provider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$def", account.IsDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", Time.Now());
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public bool Delete(string label)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE label = $label";
        cmd.Parameters.AddWithValue("$label", label);
        return cmd.ExecuteNonQuery() > 0;
    }
}
