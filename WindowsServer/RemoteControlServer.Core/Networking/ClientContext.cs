using System.Net;
using System.Net.WebSockets;
using RemoteControlServer.Models;
using RemoteControlServer.Services;

namespace RemoteControlServer.Networking;

/// <summary>An authenticated client connection, passed to request handlers.</summary>
public sealed class ClientContext
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Action<long> _countOut;

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public IPAddress RemoteIp { get; }
    public Permission Permissions { get; set; }
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    internal long HistoryId { get; set; }
    public CancellationToken Aborted { get; }
    public Dictionary<string, object> Items { get; } = new();
    public event Action? Closed;
    internal void RaiseClosed() { try { Closed?.Invoke(); } catch (Exception ex) { AppLog.Error("Closed handler failed", ex); } }

    internal ClientContext(WebSocket ws, IPAddress ip, Action<long> countOut, CancellationToken aborted)
    {
        _ws = ws; RemoteIp = ip; _countOut = countOut; Aborted = aborted;
    }

    public bool Has(Permission p) => Permissions.HasFlag(p);

    public async Task SendAsync(Envelope env)
    {
        var bytes = Protocol.Serialize(env);
        await _sendLock.WaitAsync(Aborted).ConfigureAwait(false);
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Aborted);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            _countOut(bytes.Length);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            try { _ws.Abort(); } catch { }
        }
        finally { _sendLock.Release(); }
    }

    public Task PushAsync(string type, object? payload) => SendAsync(Protocol.Push(type, payload));

    public void Close(string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
                // give the peer time to complete the close handshake, then force-drop
                await Task.Delay(3000);
            }
            catch (Exception ex) { AppLog.Warn("Close failed: " + ex.Message); }
            finally { try { _ws.Abort(); } catch { } }
        });
    }
}

public delegate Task<object?> RequestFunc(ClientContext ctx, Envelope request);

/// <summary>Maps message types to handlers together with the permission each one requires.</summary>
public sealed class HandlerRegistry
{
    private readonly Dictionary<string, (Permission Required, RequestFunc Fn)> _map = new(StringComparer.Ordinal);

    public void Register(string type, Permission required, RequestFunc fn) => _map[type] = (required, fn);
    public bool TryGet(string type, out (Permission Required, RequestFunc Fn) h) => _map.TryGetValue(type, out h);
    public IEnumerable<string> Types => _map.Keys;
}
