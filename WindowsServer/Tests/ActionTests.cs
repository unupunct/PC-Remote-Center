using System.Diagnostics;
using RemoteControlServer.Models;
using RemoteControlServer.Services;
using RemoteControlServer.Shortcuts;

namespace RemoteControlServer.Tests;

public sealed class FakeSystem : ISystemActions
{
    public List<string[]> ShutdownCalls { get; } = [];
    public int NextExit { get; set; }
    public int LockCalls, SleepCalls;
    public int RunShutdown(IReadOnlyList<string> args) { ShutdownCalls.Add([.. args]); return NextExit; }
    public bool Lock() { LockCalls++; return true; }
    public bool Sleep() { SleepCalls++; return true; }
}

public sealed class FakeLauncher : IProcessLauncher
{
    public List<ProcessStartInfo> Launched { get; } = [];
    public void Launch(ProcessStartInfo psi) => Launched.Add(psi);
}

public sealed class RuntimeFixture : IAsyncLifetime
{
    public ServerRuntime Runtime { get; private set; } = null!;
    public FakeSystem Sys { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public ServerFixture Inner { get; } = new();
    public string Dir => Inner.Dir;
    public int Port => Inner.Port;

    public async Task InitializeAsync()
    {
        ServerFixture.TestHooks = null;
        // ServerFixture owns the ServerCore; the runtime's modules attach to it.
        await Inner.InitializeAsync();
        Runtime = ServerRuntime.AttachTo(Inner.Server, Sys, Launcher);
    }

    public async Task DisposeAsync()
    {
        Runtime.Terminals.Dispose();
        await Inner.DisposeAsync();
    }

    public Task<(string id, string token)> PairAsync(Permission p, string name = "Test phone") => Inner.PairAsync(p, name);
    public Task<TestClient> LoginAsync((string id, string token) c) => Inner.LoginAsync(c);
}

public class PowerTests : IClassFixture<RuntimeFixture>
{
    private readonly RuntimeFixture _f;
    public PowerTests(RuntimeFixture f) { _f = f; f.Sys.ShutdownCalls.Clear(); f.Sys.NextExit = 0; }

    [Fact]
    public async Task Restart_needs_request_then_confirm_and_nothing_happens_on_request_alone()
    {
        var cred = await _f.PairAsync(Permission.Power);
        await using var c = await _f.LoginAsync(cred);
        var req = await c.RequestAsync("power.request", new { action = "restart", delaySeconds = 20 });
        Assert.Equal("response", req.Type);
        Assert.Empty(_f.Sys.ShutdownCalls); // a single tap must never restart the PC
        var token = req.P()["confirmationToken"]!.GetValue<string>();

        var ok = await c.RequestAsync("power.confirm", new { token });
        Assert.Equal("response", ok.Type);
        var call = Assert.Single(_f.Sys.ShutdownCalls);
        Assert.Equal(["/r", "/t", "20"], call.Take(3).ToArray());
        Assert.Equal("/c", call[3]);

        var again = await c.RequestAsync("power.confirm", new { token });
        Assert.Equal("confirmation_invalid", again.Code()); // single use
        Assert.Single(_f.Sys.ShutdownCalls);
    }

