using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteControlServer.Models;
using RemoteControlServer.Security;
using RemoteControlServer.Services;
using RemoteControlServer.Storage;

namespace RemoteControlServer.Networking;

public enum ServerState { Stopped, Starting, Running, Stopping, Faulted }

public sealed record ConnectedDevice(string SessionId, string DeviceId, string Name, string Ip, DateTime ConnectedUtc, Permission Permissions);

/// <summary>
/// Composition root for everything that does not need a UI: storage, security, the TLS WebSocket
/// server, discovery. Feature modules (monitoring, terminal, power, shortcuts) register handlers.
/// </summary>
public sealed class ServerCore : IDisposable
{
    public string DataDir { get; }
    public Database Db { get; }
    public SettingsStore SettingsStore { get; }
    public DeviceStore Devices { get; }
    public ShortcutStore Shortcuts { get; }
    public AuditLog Audit { get; }
    public ConnectionHistory History { get; }
    public CertificateManager Certs { get; }
    public PairingService Pairing { get; } = new();
    public HandlerRegistry Handlers { get; } = new();
    public FailureLimiter AuthLimiter { get; } = new();
    public NetworkFilter Filter { get; }
    public ServerSettings Settings { get; private set; }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public DateTime? StartedUtc { get; private set; }
    public int ListeningPort { get; private set; }
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    public event Action? StateChanged;
    public event Action? ClientsChanged;
    /// <summary>Raised after a device authenticates. (device name, ip, firstTime)</summary>
    public event Action<string, string, bool>? DeviceConnected;

    private long _bytesIn, _bytesOut;
    private readonly List<ClientContext> _clients = [];
    private WebApplication? _app;
    private UdpClient? _udp;
    private CancellationTokenSource? _udpCts;
    private readonly object _lock = new();

    public ServerCore(string? dataDir = null)
    {
        DataDir = dataDir ?? DataPaths.Default;
        Directory.CreateDirectory(DataDir);
        AppLog.Configure(DataDir);
        Db = new Database(Path.Combine(DataDir, "pcremote.db"));
        SettingsStore = new SettingsStore(Db);
        Devices = new DeviceStore(Db);
        Shortcuts = new ShortcutStore(Db);
        Audit = new AuditLog(Db);
        History = new ConnectionHistory(Db);
        Certs = new CertificateManager(DataDir);
        Settings = SettingsStore.Load();
        Filter = new NetworkFilter(() => (Settings.LanOnly, Settings.ExtraAllowedNetworks));
        SettingsStore.Changed += () => Settings = SettingsStore.Load();
        Devices.Changed += OnDevicesChanged;
        RegisterCoreHandlers();
    }

