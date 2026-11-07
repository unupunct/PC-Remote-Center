using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemoteControlServer.Models;

[Flags]
public enum Permission
{
    None = 0,
    Monitoring = 1,
    Terminal = 2,
    Launch = 4,
    Power = 8,
    Admin = 16,
}

public static class PermissionExtensions
{
    public static string[] ToNames(this Permission p) =>
        Enum.GetValues<Permission>().Where(x => x != Permission.None && p.HasFlag(x)).Select(x => x.ToString().ToLowerInvariant()).ToArray();

    public static Permission FromNames(IEnumerable<string> names)
    {
        var r = Permission.None;
        foreach (var n in names)
            if (Enum.TryParse<Permission>(n, true, out var v) && v != Permission.None) r |= v;
        return r;
    }
}

/// <summary>Versioned wire envelope. See SharedProtocol/protocol-spec.md.</summary>
public sealed class Envelope
{
    [JsonPropertyName("version")] public int Version { get; set; } = Protocol.Version;
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("payload")] public JsonObject? Payload { get; set; }
}

public sealed class ProtocolException : Exception
{
    public string Code { get; }
    public ProtocolException(string code, string message) : base(message) => Code = code;
}

public static class Protocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 256 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static Envelope Parse(ReadOnlySpan<byte> utf8)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(utf8, Json); }
        catch (JsonException) { throw new ProtocolException("malformed", "Message is not valid JSON."); }
        if (env == null) throw new ProtocolException("malformed", "Empty message.");
        if (env.Version != Version) throw new ProtocolException("unsupported_version", $"Protocol version {env.Version} is not supported (server speaks {Version}).");
        if (string.IsNullOrWhiteSpace(env.Type) || env.Type.Length > 64) throw new ProtocolException("malformed", "Missing or invalid 'type'.");
        if (env.RequestId is { Length: > 64 }) throw new ProtocolException("malformed", "requestId too long.");
        return env;
    }

    public static byte[] Serialize(Envelope e) => JsonSerializer.SerializeToUtf8Bytes(e, Json);

    public static JsonObject ToPayload(object? o) =>
        o == null ? new JsonObject() : (JsonSerializer.SerializeToNode(o, Json) as JsonObject ?? new JsonObject());

    public static Envelope Response(string? requestId, object? payload) =>
        new() { Type = "response", RequestId = requestId, Payload = ToPayload(payload) };

    public static Envelope Error(string? requestId, string code, string message) =>
        new() { Type = "error", RequestId = requestId, Payload = ToPayload(new { code, message }) };

    public static Envelope Push(string type, object? payload) => new() { Type = type, Payload = ToPayload(payload) };
}

public static class PayloadExt
{
    public static string RequireString(this JsonObject? p, string name, int maxLen = 4096)
    {
        var v = p?[name]?.GetValue<string>();
        if (v == null) throw new ProtocolException("invalid_payload", $"'{name}' is required.");
        if (v.Length > maxLen) throw new ProtocolException("invalid_payload", $"'{name}' is too long.");
        return v;
    }

    public static string? OptString(this JsonObject? p, string name, int maxLen = 4096)
    {
        JsonNode? n = p?[name];
        if (n == null) return null;
        string? v;
        try { v = n.GetValue<string>(); } catch { throw new ProtocolException("invalid_payload", $"'{name}' must be a string."); }
        if (v != null && v.Length > maxLen) throw new ProtocolException("invalid_payload", $"'{name}' is too long.");
        return v;
    }

    public static int OptInt(this JsonObject? p, string name, int def)
    {
        var n = p?[name];
        if (n == null) return def;
        try { return n.GetValue<int>(); } catch { throw new ProtocolException("invalid_payload", $"'{name}' must be an integer."); }
    }

    public static bool OptBool(this JsonObject? p, string name, bool def)
    {
        var n = p?[name];
        if (n == null) return def;
        try { return n.GetValue<bool>(); } catch { throw new ProtocolException("invalid_payload", $"'{name}' must be a boolean."); }
    }
}
