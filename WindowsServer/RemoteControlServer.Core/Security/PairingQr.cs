using System.Net;
using QRCoder;

namespace RemoteControlServer.Security;

/// <summary>Builds the pairing link and renders it as a QR code that every phone scanner can read.</summary>
public static class PairingQr
{
    /// <summary>pcrc://pair?v=1&amp;h=ip1,ip2&amp;p=port&amp;c=code&amp;f=fingerprint&amp;n=name</summary>
    public static string BuildUri(IEnumerable<IPAddress> hosts, int port, string code, string fingerprintHex, string computerName) =>
        $"pcrc://pair?v=1&h={Uri.EscapeDataString(string.Join(',', hosts.Select(h => h.ToString())))}&p={port}&c={code}&f={fingerprintHex}&n={Uri.EscapeDataString(computerName)}";

    /// <summary>
    /// PNG of the QR code: BLACK modules on a WHITE background with the 4-module quiet zone the QR standard requires.
    /// (Light-on-dark "inverted" codes are not read by most scanners, including the app's ZXing scanner by default.)
    /// Error correction L keeps the code small (fewer, larger modules) - it is shown on a screen, not printed.
    /// </summary>
    public static byte[] RenderPng(string uri, int pixelsPerModule = 14)
    {
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.L);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule, [0, 0, 0], [255, 255, 255], drawQuietZones: true);
    }
}
