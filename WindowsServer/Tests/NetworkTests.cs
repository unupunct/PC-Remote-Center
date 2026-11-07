using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Storage;

namespace RemoteControlServer.Tests;

public class NetworkTests
{
    [Fact]
    public async Task Discovery_answers_only_the_discovery_probe_with_name_port_and_fingerprint()
    {
        var f = new ServerFixture();
        var dp = Net.FreePort();

        try
        {
            await f.InitializeAsync();
            { var s = f.Server.SettingsStore.Load(); s.DiscoveryEnabled = true; s.DiscoveryPort = dp; s.ComputerName = "TEST-PC"; f.Server.SettingsStore.Save(s); await f.Server.RestartAsync(); }
            using var udp = new UdpClient(); udp.Client.ReceiveTimeout = 3000;
            var probe = Encoding.UTF8.GetBytes("PCRC_DISCOVER_V1");
            await udp.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, dp));
            var r = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var doc = JsonDocument.Parse(r.Buffer);
            Assert.Equal("pc-remote-center", doc.RootElement.GetProperty("app").GetString());
            Assert.Equal("TEST-PC", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(f.Port, doc.RootElement.GetProperty("port").GetInt32());
            Assert.Equal(f.Fingerprint, doc.RootElement.GetProperty("fingerprint").GetString());

            // anything else gets no answer (no amplification / information leak)
            await udp.SendAsync(Encoding.UTF8.GetBytes("hello?"), new IPEndPoint(IPAddress.Loopback, dp));
            await Assert.ThrowsAnyAsync<Exception>(() => udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(800)));
        }
        finally { await f.DisposeAsync(); }
    }

    [Fact]
    public async Task Server_restart_drops_clients_and_they_can_log_in_again_with_the_same_token()
    {
        var f = new ServerFixture(); await f.InitializeAsync();
        try
        {
            var cred = await f.PairAsync(Permission.Monitoring, "survivor");
            await using var c = await f.LoginAsync(cred);
            Assert.Equal("response", (await c.RequestAsync("ping")).Type);

            await f.Server.RestartAsync();
            await c.WaitClosedAsync(15000); // clients notice the stop (network disconnection)

            await using var again = await f.LoginAsync(cred); // paired devices survive a restart
            Assert.Equal("response", (await again.RequestAsync("server.info")).Type);
            Assert.Equal(ServerState.Running, f.Server.State);
        }
        finally { await f.DisposeAsync(); }
    }

    [Fact]
    public async Task Abrupt_client_disconnect_is_cleaned_up_and_recorded()
    {
        var f = new ServerFixture(); await f.InitializeAsync();
        try
        {
            var cred = await f.PairAsync(Permission.Monitoring, "dropper");
            var c = await f.LoginAsync(cred);
            Assert.Single(f.Server.Clients);
            await c.DisposeAsync(); // aborts the socket without a close handshake
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (f.Server.Clients.Count > 0 && sw.Elapsed.TotalSeconds < 10) await Task.Delay(100);
            Assert.Empty(f.Server.Clients);
            Assert.Contains(f.Server.History.Recent(10), h => h.DeviceName == "dropper" && h.DisconnectedUtc != null);
        }
        finally { await f.DisposeAsync(); }
    }

    [Fact]
    public async Task Port_already_in_use_is_reported_as_a_failed_start()
    {
        var f = new ServerFixture(); await f.InitializeAsync();
        try
        {
            using var blocker = new TcpListener(IPAddress.Any, 0); blocker.Start();
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            await f.Server.StopAsync();
            var s = f.Server.SettingsStore.Load(); s.Port = port; f.Server.SettingsStore.Save(s);
            await Assert.ThrowsAnyAsync<Exception>(() => f.Server.StartAsync());
            Assert.Equal(ServerState.Faulted, f.Server.State);
            Assert.False(string.IsNullOrEmpty(f.Server.LastError));
        }
        finally { await f.DisposeAsync(); }
    }

    [Fact]
    public void Backup_roundtrip_never_overwrites_silently_and_restores_settings_and_shortcuts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcrc-bk-" + Guid.NewGuid().ToString("N")[..6]);
        var core = new ServerCore(dir);
        try
        {
            var backup = new BackupService(core.SettingsStore, core.Shortcuts, core.Devices);
            var st = core.SettingsStore.Load(); st.Port = 51111; st.ComputerName = "Backup-PC"; core.SettingsStore.Save(st);
            core.Shortcuts.Upsert(new Shortcut { Name = "Mine", Path = "notepad.exe" });
            var file = Path.Combine(dir, "b.json");
            backup.Export(file, includeDevices: false);
            Assert.Throws<IOException>(() => backup.Export(file, false)); // existing file is protected

            st.Port = 52222; core.SettingsStore.Save(st);
            foreach (var s in core.Shortcuts.List()) core.Shortcuts.Delete(s.Id);
            var sum = backup.Inspect(file);
            Assert.Equal(1, sum.ShortcutCount);
            backup.Restore(file, restoreDevices: false);
            Assert.Equal(51111, core.SettingsStore.Load().Port);
            Assert.Equal("Mine", Assert.Single(core.Shortcuts.List()).Name);

            File.WriteAllText(file, "not a backup");
            Assert.Throws<InvalidDataException>(() => backup.Inspect(file));
        }
        finally { core.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }
}
