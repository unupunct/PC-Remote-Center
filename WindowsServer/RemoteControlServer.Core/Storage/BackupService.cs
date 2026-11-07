using System.Text.Json;
using RemoteControlServer.Models;

namespace RemoteControlServer.Storage;

public sealed class BackupSummary
{
    public int Version { get; set; }
    public string CreatedUtc { get; set; } = "";
    public int ShortcutCount { get; set; }
    public int DeviceCount { get; set; }
    public bool HasSettings { get; set; }
}

/// <summary>
/// Configuration backup/restore as a plain JSON file (settings, shortcuts and - optionally - paired devices with their
/// token HASHES; tokens themselves are never stored anywhere). Restore never runs without the caller confirming first.
/// </summary>
public sealed class BackupService
{
    private readonly SettingsStore _settings; private readonly ShortcutStore _shortcuts; private readonly DeviceStore _devices;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public BackupService(SettingsStore s, ShortcutStore sh, DeviceStore d) { _settings = s; _shortcuts = sh; _devices = d; }

    private sealed class DeviceDto
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Model { get; set; } = "";
        public string TokenHash { get; set; } = ""; public int Permissions { get; set; } public string CreatedUtc { get; set; } = ""; public bool Revoked { get; set; }
    }

    private sealed class BackupFile
    {
        public int Version { get; set; } = 1;
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public ServerSettings? Settings { get; set; }
        public List<Shortcut> Shortcuts { get; set; } = [];
        public List<DeviceDto>? Devices { get; set; }
    }

    /// <summary>Writes the backup. Refuses to overwrite an existing file unless <paramref name="overwrite"/> is set.</summary>
    public void Export(string path, bool includeDevices, bool overwrite = false)
    {
        if (File.Exists(path) && !overwrite) throw new IOException("The file already exists. Choose another name or confirm overwriting.");
        var f = new BackupFile
        {
            Settings = _settings.Load(), Shortcuts = _shortcuts.List(),
            Devices = includeDevices ? _devices.List().Select(d => new DeviceDto
            {
                Id = d.Id, Name = d.Name, Model = d.Model, TokenHash = Convert.ToBase64String(d.TokenHash), Permissions = (int)d.Permissions,
                CreatedUtc = d.CreatedUtc.ToString("o"), Revoked = d.Revoked,
            }).ToList() : null,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(f, Opts));
    }

    public BackupSummary Inspect(string path)
    {
        var f = Read(path);
        return new BackupSummary { Version = f.Version, CreatedUtc = f.CreatedUtc, ShortcutCount = f.Shortcuts.Count, DeviceCount = f.Devices?.Count ?? 0, HasSettings = f.Settings != null };
    }

    /// <summary>Replaces settings and shortcuts with the backup and adds missing devices. The caller must have confirmed with the user.</summary>
    public void Restore(string path, bool restoreDevices)
    {
        var f = Read(path);
        if (f.Settings != null) _settings.Save(f.Settings);
        foreach (var existing in _shortcuts.List()) _shortcuts.Delete(existing.Id);
        foreach (var s in f.Shortcuts) _shortcuts.Upsert(s);
        if (restoreDevices && f.Devices != null)
        {
            var have = _devices.List().Select(d => d.Id).ToHashSet();
            foreach (var d in f.Devices.Where(d => !have.Contains(d.Id)))
            {
                _devices.Add(new DeviceRecord
                {
                    Id = d.Id, Name = d.Name, Model = d.Model, TokenHash = Convert.FromBase64String(d.TokenHash), Permissions = (Permission)d.Permissions,
                    CreatedUtc = DateTime.Parse(d.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
                });
                if (d.Revoked) _devices.Revoke(d.Id);
            }
        }
    }

    private static BackupFile Read(string path)
    {
        BackupFile? f;
        try { f = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path), Opts); }
        catch (JsonException) { throw new InvalidDataException("This is not a PC Remote Center backup file."); }
        if (f == null || f.Version < 1) throw new InvalidDataException("This is not a PC Remote Center backup file.");
        if (f.Version > 1) throw new InvalidDataException($"The backup was made by a newer version (format {f.Version}).");
        return f;
    }
}
