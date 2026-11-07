using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Services;

namespace RemoteControlServer.Terminal;

/// <summary>
/// Owns the two persistent shell sessions (cmd, powershell) and exposes them to clients as terminal.* messages.
/// UI code subscribes to <see cref="SessionStarted"/> to open the visible terminal window.
/// </summary>
public sealed class TerminalManager : IDisposable
{
    public static readonly string[] Shells = ["cmd", "powershell"];

    private readonly ServerCore _core;
    private readonly object _lock = new();
    private readonly Dictionary<string, TerminalSession?> _sessions = new() { ["cmd"] = null, ["powershell"] = null };
    private readonly Dictionary<string, List<Subscriber>> _subs = new() { ["cmd"] = [], ["powershell"] = [] };
    private readonly Dictionary<string, System.Threading.Timer?> _flushTimers = new() { ["cmd"] = null, ["powershell"] = null };
    private readonly Dictionary<string, int[]> _flushPending = new() { ["cmd"] = new int[1], ["powershell"] = new int[1] };

    private sealed class Subscriber
    {
        public required ClientContext Client { get; init; }
        public long SentTotal;
        public int Busy;
        public long Seq;
    }

    /// <summary>Raised after a session process was created (UI opens its terminal window).</summary>
    public event Action<TerminalSession>? SessionStarted;
    public event Action<TerminalSession>? SessionEnded;

    public TerminalManager(ServerCore core)
    {
        _core = core;
        const Permission T = Permission.Terminal;
        core.Handlers.Register("terminal.open", T, (c, e) => Task.FromResult(Open(c, e)));
        core.Handlers.Register("terminal.close", T, (c, e) => { Unsubscribe(c, Shell(e)); return Task.FromResult<object?>(new { }); });
        core.Handlers.Register("terminal.status", T, (c, e) => Task.FromResult<object?>(new { sessions = Shells.Select(StatusOf).ToArray() }));
        core.Handlers.Register("terminal.execute", T, (c, e) =>
        {
            var s = Running(Shell(e));
            var cmd = e.Payload.RequireString("command", 64 * 1024);
            Guard(() => s.Execute(cmd));
            // audit the action but never the command text
            _core.Audit.Write(c.DeviceName, "terminal.execute", $"{s.Shell}, {cmd.Length} chars");
            return Task.FromResult<object?>(new { accepted = true });
        });
        core.Handlers.Register("terminal.input", T, (c, e) =>
        {
            var s = Running(Shell(e));
            Guard(() => s.WriteRaw(e.Payload.RequireString("data", 8192)));
            return Task.FromResult<object?>(new { accepted = true });
        });
        core.Handlers.Register("terminal.interrupt", T, (c, e) =>
        {
            var s = Running(Shell(e));
            Guard(s.Interrupt);
            _core.Audit.Write(c.DeviceName, "terminal.interrupt", s.Shell);
            return Task.FromResult<object?>(new { accepted = true });
        });
        core.Handlers.Register("terminal.resize", T, (c, e) =>
        {
            var s = Running(Shell(e));
            int cols = e.Payload.OptInt("cols", 0), rows = e.Payload.OptInt("rows", 0);
            if (cols < 20 || cols > 400 || rows < 5 || rows > 200) throw new ProtocolException("invalid_payload", "cols must be 20-400 and rows 5-200.");
            Guard(() => s.Resize(cols, rows));
            return Task.FromResult<object?>(new { cols, rows });
        });
        core.Handlers.Register("terminal.clear", T, (c, e) =>
        {
            var s = Running(Shell(e));
            Guard(s.Clear);
            return Task.FromResult<object?>(new { accepted = true });
        });
        core.Handlers.Register("terminal.restart", T, (c, e) =>
        {
            var shell = Shell(e);
            try { Restart(shell, e.Payload.OptInt("cols", 120), e.Payload.OptInt("rows", 30)); }
            catch (Exception ex) { throw new ProtocolException("terminal_start_failed", $"Could not start {shell}: {ex.Message}"); }
            _core.Audit.Write(c.DeviceName, "terminal.restart", shell);
            return Task.FromResult<object?>(StateOf(shell, c, full: true));
        });
    }

    // ------------------------------------------------------------------ session lifecycle

    public TerminalSession? Get(string shell) { lock (_lock) return _sessions.GetValueOrDefault(shell); }

    /// <summary>Starts the shell if it was never started. A session that has exited is only recreated by <see cref="Restart"/>.</summary>
    public TerminalSession EnsureStarted(string shell, int cols = 120, int rows = 30)
    {
        lock (_lock)
        {
            var s = _sessions[shell];
            if (s != null) return s;
            return StartLocked(shell, cols, rows);
        }
    }

    public TerminalSession Restart(string shell, int cols = 120, int rows = 30)
    {
        TerminalSession? old;
        lock (_lock)
        {
            old = _sessions[shell];
            _sessions[shell] = null;
        }
        if (old != null) { old.Dispose(); }
        lock (_lock)
        {
            // keep current subscribers; they get the new state in the reply / next frame
            foreach (var sub in _subs[shell]) sub.SentTotal = 0;
            return StartLocked(shell, cols, rows);
        }
    }