    [Fact]
    public async Task Shutdown_flow_and_cancel()
    {
        var cred = await _f.PairAsync(Permission.Power);
        await using var c = await _f.LoginAsync(cred);
        var token = (await c.RequestAsync("power.request", new { action = "shutdown", delaySeconds = 60 })).P()["confirmationToken"]!.GetValue<string>();
        await c.RequestAsync("power.confirm", new { token });
        Assert.Equal("/s", _f.Sys.ShutdownCalls[^1][0]);

        var cancel = await c.RequestAsync("power.cancel");
        Assert.Equal(["/a"], _f.Sys.ShutdownCalls[^1]);
        Assert.True(cancel.P()["cancelled"]!.GetValue<bool>());

        _f.Sys.NextExit = 1116; // Windows: no shutdown in progress
        var none = await c.RequestAsync("power.cancel");
        Assert.False(none.P()["cancelled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Confirmation_is_bound_to_the_device_and_validated()
    {
        var a = await _f.PairAsync(Permission.Power, "phone-a");
        var b = await _f.PairAsync(Permission.Power, "phone-b");
        await using var ca = await _f.LoginAsync(a);
        await using var cb = await _f.LoginAsync(b);
        var token = (await ca.RequestAsync("power.request", new { action = "restart" })).P()["confirmationToken"]!.GetValue<string>();
        Assert.Equal("confirmation_invalid", (await cb.RequestAsync("power.confirm", new { token })).Code());
        Assert.Equal("confirmation_invalid", (await ca.RequestAsync("power.confirm", new { token = "00" })).Code());
        Assert.Equal("invalid_payload", (await ca.RequestAsync("power.request", new { action = "format-disk" })).Code());
        Assert.Equal("invalid_payload", (await ca.RequestAsync("power.request", new { action = "restart", delaySeconds = 1 })).Code());
        Assert.Equal("invalid_payload", (await ca.RequestAsync("power.request", new { action = "restart", delaySeconds = 99999 })).Code());
        Assert.Empty(_f.Sys.ShutdownCalls);
    }

    [Fact]
    public async Task Failing_shutdown_is_reported_as_error_not_silently_ignored()
    {
        var cred = await _f.PairAsync(Permission.Power);
        await using var c = await _f.LoginAsync(cred);
        var token = (await c.RequestAsync("power.request", new { action = "restart" })).P()["confirmationToken"]!.GetValue<string>();
        _f.Sys.NextExit = 1190;
        var r = await c.RequestAsync("power.confirm", new { token });
        Assert.Equal("action_failed", r.Code());
    }

    [Fact]
    public async Task Power_actions_need_the_power_permission()
    {
        var cred = await _f.PairAsync(Permission.Monitoring | Permission.Terminal);
        await using var c = await _f.LoginAsync(cred);
        foreach (var t in new[] { "power.request", "power.confirm", "power.cancel", "power.lock" })
            Assert.Equal("forbidden", (await c.RequestAsync(t, new { action = "restart", token = "x" })).Code());
        Assert.Empty(_f.Sys.ShutdownCalls);
    }

    [Fact]
    public async Task Sleep_requires_confirmation_and_lock_runs_immediately()
    {
        var cred = await _f.PairAsync(Permission.Power);
        await using var c = await _f.LoginAsync(cred);
        var t = (await c.RequestAsync("power.request", new { action = "sleep" })).P()["confirmationToken"]!.GetValue<string>();
        Assert.Equal(0, _f.Sys.SleepCalls);
        await c.RequestAsync("power.confirm", new { token = t });
        Assert.Equal(1, _f.Sys.SleepCalls);
        var before = _f.Sys.LockCalls;
        Assert.Equal("response", (await c.RequestAsync("power.lock")).Type);
        Assert.Equal(before + 1, _f.Sys.LockCalls);
    }

    [Fact]
    public void Real_shutdown_exe_can_schedule_and_cancel_a_restart()
    {
        // real Windows: schedule a restart one hour away and abort it right away
        var real = new WindowsSystemActions();
        try
        {
            Assert.Equal(0, real.RunShutdown(["/r", "/t", "3600", "/c", "PC Remote Center test"]));
        }
        finally
        {
            var abort = real.RunShutdown(["/a"]);
            Assert.True(abort == 0, "shutdown /a failed - a restart may still be scheduled! Run 'shutdown /a' manually.");
        }
        Assert.Equal(1116, real.RunShutdown(["/a"])); // nothing pending any more
    }
}

public class ShortcutTests : IClassFixture<RuntimeFixture>
{
    private readonly RuntimeFixture _f;
    public ShortcutTests(RuntimeFixture f) { _f = f; f.Launcher.Launched.Clear(); }

    private static object Sc(string name, string path, string args = "", string mode = "normal", bool confirm = false, string id = "") =>
        new { shortcut = new { id, name, description = "d", icon = "apps", path, arguments = args, workingDirectory = "", runMode = mode, confirm } };

    [Fact]
    public async Task Default_shortcuts_are_seeded_and_listed()
    {
        var cred = await _f.PairAsync(Permission.Launch);
        await using var c = await _f.LoginAsync(cred);
        var r = await c.RequestAsync("shortcuts.list");
        var names = r.P()["shortcuts"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Task Manager", names); Assert.Contains("Device Manager", names); Assert.Contains("Services", names);
        Assert.Contains("Event Viewer", names); Assert.Contains("Windows Settings", names);
    }

    [Fact]
    public async Task Run_starts_the_validated_executable_structurally()
    {
        var cred = await _f.PairAsync(Permission.Launch);
        await using var c = await _f.LoginAsync(cred);
        var id = _f.Runtime.Core.Shortcuts.List().First(s => s.Name == "Task Manager").Id;
        var r = await c.RequestAsync("shortcuts.run", new { id });
        Assert.Equal("response", r.Type);
        var psi = Assert.Single(_f.Launcher.Launched);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "taskmgr.exe"), psi.FileName, ignoreCase: true);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public async Task Save_requires_admin_and_rejects_dangerous_definitions()
    {
        var weak = await _f.PairAsync(Permission.Launch);
        await using var w = await _f.LoginAsync(weak);
        Assert.Equal("forbidden", (await w.RequestAsync("shortcuts.save", Sc("x", "notepad.exe"))).Code());
        Assert.Equal("forbidden", (await w.RequestAsync("shortcuts.delete", new { id = "x" })).Code());

        var cred = await _f.PairAsync(Permission.Launch | Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        async Task<string> Bad(object payload) => (await c.RequestAsync("shortcuts.save", payload)).Code();
        Assert.Equal("invalid_shortcut", await Bad(Sc("script", "C:\\Windows\\System32\\drivers\\etc\\hosts.bat")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("ps", "powershell.ps1")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("missing", "C:\\nope\\nothing.exe")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("rel", "..\\..\\Windows\\System32\\cmd.exe")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("uri", "javascript:alert(1)")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("uri2", "https://example.com")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("ctl", "notepad.exe", "a\r\nb")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("", "notepad.exe")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("mode", "notepad.exe", "", "godmode")));
        Assert.Equal("invalid_shortcut", await Bad(Sc("elev", "notepad.exe", "", "elevated"))); // elevated launching is off by default
        Assert.Equal("invalid_shortcut", await Bad(Sc("uriargs", "ms-settings:", "foo")));
    }

    [Fact]
    public async Task Add_edit_reorder_delete_work_and_persist()
    {
        var cred = await _f.PairAsync(Permission.Launch | Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        var add = await c.RequestAsync("shortcuts.save", Sc("My Notepad", "notepad.exe", "\"C:\\some file.txt\" /p"));
        Assert.Equal("response", add.Type);
        var id = add.P()["shortcut"]!["id"]!.GetValue<string>();
        Assert.Equal("My Notepad", _f.Runtime.Core.Shortcuts.Get(id)!.Name);

        var edit = await c.RequestAsync("shortcuts.save", Sc("Renamed", "notepad.exe", "", "minimized", true, id));
        Assert.Equal("Renamed", edit.P()["shortcut"]!["name"]!.GetValue<string>());

        var ids = _f.Runtime.Core.Shortcuts.List().Select(s => s.Id).Reverse().ToList();
        var reorder = await c.RequestAsync("shortcuts.reorder", new { ids });
        Assert.Equal("response", reorder.Type);
        Assert.Equal(ids, _f.Runtime.Core.Shortcuts.List().Select(s => s.Id).ToList());
        Assert.Equal("invalid_payload", (await c.RequestAsync("shortcuts.reorder", new { ids = ids.Take(1).ToList() })).Code());

        Assert.Equal("response", (await c.RequestAsync("shortcuts.delete", new { id })).Type);
        Assert.Null(_f.Runtime.Core.Shortcuts.Get(id));
        Assert.Equal("not_found", (await c.RequestAsync("shortcuts.delete", new { id })).Code());
    }

    [Fact]
    public async Task Confirmation_flag_and_arguments_are_enforced_without_shell_interpretation()
    {
        var cred = await _f.PairAsync(Permission.Launch | Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        var id = (await c.RequestAsync("shortcuts.save", Sc("Risky", "notepad.exe", "& calc.exe | del /q *", "minimized", true))).P()["shortcut"]!["id"]!.GetValue<string>();

        Assert.Equal("confirmation_required", (await c.RequestAsync("shortcuts.run", new { id })).Code());
        Assert.Empty(_f.Launcher.Launched);
        Assert.Equal("response", (await c.RequestAsync("shortcuts.run", new { id, confirmed = true })).Type);
        var psi = Assert.Single(_f.Launcher.Launched);
        // the metacharacters reach the program as plain arguments; no cmd.exe is involved
        Assert.EndsWith("notepad.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("& calc.exe | del /q *", psi.Arguments);
        Assert.Equal(ProcessWindowStyle.Minimized, psi.WindowStyle);
        Assert.NotEqual("cmd.exe", Path.GetFileName(psi.FileName).ToLowerInvariant());
    }

    [Fact]
    public async Task Elevated_launch_is_off_by_default_needs_admin_and_uses_uac()
    {
        var cred = await _f.PairAsync(Permission.Launch | Permission.Admin);
        await using var c = await _f.LoginAsync(cred);
        Assert.Equal("invalid_shortcut", (await c.RequestAsync("shortcuts.save", Sc("Elev", "notepad.exe", "", "elevated"))).Code());

        var s = _f.Runtime.Core.SettingsStore.Load(); s.AllowElevatedLaunch = true; _f.Runtime.Core.SettingsStore.Save(s);
        try
        {
            var id = (await c.RequestAsync("shortcuts.save", Sc("Elev", "notepad.exe", "", "elevated"))).P()["shortcut"]!["id"]!.GetValue<string>();
            var weak = await _f.PairAsync(Permission.Launch);
            await using var w = await _f.LoginAsync(weak);
            Assert.Equal("forbidden", (await w.RequestAsync("shortcuts.run", new { id })).Code());
            Assert.Empty(_f.Launcher.Launched);
            Assert.Equal("response", (await c.RequestAsync("shortcuts.run", new { id })).Type);
            Assert.Equal("runas", Assert.Single(_f.Launcher.Launched).Verb);
        }
        finally { s.AllowElevatedLaunch = false; _f.Runtime.Core.SettingsStore.Save(s); }
    }

    [Fact]
    public void Argument_splitter_follows_windows_rules()
    {
        Assert.Equal(["a", "b c", "d\"e"], ShortcutService.SplitArguments("a \"b c\" d\\\"e"));
        Assert.Empty(ShortcutService.SplitArguments("")!);
    }

    [Fact]
    public void Real_process_really_starts()
    {
        var real = new RealProcessLauncher();
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe")) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        real.Launch(psi); // would throw if the OS refused to start it
        Assert.Throws<System.ComponentModel.Win32Exception>(() => real.Launch(new ProcessStartInfo(@"C:\definitely\missing.exe") { UseShellExecute = true }));
    }
}
