using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteControlServer.Security;

namespace RemoteControlServer.Networking;

public enum NetworkKind { Lan, Tailscale, WireGuard, OtherVpn }

/// <summary>One usable address of a network adapter, classified by how phones reach it.</summary>
public sealed record NetInterface(NetworkKind Kind, string Name, string Description, IPAddress Address, int PrefixLength)
{
    public string KindText => Kind switch { NetworkKind.Lan => "lan", NetworkKind.Tailscale => "tailscale", NetworkKind.WireGuard => "wireguard", _ => "vpn" };
}

/// <summary>Raw adapter data, separated from <see cref="NetworkInterface"/> so classification can be unit-tested.</summary>
public sealed record AdapterSnapshot(string Name, string Description, NetworkInterfaceType Type, bool IsUp,
    IReadOnlyList<(IPAddress Address, int PrefixLength)> Addresses);

/// <summary>An extra source range that is accepted only when the connection arrives on <see cref="LocalAddress"/>.</summary>
public sealed record VpnRule(NetworkKind Kind, string Name, IPAddress Network, int Bits, IPAddress LocalAddress);

/// <summary>
/// Finds Tailscale and WireGuard adapters. Nothing here opens a tunnel or changes VPN settings: Tailscale / WireGuard
/// are installed and signed in by the user; this only recognises them, shows their addresses and (when the owner opts in)
/// accepts connections that arrive through them.
/// </summary>
public static class VpnDetector
{
    public static readonly IPAddress TailscaleV4 = IPAddress.Parse("100.64.0.0");
    public const int TailscaleV4Bits = 10;
    public static readonly IPAddress TailscaleV6 = IPAddress.Parse("fd7a:115c:a1e0::");
    public const int TailscaleV6Bits = 48;

    private static readonly string[] VirtualWords =
        ["hyper-v", "virtualbox", "vmware", "bluetooth", "loopback", "pseudo", "wan miniport", "teredo", "isatap", "6to4", "vethernet", "docker", "wsl"];
    private static readonly string[] OtherVpnWords =
        ["openvpn", "tap-windows", "zerotier", "nordlynx", "mullvad", "proton", "wintun", "cisco anyconnect", "forticlient", "globalprotect"];

    public static NetworkKind? Classify(string name, string description, NetworkInterfaceType type)
    {
        var text = (name + " " + description).ToLowerInvariant();
        if (text.Contains("tailscale")) return NetworkKind.Tailscale;
        if (text.Contains("wireguard") || System.Text.RegularExpressions.Regex.IsMatch(name, @"^wg\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return NetworkKind.WireGuard;
        if (OtherVpnWords.Any(text.Contains)) return NetworkKind.OtherVpn;
        if (VirtualWords.Any(text.Contains)) return null;
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211) return NetworkKind.Lan;
        return null;
    }

    public static List<NetInterface> Classify(IEnumerable<AdapterSnapshot> adapters)
    {
        var list = new List<NetInterface>();
        foreach (var a in adapters)
        {
            if (!a.IsUp || a.Type == NetworkInterfaceType.Loopback) continue;
            var kind = Classify(a.Name, a.Description, a.Type);
            if (kind == null) continue;
            foreach (var (addr, prefix) in a.Addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (addr.GetAddressBytes() is [169, 254, ..]) continue; // link-local
                }
                else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // IPv6 only matters for the tailnet range (fd7a:115c:a1e0::/48), not link-local addresses
                    if (kind != NetworkKind.Tailscale || !NetworkFilter.InCidr(addr, TailscaleV6, TailscaleV6Bits)) continue;
                }
                else continue;
                list.Add(new NetInterface(kind.Value, a.Name, a.Description, addr, prefix));
            }
        }
        return list;
    }

    public static List<NetInterface> Detect()
    {
        var snaps = new List<AdapterSnapshot>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var addrs = n.GetIPProperties().UnicastAddresses.Select(u => (u.Address, u.PrefixLength)).ToList();
                snaps.Add(new AdapterSnapshot(n.Name, n.Description, n.NetworkInterfaceType, n.OperationalStatus == OperationalStatus.Up, addrs));
            }
            catch (NetworkInformationException) { /* adapter vanished while enumerating */ }
        }
        return Classify(snaps);
    }

    private static List<NetInterface>? _cache; private static DateTime _cacheAt;
    private static readonly object CacheLock = new();

    /// <summary>Detection result cached for a few seconds (called for every incoming connection).</summary>
    public static List<NetInterface> CachedDetect()
    {
        lock (CacheLock)
        {
            if (_cache == null || DateTime.UtcNow - _cacheAt > TimeSpan.FromSeconds(5)) { _cache = Detect(); _cacheAt = DateTime.UtcNow; }
            return _cache;
        }
    }

    public static void InvalidateCache() { lock (CacheLock) _cache = null; }

    /// <summary>
    /// Source-range rules for the VPN kinds the owner enabled. Tailscale: the whole tailnet range. WireGuard: the subnet of the
    /// tunnel address (a /32 tunnel address has no peer subnet - add one under "extra networks" in that case).
    /// </summary>
    public static List<VpnRule> BuildRules(IEnumerable<NetInterface> interfaces, bool allowTailscale, bool allowWireGuard)
    {
        var rules = new List<VpnRule>();
        foreach (var i in interfaces)
        {
            if (i.Kind == NetworkKind.Tailscale && allowTailscale)
            {
                if (i.Address.AddressFamily == AddressFamily.InterNetwork) rules.Add(new VpnRule(i.Kind, i.Name, TailscaleV4, TailscaleV4Bits, i.Address));
                else rules.Add(new VpnRule(i.Kind, i.Name, TailscaleV6, TailscaleV6Bits, i.Address));
            }
            else if (i.Kind == NetworkKind.WireGuard && allowWireGuard && i.Address.AddressFamily == AddressFamily.InterNetwork && i.PrefixLength is > 0 and < 32)
            {
                rules.Add(new VpnRule(i.Kind, i.Name, MaskNetwork(i.Address, i.PrefixLength), i.PrefixLength, i.Address));
            }
        }
        return rules;
    }

    public static IPAddress MaskNetwork(IPAddress a, int bits)
    {
        var b = a.GetAddressBytes();
        for (int i = 0; i < b.Length; i++)
        {
            int keep = Math.Clamp(bits - i * 8, 0, 8);
            b[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(b);
    }

    /// <summary>Addresses to put into the pairing QR code: LAN first, then VPN addresses the owner has allowed.</summary>
    public static List<IPAddress> PairingHosts(IEnumerable<NetInterface> interfaces, bool allowTailscale, bool allowWireGuard)
    {
        var all = interfaces.Where(i => i.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        var hosts = all.Where(i => i.Kind == NetworkKind.Lan && NetworkFilter.IsPrivate(i.Address)).Select(i => i.Address).ToList();
        if (allowTailscale) hosts.AddRange(all.Where(i => i.Kind == NetworkKind.Tailscale).Select(i => i.Address));
        if (allowWireGuard) hosts.AddRange(all.Where(i => i.Kind == NetworkKind.WireGuard && i.PrefixLength is > 0 and < 32).Select(i => i.Address));
        return hosts.DistinctBy(a => a.ToString()).ToList();
    }
}
