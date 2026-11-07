using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Services;
using RemoteControlServer.Storage;

namespace RemoteControlServer.Shortcuts;

public interface IProcessLauncher
{
    void Launch(ProcessStartInfo psi);
}

public sealed class RealProcessLauncher : IProcessLauncher
{
    public void Launch(ProcessStartInfo psi) { using var p = Process.Start(psi); }
}

/// <summary>
/// Validates and launches user-approved shortcuts. Processes are always started through structured APIs
/// (ProcessStartInfo on a validated file); a command line is never assembled from strings and no shell is involved.
/// </summary>
public sealed class ShortcutService
{
    public static readonly string[] AllowedExtensions = [".exe", ".msc", ".cpl", ".lnk"];
    public static readonly string[] AllowedUriSchemes = ["ms-settings"];
    public static readonly string[] RunModes = ["normal", "minimized", "maximized", "hidden", "elevated"];
    private static readonly Regex IconRx = new("^[a-z0-9_]{1,32}$", RegexOptions.Compiled);

    private readonly ServerCore _core;
    private readonly IProcessLauncher _launcher;

    public ShortcutService(ServerCore core, IProcessLauncher? launcher = null)
    {
        _core = core; _launcher = launcher ?? new RealProcessLauncher();
        SeedDefaults();

        core.Handlers.Register("shortcuts.list", Permission.Launch, (c, e) =>
            Task.FromResult<object?>(new { shortcuts = core.Shortcuts.List().Select(ToDto).ToArray(), elevatedAllowed = core.Settings.AllowElevatedLaunch }));

        core.Handlers.Register("shortcuts.run", Permission.Launch, (c, e) =>
        {
            var id = e.Payload.RequireString("id", 64);
            var s = core.Shortcuts.Get(id) ?? throw new ProtocolException("not_found", "Unknown shortcut.");
            if (s.Confirm && !e.Payload.OptBool("confirmed", false))
                throw new ProtocolException("confirmation_required", $"'{s.Name}' requires confirmation.");
            Run(s, c);
            core.Audit.Write(c.DeviceName, "shortcut.run", s.Name);
            return Task.FromResult<object?>(new { launched = true, name = s.Name });
        });

        core.Handlers.Register("shortcuts.save", Permission.Admin, (c, e) =>
        {
            var d = e.Payload?["shortcut"] as System.Text.Json.Nodes.JsonObject ?? throw new ProtocolException("invalid_payload", "'shortcut' object required.");
            var s = new Shortcut
            {
                Id = d.OptString("id", 64) is { Length: > 0 } sid ? sid : Guid.NewGuid().ToString("N"),
                Name = d.OptString("name", 100) ?? "", Description = d.OptString("description", 400) ?? "", Icon = d.OptString("icon", 40) ?? "apps",
                Path = d.OptString("path", 1024) ?? "", Arguments = d.OptString("arguments", 2048) ?? "",
                WorkingDirectory = d.OptString("workingDirectory", 1024) ?? "", RunMode = d.OptString("runMode", 16) ?? "normal",
                Confirm = d.OptBool("confirm", false),
            };
            var existing = core.Shortcuts.Get(s.Id);
            if (existing != null) s.SortOrder = existing.SortOrder;
            Validate(s, core.Settings.AllowElevatedLaunch);
            core.Shortcuts.Upsert(s);
            core.Audit.Write(c.DeviceName, existing == null ? "shortcut.add" : "shortcut.edit", s.Name);
            return Task.FromResult<object?>(new { shortcut = ToDto(core.Shortcuts.Get(s.Id)!) });
        });

        core.Handlers.Register("shortcuts.delete", Permission.Admin, (c, e) =>
        {
            var id = e.Payload.RequireString("id", 64);
            var s = core.Shortcuts.Get(id) ?? throw new ProtocolException("not_found", "Unknown shortcut.");
            core.Shortcuts.Delete(id);
            core.Audit.Write(c.DeviceName, "shortcut.delete", s.Name);
            return Task.FromResult<object?>(new { deleted = true });
        });

        core.Handlers.Register("shortcuts.reorder", Permission.Admin, (c, e) =>
        {
            var arr = e.Payload?["ids"] as System.Text.Json.Nodes.JsonArray ?? throw new ProtocolException("invalid_payload", "'ids' array required.");
            var ids = arr.Select(x => x?.GetValue<string>() ?? "").ToList();
            var known = core.Shortcuts.List().Select(s => s.Id).ToHashSet();
            if (ids.Count != known.Count || ids.Any(i => !known.Contains(i)) || ids.Distinct().Count() != ids.Count)
                throw new ProtocolException("invalid_payload", "ids must list every shortcut exactly once.");
            core.Shortcuts.Reorder(ids);
            core.Audit.Write(c.DeviceName, "shortcut.reorder");
            return Task.FromResult<object?>(new { reordered = true });
        });
    }

