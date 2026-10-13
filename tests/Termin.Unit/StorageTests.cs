using Termin.Storage;

namespace Termin.Unit;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "termin-test-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;

    public StorageTests()
    {
        _db = new Database(Path.Combine(_dir, "t.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Secret_box_round_trips_and_binds_associated_data()
    {
        var box = SecretBox.FromConfig(Convert.ToBase64String(new byte[32]), out var derived);
        Assert.False(derived);
        var blob = box.Encrypt("app-password", "account:family");
        Assert.Equal("app-password", box.Decrypt(blob, "account:family"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => box.Decrypt(blob, "account:work"));
    }

    [Fact]
    public void Accounts_are_stored_encrypted()
    {
        var box = SecretBox.FromConfig("a long random passphrase for tests", out _);
        var store = new AccountStore(_db, box);
        store.Upsert(new StoredAccount("family", "https://dav.example", "lukas", "s3cret!", "nextcloud", true));

        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT secret_enc FROM accounts";
            var raw = (byte[])cmd.ExecuteScalar()!;
            Assert.DoesNotContain("s3cret", System.Text.Encoding.UTF8.GetString(raw));
        }

        var a = Assert.Single(store.List());
        Assert.Equal("s3cret!", a.Secret);
        Assert.True(store.Delete("family"));
    }

    [Fact]
    public void Password_hash_is_argon2id()
    {
        var hash = PasswordHasher.Hash("correct horse");
        Assert.StartsWith("$argon2id$v=19$", hash);
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.False(PasswordHasher.Verify("x", "garbage"));
    }

    [Fact]
    public void Pkce_s256_verifies()
    {
        // RFC 7636 appendix B
        Assert.True(Tokens.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(Tokens.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXX", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [Fact]
    public void Codes_are_single_use_and_tokens_hashed()
    {
        var store = new OAuthStore(_db);
        var client = store.RegisterClient("Claude", ["https://claude.ai/api/mcp/auth_callback"]);
        var code = store.CreateCode(new AuthCode(client.ClientId, "https://claude.ai/api/mcp/auth_callback", "challenge", "calendar.read", null, "admin"));
        Assert.NotNull(store.ConsumeCode(code));
        Assert.Null(store.ConsumeCode(code));

        var tokens = store.IssueTokens(client.ClientId, "calendar.read", "https://t.example/mcp", "admin");
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM oauth_tokens WHERE token_hash = $t";
        cmd.Parameters.AddWithValue("$t", tokens.AccessToken);
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        Assert.NotNull(store.ValidateAccessToken(tokens.AccessToken));
    }

    [Fact]
    public void Refresh_rotates_and_reuse_revokes_family()
    {
        var store = new OAuthStore(_db);
        var client = store.RegisterClient(null, ["http://localhost:1234/cb"]);
        var first = store.IssueTokens(client.ClientId, "calendar.read calendar.write", null, "admin");

        var (ok, second) = store.Refresh(first.RefreshToken, client.ClientId, null);
        Assert.Equal(OAuthStore.RefreshOutcome.Ok, ok);
        Assert.NotNull(second);

        var (reused, none) = store.Refresh(first.RefreshToken, client.ClientId, null);
        Assert.Equal(OAuthStore.RefreshOutcome.Reused, reused);
        Assert.Null(none);
        Assert.Null(store.ValidateAccessToken(second!.AccessToken));
        Assert.NotEqual(OAuthStore.RefreshOutcome.Ok, store.Refresh(second.RefreshToken, client.ClientId, null).Outcome);
    }

    [Fact]
    public void Refresh_can_narrow_but_not_widen_scope()
    {
        var store = new OAuthStore(_db);
        var client = store.RegisterClient(null, ["http://localhost/cb"]);
        var t = store.IssueTokens(client.ClientId, "calendar.read", null, "admin");
        Assert.Equal(OAuthStore.RefreshOutcome.Invalid, store.Refresh(t.RefreshToken, client.ClientId, "calendar.read calendar.write").Outcome);
    }

    [Fact]
    public void Audit_prunes_old_entries()
    {
        var audit = new AuditStore(_db);
        audit.Write(new AuditEntry("create_event", "family", "personal", "u1", null, "ok", "c1"));
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO audit (at, tool, result) VALUES ('2020-01-01T00:00:00.000Z', 'delete_event', 'ok')";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(1, audit.Prune());
        Assert.Single(audit.Recent());
    }
}
