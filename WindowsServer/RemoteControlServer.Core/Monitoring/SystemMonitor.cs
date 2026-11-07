using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Services;

namespace RemoteControlServer.Monitoring;

public sealed class CpuInfo
{
    public string Model { get; set; } = "";
    public double? Usage { get; set; }
    public double[] Cores { get; set; } = [];
    public int LogicalCores { get; set; }
    public int? PhysicalCores { get; set; }
    public double? FrequencyMhz { get; set; }
    public double? TemperatureC { get; set; }
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public double? Usage { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }
    public double? VramPercent { get; set; }
    public double? TemperatureC { get; set; }
}

public sealed class RamInfo
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double Percent { get; set; }
}

public sealed class DiskInfoDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public string Model { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes { get; set; }
    public double Percent { get; set; }
    public double? ReadBps { get; set; }
    public double? WriteBps { get; set; }
    public double? TemperatureC { get; set; }
}

public sealed class HardwareSnapshot
{
    public string TimeUtc { get; set; } = "";
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = [];
    public RamInfo Ram { get; set; } = new();
    public List<DiskInfoDto> Disks { get; set; } = [];
    public long UptimeSeconds { get; set; }
}

/// <summary>
/// Real hardware sampler. CPU/RAM/disk use Windows performance counters and Win32 APIs; temperatures,
/// GPU load and VRAM come from LibreHardwareMonitor (NVIDIA, AMD, Intel). Anything the hardware or
/// driver does not expose stays null - values are never invented. The sampler only runs while
/// somebody holds an <see cref="Acquire"/> lease (a phone subscription or the visible dashboard).
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly Func<int> _intervalMs;
    private readonly object _gate = new();
    private int _leases;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private Computer? _computer;
    private PerformanceCounter? _cpuTotal, _cpuFreq;
    private List<PerformanceCounter> _coreCounters = [];
    private readonly Dictionary<string, (PerformanceCounter read, PerformanceCounter write)> _diskCounters = new();
    private readonly string _cpuModel;
    private readonly int? _physicalCores;
    private Dictionary<string, string> _driveModels = new();
    private int _tick;
    private List<(string name, double? temp)> _storageTemps = [];

    public HardwareSnapshot? Latest { get; private set; }
    public event Action<HardwareSnapshot>? Sampled;
    public string? LhmStatus { get; private set; }

    public SystemMonitor(Func<int>? intervalMs = null)
    {
        _intervalMs = intervalMs ?? (() => 1500);
        _cpuModel = ReadCpuModel();
        _physicalCores = ReadPhysicalCores();
    }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_leases++ == 0) StartLoop();
        }
        return new Lease(this);
    }

    private sealed class Lease(SystemMonitor m) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) m.Release(); }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases == 0) StopLoop();
        }
    }

    private void StartLoop()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => Loop(ct));
    }

    private void StopLoop()
    {
        _cts?.Cancel();
        var l = _loop; _loop = null;
        try { l?.Wait(3000); } catch { }
        _cts?.Dispose(); _cts = null;
        Teardown();
    }

    private async Task Loop(CancellationToken ct)
    {
        // prime counters (first read of a rate counter is always 0); Sample() performs the lazy setup
        try { Sample(); } catch (Exception ex) { AppLog.Error("Monitor priming failed", ex); }
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(_intervalMs(), 500, 10000)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var snap = Sample();
                    Latest = snap;
                    Sampled?.Invoke(snap);
                }
                catch (Exception ex) { AppLog.Error("Monitor sample failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ setup

    private void Setup()
    {
        try
        {
            _cpuTotal = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", true);
            _cpuFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
            var cat = new PerformanceCounterCategory("Processor Information");
            _coreCounters = cat.GetInstanceNames()
                .Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, new NumericInstanceComparer())
                .Select(n => new PerformanceCounter("Processor Information", "% Processor Time", n, true)).ToList();
            foreach (var c in _coreCounters) c.NextValue();
            _cpuTotal.NextValue(); _cpuFreq.NextValue();
        }
        catch (Exception ex) { AppLog.Warn("CPU performance counters unavailable: " + ex.Message); }

        try { _driveModels = ReadDriveModels(); } catch (Exception ex) { AppLog.Warn("Drive model lookup failed: " + ex.Message); }

        try
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true };
            _computer.Open();
            LhmStatus = "ok";
        }
        catch (Exception ex)
        {
            LhmStatus = "unavailable: " + ex.Message;
            AppLog.Warn("LibreHardwareMonitor unavailable: " + ex.Message);
            _computer = null;
        }
    }

    private void Teardown()
    {
        lock (_sampleLock)
        {
            TeardownCore();
            _setupDone = false;
        }
    }

    private void TeardownCore()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
        _cpuTotal?.Dispose(); _cpuFreq?.Dispose(); _cpuTotal = _cpuFreq = null;
        foreach (var c in _coreCounters) c.Dispose();
        _coreCounters = [];
        foreach (var (r, w) in _diskCounters.Values) { r.Dispose(); w.Dispose(); }
        _diskCounters.Clear();
    }

    // ------------------------------------------------------------------ sampling

    private readonly object _sampleLock = new();
    private bool _setupDone;

    /// <summary>Takes one real sample. Safe to call from any thread; performs one-time setup on first use.</summary>
    public HardwareSnapshot Sample()
    {
        lock (_sampleLock)
        {
            if (!_setupDone)
            {
                try { Setup(); } catch (Exception ex) { AppLog.Error("Monitor setup failed", ex); }
                _setupDone = true;
            }
            return SampleCore();
        }
    }

    private HardwareSnapshot SampleCore()
    {
        var snap = new HardwareSnapshot
        {
            TimeUtc = DateTime.UtcNow.ToString("o"),
            Cpu = SampleCpu(),
            Ram = SampleRam(),
            UptimeSeconds = Environment.TickCount64 / 1000,
        };
        UpdateLhm();
        snap.Cpu.TemperatureC = CpuTemperature();
        snap.Gpus = SampleGpus();
        snap.Disks = SampleDisks();
        if (Latest == null) Latest = snap;
        return snap;
    }

    private CpuInfo SampleCpu()
    {
        var cpu = new CpuInfo { Model = _cpuModel, LogicalCores = Environment.ProcessorCount, PhysicalCores = _physicalCores };
        try { if (_cpuTotal != null) cpu.Usage = Math.Round(Math.Clamp(_cpuTotal.NextValue(), 0, 100), 1); } catch { }
        try { if (_cpuFreq != null) { var f = _cpuFreq.NextValue(); if (f > 0) cpu.FrequencyMhz = Math.Round(f); } } catch { }
        var cores = new List<double>();
        foreach (var c in _coreCounters)
        {
            try { cores.Add(Math.Round(Math.Clamp(c.NextValue(), 0, 100), 1)); } catch { }
        }
        cpu.Cores = [.. cores];
        return cpu;
    }

    private static RamInfo SampleRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        long total = (long)m.ullTotalPhys, avail = (long)m.ullAvailPhys, used = total - avail;
        return new RamInfo { TotalBytes = total, AvailableBytes = avail, UsedBytes = used, Percent = Math.Round(used * 100.0 / total, 1) };
    }

    private void UpdateLhm()
    {
        if (_computer == null) return;
        _tick++;
        foreach (var hw in _computer.Hardware)
        {
            // storage sensors are slow to query - refresh every ~10 samples
            if (hw.HardwareType == HardwareType.Storage && _tick % 10 != 1) continue;
            try { UpdateRecursive(hw); } catch (Exception ex) { AppLog.Warn($"LHM update failed for {hw.Name}: {ex.Message}"); }
        }
        if (_tick % 10 == 1)
        {
            _storageTemps = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage)
                .Select(h => (h.Name, (double?)h.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).Select(s => (double)s.Value!.Value).DefaultIfEmpty(double.NaN).First()))
                .Select(t => (t.Name, t.Item2 is double d && !double.IsNaN(d) ? t.Item2 : null)).ToList();
        }
    }

    private static void UpdateRecursive(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
    }

    private double? CpuTemperature()
    {
        var cpu = _computer?.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return null;
        var temps = AllSensors(cpu).Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        if (temps.Count == 0) return null;
        var pkg = temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                          || s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                                          || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase));
        var v = pkg?.Value ?? temps.Max(s => s.Value!.Value);
        return Math.Round((double)v, 1);
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    private List<GpuInfo> SampleGpus()
    {
        var list = new List<GpuInfo>();
        if (_computer == null) return list;
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
        {
            var sensors = AllSensors(hw).ToList();
            var g = new GpuInfo { Name = hw.Name, Vendor = hw.HardwareType switch { HardwareType.GpuNvidia => "NVIDIA", HardwareType.GpuAmd => "AMD", _ => "Intel" } };

            var core = sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue);
            if (core != null) g.Usage = Math.Round((double)core.Value!.Value, 1);
            else
            {
                var d3d = sensors.Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("D3D", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue).ToList();
                if (d3d.Count > 0) g.Usage = Math.Round((double)d3d.Max(s => s.Value!.Value), 1);
            }

            var temp = sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)
                       ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (temp != null) g.TemperatureC = Math.Round((double)temp.Value!.Value, 1);

            double? Mem(params string[] names)
            {
                foreach (var n in names)
                {
                    var s = sensors.FirstOrDefault(x => x.SensorType is SensorType.SmallData or SensorType.Data && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && x.Value.HasValue);
                    if (s != null) return s.SensorType == SensorType.Data ? (double)s.Value!.Value * 1024 : (double)s.Value!.Value; // MB
                }
                return null;
            }
            g.VramUsedMb = Mem("GPU Memory Used", "D3D Dedicated Memory Used");
            g.VramTotalMb = Mem("GPU Memory Total", "D3D Dedicated Memory Total");
            if (g.VramTotalMb == null && g.VramUsedMb != null)
            {
                var free = Mem("GPU Memory Free");
                if (free != null) g.VramTotalMb = g.VramUsedMb + free;
            }
            if (g.VramUsedMb != null) g.VramUsedMb = Math.Round(g.VramUsedMb.Value);
            if (g.VramTotalMb != null) g.VramTotalMb = Math.Round(g.VramTotalMb.Value);
            if (g.VramUsedMb != null && g.VramTotalMb is > 0) g.VramPercent = Math.Round(g.VramUsedMb.Value * 100 / g.VramTotalMb.Value, 1);
            list.Add(g);
        }
        // discrete GPUs first
        return list.OrderByDescending(g => g.Vendor != "Intel").ThenByDescending(g => g.VramTotalMb ?? 0).ToList();
    }

    private List<DiskInfoDto> SampleDisks()
    {
        var list = new List<DiskInfoDto>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var id = d.Name.TrimEnd('\\');
                var dto = new DiskInfoDto
                {
                    Id = id, Label = d.VolumeLabel, FileSystem = d.DriveFormat,
                    TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace, UsedBytes = d.TotalSize - d.AvailableFreeSpace,
                };
                dto.Percent = dto.TotalBytes == 0 ? 0 : Math.Round(dto.UsedBytes * 100.0 / dto.TotalBytes, 1);
                if (_driveModels.TryGetValue(id, out var model))
                {
                    dto.Model = model;
                    var match = _storageTemps.FirstOrDefault(t => t.temp != null && (t.name.Contains(model, StringComparison.OrdinalIgnoreCase) || model.Contains(t.name, StringComparison.OrdinalIgnoreCase)));
                    dto.TemperatureC = match.temp;
                }
                var counters = GetDiskCounters(id);
                if (counters != null)
                {
                    try { dto.ReadBps = Math.Round(counters.Value.read.NextValue()); } catch { }
                    try { dto.WriteBps = Math.Round(counters.Value.write.NextValue()); } catch { }
                }
                list.Add(dto);
            }
            catch { /* drive vanished */ }
        }
        return list;
    }

    private (PerformanceCounter read, PerformanceCounter write)? GetDiskCounters(string id)
    {
        if (_diskCounters.TryGetValue(id, out var c)) return c;
        try
        {
            var r = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", id, true);
            var w = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", id, true);
            r.NextValue(); w.NextValue();
            _diskCounters[id] = (r, w);
            return (r, w);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ static info

    private static string ReadCpuModel()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var n = k?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(n)) return string.Join(' ', n.Split([' '], StringSplitOptions.RemoveEmptyEntries));
        }
        catch { }
        return "Unknown CPU";
    }

    private static int? ReadPhysicalCores()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            return s.Get().Cast<ManagementObject>().Sum(o => Convert.ToInt32(o["NumberOfCores"]));
        }
        catch { return null; }
    }

    /// <summary>Maps drive letters to physical disk model names (Win32_DiskDrive -> partition -> logical disk).</summary>
    private static Dictionary<string, string> ReadDriveModels()
    {
        static string Id(object? reference)
        {
            var m = System.Text.RegularExpressions.Regex.Match(reference?.ToString() ?? "", "DeviceID=\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
        }

        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var drives = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive"))
            foreach (ManagementObject d in drives.Get()) models[(string)d["DeviceID"]] = (d["Model"] as string)?.Trim() ?? "";

        var partToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            foreach (ManagementObject r in s.Get()) partToDisk[Id(r["Dependent"])] = Id(r["Antecedent"]);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            foreach (ManagementObject r in s.Get())
                if (partToDisk.TryGetValue(Id(r["Antecedent"]), out var diskId) && models.TryGetValue(diskId, out var model))
                    map[Id(r["Dependent"])] = model;
        return map;
    }

    public void Dispose()
    {
        lock (_gate) { _leases = 0; StopLoop(); }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private sealed class NumericInstanceComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            static (int, int) P(string? s)
            {
                var p = (s ?? "").Split(',');
                return (p.Length > 0 && int.TryParse(p[0], out var x) ? x : 0, p.Length > 1 && int.TryParse(p[1], out var y) ? y : 0);
            }
            return P(a).CompareTo(P(b));
        }
    }
}

