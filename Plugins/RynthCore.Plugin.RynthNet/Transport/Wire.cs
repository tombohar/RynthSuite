// ============================================================================
//  RynthNet - Transport/Wire.cs
//  The wire format: a frame is a 4-byte little-endian length followed by that
//  many bytes of UTF-8 JSON (one object). Written with Utf8JsonWriter and read
//  with JsonDocument: no reflection, safe under NativeAOT and in a collectible
//  plugin context.
//
//  Every object has "t" (its type). hello also carries "proto" (the protocol
//  version); a peer with a different major version is refused. Unknown types
//  and unknown fields are ignored, so fields can be added without a bump.
//
//    hello  {t,proto,node,pid,app,char,world,acct,login,tags[]}   first frame, both ways
//    ident  {t,char,world,acct,login,tags[]}                       identity changed
//    status {t,s:{...}}                                            vitals/position/bot, ~1 Hz
//    cmd    {t,id,to:{k,v},text,delay}                             run a chat command
//    ack    {t,id,ok,why}                                          answer to cmd
//    data   {t,ch,msg,to:{k,v}}                                    channel message (plugins, Lua)
//    ping   {t,ts} / pong {t,ts}                                   liveness + round trip
//    bye    {t,why}                                                leaving on purpose
// ============================================================================

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;

namespace RynthCore.Plugin.RynthNet.Transport;

