// ============================================================================
//  RynthSuite - Plugins/Shared/EngineServerState.cs
//  RynthCore's answer to "which server is this?" (plugin API v86,
//  GetServerStateJson), parsed: every server the engine knows (built-in ILT and
//  Aelrynth, plus each loaded server plugin's declaration), whether it is
//  active, this character's override, and which plugin owns it (the one to ask
//  for its settings through "RynthServer.Profile"; RynthILT answers it for "ilt").
//  The same verdict the Mini Remote, the menus and the Diagnostics server
//  picker use, so every plugin follows one choice, overrides included.
//
//  Pure (BCL only): the plugins' test tools compile it. ServerStateClient.cs
//  polls the engine and the owner plugin.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RynthCore.Plugin.Shared;

/// <summary>One server in the engine's list.</summary>
internal sealed record EngineServer(string Id, string Name, bool Active, bool Detected, string Force, bool BuiltIn,
                                    string Plugin, string Reason);

/// <summary>The engine's server list at one moment (GetServerStateJson, schema 1).</summary>
internal sealed class EngineServerState
{
    public int Revision { get; private init; }
    /// <summary>The connect host the client was launched with ("" when not known).</summary>
    public string Host { get; private init; } = string.Empty;
    /// <summary>The world name the server announced at login ("" before, or without the hook).</summary>
    public string World { get; private init; } = string.Empty;
    /// <summary>"auto", a server id (picked on the Diagnostics page or "/rc server pick"), or "mixed".</summary>
    public string Pick { get; private init; } = "auto";
    /// <summary>The active server's id, "" when none is (a retail-like world).</summary>
    public string Active { get; private init; } = string.Empty;
    public string ActiveName { get; private init; } = string.Empty;
    /// <summary>The plugin that owns the active server ("" when none is loaded).</summary>
    public string ActivePlugin { get; private init; } = string.Empty;
    public IReadOnlyList<EngineServer> Servers { get; private init; } = Array.Empty<EngineServer>();

    /// <summary>True when a server is active.</summary>
    public bool HasActive => Active.Length > 0;

    /// <summary>True when the active server was chosen by hand (a pick), not detected.</summary>
    public bool Picked => !Pick.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server with <paramref name="id"/>, or null when the engine doesn't list it.</summary>
    public EngineServer? Find(string id)
    {
        foreach (EngineServer s in Servers)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>True when <paramref name="id"/> is the active server.</summary>
    public bool IsActive(string id) => Active.Equals(id, StringComparison.OrdinalIgnoreCase) && Active.Length > 0;

    /// <summary>
    /// Parses GetServerStateJson output. Null for empty or unusable text, or another schema's.
    /// Missing fields read as empty; nothing throws.
    /// </summary>
    public static EngineServerState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Int(root, "schema") != 1) return null;

            var servers = new List<EngineServer>();
            if (root.TryGetProperty("servers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string id = Str(e, "id");
                    if (id.Length == 0) continue;
                    servers.Add(new EngineServer(id, Str(e, "name"), Bool(e, "active"), Bool(e, "detected"),
                        Str(e, "force"), Bool(e, "builtIn"), Str(e, "plugin"), Str(e, "reason")));
                }
            }

            return new EngineServerState
            {
                Revision = Int(root, "revision"),
                Host = Str(root, "host"),
                World = Str(root, "world"),
                Pick = Str(root, "pick") is { Length: > 0 } pick ? pick : "auto",
                Active = Str(root, "active"),
                ActiveName = Str(root, "activeName"),
                ActivePlugin = Str(root, "activePlugin"),
                Servers = servers,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;

    internal static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    internal static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}

/// <summary>
/// The headline facts of a server owner's profile JSON ("RynthServer.Profile" from the plugin that
/// owns the server), read leniently: a field the owner doesn't send stays "".
/// </summary>
internal sealed record ServerProfileView(string Id, string Name, string Plugin, string Ruleset, string Emulator,
                                         string Botting, string BottingNote, string AccountLimit)
{
    /// <summary>Null for empty or unusable text.</summary>
    public static ServerProfileView? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new ServerProfileView(
                EngineServerState.Str(root, "id"), EngineServerState.Str(root, "name"), EngineServerState.Str(root, "plugin"),
                EngineServerState.Str(root, "ruleset"), EngineServerState.Str(root, "emulator"),
                EngineServerState.Str(root, "botting"), EngineServerState.Str(root, "bottingNote"),
                EngineServerState.Str(root, "accountLimit"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A live number from the profile's "values" (a server plugin's ServerPackPlugin.SetProfileValue,
    /// e.g. "voidDotDurationScale"), or <paramref name="fallback"/> when the profile, the value or a
    /// finite number isn't there (an older plugin, another server, a server without the rule).
    /// </summary>
    public static double Value(string? json, string key, double fallback)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(key)) return fallback;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Object
                || !values.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetDouble(out double d) || !double.IsFinite(d))
                return fallback;
            return d;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>One plain-ASCII line for a status report ("PvE, ACE; botting forbidden - ...").</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Ruleset.Length > 0 || Emulator.Length > 0)
            parts.Add(Ruleset.Length > 0 && Emulator.Length > 0 ? Ruleset + ", " + Emulator : Ruleset + Emulator);
        if (Botting.Length > 0) parts.Add("botting " + Botting + (BottingNote.Length > 0 ? " - " + BottingNote : string.Empty));
        if (AccountLimit.Length > 0) parts.Add("accounts: " + AccountLimit);
        return parts.Count == 0 ? "no details" : string.Join("; ", parts);
    }
}

