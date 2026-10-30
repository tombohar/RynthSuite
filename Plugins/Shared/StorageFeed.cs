// ============================================================================
//  RynthSuite - Plugins/Shared/StorageFeed.cs
//  Storage the client is never shown, handed to RynthInventory by another plugin: a server plugin
//  that can read a server-side store (RynthAelrynth reads Aelrynth's account vault with /mule-data)
//  sends RynthInventory the whole record, and RynthInventory files it with the character's storage
//  (searched, shown with icons and merged across characters like a container that was opened).
//  RynthInventory knows nothing about the server: the sender decides what the store is and when to
//  read it; without the sender (its server plugin not installed) nothing is asked and nothing is sent.
//
//  Transport: the engine's plugin-command broker, SendPluginCommand("RynthInventory", "storage",
//  json) -> RynthInventory's RynthPluginApplyRemoteCommand. The strings travel as ANSI, so the JSON
//  is plain ASCII (anything else \u-escaped). Payload (version 1):
//    { "v": 1,
//      "id": 4278190080,          // a stand-in container id, top byte 0xFF (never a real object's);
//                                 // the same id replaces the earlier copy (per character file)
//      "name": "Aelrynth vault", "label": "Account vault",
//      "items": [ { "o": id, "w": wcid, "n": "name", "i": icon, "u": underlay, "v": overlay,
//                   "s": stack, "m": max stack, "y": item type }, ... ] }
//  Compiled into RynthInventory (receiver) and the senders (linked source), so the format can't drift.
//  Pure: RynthInventory.Tests and RynthServerPack.Tests cover it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.Shared;

/// <summary>One stored item, as a sender read it (the vault's "o", "w", "n", ... fields).</summary>
internal sealed record StorageFeedItem(uint Id, uint Wcid, string Name, uint Icon, uint Underlay, uint Overlay,
                                       int Stack, int MaxStack, uint ItemType);

/// <summary>A whole store: its stand-in id, its name and label, and everything in it.</summary>
internal sealed record StorageFeedRecord(uint Id, string Name, string Label, IReadOnlyList<StorageFeedItem> Items);

internal static class StorageFeed
{
    /// <summary>The receiver, and the plugin command it answers.</summary>
    public const string TargetPlugin = "RynthInventory";
    public const string Action = "storage";
    public const int Version = 1;
    public const int MaxItems = 5000;
    public const int MaxJsonLength = 4 * 1024 * 1024;
    public const int MaxNameLength = 64;

    /// <summary>A stand-in container id: top byte 0xFF, where no object the client has seen lives.</summary>
    public static bool IsStandInId(uint id) => id >> 24 == 0xFF;

    /// <summary>The payload for <paramref name="record"/> (plain ASCII).</summary>
    public static string ToJson(StorageFeedRecord record)
    {
        var sb = new StringBuilder(64 + record.Items.Count * 120);
        sb.Append("{\"v\":").Append(Version).Append(",\"id\":").Append(record.Id.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"name\":"); Str(sb, record.Name);
        sb.Append(",\"label\":"); Str(sb, record.Label);
        sb.Append(",\"items\":[");
        for (int i = 0; i < record.Items.Count; i++)
        {
            StorageFeedItem it = record.Items[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"o\":").Append(it.Id.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"w\":").Append(it.Wcid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"n\":"); Str(sb, it.Name);
            sb.Append(",\"i\":").Append(it.Icon.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"u\":").Append(it.Underlay.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"v\":").Append(it.Overlay.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"s\":").Append(it.Stack.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"m\":").Append(it.MaxStack.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"y\":").Append(it.ItemType.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// The record in a payload, or null (with the reason in <paramref name="error"/>) for anything
    /// unusable: not JSON, a newer version, an id that isn't a stand-in, no name, too many items.
    /// Never throws.
    /// </summary>
    public static StorageFeedRecord? Parse(string? json, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) { error = "empty"; return null; }
        if (json.Length > MaxJsonLength) { error = "too large"; return null; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return null; }
            int v = Int(root, "v", 0);
            if (v < 1 || v > Version) { error = "version " + v; return null; }
            uint id = UInt(root, "id");
            if (!IsStandInId(id)) { error = $"id 0x{id:X8} is not a stand-in (top byte 0xFF)"; return null; }
            string name = Cap(Str(root, "name"));
            if (name.Length == 0) { error = "no name"; return null; }
            string label = Cap(Str(root, "label"));
            var items = new List<StorageFeedItem>();
            if (root.TryGetProperty("items", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (items.Count >= MaxItems) { error = $"more than {MaxItems} items"; return null; }
                    items.Add(new StorageFeedItem(UInt(e, "o"), UInt(e, "w"), Str(e, "n"), UInt(e, "i"), UInt(e, "u"), UInt(e, "v"),
                                                  Math.Max(1, Int(e, "s", 1)), Int(e, "m", 0), UInt(e, "y")));
                }
            }
            return new StorageFeedRecord(id, name, label.Length > 0 ? label : name, items);
        }
        catch (JsonException ex)
        {
            error = "bad JSON: " + ex.Message;
            return null;
        }
    }

    private static string Cap(string s)
    {
        s = s.Trim();
        return s.Length <= MaxNameLength ? s : s[..MaxNameLength];
    }

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static uint UInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint u) ? u : 0u;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    /// <summary>A JSON string literal in plain ASCII (control and non-ASCII characters \u-escaped).</summary>
    private static void Str(StringBuilder sb, string? s)
    {
        sb.Append('"');
        foreach (char c in s ?? string.Empty)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
