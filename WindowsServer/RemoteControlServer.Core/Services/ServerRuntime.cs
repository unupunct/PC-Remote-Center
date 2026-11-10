using RemoteControlServer.Models;
using RemoteControlServer.Monitoring;
using RemoteControlServer.Networking;
using RemoteControlServer.Shortcuts;
using RemoteControlServer.Terminal;

namespace RemoteControlServer.Services;

/// <summary>Everything the server does, wired together. The WPF app and the tests both build one of these.</summary>
public sealed class ServerRuntime : IDisposable
{
    public ServerCore Core { get; }
    public SystemMonitor Monitor { get; }
    public MonitorModule MonitorModule { get; }
    public TerminalManager Terminals { get; }
    public PowerService Power { get; }
    public ShortcutService Shortcuts { get; }
    public ProcessService Processes { get; }

    public ServerRuntime(string? dataDir = null, ISystemActions? systemActions = null, IProcessLauncher? launcher = null)
        : this(new ServerCore(dataDir), systemActions, launcher) { }

    /// <summary>Attaches all modules to an existing core (used by tests that own the core).</summary>
    public static ServerRuntime AttachTo(ServerCore core, ISystemActions? systemActions = null, IProcessLauncher? launcher = null) =>
        new(core, systemActions, launcher);

    private ServerRuntime(ServerCore core, ISystemActions? systemActions, IProcessLauncher? launcher)
    {
        Core = core;
        Monitor = new SystemMonitor(() => Core.Settings.MonitorIntervalMs);
        MonitorModule = new MonitorModule(Core, Monitor);
        Terminals = new TerminalManager(Core);
        Power = new PowerService(Core, systemActions);
        Shortcuts = new ShortcutService(Core, launcher);
        Processes = new ProcessService(Core);

        Core.Handlers.Register("wol.info", Permission.Monitoring, (c, e) =>
        {
            var wol = NetworkAdapters.WakeOnLanEnabled();
            var adapters = NetworkAdapters.MacAddresses().Select(a =>
            {
                var d = Protocol.ToPayload(a);
                var name = d["name"]!.GetValue<string>();
                d["wakeOnLanEnabled"] = wol.TryGetValue(name, out var v) && v != null ? v : null;
                return d;
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                adapters,
                note = "Wake-on-LAN must be enabled in the BIOS/UEFI and the adapter's driver settings. The phone sends the magic packet; the PC cannot wake itself.",
            });
        });
        Core.Audit.Purge(Core.Settings.LogRetentionDays);
        AppLog.Purge(Core.Settings.LogRetentionDays);
    }

    public void Dispose()
    {
        Terminals.Dispose();
        Core.Dispose();
        Monitor.Dispose();
    }
}
