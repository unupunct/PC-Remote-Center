using System.IO;
using System.Windows;
using RemoteControlServer.App.Services;
using RemoteControlServer.App.ViewModels;
using RemoteControlServer.App.Views;
using RemoteControlServer.Networking;
using RemoteControlServer.Services;
using RemoteControlServer.Storage;

namespace RemoteControlServer.App;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showEvent;
    private ServerRuntime? _runtime;
    private AppViewModel? _vm;
    private MainWindow? _window;
    private TrayService? _tray;
    private TerminalWindowService? _terms;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\PCRemoteCenter.Server", out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PCRemoteCenter.Show");
        if (!first) { _showEvent.Set(); Shutdown(); return; }

        DispatcherUnhandledException += (_, a) => { CrashLog(a.Exception); a.Handled = true; ShowCrash(a.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog(a.Exception); a.SetObserved(); };

        try
        {
            _runtime = new ServerRuntime();
            _vm = new AppViewModel(_runtime);
            _terms = new TerminalWindowService(_runtime);
            _tray = new TrayService(_runtime);
            _tray.OpenRequested += OpenDashboard;
            _tray.ExitRequested += ExitApp;
            _vm.DeviceConnectedNotice += (name, ip, isNew) =>
                _tray.Notify(isNew ? "New device paired" : "Device connected", $"{name} connected from {ip}.", warning: isNew);
            _window = new MainWindow(_vm, _terms);

            var thread = new Thread(() => { while (_showEvent.WaitOne()) { if (_exiting) return; Dispatcher.BeginInvoke(OpenDashboard); } }) { IsBackground = true };
            thread.Start();

            if (!e.Args.Contains("--minimized")) { _window.Show(); }
            if (_runtime.Core.Settings.AutoStartServer)
            {
                try { await _runtime.Core.StartAsync(); }
                catch (Exception ex)
                {
                    _tray.Notify("Server could not start", ex.Message, true);
                    if (!_window.IsVisible) OpenDashboard();
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog(ex);
            MessageBox.Show("PC Remote Center could not start:\n\n" + ex.Message + "\n\nDetails were written to the log folder.", "PC Remote Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OpenDashboard() { _window?.ShowAndActivate(); }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { _runtime?.Core.StopAsync().Wait(5000); } catch { }
        _terms?.CloseAll();
        _tray?.Dispose();
        _vm?.Dispose();
        if (_window != null) { _window.ReallyClose = true; _window.Close(); }
        _showEvent?.Set();
        try { _runtime?.Dispose(); } catch { }
        try { _single?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    private static void CrashLog(Exception? ex)
    {
        try
        {
            AppLog.Error("Unhandled exception", ex);
            var dir = Path.Combine(DataPaths.Default, "logs"); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {ex}\r\n\r\n");
        }
        catch { }
    }

    private static void ShowCrash(Exception ex) =>
        MessageBox.Show("An unexpected error occurred. The server keeps running.\n\n" + ex.Message + "\n\nDetails were written to crash.log.", "PC Remote Center", MessageBoxButton.OK, MessageBoxImage.Warning);
}
