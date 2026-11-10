using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteControlServer.Models;

namespace RemoteControlServer.Security;

/// <summary>Windows DPAPI (current user) wrapper for secrets stored on disk.</summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = "PCRemoteCenter.v1"u8.ToArray();
    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Creates and persists the self-signed TLS certificate (private key protected with DPAPI).</summary>
public sealed class CertificateManager
{
    private readonly string _file;
    public X509Certificate2 Certificate { get; private set; }
    public string Fingerprint => Convert.ToHexString(Certificate.GetCertHash(HashAlgorithmName.SHA256));

    public CertificateManager(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "server-cert.dpapi");
        Certificate = LoadOrCreate();
    }

    private X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(_file))
        {
            try
            {
                var pfx = Dpapi.Unprotect(File.ReadAllBytes(_file));
                var cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                if (cert.NotAfter > DateTime.UtcNow.AddDays(30)) return cert;
            }
            catch { /* regenerate below */ }
        }
        return Regenerate();
    }

    /// <summary>Generates a new certificate. Paired devices pin the fingerprint, so they must re-pair afterwards.</summary>
    public X509Certificate2 Regenerate()
    {
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest($"CN=PC Remote Center ({Environment.MachineName})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in NetworkInfo.LocalAddresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = self.Export(X509ContentType.Pfx);
        File.WriteAllBytes(_file, Dpapi.Protect(pfx));
        Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        return Certificate;
    }
}

public static class NetworkInfo
{
    public static List<IPAddress> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
}

/// <summary>Decides whether a remote address may connect (LAN-only by default).</summary>
public sealed class NetworkFilter
{
    private readonly Func<(bool lanOnly, string extra)> _cfg;
    private readonly Func<IReadOnlyList<RemoteControlServer.Networking.VpnRule>>? _vpn;

    public NetworkFilter(Func<(bool lanOnly, string extra)> cfg, Func<IReadOnlyList<RemoteControlServer.Networking.VpnRule>>? vpnRules = null)
    { _cfg = cfg; _vpn = vpnRules; }

    /// <param name="local">Local address the connection arrived on. VPN rules (Tailscale/WireGuard) only match when it
    /// equals the VPN adapter's own address, so a spoofed 100.x source on the LAN adapter is still refused.</param>
    public bool IsAllowed(IPAddress? addr, IPAddress? local = null)
    {
        if (addr == null) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (local is { IsIPv4MappedToIPv6: true }) local = local.MapToIPv4();
        if (IPAddress.IsLoopback(addr)) return true;
        var (lanOnly, extra) = _cfg();
        if (!lanOnly) return true;
        foreach (var part in extra.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (TryCidr(part, out var net, out var bits) && InCidr(addr, net!, bits)) return true;
        if (local != null && _vpn != null)
            foreach (var r in _vpn())
                if (r.LocalAddress.Equals(local) && InCidr(addr, r.Network, r.Bits)) return true;
        return IsPrivate(addr);
    }

    /// <summary>RFC 1918, link-local and IPv6 link-local/unique-local addresses.</summary>
    public static bool IsPrivate(IPAddress addr)
    {
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            return addr.IsIPv6LinkLocal || addr.IsIPv6UniqueLocal || addr.IsIPv6SiteLocal;
        var b = addr.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public static bool TryCidr(string s, out IPAddress? net, out int bits)
    {
        net = null; bits = 0;
        var p = s.Split('/');
        if (!IPAddress.TryParse(p[0], out net)) return false;
        bits = net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (p.Length == 2 && !int.TryParse(p[1], out bits)) return false;
        return bits >= 0 && bits <= (net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    public static bool InCidr(IPAddress addr, IPAddress net, int bits)
    {
        if (addr.AddressFamily != net.AddressFamily) return false;
        var a = addr.GetAddressBytes(); var n = net.GetAddressBytes();
        int full = bits / 8, rem = bits % 8;
        for (int i = 0; i < full; i++) if (a[i] != n[i]) return false;
        if (rem == 0) return true;
        int mask = 0xFF << (8 - rem);
        return (a[full] & mask) == (n[full] & mask);
    }
}

public sealed class PairingOffer
{
    public string Code { get; init; } = "";
    public DateTime ExpiresUtc { get; init; }
    public Permission Permissions { get; init; }
}

/// <summary>One-time pairing codes. A code works exactly once and expires.</summary>
public sealed class PairingService
{
    private readonly ConcurrentDictionary<string, PairingOffer> _offers = new();
    public event Action? Changed;

    public PairingOffer Create(Permission permissions, TimeSpan lifetime)
    {
        var offer = new PairingOffer
        {
            Code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8"),
            ExpiresUtc = DateTime.UtcNow + lifetime,
            Permissions = permissions,
        };
        _offers[offer.Code] = offer;
        Changed?.Invoke();
        return offer;
    }

    public PairingOffer? Active
    {
        get
        {
            Prune();
            return _offers.Values.OrderByDescending(o => o.ExpiresUtc).FirstOrDefault();
        }
    }

    public PairingOffer? Consume(string code)
    {
        Prune();
        if (_offers.TryRemove(code, out var offer) && offer.ExpiresUtc > DateTime.UtcNow)
        {
            Changed?.Invoke();
            return offer;
        }
        return null;
    }

    public void CancelAll() { _offers.Clear(); Changed?.Invoke(); }

    private void Prune()
    {
        foreach (var kv in _offers.Where(k => k.Value.ExpiresUtc <= DateTime.UtcNow).ToList()) _offers.TryRemove(kv.Key, out _);
    }
}

public static class TokenService
{
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    public static bool Verify(string token, byte[] hash) => CryptographicOperations.FixedTimeEquals(Hash(token), hash);
    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Sliding-window failure counter per key (IP). Locks out after too many failures.</summary>
public sealed class FailureLimiter
{
    private readonly int _max; private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, List<DateTime>> _fails = new();
    public FailureLimiter(int max = 5, TimeSpan? window = null) { _max = max; _window = window ?? TimeSpan.FromMinutes(5); }

    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_fails.TryGetValue(key, out var list)) return false;
        lock (list)
        {
            list.RemoveAll(t => DateTime.UtcNow - t > _window);
            if (list.Count < _max) return false;
            retryAfter = _window - (DateTime.UtcNow - list[0]);
            return true;
        }
    }

    public void RecordFailure(string key)
    {
        var list = _fails.GetOrAdd(key, _ => []);
        lock (list) list.Add(DateTime.UtcNow);
    }

    public void Reset(string key) => _fails.TryRemove(key, out _);
}

/// <summary>Per-connection message rate limiter (fixed one-second window).</summary>
public sealed class MessageRateLimiter
{
    private readonly int _perSecond; private long _windowStart; private int _count;
    public MessageRateLimiter(int perSecond = 200) => _perSecond = perSecond;
    public bool Allow()
    {
        var now = Environment.TickCount64;
        if (now - _windowStart >= 1000) { _windowStart = now; _count = 0; }
        return ++_count <= _perSecond;
    }
}