/// <summary>
/// One salvage from a server plugin's profile "salvage" list (ServerPackInfo.Salvage): a tinkering
/// recipe the server adds, for the Mini Remote's Crafting tab. The fields after
/// <paramref name="NameMatch"/> are optional in the JSON; absent, a row behaves as before them.
/// </summary>
/// <param name="AppliedInt">"applied": an int property of the item that reads 2 once the row is on it (0 = none).</param>
/// <param name="Prepared">"prepared": the bag must first be prepared on the chosen tier's Ammunition.</param>
/// <param name="Order">"order": apply order (lower first); 0 = the kind-based default.</param>
/// <param name="AfterCap">"afterCap": applied last, only once the item's tink slots are full.</param>
/// <param name="StackGroup">"stackGroup": rows in one group share <paramref name="StackCap"/> per item ("" = none).</param>
/// <param name="StackCap">"stackCap": the group's combined cap per item.</param>
/// <summary>
/// Which jewelry is a trinket on the active server (the profile's "trinkets": name words and words
/// that rule a name out again). <see cref="None"/> (no server, or one without trinkets): nothing is.
/// </summary>
internal sealed record ServerTrinketRule(IReadOnlyList<string> Names, IReadOnlyList<string> Except)
{
    public static readonly ServerTrinketRule None = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>True when <paramref name="name"/> holds one of the words and none of the exceptions.</summary>
    public bool IsTrinket(string? name)
    {
        string n = name ?? string.Empty;
        if (n.Length == 0 || Names.Count == 0) return false;
        foreach (string x in Except)
            if (x.Length > 0 && n.Contains(x, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string w in Names)
            if (w.Length > 0 && n.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The profile's rule, read leniently: <see cref="None"/> without one (or for unusable text).</summary>
    public static ServerTrinketRule Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("trinkets", out JsonElement t) || t.ValueKind != JsonValueKind.Object)
                return None;
            var names = Words(t, "names");
            return names.Count == 0 ? None : new ServerTrinketRule(names, Words(t, "except"));
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static List<string> Words(JsonElement e, string name)
    {
        var list = new List<string>();
        if (!e.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement w in a.EnumerateArray())
            if (w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 and <= 64 } s) list.Add(s.Trim());
        return list;
    }
}

internal sealed record ServerSalvageView(string Bench, int Material, string Kind, string Effect, bool UsesTinkSlot, string NameMatch,
                                         int AppliedInt = 0, bool Prepared = false, int Order = 0, bool AfterCap = false,
                                         string StackGroup = "", int StackCap = 0)
{
    /// <summary>
    /// The profile's salvage rows, read leniently: a missing list (an older plugin) is empty; a row
    /// without a bench or a material id is skipped; "slot" defaults to true, the optional fields to
    /// none (a stack group without a positive cap is no group).
    /// </summary>
    public static IReadOnlyList<ServerSalvageView> ParseList(string? json)
    {
        var rows = new List<ServerSalvageView>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("salvage", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (JsonElement e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string bench = EngineServerState.Str(e, "bench").Trim().ToLowerInvariant();
                int material = EngineServerState.Int(e, "material");
                if (bench.Length == 0 || material <= 0) continue;
                string kind = EngineServerState.Str(e, "kind").Trim().ToLowerInvariant();
                bool slot = !(e.TryGetProperty("slot", out JsonElement s) && s.ValueKind == JsonValueKind.False);
                string group = EngineServerState.Str(e, "stackGroup").Trim();
                int cap = EngineServerState.Int(e, "stackCap");
                if (cap <= 0) group = string.Empty;
                rows.Add(new ServerSalvageView(bench, material, kind.Length > 0 ? kind : "tink",
                                               EngineServerState.Str(e, "effect"), slot, EngineServerState.Str(e, "nameMatch"),
                                               AppliedInt: EngineServerState.Int(e, "applied"),
                                               Prepared: EngineServerState.Bool(e, "prepared"),
                                               Order: EngineServerState.Int(e, "order"),
                                               AfterCap: EngineServerState.Bool(e, "afterCap"),
                                               StackGroup: group, StackCap: group.Length > 0 ? cap : 0));
            }
        }
        catch (JsonException)
        {
            rows.Clear();
        }
        return rows;
    }
}
