namespace Termin.Storage;

public sealed record OAuthClient(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, DateTimeOffset CreatedAt);

public sealed record AuthCode(string ClientId, string RedirectUri, string CodeChallenge, string Scope, string? Resource, string Subject);

public sealed record AccessGrant(string ClientId, string Scope, string? Resource, string Subject, DateTimeOffset ExpiresAt);

public sealed record IssuedTokens(string AccessToken, string RefreshToken, int ExpiresIn, string Scope);

/// <summary>
/// OAuth 2.1 state: registered clients, one-time authorization codes and opaque
/// tokens (stored as SHA-256 hashes). Refresh tokens rotate; reusing a rotated
/// refresh token revokes the whole token family.
/// </summary>
public sealed class OAuthStore(Database db)
{
    public static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AccessTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(30);

    public OAuthClient RegisterClient(string? name, IReadOnlyList<string> redirectUris)
    {
        var client = new OAuthClient("termin_" + Tokens.New()[..24], name, redirectUris, DateTimeOffset.UtcNow);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO oauth_clients (client_id, client_name, redirect_uris, created_at) VALUES ($id, $name, $uris, $now)";
        cmd.Parameters.AddWithValue("$id", client.ClientId);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uris", string.Join('\n', redirectUris));
        cmd.Parameters.AddWithValue("$now", Time.Format(client.CreatedAt));
        cmd.ExecuteNonQuery();
        return client;
    }

    public OAuthClient? FindClient(string clientId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT client_id, client_name, redirect_uris, created_at FROM oauth_clients WHERE client_id = $id";
        cmd.Parameters.AddWithValue("$id", clientId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new OAuthClient(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
            r.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries), Time.Parse(r.GetString(3)));
    }