    public static object ToDto(Shortcut s) => new
    {
        id = s.Id, name = s.Name, description = s.Description, icon = s.Icon, path = s.Path, arguments = s.Arguments,
        workingDirectory = s.WorkingDirectory, runMode = s.RunMode, confirm = s.Confirm,
    };

    // ------------------------------------------------------------------ validation

    public static void Validate(Shortcut s, bool elevatedAllowed)
    {
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 60) throw new ProtocolException("invalid_shortcut", "Name is required (max 60 characters).");
        s.Name = s.Name.Trim();
        if (s.Description.Length > 200) throw new ProtocolException("invalid_shortcut", "Description is too long (max 200).");
        if (!IconRx.IsMatch(s.Icon)) throw new ProtocolException("invalid_shortcut", "Icon must be a short lowercase identifier.");
        if (!RunModes.Contains(s.RunMode)) throw new ProtocolException("invalid_shortcut", "Run mode must be one of: " + string.Join(", ", RunModes));
        if (s.RunMode == "elevated" && !elevatedAllowed)
            throw new ProtocolException("invalid_shortcut", "Elevated launching is disabled. Enable it explicitly in the server's security settings first.");
        if (HasControl(s.Arguments) || s.Arguments.Length > 1024) throw new ProtocolException("invalid_shortcut", "Arguments contain control characters or are too long.");
        if (HasControl(s.Path) || HasControl(s.WorkingDirectory)) throw new ProtocolException("invalid_shortcut", "Path contains control characters.");

