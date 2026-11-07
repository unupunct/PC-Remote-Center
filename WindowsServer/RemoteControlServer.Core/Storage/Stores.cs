using System.Text.Json;
using RemoteControlServer.Models;

namespace RemoteControlServer.Storage;

public static class DataPaths
{
    public static string Default => Environment.GetEnvironmentVariable("PCRC_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCRemoteCenter");
}

public sealed class ServerSettings
{
    public int Port { get; set; } = 47821;
    public int DiscoveryPort { get; set; } = 47822;
    public bool DiscoveryEnabled { get; set; } = true;
    public string ComputerName { get; set; } = Environment.GetEnvironmentVariable("PCRC_COMPUTER_NAME") is { Length: > 0 } n ? n : Environment.MachineName;
    /// <summary>Extra CIDR ranges (comma separated) allowed in addition to private LAN ranges, e.g. a VPN subnet "10.8.0.0/24".</summary>
    public string ExtraAllowedNetworks { get; set; } = "";
    /// <summary>When false (default) only loopback, private LAN, link-local and CGNAT-less ranges are accepted.</summary>
    public bool LanOnly { get; set; } = true;
    public int SessionMaxHours { get; set; } = 12;
    public int AuthTimeoutSeconds { get; set; } = 10;
    public int MonitorIntervalMs { get; set; } = 1500;
    public bool AutoStartServer { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ShowTerminalWindows { get; set; } = true;
    public bool AllowElevatedLaunch { get; set; }
    public int PairingCodeMinutes { get; set; } = 5;
    public int LogRetentionDays { get; set; } = 90;
}

public sealed class SettingsStore
{
    private readonly Database _db;
    public SettingsStore(Database db) => _db = db;
    public event Action? Changed;

    public ServerSettings Load()
    {
        var s = new ServerSettings();
        var rows = _db.Query("SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        foreach (var p in typeof(ServerSettings).GetProperties())
        {
            if (!rows.TryGetValue(p.Name, out var json)) continue;
            try { p.SetValue(s, JsonSerializer.Deserialize(json, p.PropertyType)); } catch { /* keep default */ }
        }
        return s;
    }

    public void Save(ServerSettings s)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var p in typeof(ServerSettings).GetProperties())
            Database.Exec(c, "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",
                ("$k", p.Name), ("$v", JsonSerializer.Serialize(p.GetValue(s), p.PropertyType)));
        tx.Commit();
        Changed?.Invoke();
    }
}

public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public byte[] TokenHash { get; set; } = [];
    public Permission Permissions { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string LastIp { get; set; } = "";
    public bool Revoked { get; set; }
}

public sealed class DeviceStore
{
    private readonly Database _db;
    public DeviceStore(Database db) => _db = db;
    public event Action? Changed;

    private static DeviceRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1), Model = r.IsDBNull(2) ? "" : r.GetString(2),
        TokenHash = (byte[])r[3], Permissions = (Permission)r.GetInt32(4),
        CreatedUtc = DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastSeenUtc = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastIp = r.IsDBNull(7) ? "" : r.GetString(7), Revoked = r.GetInt32(8) != 0,
    };

    private const string Cols = "id,name,model,token_hash,permissions,created_utc,last_seen_utc,last_ip,revoked";

    public void Add(DeviceRecord d)
    {
        _db.Execute("INSERT INTO devices(id,name,model,token_hash,permissions,created_utc) VALUES($id,$n,$m,$h,$p,$c)",
            ("$id", d.Id), ("$n", d.Name), ("$m", d.Model), ("$h", d.TokenHash), ("$p", (int)d.Permissions), ("$c", d.CreatedUtc.ToString("o")));
        Changed?.Invoke();
    }

    public DeviceRecord? Get(string id) => _db.Query($"SELECT {Cols} FROM devices WHERE id=$id", Map, ("$id", id)).FirstOrDefault();
    public List<DeviceRecord> List() => _db.Query($"SELECT {Cols} FROM devices ORDER BY created_utc", Map);

    public void Touch(string id, string ip) =>
        _db.Execute("UPDATE devices SET last_seen_utc=$t,last_ip=$ip WHERE id=$id", ("$t", DateTime.UtcNow.ToString("o")), ("$ip", ip), ("$id", id));

    public void SetPermissions(string id, Permission p)
    {
        _db.Execute("UPDATE devices SET permissions=$p WHERE id=$id", ("$p", (int)p), ("$id", id));
        Changed?.Invoke();
    }

    public void Revoke(string id)
    {
        _db.Execute("UPDATE devices SET revoked=1 WHERE id=$id", ("$id", id));
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        _db.Execute("DELETE FROM devices WHERE id=$id", ("$id", id));
        Changed?.Invoke();
    }
}

public sealed class Shortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SortOrder { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "apps";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    /// <summary>normal | minimized | maximized | hidden | elevated</summary>
    public string RunMode { get; set; } = "normal";
    public bool Confirm { get; set; }
}

public sealed class ShortcutStore
{
    private readonly Database _db;
    public ShortcutStore(Database db) => _db = db;
    public event Action? Changed;

