using System.Runtime.InteropServices;
using RemoteControlServer.Models;
using RemoteControlServer.Monitoring;
using Xunit.Abstractions;

namespace RemoteControlServer.Tests;

/// <summary>These tests read the real hardware of the machine running them - no mocks.</summary>
public class MonitoringTests : IClassFixture<MonitoringFixture>
{
    private readonly MonitoringFixture _f;
    private readonly ITestOutputHelper _out;
    public MonitoringTests(MonitoringFixture f, ITestOutputHelper o) { _f = f; _out = o; }

    private HardwareSnapshot Snap() => _f.Snapshots[^1];

    [Fact]
    public void Cpu_reports_model_usage_per_core_and_frequency()
    {
        var c = Snap().Cpu;
        _out.WriteLine($"CPU {c.Model}: {c.Usage}% cores={c.Cores.Length} logical={c.LogicalCores} physical={c.PhysicalCores} {c.FrequencyMhz} MHz temp={c.TemperatureC} (lhm: {_f.Monitor.LhmStatus})");
        Assert.False(string.IsNullOrWhiteSpace(c.Model));
        Assert.NotNull(c.Usage);
        Assert.InRange(c.Usage!.Value, 0, 100);
        Assert.Equal(Environment.ProcessorCount, c.LogicalCores);
        Assert.Equal(c.LogicalCores, c.Cores.Length);
        Assert.All(c.Cores, v => Assert.InRange(v, 0, 100));
        Assert.NotNull(c.PhysicalCores);
        Assert.True(c.FrequencyMhz is null or > 100);
        if (c.TemperatureC != null) Assert.InRange(c.TemperatureC.Value, 1, 149);
    }

