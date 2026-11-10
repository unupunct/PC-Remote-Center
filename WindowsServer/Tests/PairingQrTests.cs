using System.Drawing;
using System.Net;
using RemoteControlServer.Security;
using ZXing;
using ZXing.Common;

namespace RemoteControlServer.Tests;

public class PairingQrTests
{
    private static readonly string Fp = new('A', 64);

    private static string Uri(string name = "DESKTOP-4BL01TS") => PairingQr.BuildUri(
        [IPAddress.Parse("192.168.1.9"), IPAddress.Parse("100.77.104.0")], 47821, "12345678", Fp, name);

    private static Bitmap ToBitmap(byte[] png) { using var ms = new MemoryStream(png); return new Bitmap(ms); }

    private static string? Decode(Bitmap bmp, bool tryInverted)
    {
        var lum = new RGBLuminanceSource(ToBgr(bmp), bmp.Width, bmp.Height, RGBLuminanceSource.BitmapFormat.BGR24);
        var reader = new BarcodeReaderGeneric { AutoRotate = false, Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, TryInverted = tryInverted } };
        return reader.Decode(lum)?.Text;
    }

    private static byte[] ToBgr(Bitmap bmp)
    {
        var bytes = new byte[bmp.Width * bmp.Height * 3]; int i = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); bytes[i++] = c.B; bytes[i++] = c.G; bytes[i++] = c.R; }
        return bytes;
    }

    [Fact]
    public void Generated_qr_is_black_on_white_and_decodes_with_a_plain_scanner()
    {
        var uri = Uri();
        using var bmp = ToBitmap(PairingQr.RenderPng(uri));
        // quiet zone is white, the first finder-pattern module (4 modules in) is black
        Assert.Equal(255, bmp.GetPixel(2, 2).R);
        var module = bmp.Width / ((bmp.Width / 14)); // pixels per module = 14
        var finder = bmp.GetPixel(4 * 14 + 3, 4 * 14 + 3);
        Assert.True(finder.R < 60 && finder.G < 60 && finder.B < 60, "finder pattern must be dark");
        // decoded WITHOUT any inversion option - exactly what a default scanner does
        Assert.Equal(uri, Decode(bmp, tryInverted: false));
    }

    [Fact]
    public void Qr_still_decodes_when_shown_small_like_on_a_phone_camera_distance()
    {
        var uri = Uri();
        using var big = ToBitmap(PairingQr.RenderPng(uri, 14));
        using var small = new Bitmap(big, new Size(260, 260)); // scaled down with smoothing, as a camera sees it
        Assert.Equal(uri, Decode(small, tryInverted: false));
    }

    [Fact]
    public void Long_computer_names_and_many_addresses_still_produce_a_scannable_code()
    {
        var hosts = Enumerable.Range(1, 4).Select(i => IPAddress.Parse($"192.168.{i}.20")).Append(IPAddress.Parse("100.77.104.0")).ToList();
        var uri = PairingQr.BuildUri(hosts, 47821, "00000001", Fp, new string('N', 40));
        using var bmp = ToBitmap(PairingQr.RenderPng(uri));
        Assert.Equal(uri, Decode(bmp, false));
        Assert.True(bmp.Width <= 1000, "code should stay a reasonable size, was " + bmp.Width);
    }

    [Fact]
    public void Uri_has_the_fields_the_phone_expects()
    {
        var u = new System.Uri(Uri("My PC"));
        Assert.Equal("pcrc", u.Scheme);
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        Assert.Equal("1", q["v"]); Assert.Equal("192.168.1.9,100.77.104.0", q["h"]); Assert.Equal("47821", q["p"]);
        Assert.Equal("12345678", q["c"]); Assert.Equal(Fp, q["f"]); Assert.Equal("My PC", q["n"]);
    }
}
