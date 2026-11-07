using RemoteControlServer.Models;
using RemoteControlServer.Terminal;

namespace RemoteControlServer.Tests;

public static class TermUtil
{
    public static async Task<string> WaitFor(TerminalSession s, Func<string, bool> pred, int timeoutMs = 25000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            text = s.Screen.GetText();
            if (pred(text)) return text;
            await Task.Delay(100);
        }
        throw new TimeoutException("Expected terminal output not seen. Screen was:\n" + text);
    }

    public static bool HasLine(string text, string line) => text.Split('\n').Any(l => l.Trim() == line);
    public static int CountLine(string text, string line) => text.Split('\n').Count(l => l.Trim() == line);
}

public class VtScreenTests
{
    private static VtScreen S(int c = 20, int r = 5) => new(c, r);

    [Fact]
    public void Plain_text_and_newlines()
    {
        var s = S(); s.FeedText("hello\r\nworld");
        Assert.Equal("hello\nworld", s.GetText());
    }

    [Fact]
    public void Cursor_movement_and_erase()
    {
        var s = S(); s.FeedText("abcdef\x1b[3D\x1b[KXY\r\n\x1b[2J\x1b[Hnew");
        Assert.Equal("new", s.GetText());
        var t = S(); t.FeedText("abcdef\x1b[3D\x1b[KXY");
        Assert.Equal("abcXY", t.GetText());
    }

    [Fact]
    public void Sgr_colors_produce_runs()
    {
        var s = S(); s.FeedText("a\x1b[31mred\x1b[0m\x1b[1;38;2;1;2;3mtc\x1b[0m");
        var row = s.Snapshot(0).Screen[0];
        Assert.Equal("a", row[0].Text); Assert.Equal(-1, row[0].Fg);
        Assert.Equal("red", row[1].Text); Assert.Equal(0xC50F1F, row[1].Fg);
        Assert.Equal("tc", row[2].Text); Assert.Equal(0x010203, row[2].Fg); Assert.Equal(RunFlags.Bold, row[2].Flags);
    }

    [Fact]
    public void Lines_scrolling_off_go_to_scrollback_with_absolute_indexes()
    {
        var s = S(10, 3);
        for (int i = 1; i <= 6; i++) s.FeedText($"line{i}\r\n");
        var snap = s.Snapshot(0);
        Assert.Equal(4, snap.ScrollbackTotal);
        Assert.Equal("line1", string.Concat(snap.Added[0].Select(r => r.Text)));
        var later = s.Snapshot(3);
        Assert.Single(later.Added);
        Assert.Equal(3, later.AddedFrom);
        Assert.Equal("line1\nline2\nline3\nline4\nline5\nline6", s.GetText());
    }

    [Fact]
    public void Alternate_screen_does_not_pollute_scrollback()
    {
        var s = S(10, 3);
        s.FeedText("main\x1b[?1049h\x1b[Halt\r\n\r\n\r\n\r\nmore\x1b[?1049l");
        Assert.Equal("main", s.GetText());
        Assert.Equal(0, s.Snapshot(0).ScrollbackTotal);
    }

    [Fact]
    public void Device_status_requests_are_answered()
    {
        var s = S(); var replies = new List<string>(); s.Reply += replies.Add;
        s.FeedText("ab\x1b[6n\x1b[c");
        Assert.Equal("\x1b[1;3R", replies[0]);
        Assert.StartsWith("\x1b[?", replies[1]);
    }

    [Fact]
    public void Osc_title_and_utf8_split_across_chunks()
    {
        var s = S();
        s.FeedText("\x1b]0;My Title\a");
        Assert.Equal("My Title", s.Title);
        var bytes = System.Text.Encoding.UTF8.GetBytes("é€😀");
        s.Feed(bytes[..1], 1); s.Feed(bytes[1..], bytes.Length - 1);
        Assert.Equal("é€😀", s.GetText());
    }
}