    [Fact]
    public void Cpu_usage_reacts_to_real_load()
    {
        using var cts = new CancellationTokenSource();
        var burners = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() => { while (!cts.IsCancellationRequested) Math.Sqrt(Random.Shared.NextDouble()); })).ToArray();
        Thread.Sleep(1500);
        var loaded = _f.Monitor.Sample().Cpu.Usage!.Value;
        Thread.Sleep(1200);
        loaded = Math.Max(loaded, _f.Monitor.Sample().Cpu.Usage!.Value);
        cts.Cancel(); Task.WaitAll(burners);
        _out.WriteLine($"usage under full load: {loaded}%");
        Assert.True(loaded > 50, $"expected >50% under full load, got {loaded}");
    }

    [Fact]
    public void Ram_totals_are_consistent_with_windows()
    {
        var r = Snap().Ram;
        _out.WriteLine($"RAM total {r.TotalBytes / 1e9:F1} GB used {r.UsedBytes / 1e9:F1} GB avail {r.AvailableBytes / 1e9:F1} GB {r.Percent}%");
        Assert.Equal(r.TotalBytes, r.UsedBytes + r.AvailableBytes);
        Assert.InRange(r.Percent, 0.1, 100);
        Assert.Equal(r.UsedBytes * 100.0 / r.TotalBytes, r.Percent, 0.1);
        // independent source: the CLR's view of physical memory
        Assert.Equal(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1e9, r.TotalBytes / 1e9, 1.0);
    }

    [Fact]
    public void Disks_match_system_drive_info()
    {
        var disks = Snap().Disks;
        Assert.NotEmpty(disks);
        foreach (var d in disks)
        {
            _out.WriteLine($"{d.Id} {d.Label} {d.FileSystem} [{d.Model}] {d.UsedBytes / 1e9:F1}/{d.TotalBytes / 1e9:F1} GB {d.Percent}% R={d.ReadBps} W={d.WriteBps} T={d.TemperatureC}");
            var di = new DriveInfo(d.Id);
            Assert.InRange(d.TotalBytes, (long)(di.TotalSize * 0.999), (long)(di.TotalSize * 1.001)); // the volume size can differ by a few KB between two reads
            Assert.Equal(d.TotalBytes, d.UsedBytes + d.FreeBytes);
            Assert.InRange(d.Percent, 0, 100);
            Assert.True(d.ReadBps is null or >= 0);
        }
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        Assert.Contains(disks, d => d.Id.Equals(sysDrive, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(disks, d => d.Model.Length > 0); // drive letter -> physical model mapping works
    }

    [Fact]
    public void Disk_write_speed_reacts_to_real_io()
    {
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var tmp = Path.Combine(Path.GetTempPath(), "pcrc-io-" + Guid.NewGuid().ToString("N"));
        double best = 0;
        try
        {
            var buf = new byte[4 * 1024 * 1024]; Random.Shared.NextBytes(buf);
            var drive = new DriveInfo(Path.GetPathRoot(tmp)!);
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _f.Monitor.Sample();
            for (int i = 0; i < 60 && sw.ElapsedMilliseconds < 3500; i++) { fs.Write(buf); fs.Flush(true); }
            var d = _f.Monitor.Sample().Disks.FirstOrDefault(x => x.Id.Equals(drive.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            best = d?.WriteBps ?? 0;
            _out.WriteLine($"write speed on {drive.Name}: {best / 1e6:F1} MB/s");
        }
        finally { try { File.Delete(tmp); } catch { } }
        Assert.True(best > 1_000_000, $"expected >1 MB/s while writing, got {best}");
    }

    [Fact]
    public void Gpu_values_are_real_or_null_never_fabricated()
    {
        var gpus = Snap().Gpus;
        _out.WriteLine($"GPUs found: {gpus.Count} (lhm: {_f.Monitor.LhmStatus})");
        foreach (var g in gpus)
        {
            _out.WriteLine($"{g.Vendor} {g.Name}: usage={g.Usage} vram={g.VramUsedMb}/{g.VramTotalMb} MB ({g.VramPercent}%) temp={g.TemperatureC}");
            Assert.False(string.IsNullOrWhiteSpace(g.Name));
            if (g.Usage != null) Assert.InRange(g.Usage.Value, 0, 100);
            if (g.VramUsedMb != null && g.VramTotalMb != null) Assert.True(g.VramUsedMb <= g.VramTotalMb + 1);
            if (g.VramPercent != null) Assert.InRange(g.VramPercent.Value, 0, 100);
            if (g.VramTotalMb == null) Assert.Null(g.VramPercent);
        }
    }

    [Fact]
    public void Sampler_stops_when_last_lease_is_released()
    {
        using var m = new SystemMonitor(() => 500);
        int count = 0;
        m.Sampled += _ => Interlocked.Increment(ref count);
        var lease = m.Acquire();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (count < 3 && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(100); // first sample includes one-time hardware setup
        Assert.True(count >= 3, $"expected >=3 samples, got {count}");
        lease.Dispose();
        var after = count; Thread.Sleep(1500);
        Assert.Equal(after, count);
    }

    [Fact]
    public async Task Subscribed_phone_receives_live_snapshots()
    {
        var sf = new ServerFixture();
        await sf.InitializeAsync();
        try
        {
            using var mon = new SystemMonitor(() => 500);
            _ = new MonitorModule(sf.Server, mon);
            var cred = await sf.PairAsync(Permission.Monitoring);
            await using var c = await sf.LoginAsync(cred);
            var sub = await c.RequestAsync("monitor.subscribe");
            Assert.Equal("response", sub.Type);
            var got = new List<DateTime>();
            for (int i = 0; i < 3; i++)
            {
                var p = await c.WaitPushAsync(e => e.Type == "monitor.snapshot", 8000);
                var cpu = p.P()["snapshot"]!["cpu"]!;
                Assert.NotNull(cpu["model"]);
                Assert.NotNull(p.P()["snapshot"]!["ram"]!["totalBytes"]);
                got.Add(DateTime.UtcNow);
            }
            Assert.True((got[2] - got[0]).TotalSeconds < 4, "snapshots should arrive every 0.5-2 s");
            await c.RequestAsync("monitor.unsubscribe");

            // a device without the monitoring permission is refused
            var weak = await sf.PairAsync(Permission.Launch, "no-monitor");
            await using var c2 = await sf.LoginAsync(weak);
            Assert.Equal("forbidden", (await c2.RequestAsync("monitor.subscribe")).Code());
        }
        finally { await sf.DisposeAsync(); }
    }
}

public sealed class MonitoringFixture : IDisposable
{
    public SystemMonitor Monitor { get; } = new(() => 1000);
    public List<HardwareSnapshot> Snapshots { get; } = [];
    private readonly IDisposable _lease;

    public MonitoringFixture()
    {
        _lease = Monitor.Acquire();
        Thread.Sleep(2500);
        Snapshots.Add(Monitor.Sample());
    }

    public void Dispose() { _lease.Dispose(); Monitor.Dispose(); }
}
