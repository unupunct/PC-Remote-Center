using Microsoft.Data.Sqlite;

namespace RemoteControlServer.Storage;

/// <summary>SQLite storage with versioned migrations. Connections are opened per operation (pooled).</summary>
public sealed class Database
{
    public string Path { get; }
    private readonly string _cs;

    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE devices (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, model TEXT, token_hash BLOB NOT NULL,
            permissions INTEGER NOT NULL, created_utc TEXT NOT NULL, last_seen_utc TEXT, last_ip TEXT, revoked INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE shortcuts (
            id TEXT PRIMARY KEY, sort_order INTEGER NOT NULL, name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
            icon TEXT NOT NULL DEFAULT 'apps', path TEXT NOT NULL, arguments TEXT NOT NULL DEFAULT '',
            working_dir TEXT NOT NULL DEFAULT '', run_mode TEXT NOT NULL DEFAULT 'normal', confirm INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE connection_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, device_name TEXT, ip TEXT,
            connected_utc TEXT NOT NULL, disconnected_utc TEXT, result TEXT NOT NULL);
        CREATE TABLE audit_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, actor TEXT NOT NULL,
            action TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '', success INTEGER NOT NULL DEFAULT 1);
        CREATE INDEX ix_audit_ts ON audit_log(ts_utc);
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public Database(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        int current = Scalar<long?>(c, "SELECT MAX(version) FROM schema_version") is long l ? (int)l : 0;
        if (current > Migrations.Length)
            throw new InvalidOperationException($"Database schema v{current} is newer than this application (v{Migrations.Length}).");
        for (int v = current; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v];
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO schema_version(version) VALUES ($v)";
            cmd.Parameters.AddWithValue("$v", v + 1);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int SchemaVersion()
    {
        using var c = Open();
        return Scalar<long?>(c, "SELECT MAX(version) FROM schema_version") is long l ? (int)l : 0;
    }

    public static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        if (r == null || r is DBNull) return default;
        var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(r, t);
    }

    public void Execute(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        Exec(c, sql, args);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
