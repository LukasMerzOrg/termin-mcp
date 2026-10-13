using Microsoft.Data.Sqlite;

namespace Termin.Storage;

/// <summary>SQLite access. All state lives in one file on the data volume.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    public bool Ping()
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1";
            return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        }
        catch
        {
            return false;
        }
    }

    private void Migrate()
    {
        using var c = Open();
        using var wal = c.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode = WAL;";
        wal.ExecuteNonQuery();

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS accounts (
                label       TEXT PRIMARY KEY,
                base_url    TEXT NOT NULL,
                username    TEXT NOT NULL,
                secret_enc  BLOB NOT NULL,
                provider    TEXT,
                is_default  INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS oauth_clients (
                client_id     TEXT PRIMARY KEY,
                client_name   TEXT,
                redirect_uris TEXT NOT NULL,
                created_at    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS oauth_codes (
                code_hash       TEXT PRIMARY KEY,
                client_id       TEXT NOT NULL,
                redirect_uri    TEXT NOT NULL,
                code_challenge  TEXT NOT NULL,
                scope           TEXT NOT NULL,
                resource        TEXT,
                subject         TEXT NOT NULL,
                expires_at      TEXT NOT NULL,
                used            INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS oauth_tokens (
                token_hash  TEXT PRIMARY KEY,
                kind        TEXT NOT NULL,
                client_id   TEXT NOT NULL,
                scope       TEXT NOT NULL,
                resource    TEXT,
                subject     TEXT NOT NULL,
                family_id   TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                revoked     INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tokens_family ON oauth_tokens(family_id);
            CREATE INDEX IF NOT EXISTS ix_tokens_expires ON oauth_tokens(expires_at);

            CREATE TABLE IF NOT EXISTS audit (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                at        TEXT NOT NULL,
                tool      TEXT NOT NULL,
                account   TEXT,
                calendar  TEXT,
                uid       TEXT,
                scope     TEXT,
                result    TEXT NOT NULL,
                client_id TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_audit_at ON audit(at);
            """;
        cmd.ExecuteNonQuery();
    }
}

internal static class Time
{
    public static string Now() => Format(DateTimeOffset.UtcNow);

    public static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}
