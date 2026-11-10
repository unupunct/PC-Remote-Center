using System.ComponentModel;
using System.Diagnostics;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;

namespace RemoteControlServer.Services;

public sealed record ProcessRow(int Pid, string Name, double? Cpu, double MemoryMb, int Threads, string? Window);

/// <summary>Reads the process list (CPU % measured between two reads) and can end a process the owner allowed.</summary>
public sealed class ProcessService
{
    /// <summary>Processes that must never be ended remotely (Windows would crash or log the user off).</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "memory compression", "memcompression", "secure system",
        "smss", "csrss", "wininit", "services", "lsass", "winlogon", "dwm", "fontdrvhost", "lsaiso", "svchost",
    };

    private readonly object _lock = new();
    private Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastAt = DateTime.MinValue;
    private readonly ServerCore? _core;

    public ProcessService(ServerCore? core = null)
    {
        _core = core;
        if (core == null) return;
        core.Handlers.Register("process.list", Permission.Monitoring, (c, e) =>
        {
            var sort = (e.Payload.OptString("sort", 16) ?? "memory").ToLowerInvariant();
            if (sort is not ("cpu" or "memory" or "name")) throw new ProtocolException("invalid_payload", "sort must be cpu, memory or name.");
            int limit = Math.Clamp(e.Payload.OptInt("limit", 60), 1, 300);
            var filter = e.Payload.OptString("filter", 64);
            var rows = List(sort, limit, filter, out int total);
            return Task.FromResult<object?>(new { total, sort, processes = rows });
        });

        core.Handlers.Register("process.kill", Permission.Admin, (c, e) =>
        {
            int pid = e.Payload.OptInt("pid", -1);
            if (pid <= 0) throw new ProtocolException("invalid_payload", "'pid' is required.");
            bool tree = e.Payload.OptBool("tree", false);
            // server-enforced confirmation (same pattern as shortcuts): the phone must show a dialog and resend with confirmed=true
            if (!e.Payload.OptBool("confirmed", false)) throw new ProtocolException("confirmation_required", "Ending a process requires confirmation.");
            var name = Kill(pid, tree);
            core.Audit.Write(c.DeviceName, "process.kill", $"{name} (pid {pid}{(tree ? ", tree" : "")})");
            return Task.FromResult<object?>(new { killed = true, pid, name });
        });
    }

    public List<ProcessRow> List(string sort, int limit, string? filter, out int total)
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<int, TimeSpan>();
        var rows = new List<ProcessRow>();
        Dictionary<int, TimeSpan> prev; DateTime prevAt;
        lock (_lock) { prev = _lastCpu; prevAt = _lastAt; }
        // first call (or stale data): take a short baseline so CPU % is real instead of unknown
        if (prevAt == DateTime.MinValue || now - prevAt > TimeSpan.FromSeconds(30))
        {
            prev = Snapshot(); prevAt = DateTime.UtcNow;
            Thread.Sleep(400);
            now = DateTime.UtcNow;
        }
        double wall = Math.Max(0.05, (now - prevAt).TotalSeconds);
        int cores = Environment.ProcessorCount;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    double? cpu = null;
                    try
                    {
                        var t = p.TotalProcessorTime; current[p.Id] = t;
                        if (prev.TryGetValue(p.Id, out var before)) cpu = Math.Round(Math.Clamp((t - before).TotalSeconds / wall / cores * 100, 0, 100), 1);
                    }
                    catch (Win32Exception) { /* protected process: CPU time not readable */ }
                    catch (InvalidOperationException) { continue; } // exited meanwhile
                    string? win = null;
                    try { win = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : Truncate(p.MainWindowTitle, 80); } catch { }
                    rows.Add(new ProcessRow(p.Id, p.ProcessName, cpu, Math.Round(p.WorkingSet64 / 1048576.0, 1), p.Threads.Count, win));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
        lock (_lock) { _lastCpu = current; _lastAt = now; }

        if (!string.IsNullOrWhiteSpace(filter))
            rows = rows.Where(r => r.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) || (r.Window?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        total = rows.Count;
        IEnumerable<ProcessRow> ordered = sort switch
        {
            "cpu" => rows.OrderByDescending(r => r.Cpu ?? -1).ThenByDescending(r => r.MemoryMb),
            "name" => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(r => r.MemoryMb),
        };
        return ordered.Take(limit).ToList();
    }

    private static Dictionary<int, TimeSpan> Snapshot()
    {
        var d = new Dictionary<int, TimeSpan>();
        foreach (var p in Process.GetProcesses())
            using (p) { try { d[p.Id] = p.TotalProcessorTime; } catch { } }
        return d;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Ends a process (optionally its whole tree) after checking it is not protected. Returns its name.</summary>
    public string Kill(int pid, bool tree)
    {
        if (pid <= 4 || pid == Environment.ProcessId) throw new ProtocolException("forbidden", "This process cannot be ended.");
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new ProtocolException("not_found", "That process is no longer running."); }
        using (p)
        {
            var name = p.ProcessName;
            if (Protected.Contains(name)) throw new ProtocolException("forbidden", $"'{name}' is a critical Windows process and cannot be ended remotely.");
            if (name.Equals("RemoteControlServer", StringComparison.OrdinalIgnoreCase)) throw new ProtocolException("forbidden", "The server cannot end itself.");
            try { p.Kill(tree); p.WaitForExit(3000); }
            catch (Win32Exception ex) { throw new ProtocolException("action_failed", $"Windows refused to end '{name}': {ex.Message}"); }
            catch (InvalidOperationException) { throw new ProtocolException("not_found", "That process is no longer running."); }
            return name;
        }
    }
}