    private TerminalSession StartLocked(string shell, int cols, int rows)
    {
        var s = TerminalSession.Start(shell, null, Math.Clamp(cols, 20, 400), Math.Clamp(rows, 5, 200));
        _sessions[shell] = s;
        s.OutputChanged += () => ScheduleFlush(shell);
        s.Exited += _ => { SessionEnded?.Invoke(s); };
        _core.Audit.Write("system", "terminal.start", shell);
        SessionStarted?.Invoke(s);
        return s;
    }

    private TerminalSession Running(string shell)
    {
        var s = Get(shell);
        if (s == null || !s.IsRunning) throw new ProtocolException("shell_not_running", $"The {shell} session is not running. Open or restart it first.");
        return s;
    }

    private static void Guard(Action a)
    {
        try { a(); }
        catch (ArgumentException ex) { throw new ProtocolException("invalid_payload", ex.Message); }
        catch (InvalidOperationException ex) { throw new ProtocolException("shell_not_running", ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { throw new ProtocolException("terminal_error", ex.Message); }
        catch (IOException ex) { throw new ProtocolException("terminal_error", ex.Message); }
    }

    private static string Shell(Envelope e)
    {
        var shell = e.Payload.RequireString("shell", 16).ToLowerInvariant();
        if (shell == "pwsh" || shell == "ps") shell = "powershell";
        if (!Shells.Contains(shell)) throw new ProtocolException("invalid_payload", "shell must be 'cmd' or 'powershell'.");
        return shell;
    }

    // ------------------------------------------------------------------ client protocol

    private object? Open(ClientContext c, Envelope e)
    {
        var shell = Shell(e);
        try { EnsureStarted(shell, e.Payload.OptInt("cols", 120), e.Payload.OptInt("rows", 30)); }
        catch (Exception ex) { throw new ProtocolException("terminal_start_failed", $"Could not start {shell}: {ex.Message}"); }
        lock (_lock)
        {
            if (!_subs[shell].Any(s => s.Client == c))
            {
                _subs[shell].Add(new Subscriber { Client = c });
                c.Closed += () => Unsubscribe(c, shell);
            }
        }
        _core.Audit.Write(c.DeviceName, "terminal.open", shell);
        return StateOf(shell, c, full: true);
    }

    private void Unsubscribe(ClientContext c, string shell)
    {
        lock (_lock) _subs[shell].RemoveAll(s => s.Client == c);
    }

    private object StatusOf(string shell)
    {
        var s = Get(shell);
        return new { shell, started = s != null, running = s?.IsRunning ?? false, exitCode = s?.ExitCode, pid = s?.ProcessId ?? 0 };
    }

    private object StateOf(string shell, ClientContext c, bool full)
    {
        var s = Get(shell)!;
        Subscriber? sub;
        lock (_lock) sub = _subs[shell].FirstOrDefault(x => x.Client == c);
        var snap = s.Screen.Snapshot(full ? 0 : sub?.SentTotal ?? 0, full ? 1000 : 2000);
        if (sub != null) sub.SentTotal = snap.ScrollbackTotal;
        return Frame(shell, s, snap, sub != null ? sub.Seq++ : 0, full);
    }

    private static object Frame(string shell, TerminalSession s, ScreenState snap, long seq, bool full) => new
    {
        shell, seq, full, running = s.IsRunning, exitCode = s.ExitCode, title = snap.Title,
        cols = snap.Cols, rows = snap.Rows, cursor = new { x = snap.CursorX, y = snap.CursorY, visible = snap.CursorVisible },
        alt = snap.AltScreen, scrollbackTotal = snap.ScrollbackTotal, scrollbackStart = snap.ScrollbackStart, addedFrom = snap.AddedFrom,
        added = snap.Added.Select(r => r.Select(x => x.ToWire()).ToArray()).ToArray(),
        screen = snap.Screen.Select(r => r.Select(x => x.ToWire()).ToArray()).ToArray(),
    };

    /// <summary>Coalesces bursts of output into ~25 frames/s per shell.</summary>
    private void ScheduleFlush(string shell)
    {
        lock (_lock)
        {
            if (_subs[shell].Count == 0) return;
            if (Interlocked.Exchange(ref _flushPending[shell][0], 1) == 1) return;
            _flushTimers[shell]?.Dispose();
            _flushTimers[shell] = new System.Threading.Timer(_ => Flush(shell), null, 40, Timeout.Infinite);
        }
    }

    private void Flush(string shell)
    {
        Interlocked.Exchange(ref _flushPending[shell][0], 0);
        var s = Get(shell);
        if (s == null) return;
        List<Subscriber> subs;
        lock (_lock) subs = [.. _subs[shell]];
        foreach (var sub in subs)
        {
            if (Interlocked.CompareExchange(ref sub.Busy, 1, 0) != 0) { ScheduleFlush(shell); continue; } // previous frame still in flight
            var snap = s.Screen.Snapshot(sub.SentTotal);
            sub.SentTotal = snap.ScrollbackTotal;
            var frame = Frame(shell, s, snap, sub.Seq++, false);
            _ = sub.Client.PushAsync("terminal.output", frame).ContinueWith(_ => Interlocked.Exchange(ref sub.Busy, 0));
        }
    }

    public void Dispose()
    {
        foreach (var t in _flushTimers.Values) t?.Dispose();
        TerminalSession?[] all;
        lock (_lock) { all = [.. _sessions.Values]; foreach (var k in _sessions.Keys.ToList()) _sessions[k] = null; }
        foreach (var s in all) s?.Dispose();
    }
}