        var target = ResolveTarget(s.Path);
        if (target.error != null) throw new ProtocolException("invalid_shortcut", target.error);
        if (target.isUri && s.Arguments.Length > 0) throw new ProtocolException("invalid_shortcut", "A settings URI cannot take arguments.");
        if (!string.IsNullOrEmpty(s.Arguments) && !ParsesAsArguments(s.Arguments)) throw new ProtocolException("invalid_shortcut", "Arguments are not a valid command line.");
        if (!string.IsNullOrEmpty(s.WorkingDirectory) && !Directory.Exists(s.WorkingDirectory))
            throw new ProtocolException("invalid_shortcut", "Working directory does not exist.");
    }

    private static bool HasControl(string s) => s.Any(ch => char.IsControl(ch));

    /// <summary>Resolves a shortcut path to an existing, allowed file or an allowed URI.</summary>
    public static (string? resolved, bool isUri, string? error) ResolveTarget(string path)
    {
        path = path.Trim();
        if (path.Length == 0) return (null, false, "Path is required.");
        var colon = path.IndexOf(':');
        if (colon > 1 && !path.Contains('\\') && !path.Contains('/'))
        {
            var scheme = path[..colon].ToLowerInvariant();
            return AllowedUriSchemes.Contains(scheme) ? (path, true, null) : (null, false, $"URI scheme '{scheme}' is not allowed.");
        }
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return (null, false, $"Only {string.Join(", ", AllowedExtensions)} files can be launched (got '{ext}'). Scripts must be run from the terminal.");

        string? resolved = null;
        if (System.IO.Path.IsPathFullyQualified(path)) resolved = File.Exists(path) ? System.IO.Path.GetFullPath(path) : null;
        else if (!path.Contains('\\') && !path.Contains('/'))
        {
            var dirs = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
            foreach (var d in dirs)
            {
                try { var f = System.IO.Path.Combine(d, path); if (File.Exists(f)) { resolved = f; break; } } catch { }
            }
        }
        else return (null, false, "Use a full path (C:\\...) or a bare file name such as taskmgr.exe.");
        return resolved == null ? (null, false, "File was not found: " + path) : (resolved, false, null);
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmd, out int argc);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    public static bool ParsesAsArguments(string args) => SplitArguments(args) != null;

    /// <summary>Splits an argument string with the standard Windows rules.</summary>
    public static List<string>? SplitArguments(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return [];
        var p = CommandLineToArgvW("x " + args, out int argc);
        if (p == IntPtr.Zero) return null;
        try
        {
            var list = new List<string>();
            for (int i = 1; i < argc; i++) list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!);
            return list;
        }
        finally { LocalFree(p); }
    }

    // ------------------------------------------------------------------ launching

    public void Run(Shortcut s, ClientContext? by)
    {
        var (resolved, isUri, error) = ResolveTarget(s.Path);
        if (error != null) throw new ProtocolException("launch_failed", error);
        var psi = new ProcessStartInfo(resolved!) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(s.WorkingDirectory) && Directory.Exists(s.WorkingDirectory)) psi.WorkingDirectory = s.WorkingDirectory;

        if (!isUri && s.Arguments.Length > 0)
        {
            var parts = SplitArguments(s.Arguments) ?? throw new ProtocolException("launch_failed", "Arguments are not a valid command line.");
            // re-quote each parsed argument with the standard rules, so the string handed to the OS is exactly the validated list
            psi.Arguments = string.Join(' ', parts.Select(QuoteArg));
        }

        switch (s.RunMode)
        {
            case "minimized": psi.WindowStyle = ProcessWindowStyle.Minimized; break;
            case "maximized": psi.WindowStyle = ProcessWindowStyle.Maximized; break;
            case "hidden": psi.WindowStyle = ProcessWindowStyle.Hidden; break;
            case "elevated":
                if (!_core.Settings.AllowElevatedLaunch) throw new ProtocolException("forbidden", "Elevated launching is disabled in the server's security settings.");
                if (by != null && !by.Has(Permission.Admin)) throw new ProtocolException("forbidden", "Elevated launching needs the 'admin' permission.");
                psi.Verb = "runas"; // Windows shows its normal UAC prompt on the PC: never silent
                break;
        }
        try { _launcher.Launch(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new ProtocolException("launch_cancelled", "The UAC prompt on the PC was declined."); }
        catch (Win32Exception ex) { throw new ProtocolException("launch_failed", $"Could not start '{s.Name}': {ex.Message}"); }
    }

    private static string QuoteArg(string a)
    {
        if (a.Length > 0 && !a.Any(ch => ch is ' ' or '\t' or '"')) return a;
        var sb = new System.Text.StringBuilder("\"");
        int bs = 0;
        foreach (var ch in a)
        {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(ch); bs = 0;
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ defaults

    private void SeedDefaults()
    {
        if (_core.Shortcuts.List().Count > 0) return;
        var seeds = new (string name, string desc, string icon, string path)[]
        {
            ("Task Manager", "Processes and performance", "monitor", "taskmgr.exe"),
            ("Device Manager", "Hardware and drivers", "memory", "devmgmt.msc"),
            ("Services", "Windows services", "settings", "services.msc"),
            ("Event Viewer", "System and application logs", "list", "eventvwr.msc"),
            ("Network Connections", "Network adapters", "network", "ncpa.cpl"),
            ("Resource Monitor", "CPU, disk and network usage", "chart", "resmon.exe"),
            ("Windows Settings", "Open the Settings app", "settings", "ms-settings:"),
            ("Registry Editor", "Edit the Windows registry", "build", "regedit.exe"),
        };
        foreach (var (name, desc, icon, path) in seeds)
            if (ResolveTarget(path).error == null)
                _core.Shortcuts.Upsert(new Shortcut { Name = name, Description = desc, Icon = icon, Path = path, Confirm = name == "Registry Editor" });

        var browsers = new (string name, string icon, string[] candidates)[]
        {
            ("Chrome", "web", [@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"]),
            ("Firefox", "web", [@"C:\Program Files\Mozilla Firefox\firefox.exe", @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"]),
            ("Microsoft Edge", "web", [@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"]),
            ("Visual Studio Code", "code", [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"), @"C:\Program Files\Microsoft VS Code\Code.exe"]),
        };
        foreach (var (name, icon, cands) in browsers)
        {
            var found = cands.FirstOrDefault(File.Exists);
            if (found != null) _core.Shortcuts.Upsert(new Shortcut { Name = name, Description = "Open " + name, Icon = icon, Path = found });
        }
    }
}
