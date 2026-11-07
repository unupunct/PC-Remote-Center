using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;

namespace RemoteControlServer.Services;

/// <summary>Executes the actual OS operations. Replaceable in tests so no test ever shuts the machine down.</summary>
public interface ISystemActions
{
    /// <summary>Runs shutdown.exe with structured arguments; returns exit code.</summary>
    int RunShutdown(IReadOnlyList<string> args);
    bool Lock();
    bool Sleep();
}

public sealed class WindowsSystemActions : ISystemActions
{
    public int RunShutdown(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("shutdown.exe could not be started");
        p.WaitForExit(10000);
        return p.HasExited ? p.ExitCode : -1;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public bool Lock() => LockWorkStation();

    public bool Sleep()
    {
        // SetSuspendState blocks until resume; run it off-thread so the reply can be sent first
        Task.Run(() => SetSuspendState(false, false, false));
        return true;
    }
}

/// <summary>
/// restart / shutdown / sleep need a two-step flow enforced by the server: <c>power.request</c> returns a short-lived
/// single-use confirmation token bound to the device and action, <c>power.confirm</c> executes it.
/// </summary>
public sealed class PowerService
{
    private sealed record Pending(string DeviceId, string Action, int Delay, DateTime Expires);

    private readonly ServerCore _core;
    private readonly ISystemActions _sys;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(30);
    public DateTime? ScheduledAtUtc { get; private set; }
    public string? ScheduledAction { get; private set; }

    public PowerService(ServerCore core, ISystemActions? sys = null)
    {
        _core = core; _sys = sys ?? new WindowsSystemActions();
        const Permission P = Permission.Power;

        core.Handlers.Register("power.request", P, (c, e) =>
        {
            var action = e.Payload.RequireString("action", 16).ToLowerInvariant();
            if (action is not ("restart" or "shutdown" or "sleep")) throw new ProtocolException("invalid_payload", "action must be restart, shutdown or sleep.");
            int delay = action == "sleep" ? 0 : e.Payload.OptInt("delaySeconds", 30);
            if (action != "sleep" && (delay < 5 || delay > 600)) throw new ProtocolException("invalid_payload", "delaySeconds must be between 5 and 600.");
            Prune();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _pending[token] = new Pending(c.DeviceId, action, delay, DateTime.UtcNow + ConfirmWindow);
            _core.Audit.Write(c.DeviceName, "power.request", action);
            return Task.FromResult<object?>(new { confirmationToken = token, action, delaySeconds = delay, expiresInSeconds = (int)ConfirmWindow.TotalSeconds });
        });

        core.Handlers.Register("power.confirm", P, (c, e) =>
        {
            var token = e.Payload.RequireString("token", 64);
            Prune();
            if (!_pending.TryRemove(token, out var p) || p.DeviceId != c.DeviceId || p.Expires < DateTime.UtcNow)
                throw new ProtocolException("confirmation_invalid", "The confirmation expired or is invalid. Request the action again.");
            return Task.FromResult<object?>(Execute(c, p));
        });

        core.Handlers.Register("power.cancel", P, (c, e) =>
        {
            int code = _sys.RunShutdown(["/a"]);
            bool cancelled = code == 0;
            if (cancelled) { ScheduledAtUtc = null; ScheduledAction = null; }
            _core.Audit.Write(c.DeviceName, "power.cancel", cancelled ? "cancelled" : $"nothing to cancel (exit {code})", true);
            return Task.FromResult<object?>(new
            {
                cancelled,
                message = cancelled ? "The pending restart/shutdown was cancelled." : "No restart or shutdown is pending (or Windows no longer allows cancelling it).",
            });
        });

        core.Handlers.Register("power.lock", P, (c, e) =>
        {
            if (!_sys.Lock()) throw new ProtocolException("action_failed", "Windows refused to lock the workstation: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            _core.Audit.Write(c.DeviceName, "power.lock");
            return Task.FromResult<object?>(new { locked = true });
        });

        core.Handlers.Register("power.status", Permission.Monitoring, (c, e) =>
            Task.FromResult<object?>(new { scheduled = ScheduledAtUtc != null && ScheduledAtUtc > DateTime.UtcNow, action = ScheduledAction, atUtc = ScheduledAtUtc?.ToString("o") }));
    }

    private object Execute(ClientContext c, Pending p)
    {
        switch (p.Action)
        {
            case "restart":
            case "shutdown":
            {
                var comment = $"PC Remote Center: {p.Action} requested by {Sanitize(c.DeviceName)}";
                int code = _sys.RunShutdown([p.Action == "restart" ? "/r" : "/s", "/t", p.Delay.ToString(), "/c", comment]);
                if (code != 0)
                {
                    _core.Audit.Write(c.DeviceName, "power." + p.Action, $"shutdown.exe exit {code}", false);
                    throw new ProtocolException("action_failed", code == 1190
                        ? "A shutdown is already scheduled. Cancel it first."
                        : $"Windows could not schedule the {p.Action} (shutdown.exe exit code {code}).");
                }
                ScheduledAtUtc = DateTime.UtcNow.AddSeconds(p.Delay); ScheduledAction = p.Action;
                _core.Audit.Write(c.DeviceName, "power." + p.Action, $"in {p.Delay}s");
                return new { scheduled = true, action = p.Action, delaySeconds = p.Delay, atUtc = ScheduledAtUtc.Value.ToString("o") };
            }
            case "sleep":
                _core.Audit.Write(c.DeviceName, "power.sleep");
                if (!_sys.Sleep()) throw new ProtocolException("action_failed", "Windows refused to enter sleep.");
                return new { sleeping = true };
            default: throw new ProtocolException("invalid_payload", "Unknown action.");
        }
    }

    private static string Sanitize(string s) => new string(s.Where(ch => !char.IsControl(ch) && ch != '"').Take(40).ToArray());

    private void Prune()
    {
        foreach (var kv in _pending.Where(k => k.Value.Expires < DateTime.UtcNow).ToList()) _pending.TryRemove(kv.Key, out _);
    }
}