    public List<Shortcut> List() => _db.Query(
        "SELECT id,sort_order,name,description,icon,path,arguments,working_dir,run_mode,confirm FROM shortcuts ORDER BY sort_order,name",
        r => new Shortcut
        {
            Id = r.GetString(0), SortOrder = r.GetInt32(1), Name = r.GetString(2), Description = r.GetString(3), Icon = r.GetString(4),
            Path = r.GetString(5), Arguments = r.GetString(6), WorkingDirectory = r.GetString(7), RunMode = r.GetString(8), Confirm = r.GetInt32(9) != 0,
        });

    public Shortcut? Get(string id) => List().FirstOrDefault(s => s.Id == id);

    public void Upsert(Shortcut s)
    {
        if (s.SortOrder == 0 && Get(s.Id) == null) s.SortOrder = (List().Select(x => x.SortOrder).DefaultIfEmpty(0).Max()) + 1;
        _db.Execute("""
            INSERT INTO shortcuts(id,sort_order,name,description,icon,path,arguments,working_dir,run_mode,confirm)
            VALUES($id,$o,$n,$d,$i,$p,$a,$w,$r,$c)
            ON CONFLICT(id) DO UPDATE SET name=$n,description=$d,icon=$i,path=$p,arguments=$a,working_dir=$w,run_mode=$r,confirm=$c
            """,
            ("$id", s.Id), ("$o", s.SortOrder), ("$n", s.Name), ("$d", s.Description), ("$i", s.Icon), ("$p", s.Path),
            ("$a", s.Arguments), ("$w", s.WorkingDirectory), ("$r", s.RunMode), ("$c", s.Confirm ? 1 : 0));
        Changed?.Invoke();
    }

    public void Delete(string id) { _db.Execute("DELETE FROM shortcuts WHERE id=$id", ("$id", id)); Changed?.Invoke(); }

    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        for (int i = 0; i < orderedIds.Count; i++)
            Database.Exec(c, "UPDATE shortcuts SET sort_order=$o WHERE id=$id", ("$o", i + 1), ("$id", orderedIds[i]));
        tx.Commit();
        Changed?.Invoke();
    }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Success { get; set; }
}

/// <summary>Audit log. Never pass passwords, tokens or command contents in <c>detail</c>.</summary>
public sealed class AuditLog
{
    private readonly Database _db;
    public AuditLog(Database db) => _db = db;
    public event Action<AuditEntry>? Added;

    public void Write(string actor, string action, string detail = "", bool success = true)
    {
        var now = DateTime.UtcNow;
        _db.Execute("INSERT INTO audit_log(ts_utc,actor,action,detail,success) VALUES($t,$a,$x,$d,$s)",
            ("$t", now.ToString("o")), ("$a", actor), ("$x", action), ("$d", detail), ("$s", success ? 1 : 0));
        Added?.Invoke(new AuditEntry { TimeUtc = now, Actor = actor, Action = action, Detail = detail, Success = success });
    }

    public List<AuditEntry> Recent(int count = 500) => _db.Query(
        "SELECT id,ts_utc,actor,action,detail,success FROM audit_log ORDER BY id DESC LIMIT $n",
        r => new AuditEntry
        {
            Id = r.GetInt64(0), TimeUtc = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Actor = r.GetString(2), Action = r.GetString(3), Detail = r.GetString(4), Success = r.GetInt32(5) != 0,
        }, ("$n", count));

    public void Purge(int retentionDays) =>
        _db.Execute("DELETE FROM audit_log WHERE ts_utc < $t", ("$t", DateTime.UtcNow.AddDays(-retentionDays).ToString("o")));
}

public sealed class ConnectionHistoryEntry
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime ConnectedUtc { get; set; }
    public DateTime? DisconnectedUtc { get; set; }
    public string Result { get; set; } = "";
}

public sealed class ConnectionHistory
{
    private readonly Database _db;
    public ConnectionHistory(Database db) => _db = db;

    public long Begin(string deviceId, string name, string ip, string result)
    {
        using var c = _db.Open();
        Database.Exec(c, "INSERT INTO connection_history(device_id,device_name,ip,connected_utc,result) VALUES($d,$n,$i,$t,$r)",
            ("$d", deviceId), ("$n", name), ("$i", ip), ("$t", DateTime.UtcNow.ToString("o")), ("$r", result));
        return Database.Scalar<long>(c, "SELECT last_insert_rowid()");
    }

    public void End(long id) => _db.Execute("UPDATE connection_history SET disconnected_utc=$t WHERE id=$id", ("$t", DateTime.UtcNow.ToString("o")), ("$id", id));

    public List<ConnectionHistoryEntry> Recent(int count = 200) => _db.Query(
        "SELECT id,device_id,device_name,ip,connected_utc,disconnected_utc,result FROM connection_history ORDER BY id DESC LIMIT $n",
        r => new ConnectionHistoryEntry
        {
            Id = r.GetInt64(0), DeviceId = r.GetString(1), DeviceName = r.GetString(2), Ip = r.GetString(3),
            ConnectedUtc = DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DisconnectedUtc = r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Result = r.GetString(6),
        }, ("$n", count));
}
