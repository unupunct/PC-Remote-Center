using System.Net;
using System.Net.WebSockets;
using System.Text;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Security;
using RemoteControlServer.Storage;

namespace RemoteControlServer.Tests;

public class SecurityTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public SecurityTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task Pairing_with_valid_one_time_code_succeeds_and_code_cannot_be_reused()
    {
        _f.Server.AuthLimiter.Reset("127.0.0.1");
        var offer = _f.Server.Pairing.Create(Permission.Monitoring | Permission.Terminal, TimeSpan.FromMinutes(1));
        await using var c1 = await _f.ConnectAsync();
        var ok = await c1.RequestAsync("auth.pair", new { code = offer.Code, deviceName = "Phone A" });
        Assert.Equal("response", ok.Type);
        Assert.False(string.IsNullOrEmpty(ok.P()["token"]?.GetValue<string>()));
        var perms = ok.P()["permissions"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("monitoring", perms); Assert.Contains("terminal", perms); Assert.DoesNotContain("power", perms);

        await using var c2 = await _f.ConnectAsync();
        var bad = await c2.RequestAsync("auth.pair", new { code = offer.Code, deviceName = "Phone B" });
        Assert.Equal("error", bad.Type);
        Assert.Equal("auth_failed", bad.Code());
    }

    [Fact]
    public async Task Expired_pairing_code_is_rejected()
    {
        _f.Server.AuthLimiter.Reset("127.0.0.1");
        var offer = _f.Server.Pairing.Create(Permission.Monitoring, TimeSpan.FromMilliseconds(-1));
        await using var c = await _f.ConnectAsync();
        var r = await c.RequestAsync("auth.pair", new { code = offer.Code, deviceName = "x" });
        Assert.Equal("auth_failed", r.Code());
    }

    [Fact]
    public async Task Login_with_stored_token_works_and_wrong_token_fails()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var good = await _f.LoginAsync(cred);
        var info = await good.RequestAsync("server.info");
        Assert.Equal("response", info.Type);

        _f.Server.AuthLimiter.Reset("127.0.0.1");
        await using var bad = await _f.ConnectAsync();
        var r = await bad.RequestAsync("auth.login", new { deviceId = cred.id, token = "not-the-token" });
        Assert.Equal("auth_failed", r.Code());
        await bad.WaitClosedAsync();
    }

    [Fact]
    public async Task Token_is_stored_only_as_hash()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        var rec = _f.Server.Devices.Get(cred.id)!;
        Assert.NotEqual(Encoding.UTF8.GetBytes(cred.token), rec.TokenHash);
        Assert.Equal(32, rec.TokenHash.Length);
        foreach (var f in Directory.GetFiles(_f.Dir, "pcremote.db*").Where(x => !x.EndsWith("-shm")))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream(); fs.CopyTo(ms);
            Assert.True(ms.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(cred.token)) < 0, "plaintext token found in " + f);
        }
    }

    [Fact]
    public async Task Unauthenticated_commands_are_refused_and_connection_closed()
    {
        _f.Server.AuthLimiter.Reset("127.0.0.1");
        await using var c = await _f.ConnectAsync();
        var r = await c.RequestAsync("terminal.execute", new { shell = "cmd", command = "whoami" });
        Assert.Equal("error", r.Type);
        Assert.Equal("unauthenticated", r.Code());
        await c.WaitClosedAsync();
    }

    [Fact]
    public async Task Revoking_a_device_disconnects_it_and_blocks_future_logins()
    {
        var cred = await _f.PairAsync(Permission.Monitoring, "Revoke me");
        await using var c = await _f.LoginAsync(cred);
        _f.Server.Devices.Revoke(cred.id);
        await c.WaitClosedAsync();

        _f.Server.AuthLimiter.Reset("127.0.0.1");
        await using var again = await _f.ConnectAsync();
        var r = await again.RequestAsync("auth.login", new { deviceId = cred.id, token = cred.token });
        Assert.Equal("auth_failed", r.Code());
    }

    [Fact]
    public async Task Permissions_are_enforced_per_message_type()
    {
        _f.Server.Handlers.Register("test.needs.terminal", Permission.Terminal, (c, e) => Task.FromResult<object?>(new { ran = true }));
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var c = await _f.LoginAsync(cred);
        var r = await c.RequestAsync("test.needs.terminal");
        Assert.Equal("forbidden", r.Code());

        _f.Server.Devices.SetPermissions(cred.id, Permission.Monitoring | Permission.Terminal);
        var push = await c.WaitPushAsync(e => e.Type == "auth.permissions");
        Assert.NotNull(push);
        var ok = await c.RequestAsync("test.needs.terminal");
        Assert.Equal("response", ok.Type);
    }

    [Fact]
    public async Task Malformed_messages_return_errors_without_dropping_the_session()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var c = await _f.LoginAsync(cred);
        await c.SendRawAsync(Encoding.UTF8.GetBytes("this is not json"));
        var e1 = await c.WaitPushAsync(e => e.Type == "error");
        Assert.Equal("malformed", e1.Code());

        await c.SendRawAsync(Encoding.UTF8.GetBytes("""{"version":99,"type":"ping"}"""));
        var e2 = await c.WaitPushAsync(e => e.Type == "error");
        Assert.Equal("unsupported_version", e2.Code());

        var unknown = await c.RequestAsync("does.not.exist");
        Assert.Equal("unknown_type", unknown.Code());

        var ping = await c.RequestAsync("ping");
        Assert.Equal("response", ping.Type);
    }

    [Fact]
    public async Task Duplicate_request_ids_are_rejected()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var c = await _f.LoginAsync(cred);
        var a = await c.RequestAsync("ping", null, "same-id");
        Assert.Equal("response", a.Type);
        var b = await c.RequestAsync("ping", null, "same-id");
        Assert.Equal("duplicate_request", b.Code());
    }

    [Fact]
    public async Task Oversized_message_is_rejected_and_closes_connection()
    {
        var cred = await _f.PairAsync(Permission.Monitoring);
        await using var c = await _f.LoginAsync(cred);
        var big = new string('a', Protocol.MaxMessageBytes + 1000);
        await c.SendRawAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"ping\",\"payload\":{\"x\":\"" + big + "\"}}"));
        await c.WaitClosedAsync();
    }

    [Fact]
    public async Task Repeated_bad_pairing_codes_trigger_lockout()
    {
        var server = _f.Server;
        server.AuthLimiter.Reset("127.0.0.1");
        for (int i = 0; i < 5; i++)
        {
            await using var c = await _f.ConnectAsync();
            var r = await c.RequestAsync("auth.pair", new { code = "00000000", deviceName = "attacker" });
            Assert.Equal("auth_failed", r.Code());
        }
        await using var locked = await _f.ConnectAsync();
        var first = await locked.WaitPushAsync(e => e.Type == "error");
        Assert.Equal("rate_limited", first.Code());
        server.AuthLimiter.Reset("127.0.0.1");
    }

    [Fact]
    public async Task Connection_without_auth_times_out()
    {
        var s = _f.Server.SettingsStore.Load(); s.AuthTimeoutSeconds = 3; _f.Server.SettingsStore.Save(s);
        _f.Server.AuthLimiter.Reset("127.0.0.1");
        await using var c = await _f.ConnectAsync();
        var e = await c.WaitPushAsync(x => x.Type == "error", 8000);
        Assert.Equal("auth_timeout", e.Code());
        s.AuthTimeoutSeconds = 10; _f.Server.SettingsStore.Save(s);
    }

    [Fact]
    public async Task Tls_pinning_rejects_wrong_fingerprint()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => TestClient.ConnectAsync(_f.Port, new string('0', 64)));
        await using var ok = await TestClient.ConnectAsync(_f.Port, _f.Fingerprint);
    }

    [Fact]
    public async Task Plain_http_websocket_is_not_accepted()
    {
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_f.Port}/ws"), CancellationToken.None));
    }

    [Fact]
    public void Network_filter_is_lan_only_by_default()
    {
        var f = new NetworkFilter(() => (true, "10.8.0.0/24"));
        Assert.True(f.IsAllowed(IPAddress.Parse("192.168.1.20")));
        Assert.True(f.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.True(f.IsAllowed(IPAddress.Parse("172.16.5.5")));
        Assert.True(f.IsAllowed(IPAddress.Loopback));
        Assert.False(f.IsAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(f.IsAllowed(IPAddress.Parse("172.32.0.1")));
        Assert.False(f.IsAllowed(IPAddress.Parse("100.64.0.9")));
        var vpn = new NetworkFilter(() => (true, "100.64.0.0/10"));
        Assert.True(vpn.IsAllowed(IPAddress.Parse("100.100.1.1")));
        var open = new NetworkFilter(() => (false, ""));
        Assert.True(open.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Database_migrates_to_latest_and_reopens()
    {
        var path = Path.Combine(_f.Dir, "migr.db");
        var db = new Database(path);
        Assert.Equal(Database.LatestVersion, db.SchemaVersion());
        var db2 = new Database(path);
        Assert.Equal(Database.LatestVersion, db2.SchemaVersion());
    }

    [Fact]
    public void Audit_log_records_events()
    {
        _f.Server.Audit.Write("tester", "unit.test", "detail");
        Assert.Contains(_f.Server.Audit.Recent(50), e => e.Action == "unit.test" && e.Actor == "tester");
    }
}