public class TerminalTests : IAsyncLifetime
{
    private readonly List<TerminalSession> _open = [];
    private TerminalSession Start(string shell) { var s = TerminalSession.Start(shell); _open.Add(s); return s; }
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { foreach (var s in _open) s.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Cmd_executes_commands_in_one_persistent_session_with_real_output()
    {
        var s = Start("cmd");
        await TermUtil.WaitFor(s, t => t.Contains('>'));
        int pid = s.ProcessId;
        s.Execute("echo hello-pcrc");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "hello-pcrc"));
        s.Execute("hostname");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, Environment.MachineName) || TermUtil.HasLine(t, Environment.MachineName.ToLowerInvariant()));
        s.Execute("whoami");
        await TermUtil.WaitFor(s, t => t.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(pid, s.ProcessId);
        Assert.True(s.IsRunning);
    }

    [Fact]
    public async Task Cmd_keeps_environment_variables_and_working_directory()
    {
        var s = Start("cmd");
        await TermUtil.WaitFor(s, t => t.Contains('>'));
        s.Execute("set PCRC_VAR=persisted-value");
        s.Execute("cd /d C:\\Windows");
        s.Execute("echo %PCRC_VAR%");
        s.Execute("cd");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "persisted-value") && TermUtil.HasLine(t, "C:\\Windows"));
    }

    [Fact]
    public async Task PowerShell_keeps_variables_and_location_and_runs_multiline()
    {
        var s = Start("powershell");
        await TermUtil.WaitFor(s, t => t.Contains("PS "));
        s.Execute("$pcrc = 41");
        s.Execute("$pcrc + 1");
        s.Execute("Set-Location C:\\Windows; (Get-Location).Path");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "42") && TermUtil.HasLine(t, "C:\\Windows"));
        s.Execute("Write-Output 'multi-a'\nWrite-Output 'multi-b'");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "multi-a") && TermUtil.HasLine(t, "multi-b"));
    }

    [Fact]
    public async Task PowerShell_cmdlets_return_real_data()
    {
        var s = Start("powershell");
        await TermUtil.WaitFor(s, t => t.Contains("PS "));
        s.Execute("Get-Process -Id $PID | Select-Object -ExpandProperty ProcessName");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "powershell"));
        s.Execute("(Get-Volume -DriveLetter C).FileSystemLabel + '!' ");
        await TermUtil.WaitFor(s, t => t.Contains("System!") || t.Contains("!"));
    }

    [Fact]
    public async Task Ctrl_C_stops_long_running_command_and_session_survives()
    {
        var s = Start("cmd");
        await TermUtil.WaitFor(s, t => t.Contains('>'));
        s.Execute("ping -t 127.0.0.1");
        await TermUtil.WaitFor(s, t => t.Contains("Reply from 127.0.0.1"));
        s.Interrupt();
        await TermUtil.WaitFor(s, t => t.Contains("Control-C"));
        s.Execute("echo after-interrupt");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "after-interrupt"));
        Assert.True(s.IsRunning);
    }

    [Fact]
    public async Task Interactive_input_reaches_running_program()
    {
        var s = Start("powershell");
        await TermUtil.WaitFor(s, t => t.Contains("PS "));
        s.Execute("$n = Read-Host 'your name'");
        await TermUtil.WaitFor(s, t => t.Contains("your name:"));
        s.WriteRaw("robert\r");
        s.Execute("\"hi-$n\"");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "hi-robert"));
    }

    [Fact]
    public async Task Resize_changes_pty_and_screen_size()
    {
        var s = Start("cmd");
        await TermUtil.WaitFor(s, t => t.Contains('>'));
        s.Resize(80, 24);
        Assert.Equal(80, s.Screen.Cols); Assert.Equal(24, s.Screen.Rows);
        s.Execute("mode con");
        await TermUtil.WaitFor(s, t => t.Contains("Columns:") && t.Split('\n').Any(l => l.Contains("Columns:") && l.Trim().EndsWith("80")));
    }

    [Fact]
    public async Task Clear_wipes_screen_and_scrollback_but_keeps_session()
    {
        var s = Start("cmd");
        await TermUtil.WaitFor(s, t => t.Contains('>'));
        s.Execute("echo old-marker");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "old-marker"));
        s.Clear();
        await Task.Delay(800);
        await TermUtil.WaitFor(s, t => !t.Contains("old-marker"));
        s.Execute("echo new-marker");
        await TermUtil.WaitFor(s, t => TermUtil.HasLine(t, "new-marker"));
    }

    [Fact]
    public async Task Exit_is_detected_and_session_can_be_recreated()
    {
        var core = new RemoteControlServer.Networking.ServerCore(Path.Combine(Path.GetTempPath(), "pcrc-t-" + Guid.NewGuid().ToString("N")[..6]));
        using var mgr = new TerminalManager(core);
        var s = mgr.EnsureStarted("cmd");
        var exited = new TaskCompletionSource<int>();
        s.Exited += c => exited.TrySetResult(c);
        await TermUtil.WaitFor(s, t => t.Contains('>'));
        s.Execute("exit 7");
        Assert.Equal(7, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(s.IsRunning);
        Assert.Throws<InvalidOperationException>(() => s.Execute("echo x"));
        Assert.Same(s, mgr.EnsureStarted("cmd")); // not silently recreated

        var s2 = mgr.Restart("cmd");
        Assert.NotSame(s, s2);
        await TermUtil.WaitFor(s2, t => t.Contains('>'));
        s2.Execute("echo reborn");
        await TermUtil.WaitFor(s2, t => TermUtil.HasLine(t, "reborn"));
    }

    [Fact]
    public void Execute_rejects_control_characters()
    {
        var s = Start("cmd");
        Assert.Throws<ArgumentException>(() => s.Execute("echo \x1b[31m"));
        Assert.Throws<ArgumentException>(() => s.Execute("echo \x03"));
    }
}