    public IReadOnlyList<ConnectedDevice> Clients
    {
        get { lock (_lock) return _clients.Select(c => new ConnectedDevice(c.SessionId, c.DeviceId, c.DeviceName, c.RemoteIp.ToString(), c.ConnectedUtc, c.Permissions)).ToList(); }
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task StartAsync()
    {
        if (State is ServerState.Running or ServerState.Starting) return;
        SetState(ServerState.Starting);
        try
        {
            Settings = SettingsStore.Load();
            var port = Settings.Port;
            // Fail loudly if another program owns the port (Kestrel alone may coexist silently with another listener)
            try { using var probe = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true }; probe.Start(); }
            catch (SocketException ex) { throw new InvalidOperationException($"TCP port {port} is already in use by another program ({ex.Message}). Choose a different port in Network Settings.", ex); }
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 64 * 1024;
                k.Limits.MaxConcurrentConnections = 50;
                k.ListenAnyIP(port, o => o.UseHttps(h =>
                {
                    h.ServerCertificate = Certs.Certificate;
                    h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                }));
            });
            var app = builder.Build();
            app.Use(async (ctx, next) =>
            {
                if (!Filter.IsAllowed(ctx.Connection.RemoteIpAddress))
                {
                    AppLog.Warn("Rejected connection from disallowed network", new { ip = ctx.Connection.RemoteIpAddress?.ToString() });
                    ctx.Response.StatusCode = 403;
                    return;
                }
                await next();
            });
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.Map("/ws", async ctx =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await RunSessionAsync(ws, ctx.Connection.RemoteIpAddress ?? IPAddress.None, ctx.RequestAborted);
            });
            await app.StartAsync();
            _app = app;
            ListeningPort = port;
            StartedUtc = DateTime.UtcNow;
            StartDiscovery();
            LastError = null;
            SetState(ServerState.Running);
            Audit.Write("system", "server.start", $"port {port}");
            AppLog.Info("Server started", new { port });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("Server failed to start", ex);
            Audit.Write("system", "server.start", ex.Message, false);
            try { if (_app != null) await _app.DisposeAsync(); } catch { }
            _app = null;
            SetState(ServerState.Faulted);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (State is ServerState.Stopped or ServerState.Stopping) return;
        SetState(ServerState.Stopping);
        try
        {
            StopDiscovery();
            List<ClientContext> list;
            lock (_lock) list = [.. _clients];
            foreach (var c in list) c.Close("server stopping");
            if (_app != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _app.StopAsync(cts.Token); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
            Audit.Write("system", "server.stop");
            AppLog.Info("Server stopped");
        }
        finally
        {
            StartedUtc = null;
            SetState(ServerState.Stopped);
        }
    }

    public async Task RestartAsync() { await StopAsync(); await StartAsync(); }

    private void SetState(ServerState s) { State = s; StateChanged?.Invoke(); }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    // ---------------------------------------------------------------- discovery

    private void StartDiscovery()
    {
        if (!Settings.DiscoveryEnabled) return;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Settings.DiscoveryPort)) { EnableBroadcast = true };
            _udpCts = new CancellationTokenSource();
            var udp = _udp; var ct = _udpCts.Token;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(ct);
                        if (!Filter.IsAllowed(r.RemoteEndPoint.Address)) continue;
                        var text = Encoding.UTF8.GetString(r.Buffer);
                        if (text != "PCRC_DISCOVER_V1") continue;
                        var reply = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            app = "pc-remote-center", version = Protocol.Version, name = Settings.ComputerName,
                            port = ListeningPort, fingerprint = Certs.Fingerprint,
                        }, Protocol.Json);
                        await udp.SendAsync(reply, r.RemoteEndPoint, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex) { AppLog.Warn("Discovery error: " + ex.Message); await Task.Delay(500, CancellationToken.None); }
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Discovery could not start: " + ex.Message);
            _udp = null;
        }
    }

    private void StopDiscovery()
    {
        try { _udpCts?.Cancel(); _udp?.Dispose(); } catch { }
        _udp = null; _udpCts = null;
    }

    // ---------------------------------------------------------------- sessions

    private void OnDevicesChanged()
    {
        List<ClientContext> list;
        lock (_lock) list = [.. _clients];
        foreach (var c in list)
        {
            if (string.IsNullOrEmpty(c.DeviceId)) continue;
            var d = Devices.Get(c.DeviceId);
            if (d == null || d.Revoked) { Audit.Write("system", "device.disconnected", $"{c.DeviceName}: revoked"); c.Close("revoked"); }
            else if (d.Permissions != c.Permissions)
            {
                c.Permissions = d.Permissions;
                _ = c.PushAsync("auth.permissions", new { permissions = d.Permissions.ToNames() });
            }
        }
        ClientsChanged?.Invoke();
    }

    public void Disconnect(string deviceId)
    {
        List<ClientContext> list;
        lock (_lock) list = _clients.Where(c => c.DeviceId == deviceId).ToList();
        foreach (var c in list) c.Close("disconnected by server");
    }

    private async Task<Envelope?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                if (r.MessageType != WebSocketMessageType.Text) throw new ProtocolException("malformed", "Only text frames are supported.");
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > Protocol.MaxMessageBytes) throw new ProtocolException("too_large", "Message exceeds size limit.");
                if (r.EndOfMessage) break;
            }
            Interlocked.Add(ref _bytesIn, ms.Length);
            return Protocol.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Runs one client connection from handshake to close. Public so tests can drive it.</summary>
    public async Task RunSessionAsync(WebSocket ws, IPAddress ip, CancellationToken aborted)
    {
        var ctx = new ClientContext(ws, ip, n => Interlocked.Add(ref _bytesOut, n), aborted);
        long historyId = 0;
        var ipStr = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        try
        {
            if (!await AuthenticateAsync(ws, ctx, ipStr)) return; // finally-block performs the graceful close
            historyId = ctx.HistoryId;
            using var link = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ctx.ExpiresUtc - DateTime.UtcNow, link.Token);
                    Audit.Write(ctx.DeviceName, "session.expired");
                    await ctx.PushAsync("session.expired", new { message = "Session expired. Please reconnect." });
                    ctx.Close("session expired");
                }
                catch (OperationCanceledException) { }
            });
            var limiter = new MessageRateLimiter(300);
            var seen = new Queue<string>();
            var seenSet = new HashSet<string>();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(link.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(60)); // heartbeat: client pings every ~10s
                    Envelope? env;
                    try { env = await ReceiveAsync(ws, idle.Token); }
                    catch (ProtocolException pe) { await ctx.SendAsync(Protocol.Error(null, pe.Code, pe.Message)); if (pe.Code == "too_large") break; continue; }
                    if (env == null) break;
                    if (!limiter.Allow()) { await ctx.SendAsync(Protocol.Error(env.RequestId, "rate_limited", "Too many requests.")); continue; }
                    await DispatchAsync(ctx, env, seen, seenSet);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally { link.Cancel(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("Session crashed", ex);
        }
        finally
        {
            ctx.RaiseClosed();
            bool wasListed;
            lock (_lock) wasListed = _clients.Remove(ctx);
            if (historyId != 0) History.End(historyId);
            if (wasListed) { Audit.Write(ctx.DeviceName, "device.disconnected", ipStr); ClientsChanged?.Invoke(); }
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private async Task<bool> AuthenticateAsync(WebSocket ws, ClientContext ctx, string ip)
    {
        if (AuthLimiter.IsLocked(ip, out var wait))
        {
            await ctx.SendAsync(Protocol.Error(null, "rate_limited", $"Too many failed attempts. Try again in {Math.Ceiling(wait.TotalSeconds)} s."));
            return false;
        }
        // NOTE: cancelling WebSocket.ReceiveAsync aborts the socket, so the timeout is raced instead of cancelled.
        var recv = ReceiveAsync(ws, ctx.Aborted);
        Envelope? first;
        try
        {
            var done = await Task.WhenAny(recv, Task.Delay(TimeSpan.FromSeconds(Math.Max(3, Settings.AuthTimeoutSeconds)), ctx.Aborted));
            if (done != recv)
            {
                _ = recv.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                await ctx.SendAsync(Protocol.Error(null, "auth_timeout", "Authentication timed out."));
                ctx.Close("auth timeout");
                return false;
            }
            first = await recv;
        }
        catch (OperationCanceledException) { return false; }
        catch (ProtocolException pe) { await ctx.SendAsync(Protocol.Error(null, pe.Code, pe.Message)); return false; }
        catch (WebSocketException) { return false; }
        if (first == null) return false;

        try
        {
            switch (first.Type)
            {
                case "auth.pair":
                {
                    var code = first.Payload.RequireString("code", 16);
                    // names appear in the UI, notifications and logs: strip control characters
                    static string Clean(string s) => new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
                    var name = Clean(first.Payload.OptString("deviceName", 64) ?? "Android device");
                    var model = Clean(first.Payload.OptString("deviceModel", 64) ?? "");
                    var offer = Pairing.Consume(code);
                    if (offer == null)
                    {
                        AuthLimiter.RecordFailure(ip);
                        Audit.Write("unknown", "auth.pair", $"invalid or expired code from {ip}", false);
                        await ctx.SendAsync(Protocol.Error(first.RequestId, "auth_failed", "Pairing code is invalid or expired."));
                        return false;
                    }
                    var token = TokenService.NewToken();
                    var dev = new DeviceRecord
                    {
                        Id = Guid.NewGuid().ToString("N"), Name = name.Length == 0 ? "Android device" : name, Model = model,
                        TokenHash = TokenService.Hash(token), Permissions = offer.Permissions, CreatedUtc = DateTime.UtcNow,
                    };
                    Devices.Add(dev);
                    Complete(ctx, dev, ip);
                    Audit.Write(dev.Name, "device.paired", $"{ip}; permissions {string.Join(',', dev.Permissions.ToNames())}");
                    await ctx.SendAsync(Protocol.Response(first.RequestId, AuthOkPayload(ctx, token)));
                    DeviceConnected?.Invoke(dev.Name, ip, true);
                    return true;
                }
                case "auth.login":
                {
                    var id = first.Payload.RequireString("deviceId", 64);
                    var token = first.Payload.RequireString("token", 128);
                    var dev = Devices.Get(id);
                    if (dev == null || dev.Revoked || !TokenService.Verify(token, dev.TokenHash))
                    {
                        AuthLimiter.RecordFailure(ip);
                        Audit.Write(dev?.Name ?? "unknown", "auth.login", dev is { Revoked: true } ? "revoked device" : $"bad credentials from {ip}", false);
                        await ctx.SendAsync(Protocol.Error(first.RequestId, "auth_failed", dev is { Revoked: true } ? "This device has been revoked." : "Authentication failed."));
                        return false;
                    }
                    AuthLimiter.Reset(ip);
                    Complete(ctx, dev, ip);
                    Devices.Touch(dev.Id, ip);
                    Audit.Write(dev.Name, "device.connected", ip);
                    await ctx.SendAsync(Protocol.Response(first.RequestId, AuthOkPayload(ctx, null)));
                    DeviceConnected?.Invoke(dev.Name, ip, false);
                    return true;
                }
                default:
                    AuthLimiter.RecordFailure(ip);
                    await ctx.SendAsync(Protocol.Error(first.RequestId, "unauthenticated", "Authenticate first (auth.pair or auth.login)."));
                    return false;
            }
        }
        catch (ProtocolException pe)
        {
            AuthLimiter.RecordFailure(ip);
            await ctx.SendAsync(Protocol.Error(first.RequestId, pe.Code, pe.Message));
            return false;
        }
    }

    private void Complete(ClientContext ctx, DeviceRecord dev, string ip)
    {
        ctx.DeviceId = dev.Id; ctx.DeviceName = dev.Name; ctx.Permissions = dev.Permissions;
        ctx.ExpiresUtc = DateTime.UtcNow.AddHours(Math.Max(1, Settings.SessionMaxHours));
        // registered before the auth response is sent so a revoke right after login can never miss this session
        ctx.HistoryId = History.Begin(ctx.DeviceId, ctx.DeviceName, ip, "ok");
        lock (_lock) _clients.Add(ctx);
        ClientsChanged?.Invoke();
    }

    private object AuthOkPayload(ClientContext ctx, string? token) => new
    {
        deviceId = ctx.DeviceId, token, permissions = ctx.Permissions.ToNames(),
        computerName = Settings.ComputerName, serverVersion = typeof(ServerCore).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        sessionExpiresUtc = ctx.ExpiresUtc.ToString("o"), protocol = Protocol.Version,
    };

    private async Task DispatchAsync(ClientContext ctx, Envelope env, Queue<string> seen, HashSet<string> seenSet)
    {
        if (env.RequestId != null)
        {
            if (!seenSet.Add(env.RequestId))
            {
                await ctx.SendAsync(Protocol.Error(env.RequestId, "duplicate_request", "requestId was already processed."));
                return;
            }
            seen.Enqueue(env.RequestId);
            if (seen.Count > 512) seenSet.Remove(seen.Dequeue());
        }
        if (!Handlers.TryGet(env.Type, out var h))
        {
            await ctx.SendAsync(Protocol.Error(env.RequestId, "unknown_type", $"Unknown message type '{env.Type}'."));
            return;
        }
        if (h.Required != Permission.None && !ctx.Has(h.Required))
        {
            Audit.Write(ctx.DeviceName, "denied", $"{env.Type} requires {h.Required}", false);
            await ctx.SendAsync(Protocol.Error(env.RequestId, "forbidden", $"This device does not have the '{h.Required.ToString().ToLowerInvariant()}' permission."));
            return;
        }
        try
        {
            var result = await h.Fn(ctx, env);
            if (env.RequestId != null) await ctx.SendAsync(Protocol.Response(env.RequestId, result));
        }
        catch (ProtocolException pe) { await ctx.SendAsync(Protocol.Error(env.RequestId, pe.Code, pe.Message)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error($"Handler {env.Type} failed", ex);
            await ctx.SendAsync(Protocol.Error(env.RequestId, "internal_error", "The server could not complete the request."));
        }
    }

    // ---------------------------------------------------------------- core handlers

    private void RegisterCoreHandlers()
    {
        Handlers.Register("ping", Permission.None, (c, e) => Task.FromResult<object?>(new { serverTimeUtc = DateTime.UtcNow.ToString("o") }));
        Handlers.Register("server.info", Permission.None, (c, e) => Task.FromResult<object?>(new
        {
            computerName = Settings.ComputerName, permissions = c.Permissions.ToNames(), protocol = Protocol.Version,
            uptimeSeconds = StartedUtc == null ? 0 : (int)(DateTime.UtcNow - StartedUtc.Value).TotalSeconds,
            macAddresses = Services.NetworkAdapters.MacAddresses(),
        }));
    }
}
