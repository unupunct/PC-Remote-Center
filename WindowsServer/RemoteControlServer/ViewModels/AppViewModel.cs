using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using RemoteControlServer.Models;
using RemoteControlServer.Monitoring;
using RemoteControlServer.Networking;
using RemoteControlServer.Security;
using RemoteControlServer.Services;
using RemoteControlServer.Storage;

namespace RemoteControlServer.App.ViewModels;

public class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CoreVm { public string Name { get; init; } = ""; public double Value { get; init; } public string Text => $"{Value:0}%"; }

public sealed class DiskVm
{
    public string Id { get; init; } = ""; public string Title { get; init; } = ""; public string Capacity { get; init; } = "";
    public double Percent { get; init; } public string Speed { get; init; } = ""; public string Temp { get; init; } = "";
}

public sealed class DeviceVm
{
    public string Id { get; init; } = ""; public string Name { get; init; } = ""; public string Model { get; init; } = "";
    public string Permissions { get; init; } = ""; public string LastSeen { get; init; } = ""; public string LastIp { get; init; } = "";
    public string Status { get; init; } = ""; public bool Revoked { get; init; } public Permission Perm { get; init; }
}

public sealed class ConnVm { public string SessionId { get; init; } = ""; public string DeviceId { get; init; } = ""; public string Name { get; init; } = ""; public string Ip { get; init; } = ""; public string Since { get; init; } = ""; public string Permissions { get; init; } = ""; }
public sealed class AuditVm { public string Time { get; init; } = ""; public string Actor { get; init; } = ""; public string Action { get; init; } = ""; public string Detail { get; init; } = ""; public string Result { get; init; } = ""; public bool Success { get; init; } }
public sealed class HistoryVm { public string Time { get; init; } = ""; public string Device { get; init; } = ""; public string Ip { get; init; } = ""; public string Duration { get; init; } = ""; }

public sealed class AppViewModel : Bindable, IDisposable
{
    public ServerRuntime Runtime { get; }
    public ServerCore Core => Runtime.Core;
    public BackupService Backup { get; }
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private IDisposable? _lease;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastIn, _lastOut; private DateTime _lastTraffic = DateTime.UtcNow;