    public string CreateCode(AuthCode code)
    {
        var raw = Tokens.New("tc_");
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO oauth_codes (code_hash, client_id, redirect_uri, code_challenge, scope, resource, subject, expires_at)
            VALUES ($h, $client, $redirect, $challenge, $scope, $resource, $subject, $exp)
            """;
        cmd.Parameters.AddWithValue("$h", Tokens.Hash(raw));
        cmd.Parameters.AddWithValue("$client", code.ClientId);
        cmd.Parameters.AddWithValue("$redirect", code.RedirectUri);
        cmd.Parameters.AddWithValue("$challenge", code.CodeChallenge);
        cmd.Parameters.AddWithValue("$scope", code.Scope);
        cmd.Parameters.AddWithValue("$resource", (object?)code.Resource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subject", code.Subject);
        cmd.Parameters.AddWithValue("$exp", Time.Format(DateTimeOffset.UtcNow + CodeTtl));
        cmd.ExecuteNonQuery();
        return raw;
    }

    /// <summary>Consumes a code exactly once. Returns null if unknown, expired or already used.</summary>
    public AuthCode? ConsumeCode(string raw)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE oauth_codes SET used = 1
            WHERE code_hash = $h AND used = 0 AND expires_at > $now
            RETURNING client_id, redirect_uri, code_challenge, scope, resource, subject
            """;
        cmd.Parameters.AddWithValue("$h", Tokens.Hash(raw));
        cmd.Parameters.AddWithValue("$now", Time.Now());
        AuthCode? result = null;
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
                result = new AuthCode(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5));
        }
        tx.Commit();
        return result;
    }

    public IssuedTokens IssueTokens(string clientId, string scope, string? resource, string subject, string? familyId = null)
    {
        familyId ??= Guid.NewGuid().ToString("N");
        var access = Tokens.New("ta_");
        var refresh = Tokens.New("tr_");
        var now = DateTimeOffset.UtcNow;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Insert(access, "access", now + AccessTtl);
        Insert(refresh, "refresh", now + RefreshTtl);
        tx.Commit();
        return new IssuedTokens(access, refresh, (int)AccessTtl.TotalSeconds, scope);

        void Insert(string raw, string kind, DateTimeOffset expires)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO oauth_tokens (token_hash, kind, client_id, scope, resource, subject, family_id, expires_at, created_at)
                VALUES ($h, $kind, $client, $scope, $resource, $subject, $family, $exp, $now)
                """;
            cmd.Parameters.AddWithValue("$h", Tokens.Hash(raw));
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$client", clientId);
            cmd.Parameters.AddWithValue("$scope", scope);
            cmd.Parameters.AddWithValue("$resource", (object?)resource ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subject", subject);
            cmd.Parameters.AddWithValue("$family", familyId);
            cmd.Parameters.AddWithValue("$exp", Time.Format(expires));
            cmd.Parameters.AddWithValue("$now", Time.Format(now));
            cmd.ExecuteNonQuery();
        }
    }

    public AccessGrant? ValidateAccessToken(string raw)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT client_id, scope, resource, subject, expires_at FROM oauth_tokens
            WHERE token_hash = $h AND kind = 'access' AND revoked = 0 AND expires_at > $now
            """;
        cmd.Parameters.AddWithValue("$h", Tokens.Hash(raw));
        cmd.Parameters.AddWithValue("$now", Time.Now());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AccessGrant(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), Time.Parse(r.GetString(4)));
    }

    public enum RefreshOutcome { Ok, Invalid, Reused }

    /// <summary>Rotates a refresh token. Reuse of an already-rotated token revokes its family.</summary>
    public (RefreshOutcome Outcome, IssuedTokens? Tokens) Refresh(string raw, string clientId, string? requestedScope)
    {
        string scope, subject, family;
        string? resource;
        using (var c = db.Open())
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT client_id, scope, resource, subject, family_id, revoked, expires_at FROM oauth_tokens
                WHERE token_hash = $h AND kind = 'refresh'
                """;
            cmd.Parameters.AddWithValue("$h", Tokens.Hash(raw));
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return (RefreshOutcome.Invalid, null);
                if (r.GetString(0) != clientId) return (RefreshOutcome.Invalid, null);
                scope = r.GetString(1);
                resource = r.IsDBNull(2) ? null : r.GetString(2);
                subject = r.GetString(3);
                family = r.GetString(4);
                var revoked = r.GetInt64(5) == 1;
                var expired = Time.Parse(r.GetString(6)) <= DateTimeOffset.UtcNow;
                if (revoked)
                {
                    r.Close();
                    RevokeFamily(c, tx, family);
                    tx.Commit();
                    return (RefreshOutcome.Reused, null);
                }
                if (expired) return (RefreshOutcome.Invalid, null);
            }

            using var mark = c.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "UPDATE oauth_tokens SET revoked = 1 WHERE token_hash = $h";
            mark.Parameters.AddWithValue("$h", Tokens.Hash(raw));
            mark.ExecuteNonQuery();
            tx.Commit();
        }

        if (requestedScope is not null)
        {
            var granted = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var asked = requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!asked.All(granted.Contains)) return (RefreshOutcome.Invalid, null);
            scope = string.Join(' ', asked);
        }
        return (RefreshOutcome.Ok, IssueTokens(clientId, scope, resource, subject, family));
    }

    public void Revoke(string raw)
    {
        using var c = db.Open();
        using var find = c.CreateCommand();
        find.CommandText = "SELECT family_id FROM oauth_tokens WHERE token_hash = $h";
        find.Parameters.AddWithValue("$h", Tokens.Hash(raw));
        if (find.ExecuteScalar() is string family)
        {
            using var tx = c.BeginTransaction();
            RevokeFamily(c, tx, family);
            tx.Commit();
        }
    }

    public int RevokeAll()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE oauth_tokens SET revoked = 1 WHERE revoked = 0";
        return cmd.ExecuteNonQuery();
    }

    public int ActiveClientCount()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT client_id) FROM oauth_tokens WHERE revoked = 0 AND expires_at > $now";
        cmd.Parameters.AddWithValue("$now", Time.Now());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Cleanup()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM oauth_codes WHERE expires_at < $now;
            DELETE FROM oauth_tokens WHERE expires_at < $now;
            """;
        cmd.Parameters.AddWithValue("$now", Time.Now());
        cmd.ExecuteNonQuery();
    }

    private static void RevokeFamily(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string family)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE oauth_tokens SET revoked = 1 WHERE family_id = $f";
        cmd.Parameters.AddWithValue("$f", family);
        cmd.ExecuteNonQuery();
    }
}
