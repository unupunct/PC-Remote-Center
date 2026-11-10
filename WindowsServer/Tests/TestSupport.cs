using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Storage;

namespace RemoteControlServer.Tests;

public static class Net
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}

/// <summary>A real server instance (TLS, SQLite, temp data dir) shared by tests in a class.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public ServerCore Server { get; private set; } = null!;
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pcrc-test-" + Guid.NewGuid().ToString("N")[..8]);
    public int Port { get; private set; }
    public string Fingerprint => Server.Certs.Fingerprint;

    public async Task InitializeAsync()
    {
        Server = new ServerCore(Dir);
        Port = Net.FreePort();
        var s = Server.SettingsStore.Load();
        s.Port = Port; s.DiscoveryEnabled = false;
        Server.SettingsStore.Save(s);
        TestHooks?.Invoke(Server);
        await Server.StartAsync();
    }

    public static Action<ServerCore>? TestHooks { get; set; }

    public async Task DisposeAsync()
    {
        await Server.StopAsync();
        try { Directory.Delete(Dir, true); } catch { }
    }

    public Task<TestClient> ConnectAsync(string? pin = "auto") => TestClient.ConnectAsync(Port, pin == "auto" ? Fingerprint : pin);

    /// <summary>Pairs a new device and returns (deviceId, token).</summary>
    public async Task<(string id, string token)> PairAsync(Permission perms, string name = "Test phone")
    {
        Server.AuthLimiter.Reset("127.0.0.1");
        var offer = Server.Pairing.Create(perms, TimeSpan.FromMinutes(1));
        await using var c = await ConnectAsync();
        var r = await c.RequestAsync("auth.pair", new { code = offer.Code, deviceName = name });
        Assert.Equal("response", r.Type);
        return (r.Payload!["deviceId"]!.GetValue<string>(), r.Payload["token"]!.GetValue<string>());
    }

    public async Task<TestClient> LoginAsync((string id, string token) cred)
    {
        Server.AuthLimiter.Reset("127.0.0.1");
        var c = await ConnectAsync();
        var r = await c.RequestAsync("auth.login", new { deviceId = cred.id, token = cred.token });
        Assert.Equal("response", r.Type);
        return c;
    }
}

public sealed class TestClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Dictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    public Channel<Envelope> Pushes { get; } = Channel.CreateUnbounded<Envelope>();
    public Task Receiver { get; private set; } = Task.CompletedTask;
    public bool Closed { get; private set; }

    public static async Task<TestClient> ConnectAsync(int port, string? pinFingerprint, string host = "127.0.0.1")
    {
        var c = new TestClient();
        c._ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && (pinFingerprint == null || Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == pinFingerprint);
        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await c._ws.ConnectAsync(new Uri($"wss://{host}:{port}/ws"), to.Token);
        c.Receiver = Task.Run(c.ReceiveLoop);
        return c;
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { return; }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var env = Protocol.Parse(ms.ToArray());
                TaskCompletionSource<Envelope>? tcs = null;
                lock (_pending) if (env.RequestId != null) _pending.Remove(env.RequestId, out tcs);
                if (tcs != null) tcs.TrySetResult(env); else await Pushes.Writer.WriteAsync(env);
            }
        }
        catch { }
        finally
        {
            Closed = true;
            lock (_pending) foreach (var t in _pending.Values) t.TrySetCanceled();
            Pushes.Writer.TryComplete();
        }
    }

    public Task SendRawAsync(byte[] bytes) => _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task<Envelope> RequestAsync(string type, object? payload = null, string? requestId = null, int timeoutMs = 10000)
    {
        requestId ??= Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[requestId] = tcs;
        var env = new Envelope { Type = type, RequestId = requestId, Payload = Protocol.ToPayload(payload) };
        await SendRawAsync(Protocol.Serialize(env));
        using var to = new CancellationTokenSource(timeoutMs);
        using var _ = to.Token.Register(() => tcs.TrySetException(new TimeoutException($"No reply to {type}")));
        return await tcs.Task;
    }

    public async Task<Envelope> WaitPushAsync(Func<Envelope, bool> pred, int timeoutMs = 10000)
    {
        using var to = new CancellationTokenSource(timeoutMs);
        try
        {
            while (await Pushes.Reader.WaitToReadAsync(to.Token))
                while (Pushes.Reader.TryRead(out var e)) if (pred(e)) return e;
        }
        catch (OperationCanceledException) { }
        throw new TimeoutException("Push not received");
    }

    public async Task WaitClosedAsync(int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Closed && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        Assert.True(Closed, "connection should have been closed by the server");
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); _ws.Abort(); } catch { }
        try { await Receiver; } catch { }
        _ws.Dispose();
    }
}

public static class EnvExt
{
    public static string Code(this Envelope e) => e.Payload?["code"]?.GetValue<string>() ?? "";
    public static JsonObject P(this Envelope e) => e.Payload ?? new JsonObject();
}