    // ---- server
    private string _stateText = "Stopped"; public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    private Brush _stateBrush = Brushes.Gray; public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    private string _uptime = "—"; public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }
    private string _traffic = "0 B/s ↓   0 B/s ↑"; public string Traffic { get => _traffic; private set => Set(ref _traffic, value); }
    private string _totals = ""; public string Totals { get => _totals; private set => Set(ref _totals, value); }
    private string _endpoint = ""; public string Endpoint { get => _endpoint; private set => Set(ref _endpoint, value); }
    private string _error = ""; public string ServerError { get => _error; private set => Set(ref _error, value); }
    private int _clientCount; public int ClientCount { get => _clientCount; private set => Set(ref _clientCount, value); }
    public string Fingerprint => FormatFp(Core.Certs.Fingerprint);
    public string RunningAs { get; } = (Environment.GetEnvironmentVariable("PCRC_DEMO") == "1" ? "DEMO-PC\\user" : $"{Environment.UserDomainName}\\{Environment.UserName}") + (IsAdmin() ? "  (administrator)" : "  (standard user)");
    public string LocalAddresses => string.Join(", ", NetworkInfo.LocalAddresses().Select(a => a.ToString()));

    // ---- hardware
    private double? _cpu, _gpu, _ram, _disk;
    public double? Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double? Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public double? Ram { get => _ram; private set => Set(ref _ram, value); }
    public double? DiskPct { get => _disk; private set => Set(ref _disk, value); }
    private string _cpuModel = "—", _cpuDetail = "—", _gpuModel = "No GPU reported", _gpuDetail = "—", _ramDetail = "—", _diskDetail = "—";
    public string CpuModel { get => _cpuModel; private set => Set(ref _cpuModel, value); }
    public string CpuDetail { get => _cpuDetail; private set => Set(ref _cpuDetail, value); }
    public string GpuModel { get => _gpuModel; private set => Set(ref _gpuModel, value); }
    public string GpuDetail { get => _gpuDetail; private set => Set(ref _gpuDetail, value); }
    public string RamDetail { get => _ramDetail; private set => Set(ref _ramDetail, value); }
    public string DiskDetail { get => _diskDetail; private set => Set(ref _diskDetail, value); }
    private IReadOnlyList<double> _cpuH = [], _gpuH = [], _ramH = [];
    public IReadOnlyList<double> CpuHistory { get => _cpuH; private set => Set(ref _cpuH, value); }
    public IReadOnlyList<double> GpuHistory { get => _gpuH; private set => Set(ref _gpuH, value); }
    public IReadOnlyList<double> RamHistory { get => _ramH; private set => Set(ref _ramH, value); }
    private readonly List<double> _cpuL = [], _gpuL = [], _ramL = [];
    public ObservableCollection<CoreVm> Cores { get; } = [];
    public ObservableCollection<DiskVm> Disks { get; } = [];

    // ---- lists
    public ObservableCollection<DeviceVm> Devices { get; } = [];
    public ObservableCollection<ConnVm> Connected { get; } = [];
    public ObservableCollection<AuditVm> Audit { get; } = [];
    public ObservableCollection<AuditVm> RecentActions { get; } = [];
    public ObservableCollection<HistoryVm> History { get; } = [];
    public ObservableCollection<Shortcut> Shortcuts { get; } = [];
    public ObservableCollection<string> WolRows { get; } = [];

    // ---- pairing
    private ImageSource? _qr; public ImageSource? PairQr { get => _qr; private set { Set(ref _qr, value); Raise(nameof(NoQrVisibility)); } }
    private string _pairCode = ""; public string PairCode { get => _pairCode; private set => Set(ref _pairCode, value); }
    private string _pairInfo = "No active pairing code"; public string PairInfo { get => _pairInfo; private set => Set(ref _pairInfo, value); }
    private string _pairUri = ""; public string PairUri { get => _pairUri; private set => Set(ref _pairUri, value); }

    public bool CanStart => Core.State is ServerState.Stopped or ServerState.Faulted;
    public bool CanStop => Core.State == ServerState.Running;
    public Visibility NoClientsVisibility => ClientCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoQrVisibility => PairQr == null ? Visibility.Visible : Visibility.Collapsed;

    public event Action<string, string, bool>? DeviceConnectedNotice;

    public AppViewModel(ServerRuntime runtime)
    {
        Runtime = runtime;
        Backup = new BackupService(Core.SettingsStore, Core.Shortcuts, Core.Devices);
        Core.StateChanged += () => _ui.BeginInvoke(RefreshServer);
        Core.ClientsChanged += () => _ui.BeginInvoke(() => { RefreshConnected(); RefreshDevices(); });
        Core.Devices.Changed += () => _ui.BeginInvoke(RefreshDevices);
        Core.Shortcuts.Changed += () => _ui.BeginInvoke(RefreshShortcuts);
        Core.Audit.Added += _ => _ui.BeginInvoke(RefreshAuditSoon);
        Core.DeviceConnected += (n, ip, first) => _ui.BeginInvoke(() => DeviceConnectedNotice?.Invoke(n, ip, first));
        Core.Pairing.Changed += () => _ui.BeginInvoke(RefreshPairing);
        Runtime.Monitor.Sampled += s => _ui.BeginInvoke(() => ApplySnapshot(s));
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        RefreshAll();
    }

    /// <summary>Called when the dashboard window becomes visible/hidden so sampling only runs while someone watches.</summary>
    public void SetVisible(bool visible)
    {
        if (visible && _lease == null) { _lease = Runtime.Monitor.Acquire(); RefreshAll(); }
        else if (!visible && _lease != null) { _lease.Dispose(); _lease = null; }
    }

    // ------------------------------------------------------------------ refresh

    public void RefreshAll() { RefreshServer(); RefreshConnected(); RefreshDevices(); RefreshShortcuts(); RefreshAudit(); RefreshHistory(); RefreshPairing(); }

    private bool _auditPending;
    private void RefreshAuditSoon()
    {
        if (_auditPending) return; _auditPending = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _auditPending = false; RefreshAudit(); RefreshHistory(); });
    }

    public void RefreshServer()
    {
        var s = Core.State;
        StateText = s switch { ServerState.Running => "Running", ServerState.Starting => "Starting…", ServerState.Stopping => "Stopping…", ServerState.Faulted => "Error", _ => "Stopped" };
        StateBrush = s switch { ServerState.Running => (Brush)Application.Current.Resources["GreenBrush"], ServerState.Faulted => (Brush)Application.Current.Resources["RedBrush"], ServerState.Stopped => (Brush)Application.Current.Resources["MutedBrush"], _ => (Brush)Application.Current.Resources["AmberBrush"] };
        Endpoint = s == ServerState.Running ? $"wss://{Core.Settings.ComputerName}:{Core.ListeningPort}" + (Environment.GetEnvironmentVariable("PCRC_DEMO") == "1" ? "" : $"   (LAN: {LocalAddresses})") : "—";
        ServerError = s == ServerState.Faulted ? (Core.LastError ?? "") : "";
        Raise(nameof(Fingerprint)); Raise(nameof(LocalAddresses)); Raise(nameof(CanStart)); Raise(nameof(CanStop));
    }

    private void OnTick()
    {
        if (Core.StartedUtc is { } st) { var u = DateTime.UtcNow - st; Uptime = u.TotalDays >= 1 ? $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m" : $"{u.Hours:00}:{u.Minutes:00}:{u.Seconds:00}"; }
        else Uptime = "—";
        var now = DateTime.UtcNow; var dt = Math.Max(0.1, (now - _lastTraffic).TotalSeconds);
        long i = Core.BytesIn, o = Core.BytesOut;
        Traffic = $"{Rate((i - _lastIn) / dt)} ↓   {Rate((o - _lastOut) / dt)} ↑";
        Totals = $"Total received {Bytes(i)} · sent {Bytes(o)}";
        _lastIn = i; _lastOut = o; _lastTraffic = now;
        if (Core.Pairing.Active != null) UpdatePairInfo();
        else if (PairCode.Length > 0) RefreshPairing();
    }

    private void RefreshConnected()
    {
        Connected.Clear();
        foreach (var c in Core.Clients)
            Connected.Add(new ConnVm { SessionId = c.SessionId, DeviceId = c.DeviceId, Name = c.Name, Ip = c.Ip, Since = c.ConnectedUtc.ToLocalTime().ToString("HH:mm:ss"), Permissions = string.Join(", ", c.Permissions.ToNames()) });
        ClientCount = Connected.Count;
        Raise(nameof(NoClientsVisibility));
    }

    public void RefreshDevices()
    {
        var live = Core.Clients.Select(c => c.DeviceId).ToHashSet();
        Devices.Clear();
        foreach (var d in Core.Devices.List())
            Devices.Add(new DeviceVm
            {
                Id = d.Id, Name = d.Name, Model = d.Model, Perm = d.Permissions, Permissions = string.Join(", ", d.Permissions.ToNames()),
                LastSeen = d.LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never", LastIp = d.LastIp,
                Status = d.Revoked ? "Revoked" : live.Contains(d.Id) ? "Connected" : "Paired", Revoked = d.Revoked,
            });
    }

    public void RefreshShortcuts() { Shortcuts.Clear(); foreach (var s in Core.Shortcuts.List()) Shortcuts.Add(s); }

    public void RefreshAudit()
    {
        Audit.Clear();
        foreach (var e in Core.Audit.Recent(400)) Audit.Add(ToVm(e));
        RecentActions.Clear();
        foreach (var e in Audit.Take(8)) RecentActions.Add(e);
    }

    private static AuditVm ToVm(AuditEntry e) => new() { Time = e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Actor = e.Actor, Action = e.Action, Detail = e.Detail, Success = e.Success, Result = e.Success ? "ok" : "failed" };

    public void RefreshHistory()
    {
        History.Clear();
        foreach (var h in Core.History.Recent(100))
            History.Add(new HistoryVm
            {
                Time = h.ConnectedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Device = h.DeviceName, Ip = h.Ip,
                Duration = h.DisconnectedUtc is { } d ? (d - h.ConnectedUtc).ToString(@"hh\:mm\:ss") : "connected",
            });
    }

    // ------------------------------------------------------------------ pairing

    public void CreatePairing(Permission perms)
    {
        if (Core.State != ServerState.Running) throw new InvalidOperationException("Start the server first.");
        Core.Pairing.CancelAll();
        var offer = Core.Pairing.Create(perms, TimeSpan.FromMinutes(Math.Clamp(Core.Settings.PairingCodeMinutes, 1, 30)));
        Core.Audit.Write("owner", "pairing.code", "pairing code created for permissions: " + string.Join(',', perms.ToNames()));
        RefreshPairing();
    }

    public void CancelPairing() { Core.Pairing.CancelAll(); }

    private void RefreshPairing()
    {
        var offer = Core.Pairing.Active;
        if (offer == null) { PairCode = ""; PairQr = null; PairUri = ""; PairInfo = "No active pairing code"; return; }
        PairCode = offer.Code.Insert(4, " ");
        VpnDetector.InvalidateCache();
        var hostList = VpnDetector.PairingHosts(VpnDetector.CachedDetect(), Core.Settings.AllowTailscale, Core.Settings.AllowWireGuard);
        if (hostList.Count == 0) hostList = NetworkInfo.LocalAddresses();
        PairUri = PairingQr.BuildUri(hostList, Core.ListeningPort, offer.Code, Core.Certs.Fingerprint, Core.Settings.ComputerName);
        var png = PairingQr.RenderPng(PairUri); // black on white with quiet zone: readable by every scanner
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png)) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
        bmp.Freeze();
        PairQr = bmp;
        UpdatePairInfo();
    }

    private void UpdatePairInfo()
    {
        var offer = Core.Pairing.Active;
        if (offer == null) return;
        var left = offer.ExpiresUtc - DateTime.UtcNow;
        PairInfo = $"One-time code · expires in {Math.Max(0, (int)left.TotalMinutes)}:{Math.Max(0, left.Seconds):00} · grants: {string.Join(", ", offer.Permissions.ToNames())}";
    }

    // ------------------------------------------------------------------ hardware

    private void ApplySnapshot(HardwareSnapshot s)
    {
        Cpu = s.Cpu.Usage; CpuModel = s.Cpu.Model;
        CpuDetail = $"{s.Cpu.PhysicalCores?.ToString() ?? "?"} cores / {s.Cpu.LogicalCores} threads · {(s.Cpu.FrequencyMhz is { } f ? $"{f / 1000:0.00} GHz" : "clock n/a")} · {(s.Cpu.TemperatureC is { } t ? $"{t:0} °C" : "temperature n/a (run as administrator)")}";
        var g = s.Gpus.FirstOrDefault();
        Gpu = g?.Usage; GpuModel = g?.Name ?? "No GPU reported";
        GpuDetail = g == null ? "—" : $"{(g.VramUsedMb is { } u && g.VramTotalMb is { } tt ? $"VRAM {u / 1024:0.0} / {tt / 1024:0.0} GB ({g.VramPercent:0}%)" : "VRAM n/a")} · {(g.TemperatureC is { } gt ? $"{gt:0} °C" : "temperature n/a")}";
        Ram = s.Ram.Percent;
        RamDetail = $"{Bytes(s.Ram.UsedBytes)} used of {Bytes(s.Ram.TotalBytes)} · {Bytes(s.Ram.AvailableBytes)} available";
        var sysDisk = s.Disks.FirstOrDefault(d => d.Id.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) ?? s.Disks.FirstOrDefault();
        DiskPct = sysDisk?.Percent;
        DiskDetail = sysDisk == null ? "—" : $"{sysDisk.Id} {Bytes(sysDisk.UsedBytes)} of {Bytes(sysDisk.TotalBytes)} · {Bytes(sysDisk.FreeBytes)} free";
        Push(_cpuL, s.Cpu.Usage ?? 0); CpuHistory = _cpuL.ToList();
        Push(_gpuL, g?.Usage ?? 0); GpuHistory = _gpuL.ToList();
        Push(_ramL, s.Ram.Percent); RamHistory = _ramL.ToList();

        Cores.Clear();
        for (int i = 0; i < s.Cpu.Cores.Length; i++) Cores.Add(new CoreVm { Name = $"Core {i}", Value = s.Cpu.Cores[i] });
        Disks.Clear();
        foreach (var d in s.Disks)
            Disks.Add(new DiskVm
            {
                Id = d.Id, Title = $"{d.Id}  {d.Label}" + (d.Model.Length > 0 ? $"  ·  {d.Model}" : ""), Percent = d.Percent,
                Capacity = $"{Bytes(d.UsedBytes)} / {Bytes(d.TotalBytes)}  ({Bytes(d.FreeBytes)} free)",
                Speed = $"Read {(d.ReadBps is { } r ? Rate(r) : "n/a")}   Write {(d.WriteBps is { } w ? Rate(w) : "n/a")}",
                Temp = d.TemperatureC is { } tc ? $"{tc:0} °C" : "temp n/a",
            });
    }

    private static void Push(List<double> l, double v) { l.Add(v); if (l.Count > 120) l.RemoveAt(0); }

    public ObservableCollection<string> VpnRows { get; } = [];
    private string _vpnHint = ""; public string VpnHint { get => _vpnHint; private set => Set(ref _vpnHint, value); }

    /// <summary>Lists detected Tailscale / WireGuard / other VPN adapters and the address a phone would use.</summary>
    public void LoadVpn()
    {
        VpnDetector.InvalidateCache();
        var all = VpnDetector.CachedDetect().Where(i => i.Kind != NetworkKind.Lan && i.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
        VpnRows.Clear();
        foreach (var i in all)
        {
            var kind = i.Kind switch { NetworkKind.Tailscale => "Tailscale", NetworkKind.WireGuard => "WireGuard", _ => "Other VPN" };
            var allowed = i.Kind == NetworkKind.Tailscale ? Core.Settings.AllowTailscale : i.Kind == NetworkKind.WireGuard ? Core.Settings.AllowWireGuard : false;
            var note = i.Kind == NetworkKind.OtherVpn ? "detected (not managed here)" : allowed ? "connections allowed" : "connections NOT allowed yet";
            if (i.Kind == NetworkKind.WireGuard && i.PrefixLength >= 32) note = "tunnel address is a /32: add your peer subnet under 'Extra allowed networks'";
            VpnRows.Add($"{kind}: {i.Address}  (adapter \"{i.Name}\") – {note}");
        }
        if (VpnRows.Count == 0) VpnRows.Add("No Tailscale or WireGuard adapter found. Install and sign in to Tailscale (tailscale.com) or bring up a WireGuard tunnel, then press Refresh.");
        var hosts = VpnDetector.PairingHosts(VpnDetector.CachedDetect(), Core.Settings.AllowTailscale, Core.Settings.AllowWireGuard);
        VpnHint = hosts.Count == 0 ? "" : $"Phone addresses (pairing QR uses these, LAN first): {string.Join(", ", hosts)}   port {Core.Settings.Port}";
    }

    public void LoadWol()
    {
        WolRows.Clear();
        var wol = NetworkAdapters.WakeOnLanEnabled();
        foreach (var a in NetworkAdapters.MacAddresses())
        {
            bool? en = wol.TryGetValue(a.Name, out var v) ? v : null;
            WolRows.Add($"{a.Name}   MAC {a.Mac}   {a.Ipv4}   Wake-on-LAN in Windows: {(en == null ? "unknown" : en.Value ? "enabled" : "disabled")}");
        }
        if (WolRows.Count == 0) WolRows.Add("No active Ethernet/Wi-Fi adapter found.");
    }

    // ------------------------------------------------------------------ helpers

    public static string Bytes(double b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
    public static string Rate(double b) => Bytes(b) + "/s";
    public static string FormatFp(string hex) => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    private static bool IsAdmin() { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }

    public void Dispose() { _tick.Stop(); _lease?.Dispose(); }
}