public static class Wire
{
    public const int Proto = 1;
    public const int MaxFrame = 64 * 1024;
    public const int MaxText = 1000;
    public const int MaxMessage = 16 * 1024;
    public const int MaxName = 64;
    public const int MaxTags = 32;
    public const int MaxTagLength = 32;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };
    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    // ── Building frames ─────────────────────────────────────────────────────

    private static byte[] Frame(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.GetSpan(4);
        buffer.Advance(4);   // room for the length prefix
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        byte[] frame = buffer.WrittenSpan.ToArray();
        int len = frame.Length - 4;
        if (len > MaxFrame) throw new InvalidOperationException($"frame too large ({len} bytes)");
        BinaryPrimitives.WriteInt32LittleEndian(frame, len);
        return frame;
    }

    private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    private static void WriteIdentity(Utf8JsonWriter w, NodeIdentity id)
    {
        w.WriteString("char", Clip(id.Character, MaxName));
        w.WriteString("world", Clip(id.World, MaxName));
        w.WriteString("acct", Clip(id.Account, MaxName));
        w.WriteBoolean("login", id.LoggedIn);
        w.WriteStartArray("tags");
        int n = 0;
        foreach (string t in id.Tags)
        {
            if (n++ >= MaxTags) break;
            w.WriteStringValue(Clip(t, MaxTagLength));
        }
        w.WriteEndArray();
    }

    private static void WriteTarget(Utf8JsonWriter w, NetTarget to)
    {
        w.WriteStartObject("to");
        w.WriteString("k", to.KindName);
        w.WriteString("v", Clip(to.Value, 512));
        w.WriteEndObject();
    }

    public static byte[] Hello(string nodeId, int pid, NodeIdentity id) => Frame(w =>
    {
        w.WriteString("t", "hello");
        w.WriteNumber("proto", Proto);
        w.WriteString("node", nodeId);
        w.WriteNumber("pid", pid);
        w.WriteString("app", Clip(id.App, MaxName));
        WriteIdentity(w, id);
    });

    public static byte[] Ident(NodeIdentity id) => Frame(w =>
    {
        w.WriteString("t", "ident");
        WriteIdentity(w, id);
    });

    /// <summary><paramref name="statusObjectJson"/> must be one JSON object (it is validated).</summary>
    public static byte[] Status(string statusObjectJson) => Frame(w =>
    {
        w.WriteString("t", "status");
        w.WritePropertyName("s");
        w.WriteRawValue(string.IsNullOrEmpty(statusObjectJson) ? "{}" : statusObjectJson);
    });

    public static byte[] Command(string id, NetTarget to, string text, int delayMs) => Frame(w =>
    {
        w.WriteString("t", "cmd");
        w.WriteString("id", id);
        WriteTarget(w, to);
        w.WriteString("text", Clip(text, MaxText));
        w.WriteNumber("delay", Math.Clamp(delayMs, 0, 600_000));
    });

    public static byte[] Ack(string id, bool ok, string why) => Frame(w =>
    {
        w.WriteString("t", "ack");
        w.WriteString("id", id);
        w.WriteBoolean("ok", ok);
        w.WriteString("why", Clip(why, 200));
    });

    public static byte[] Data(string channel, string message, NetTarget to) => Frame(w =>
    {
        w.WriteString("t", "data");
        w.WriteString("ch", Clip(channel, MaxName));
        w.WriteString("msg", Clip(message, MaxMessage));
        WriteTarget(w, to);
    });

    public static byte[] Ping(long ts) => Frame(w => { w.WriteString("t", "ping"); w.WriteNumber("ts", ts); });
    public static byte[] Pong(long ts) => Frame(w => { w.WriteString("t", "pong"); w.WriteNumber("ts", ts); });
    public static byte[] Bye(string why) => Frame(w => { w.WriteString("t", "bye"); w.WriteString("why", Clip(why, 200)); });

    // ── Reading frames ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses one frame's payload. Returns null for anything malformed (the caller drops
    /// the connection); unknown types come back with only Type set (the caller ignores them).
    /// </summary>
    public static WireMessage? Parse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload, ReaderOptions);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string type = Str(root, "t");
            if (type.Length == 0) return null;
            var m = new WireMessage { Type = type };
            switch (type)
            {
                case "hello":
                    m.Proto = Int(root, "proto");
                    m.NodeId = Clip(Str(root, "node"), 80);
                    m.Pid = Int(root, "pid");
                    m.Identity = ReadIdentity(root, Clip(Str(root, "app"), MaxName));
                    break;
                case "ident":
                    m.Identity = ReadIdentity(root, "");
                    break;
                case "status":
                    if (root.TryGetProperty("s", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
                    {
                        string raw = s.GetRawText();
                        m.StatusJson = raw.Length <= MaxFrame ? raw : "";
                    }
                    break;
                case "cmd":
                    m.Id = Clip(Str(root, "id"), 80);
                    m.Target = ReadTarget(root);
                    m.Text = Clip(Str(root, "text"), MaxText);
                    m.DelayMs = Math.Clamp(Int(root, "delay"), 0, 600_000);
                    break;
                case "ack":
                    m.Id = Clip(Str(root, "id"), 80);
                    m.Ok = root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
                    m.Text = Clip(Str(root, "why"), 200);
                    break;
                case "data":
                    m.Channel = Clip(Str(root, "ch"), MaxName);
                    m.Text = Clip(Str(root, "msg"), MaxMessage);
                    m.Target = ReadTarget(root);
                    break;
                case "ping":
                case "pong":
                    m.Ts = Long(root, "ts");
                    break;
                case "bye":
                    m.Text = Clip(Str(root, "why"), 200);
                    break;
            }
            return m;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NodeIdentity ReadIdentity(JsonElement root, string app)
    {
        var tags = new List<string>();
        if (root.TryGetProperty("tags", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement t in arr.EnumerateArray())
            {
                if (tags.Count >= MaxTags) break;
                if (t.ValueKind != JsonValueKind.String) continue;
                string tag = Clip(t.GetString(), MaxTagLength).Trim();
                if (tag.Length > 0) tags.Add(tag);
            }
        }
        return new NodeIdentity
        {
            Character = Clip(Str(root, "char"), MaxName),
            World = Clip(Str(root, "world"), MaxName),
            Account = Clip(Str(root, "acct"), MaxName),
            LoggedIn = root.TryGetProperty("login", out JsonElement l) && l.ValueKind == JsonValueKind.True,
            Tags = tags.ToArray(),
            App = app,
        };
    }

    private static NetTarget ReadTarget(JsonElement root)
    {
        if (!root.TryGetProperty("to", out JsonElement to) || to.ValueKind != JsonValueKind.Object) return NetTarget.All;
        return NetTarget.Parse(Str(to, "k"), Clip(Str(to, "v"), 512));
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long i) ? i : 0;
}

/// <summary>One parsed frame. Only the fields of its <see cref="Type"/> are set.</summary>
public sealed class WireMessage
{
    public string Type = "";
    public int Proto;
    public string NodeId = "";
    public int Pid;
    public NodeIdentity? Identity;
    public string StatusJson = "";
    public string Id = "";
    public NetTarget Target = NetTarget.All;
    public string Text = "";
    public int DelayMs;
    public bool Ok;
    public string Channel = "";
    public long Ts;
}
