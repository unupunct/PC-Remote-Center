using System.Diagnostics;
using RemoteControlServer.Models;
using RemoteControlServer.Services;

namespace RemoteControlServer.Tests;

public class ProcessTests : IClassFixture<RuntimeFixture>
{
    private readonly RuntimeFixture _f;
    public ProcessTests(RuntimeFixture f) => _f = f;

    private static Process StartSleeper() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

    [Fact]
    public async Task List_returns_real_processes_sorted_and_filtered()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var c = await _f.LoginAsync(cred);
        var r = await c.RequestAsync("process.list", new { sort = "memory", limit = 20 });
        Assert.Equal("response", r.Type);
        var list = r.P()["processes"]!.AsArray();
        Assert.InRange(list.Count, 5, 20);
        Assert.True(r.P()["total"]!.GetValue<int>() >= list.Count);
        var mem = list.Select(p => p!["memoryMb"]!.GetValue<double>()).ToList();
        Assert.Equal(mem.OrderByDescending(x => x), mem);

        // our own test host must be in the full, name-sorted list
        var me = Process.GetCurrentProcess().ProcessName;
        var f = await c.RequestAsync("process.list", new { sort = "name", limit = 300, filter = me });
        Assert.Contains(f.P()["processes"]!.AsArray(), p => p!["pid"]!.GetValue<int>() == Environment.ProcessId);
        Assert.Equal("invalid_payload", (await c.RequestAsync("process.list", new { sort = "bogus" })).Code());
    }

    [Fact]
    public async Task Cpu_percent_is_measured_not_invented()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var c = await _f.LoginAsync(cred);
        using var cts = new CancellationTokenSource();
        var burn = Task.Run(() => { while (!cts.IsCancellationRequested) Math.Sqrt(Random.Shared.NextDouble()); });
        await c.RequestAsync("process.list", new { sort = "cpu", limit = 5 }); // baseline
        await Task.Delay(1200);
        var r = await c.RequestAsync("process.list", new { sort = "cpu", limit = 300, filter = Process.GetCurrentProcess().ProcessName });
        cts.Cancel(); await burn;
        var mine = r.P()["processes"]!.AsArray().First(p => p!["pid"]!.GetValue<int>() == Environment.ProcessId)!;
        Assert.True(mine["cpu"]!.GetValue<double>() > 3, "busy test process should show CPU use, got " + mine["cpu"]);
    }

    [Fact]
    public async Task Kill_needs_admin_and_confirmation_and_really_ends_the_process()
    {
        var weak = await _f.PairAsync(Permission.Monitoring | Permission.Terminal);
        await using var w = await _f.LoginAsync(weak);
        using var victim = StartSleeper();
        Assert.Equal("forbidden", (await w.RequestAsync("process.kill", new { pid = victim.Id, confirmed = true })).Code());
        Assert.False(victim.HasExited);

        var cred = await _f.PairAsync(Permission.Monitoring | Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        Assert.Equal("confirmation_required", (await c.RequestAsync("process.kill", new { pid = victim.Id })).Code());
        Assert.False(victim.HasExited);
        var ok = await c.RequestAsync("process.kill", new { pid = victim.Id, confirmed = true });
        Assert.Equal("response", ok.Type);
        Assert.Equal("ping", ok.P()["name"]!.GetValue<string>(), ignoreCase: true);
        Assert.True(victim.WaitForExit(5000), "process should be gone");
        Assert.Equal("not_found", (await c.RequestAsync("process.kill", new { pid = victim.Id, confirmed = true })).Code());
        Assert.Contains(_f.Runtime.Core.Audit.Recent(50), e => e.Action == "process.kill" && e.Detail.Contains("ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Critical_and_own_processes_cannot_be_ended()
    {
        var cred = await _f.PairAsync(Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        Assert.Equal("forbidden", (await c.RequestAsync("process.kill", new { pid = 4, confirmed = true })).Code());
        Assert.Equal("forbidden", (await c.RequestAsync("process.kill", new { pid = Environment.ProcessId, confirmed = true })).Code());
        Assert.Equal("invalid_payload", (await c.RequestAsync("process.kill", new { confirmed = true })).Code());
        var critical = Process.GetProcessesByName("csrss").FirstOrDefault() ?? Process.GetProcessesByName("lsass").First();
        Assert.Equal("forbidden", (await c.RequestAsync("process.kill", new { pid = critical.Id, confirmed = true })).Code());
        Assert.NotNull(Process.GetProcessById(critical.Id)); // still running (HasExited cannot even be read for protected processes)
    }

    [Fact]
    public async Task Kill_tree_ends_children_too()
    {
        var cred = await _f.PairAsync(Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 120 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        await Task.Delay(800);
        var child = Process.GetProcessesByName("ping").OrderByDescending(p => p.StartTime).First();
        var ok = await c.RequestAsync("process.kill", new { pid = parent.Id, tree = true, confirmed = true });
        Assert.Equal("response", ok.Type);
        Assert.True(parent.WaitForExit(5000)); Assert.True(child.WaitForExit(5000), "child process should be ended with the tree");
    }
}
