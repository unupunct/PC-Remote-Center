using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RemoteControlServer.App.Views;
using RemoteControlServer.Networking;
using RemoteControlServer.Services;
using RemoteControlServer.Terminal;

namespace RemoteControlServer.App.Services;

public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "PCRemoteCenter";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) != null;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true) ?? Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(Name, false);
    }
}

public static class FirewallService
{
    public const string RuleName = "PC Remote Center";

    /// <summary>Creates inbound allow rules (private profile, local subnet only). Windows shows its UAC prompt - nothing happens silently.</summary>
    public static void CreateRules(int port, int discoveryPort)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");
        if (exe.Contains('"')) throw new InvalidOperationException("Unsupported program path.");
        if (port is < 1 or > 65535 || discoveryPort is < 1 or > 65535) throw new ArgumentException("Invalid port.");
        RunNetshScript(
        [
            $"advfirewall firewall delete rule name=\"{RuleName}\"",
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private remoteip=LocalSubnet program=\"{exe}\" description=\"Remote control server (TLS)\"",
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport={discoveryPort} profile=private remoteip=LocalSubnet program=\"{exe}\" description=\"Phone discovery\"",
        ]);
    }

    public static void RemoveRules() => RunNetshScript([$"advfirewall firewall delete rule name=\"{RuleName}\""]);

    private static void RunNetshScript(string[] lines)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pcrc-fw-" + Guid.NewGuid().ToString("N") + ".netsh");
        File.WriteAllLines(tmp, lines);
        try
        {
            var psi = new ProcessStartInfo("netsh.exe") { Verb = "runas", UseShellExecute = true, Arguments = $"-f \"{tmp}\"", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
            if (!p.HasExited) throw new TimeoutException("netsh did not finish.");
            // a delete of a non-existing rule returns an error for that line; only the final exit state matters for the user
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator permission was declined."); }
        finally { try { File.Delete(tmp); } catch { } }
    }
}

/// <summary>Opens and keeps one visible terminal window per shell on this PC.</summary>
public sealed class TerminalWindowService
{
    private readonly ServerRuntime _rt;
    private readonly Dictionary<string, TerminalWindow> _windows = new();

    public TerminalWindowService(ServerRuntime rt)
    {
        _rt = rt;
        rt.Terminals.SessionStarted += s => Application.Current.Dispatcher.BeginInvoke(() => OnStarted(s));
    }

    private void OnStarted(TerminalSession s)
    {
        if (_windows.TryGetValue(s.Shell, out var w)) w.Attach(s);
        else { w = new TerminalWindow(s); _windows[s.Shell] = w; }
        if (_rt.Core.Settings.ShowTerminalWindows) ShowWindow(w);
    }

    private static void ShowWindow(TerminalWindow w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public void Show(string shell)
    {
        if (!_windows.TryGetValue(shell, out var w))
        {
            var s = _rt.Terminals.Get(shell);
            if (s == null) return;
            w = new TerminalWindow(s); _windows[shell] = w;
        }
        ShowWindow(w);
    }

    public void CloseAll()
    {
        foreach (var w in _windows.Values) { w.AllowClose = true; w.Close(); }
        _windows.Clear();
    }
}

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _status, _toggle;
    private readonly ServerRuntime _rt;

    public event Action? OpenRequested, ExitRequested;

    public TrayService(ServerRuntime rt)
    {
        _rt = rt;
        var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        _icon = new System.Windows.Forms.NotifyIcon { Icon = new System.Drawing.Icon(sri.Stream), Visible = true, Text = "PC Remote Center" };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _status = new System.Windows.Forms.ToolStripMenuItem("Status") { Enabled = false };
        _toggle = new System.Windows.Forms.ToolStripMenuItem("Stop server");
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Open dashboard", null, (_, _) => OpenRequested?.Invoke()) { Font = new System.Drawing.Font(System.Drawing.SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_toggle);
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Restart server", null, async (_, _) => await Safe(rt.Core.RestartAsync)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Exit (stops the server)", null, (_, _) => ExitRequested?.Invoke()));
        _toggle.Click += async (_, _) => await Safe(() => rt.Core.State == ServerState.Running ? rt.Core.StopAsync() : rt.Core.StartAsync());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        rt.Core.StateChanged += Update; rt.Core.ClientsChanged += Update;
        Update();
    }

    private async Task Safe(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) { _icon.ShowBalloonTip(5000, "PC Remote Center", "Server error: " + ex.Message, System.Windows.Forms.ToolTipIcon.Error); }
    }

    private void Update()
    {
        var s = _rt.Core.State; int n = _rt.Core.Clients.Count;
        var text = s == ServerState.Running ? $"Running on port {_rt.Core.ListeningPort} · {n} device(s) connected" : s.ToString();
        _status.Text = text;
        _toggle.Text = s == ServerState.Running ? "Stop server" : "Start server";
        _icon.Text = ("PC Remote Center — " + text).Substring(0, Math.Min(63, ("PC Remote Center — " + text).Length));
    }

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(6000, title, text, warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