/// <summary>Registers monitor.* handlers and pushes live snapshots to subscribed clients.</summary>
public sealed class MonitorModule
{
    private readonly ServerCore _core;
    public SystemMonitor Monitor { get; }
    private readonly Dictionary<ClientContext, (IDisposable lease, int[] busy)> _subs = new();
    private readonly object _lock = new();

    public MonitorModule(ServerCore core, SystemMonitor monitor)
    {
        _core = core; Monitor = monitor;
        monitor.Sampled += OnSampled;
        core.Handlers.Register("monitor.subscribe", Permission.Monitoring, async (c, e) =>
        {
            lock (_lock)
            {
                if (!_subs.ContainsKey(c))
                {
                    _subs[c] = (monitor.Acquire(), new int[1]);
                    c.Closed += () => Unsubscribe(c);
                }
            }
            return new { intervalMs = Math.Clamp(core.Settings.MonitorIntervalMs, 500, 10000), snapshot = Monitor.Latest };
        });
        core.Handlers.Register("monitor.unsubscribe", Permission.Monitoring, (c, e) =>
        {
            Unsubscribe(c);
            return Task.FromResult<object?>(new { });
        });
        core.Handlers.Register("monitor.snapshot", Permission.Monitoring, (c, e) =>
        {
            // one-off read: take a short lease so the sampler runs at least once
            return Task.FromResult<object?>(new { snapshot = Monitor.Latest ?? Monitor.Sample() });
        });
    }

    private void Unsubscribe(ClientContext c)
    {
        IDisposable? lease = null;
        lock (_lock) { if (_subs.Remove(c, out var s)) lease = s.lease; }
        lease?.Dispose();
    }

    private void OnSampled(HardwareSnapshot snap)
    {
        List<(ClientContext c, int[] busy)> targets;
        lock (_lock) targets = _subs.Select(kv => (kv.Key, kv.Value.busy)).ToList();
        foreach (var (c, busy) in targets)
        {
            if (Interlocked.CompareExchange(ref busy[0], 1, 0) != 0) continue; // client is slow: drop this frame
            _ = c.PushAsync("monitor.snapshot", new { snapshot = snap }).ContinueWith(_ => Interlocked.Exchange(ref busy[0], 0));
        }
    }
}
