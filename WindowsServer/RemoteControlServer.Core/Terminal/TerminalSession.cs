using System.Text;
using RemoteControlServer.Services;

namespace RemoteControlServer.Terminal;

/// <summary>One persistent interactive shell (cmd.exe or powershell.exe) hosted in a ConPTY.</summary>
public sealed class TerminalSession : IDisposable
{
    public string Shell { get; }
    public string Title => Shell == "cmd" ? "Command Prompt" : "Windows PowerShell";
    public VtScreen Screen { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public bool IsRunning => _pty is { HasExited: false };
    public int? ExitCode => _pty?.ExitCode;
    public int ProcessId => _pty?.ProcessId ?? 0;
    /// <summary>Raised (on a background thread) whenever new output was processed.</summary>
    public event Action? OutputChanged;
    public event Action<int>? Exited;

    private ConPty? _pty;

    private TerminalSession(string shell, int cols, int rows)
    {
        Shell = shell;
        Screen = new VtScreen(cols, rows);
    }

    public static string CommandLineFor(string shell)
    {
        var sys = Environment.SystemDirectory;
        return shell switch
        {
            "cmd" => Path.Combine(sys, "cmd.exe"),
            "powershell" => $"\"{Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo",
            _ => throw new ArgumentException("Unknown shell '" + shell + "'"),
        };
    }

    public static TerminalSession Start(string shell, string? workingDirectory = null, int cols = 120, int rows = 30)
    {
        var s = new TerminalSession(shell, cols, rows);
        var cwd = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pending = new List<string>();
        s.Screen.Reply += reply =>
        {
            lock (pending)
            {
                if (s._pty == null) { pending.Add(reply); return; } // ConPTY asked something before Start() returned
            }
            try { s._pty.Write(reply); } catch { }
        };
        s.Screen.Changed += () => s.OutputChanged?.Invoke();
        s._pty = ConPty.Start(CommandLineFor(shell), Directory.Exists(cwd) ? cwd : null, cols, rows,
            (buf, n) => s.Screen.Feed(buf, n),
            code =>
            {
                AppLog.Info("Shell exited", new { shell, code });
                s.Exited?.Invoke(code);
                s.OutputChanged?.Invoke();
            });
        lock (pending)
        {
            foreach (var r in pending) { try { s._pty.Write(r); } catch { } }
            pending.Clear();
        }
        AppLog.Info("Shell started", new { shell, pid = s._pty.ProcessId });
        return s;
    }

    /// <summary>Raw terminal input (keystrokes, escape sequences). Control characters are passed through on purpose (Ctrl+C, arrows ...).</summary>
    public void WriteRaw(string data)
    {
        if (data.Length > 8192) throw new ArgumentException("Input too long.");
        if (data.Contains('\0')) throw new ArgumentException("Input contains a NUL character.");
        Require().Write(data);
    }

    /// <summary>Types a command into the existing shell and presses Enter. Multi-line text is sent line by line.</summary>
    public void Execute(string command)
    {
        if (command.Length > 64 * 1024) throw new ArgumentException("Command too long.");
        var sb = new StringBuilder();
        foreach (var ch in command)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { sb.Append('\r'); continue; }
            if (ch == '\t' || ch >= 0x20 && ch != 0x7f) { sb.Append(ch); continue; }
            throw new ArgumentException("Command contains a control character; use terminal.input for raw control input.");
        }
        sb.Append('\r');
        Require().Write(sb.ToString());
    }

    /// <summary>Ctrl+C: ETX through the pseudo console input makes conhost raise CTRL_C_EVENT for the foreground program.</summary>
    public void Interrupt() => Require().Write("\x03");

    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 20, 400); rows = Math.Clamp(rows, 5, 200);
        Screen.Resize(cols, rows);
        _pty?.Resize(cols, rows);
    }

    /// <summary>Clears the screen and scrollback and asks the shell to redraw its prompt.</summary>
    public void Clear()
    {
        Screen.ClearAll();
        if (IsRunning) Require().Write(Shell == "cmd" ? "cls\r" : "Clear-Host\r");
    }

    private ConPty Require() => _pty is { HasExited: false } p ? p : throw new InvalidOperationException("The shell is not running. Restart the session first.");

    public void Dispose() => _pty?.Dispose();
}