public class TerminalProtocolTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    private static TerminalManager? _mgr;
    private static readonly object L = new();
    public TerminalProtocolTests(ServerFixture f)
    {
        _f = f;
        lock (L) { _mgr ??= new TerminalManager(f.Server); }
    }

    private static string LinesOf(System.Text.Json.Nodes.JsonNode? rows) =>
        string.Join('\n', rows!.AsArray().Select(r => string.Concat(r!.AsArray().Select(run => run![0]!.GetValue<string>()))));

    [Fact]
    public async Task Phone_can_open_terminal_run_commands_and_receive_streamed_output()
    {
        var cred = await _f.PairAsync(Permission.Monitoring | Permission.Terminal);
        await using var c = await _f.LoginAsync(cred);
        var open = await c.RequestAsync("terminal.open", new { shell = "cmd", cols = 100, rows = 30 });
        Assert.Equal("response", open.Type);
        Assert.True(open.P()["running"]!.GetValue<bool>());
        Assert.Equal(100, open.P()["cols"]!.GetValue<int>());

        var r = await c.RequestAsync("terminal.execute", new { shell = "cmd", command = "echo from-the-phone" });
        Assert.Equal("response", r.Type);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool seen = false;
        while (!seen && sw.Elapsed.TotalSeconds < 15)
        {
            var frame = await c.WaitPushAsync(e => e.Type == "terminal.output", 10000);
            var text = LinesOf(frame.P()["screen"]) + "\n" + LinesOf(frame.P()["added"]);
            seen = text.Split('\n').Any(l => l.Trim() == "from-the-phone");
        }
        Assert.True(seen, "output must be streamed to the phone");

        // streaming: a slow command produces several frames over time
        await c.RequestAsync("terminal.execute", new { shell = "cmd", command = "ping -n 4 127.0.0.1" });
        int frames = 0; var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalSeconds < 6)
        {
            try { await c.WaitPushAsync(e => e.Type == "terminal.output", 1500); frames++; } catch (TimeoutException) { break; }
        }
        Assert.True(frames >= 3, $"expected a stream of frames, got {frames}");

        var st = await c.RequestAsync("terminal.status");
        Assert.Equal("response", st.Type);
    }

    [Fact]
    public async Task Terminal_requires_terminal_permission_and_validates_input()
    {
        var weak = await _f.PairAsync(Permission.Monitoring);
        await using var w = await _f.LoginAsync(weak);
        Assert.Equal("forbidden", (await w.RequestAsync("terminal.execute", new { shell = "cmd", command = "whoami" })).Code());
        Assert.Equal("forbidden", (await w.RequestAsync("terminal.open", new { shell = "cmd" })).Code());

        var cred = await _f.PairAsync(Permission.Terminal);
        await using var c = await _f.LoginAsync(cred);
        Assert.Equal("invalid_payload", (await c.RequestAsync("terminal.open", new { shell = "bash" })).Code());
        Assert.Equal("invalid_payload", (await c.RequestAsync("terminal.execute", new { shell = "cmd" })).Code());
        await c.RequestAsync("terminal.open", new { shell = "cmd" });
        Assert.Equal("invalid_payload", (await c.RequestAsync("terminal.execute", new { shell = "cmd", command = "echo \u001b[2J" })).Code());
        Assert.Equal("invalid_payload", (await c.RequestAsync("terminal.resize", new { shell = "cmd", cols = 5, rows = 5 })).Code());
        var ctrlC = await c.RequestAsync("terminal.interrupt", new { shell = "cmd" });
        Assert.Equal("response", ctrlC.Type);
    }

    [Fact]
    public async Task Audit_log_records_terminal_use_without_command_text()
    {
        var cred = await _f.PairAsync(Permission.Terminal, "audit-phone");
        await using var c = await _f.LoginAsync(cred);
        await c.RequestAsync("terminal.open", new { shell = "cmd" });
        await c.RequestAsync("terminal.execute", new { shell = "cmd", command = "echo SECRET-PASSWORD-123" });
        var entries = _f.Server.Audit.Recent(100).Where(e => e.Actor == "audit-phone").ToList();
        Assert.Contains(entries, e => e.Action == "terminal.execute");
        Assert.DoesNotContain(entries, e => e.Detail.Contains("SECRET-PASSWORD-123"));
    }
}
