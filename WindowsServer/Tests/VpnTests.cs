using System.Net;
using System.Net.NetworkInformation;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Security;
using Xunit.Abstractions;

namespace RemoteControlServer.Tests;

public class VpnClassificationTests
{
    [Theory]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, NetworkKind.Tailscale)]
    [InlineData("wg0", "WireGuard Tunnel", NetworkInterfaceType.Unknown, NetworkKind.WireGuard)]
    [InlineData("home", "WireGuard Tunnel", NetworkInterfaceType.Tunnel, NetworkKind.WireGuard)]
    [InlineData("Ethernet 2", "Intel(R) Ethernet Connection (2) I219-LM", NetworkInterfaceType.Ethernet, NetworkKind.Lan)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, NetworkKind.Lan)]
    [InlineData("OpenVPN", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, NetworkKind.OtherVpn)]
    public void Adapters_are_classified(string name, string desc, NetworkInterfaceType type, NetworkKind expected) =>
        Assert.Equal(expected, VpnDetector.Classify(name, desc, type));

    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("Local Area Connection* 6", "WAN Miniport (IP)", NetworkInterfaceType.Ethernet)]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet)]
    public void Virtual_adapters_are_ignored(string name, string desc, NetworkInterfaceType type) =>
        Assert.Null(VpnDetector.Classify(name, desc, type));

    private static AdapterSnapshot Snap(string n, string d, NetworkInterfaceType t, string ip, int prefix, bool up = true) =>
        new(n, d, t, up, [(IPAddress.Parse(ip), prefix)]);

    private static readonly AdapterSnapshot[] Typical =
    [
        Snap("Ethernet 2", "Intel(R) Ethernet Connection (2) I219-LM", NetworkInterfaceType.Ethernet, "192.168.1.9", 24),
        Snap("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, "172.30.80.1", 20),
        Snap("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, "100.77.104.7", 32),
        Snap("office", "WireGuard Tunnel", NetworkInterfaceType.Unknown, "44.10.0.2", 24),
        Snap("Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211, "169.254.3.4", 16),            // link-local: ignored
        Snap("Old", "Intel Ethernet", NetworkInterfaceType.Ethernet, "10.0.0.5", 24, up: false),          // down: ignored
        Snap("Loopback", "Software Loopback", NetworkInterfaceType.Loopback, "127.0.0.1", 8),
    ];

    [Fact]
    public void Only_usable_addresses_are_reported()
    {
        var list = VpnDetector.Classify(Typical);
        Assert.Equal(3, list.Count);
        Assert.Contains(list, i => i.Kind == NetworkKind.Lan && i.Address.ToString() == "192.168.1.9");
        Assert.Contains(list, i => i.Kind == NetworkKind.Tailscale && i.Address.ToString() == "100.77.104.7");
        Assert.Contains(list, i => i.Kind == NetworkKind.WireGuard && i.Name == "office");
    }

    [Fact]
    public void Pairing_hosts_list_lan_first_and_vpn_only_when_allowed()
    {
        var list = VpnDetector.Classify(Typical);
        Assert.Equal(["192.168.1.9"], VpnDetector.PairingHosts(list, false, false).Select(a => a.ToString()));
        Assert.Equal(["192.168.1.9", "100.77.104.7"], VpnDetector.PairingHosts(list, true, false).Select(a => a.ToString()));
        Assert.Equal(["192.168.1.9", "100.77.104.7", "44.10.0.2"], VpnDetector.PairingHosts(list, true, true).Select(a => a.ToString()));
    }

    [Fact]
    public void Rules_exist_only_for_enabled_kinds()
    {
        var list = VpnDetector.Classify(Typical);
        Assert.Empty(VpnDetector.BuildRules(list, false, false));
        var ts = Assert.Single(VpnDetector.BuildRules(list, true, false));
        Assert.Equal(NetworkKind.Tailscale, ts.Kind); Assert.Equal("100.64.0.0", ts.Network.ToString()); Assert.Equal(10, ts.Bits);
        var wg = Assert.Single(VpnDetector.BuildRules(list, false, true));
        Assert.Equal("44.10.0.0", wg.Network.ToString()); Assert.Equal(24, wg.Bits);
    }

    [Fact]
    public void WireGuard_host_route_tunnel_address_gives_no_peer_range()
    {
        var list = VpnDetector.Classify([Snap("wg0", "WireGuard Tunnel", NetworkInterfaceType.Unknown, "10.66.66.2", 32)]);
        Assert.Empty(VpnDetector.BuildRules(list, true, true));
        Assert.Empty(VpnDetector.PairingHosts(list, true, true));
    }

    [Fact]
    public void Filter_accepts_vpn_sources_only_on_the_matching_adapter_and_only_when_enabled()
    {
        var list = VpnDetector.Classify(Typical);
        NetworkFilter Make(bool ts, bool wg) => new(() => (true, ""), () => VpnDetector.BuildRules(list, ts, wg));
        var tsPeer = IPAddress.Parse("100.99.1.1"); var tsLocal = IPAddress.Parse("100.77.104.7"); var lanLocal = IPAddress.Parse("192.168.1.9");

        Assert.False(Make(false, false).IsAllowed(tsPeer, tsLocal));          // opt-in: off by default
        Assert.True(Make(true, false).IsAllowed(tsPeer, tsLocal));            // tailnet peer via the Tailscale adapter
        Assert.False(Make(true, false).IsAllowed(tsPeer, lanLocal));          // same source address but arriving on the LAN adapter = spoof
        Assert.False(Make(true, false).IsAllowed(tsPeer));                    // unknown local address
        Assert.False(Make(true, false).IsAllowed(IPAddress.Parse("8.8.8.8"), tsLocal)); // outside the tailnet range
        Assert.False(Make(true, false).IsAllowed(IPAddress.Parse("100.128.0.1"), tsLocal)); // just outside 100.64.0.0/10

        var wgPeer = IPAddress.Parse("44.10.0.9"); var wgLocal = IPAddress.Parse("44.10.0.2");
        Assert.False(Make(true, false).IsAllowed(wgPeer, wgLocal));           // WireGuard toggle is separate
        Assert.True(Make(false, true).IsAllowed(wgPeer, wgLocal));
        Assert.False(Make(false, true).IsAllowed(IPAddress.Parse("44.10.1.9"), wgLocal)); // other subnet
        Assert.True(Make(false, false).IsAllowed(IPAddress.Parse("192.168.1.50"), lanLocal)); // LAN unaffected
    }

    [Fact]
    public void Mapped_ipv6_addresses_are_handled()
    {
        var list = VpnDetector.Classify(Typical);
        var f = new NetworkFilter(() => (true, ""), () => VpnDetector.BuildRules(list, true, false));
        Assert.True(f.IsAllowed(IPAddress.Parse("100.99.1.1").MapToIPv6(), IPAddress.Parse("100.77.104.7").MapToIPv6()));
    }
}

/// <summary>Uses the real network adapters of the machine running the tests.</summary>
public class VpnRealAdapterTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f; private readonly ITestOutputHelper _out;
    public VpnRealAdapterTests(ServerFixture f, ITestOutputHelper o) { _f = f; _out = o; }

    [Fact]
    public void Real_adapters_are_listed_and_classified()
    {
        var list = VpnDetector.Detect();
        foreach (var i in list) _out.WriteLine($"{i.Kind,-9} {i.Name} ({i.Description}) {i.Address}/{i.PrefixLength}");
        Assert.NotEmpty(list);
        Assert.All(list.Where(i => i.Kind == NetworkKind.Lan), i => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, i.Address.AddressFamily));
    }

    [Fact]
    public async Task Connection_through_the_real_tailscale_adapter_needs_the_opt_in()
    {
        var ts = VpnDetector.Detect().FirstOrDefault(i => i.Kind == NetworkKind.Tailscale && i.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (ts == null) { _out.WriteLine("No Tailscale adapter on this machine - skipped."); return; }
        _out.WriteLine($"Using Tailscale address {ts.Address} on adapter '{ts.Name}'");
        var store = _f.Server.SettingsStore; var s = store.Load();
        try
        {
            s.AllowTailscale = false; store.Save(s); VpnDetector.InvalidateCache();
            // refused before any TLS/WebSocket data is processed (HTTP 403)
            await Assert.ThrowsAnyAsync<Exception>(() => TestClient.ConnectAsync(_f.Port, _f.Fingerprint, ts.Address.ToString()));

            s.AllowTailscale = true; store.Save(s); VpnDetector.InvalidateCache();
            _f.Server.AuthLimiter.Reset(ts.Address.ToString());
            var cred = await PairVia(ts.Address.ToString());
            await using var c = await TestClient.ConnectAsync(_f.Port, _f.Fingerprint, ts.Address.ToString());
            var login = await c.RequestAsync("auth.login", new { deviceId = cred.id, token = cred.token });
            Assert.Equal("response", login.Type);
            var info = await c.RequestAsync("server.info");
            var nets = info.P()["networks"]!.AsArray().Select(n => (n!["kind"]!.GetValue<string>(), n["address"]!.GetValue<string>())).ToList();
            _out.WriteLine("server.info networks: " + string.Join(", ", nets.Select(n => $"{n.Item1}={n.Item2}")));
            Assert.Contains(("tailscale", ts.Address.ToString()), nets);
        }
        finally { s.AllowTailscale = false; store.Save(s); VpnDetector.InvalidateCache(); }
    }

    private async Task<(string id, string token)> PairVia(string host)
    {
        var offer = _f.Server.Pairing.Create(Permission.Monitoring, TimeSpan.FromMinutes(1));
        await using var c = await TestClient.ConnectAsync(_f.Port, _f.Fingerprint, host);
        var r = await c.RequestAsync("auth.pair", new { code = offer.Code, deviceName = "tailscale phone" });
        Assert.Equal("response", r.Type);
        return (r.P()["deviceId"]!.GetValue<string>(), r.P()["token"]!.GetValue<string>());
    }
}
