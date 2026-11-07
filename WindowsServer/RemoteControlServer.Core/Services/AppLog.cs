using System.Text.Json;

namespace RemoteControlServer.Services;

/// <summary>Minimal structured (JSON lines) rotating-by-day file logger. Never log secrets or command contents.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCRemoteCenter", "logs");

    public static void Configure(string dataDir)
    {
        _dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(_dir);
    }

    public static void Info(string message, object? data = null) => Write("info", message, data, null);
    public static void Warn(string message, object? data = null) => Write("warn", message, data, null);
    public static void Error(string message, Exception? ex = null, object? data = null) => Write("error", message, data, ex);

    private static void Write(string level, string message, object? data, Exception? ex)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"), level, message, data,
                exception = ex?.ToString(),
            });
            lock (Gate)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"server-{DateTime.UtcNow:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch { /* logging must never crash the app */ }
    }

    public static void Purge(int days)
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "server-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-days)) File.Delete(f);
        }
        catch { }
    }
}
