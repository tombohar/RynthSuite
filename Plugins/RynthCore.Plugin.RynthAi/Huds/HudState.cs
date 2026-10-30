// HudState.cs — Persisted per-character settings for the floating HUD windows
// (<charFolder>\huds.json). Fields, not properties, so the source-generated
// RynthAiJsonContext (IncludeFields) round-trips them under NativeAOT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.Huds;

public sealed class HudState
{
    /// <summary>Mini Remote gem grid size (UB layout: 5 columns × 6 rows).</summary>
    public const int MiniRemoteColumns = 5;
    public const int MiniRemoteSlotCount = 30;
    /// <summary>Mini Remote slot icon edge in pixels: the grow / shrink range and the default.</summary>
    public const float MinMiniRemoteSlotSize = 18f, MaxMiniRemoteSlotSize = 64f, DefaultMiniRemoteSlotSize = 28f;

    /// <summary>The "Inventory HUDs" setup window is open.</summary>
    public bool ShowSetup;

    // ── Pack item count HUD ──
    public bool ShowItemHud;
    public bool ItemHudShowNames;
    public bool ItemHudLocked;
    public float ItemHudIconSize = 20f;
    /// <summary>Items shown on the HUD, in display order (matched by name).</summary>
    public List<HudItemEntry> ItemHudItems = new();

    // ── Mini Remote ──
    public bool ShowMiniRemote;
    public bool MiniRemoteLocked;
    /// <summary>Sections laid out side by side in columns (the V/H button) instead of stacked.</summary>
    public bool MiniRemoteHorizontal;
    public bool MiniShowStats = true;
    /// <summary>Attack target row: name, health bar, distance.</summary>
    public bool MiniShowTarget = true;
    /// <summary>Pet row: the summon that is out (health, time left), else the next combat essence.</summary>
    public bool MiniShowPet = true;
    public bool MiniShowGems = true;
    public bool MiniShowToggles = true;
    /// <summary>Obsolete since 2026-10-20 (kept so huds.json loads): the Bank section's switch is RynthILT's (copied once).</summary>
    public bool MiniShowBank = true;
    public bool MiniShowRebuff = true;
    /// <summary>Chat translator row: on/off, receive / send language swap.</summary>
    public bool MiniShowTranslate = true;
    /// <summary>Obsolete since 2026-10-20 (kept so huds.json loads): the Guardian line's switch is RynthILT's (copied once).</summary>
    public bool MiniShowGuardian = true;
    /// <summary>Gem grid slots, row-major from the top-left (empty Name = unassigned).</summary>
    public List<HudItemEntry> MiniRemoteSlots = new();
    /// <summary>
    /// Slot icon edge in pixels (Ctrl+wheel over the slots, or Options > Slot size).
    /// Clamped to <see cref="MinMiniRemoteSlotSize"/>..<see cref="MaxMiniRemoteSlotSize"/>.
    /// </summary>
    public float MiniRemoteSlotSize = DefaultMiniRemoteSlotSize;
    /// <summary>
    /// The Mini Remote's selected tab: "" for the remote itself, else a tab id: its own
    /// (quests, pets) or RynthILT's (character, banking, gear, charms, games).
    /// </summary>
    public string MiniRemoteTab = string.Empty;
}

/// <summary>An item pinned to a HUD. Matched by name; Wcid / IconDid are remembered so the
/// entry still shows its icon while none are carried.</summary>
public sealed class HudItemEntry
{
    public string Name = string.Empty;
    public uint Wcid;
    public uint IconDid;

    [JsonIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(Name);
}

/// <summary>Loads / dirty-checked saves of <see cref="HudState"/>.</summary>
internal sealed class HudStore
{
    private readonly string _charFolder;
    private string _lastJson = string.Empty;

    public HudStore(string charFolder) => _charFolder = charFolder ?? string.Empty;

    private string StatePath => Path.Combine(_charFolder, "huds.json");

    public HudState Load()
    {
        HudState? s = null;
        try
        {
            if (_charFolder.Length > 0 && File.Exists(StatePath))
            {
                string json = File.ReadAllText(StatePath);
                s = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.HudState);
                if (s != null) _lastJson = json;
            }
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, $"load {StatePath}"); }
        s ??= new HudState();
        Normalize(s);
        return s;
    }

    /// <summary>Writes the state only when its JSON differs from the last write.</summary>
    public void SaveIfDirty(HudState state)
    {
        if (_charFolder.Length == 0) return;
        try
        {
            string json = JsonSerializer.Serialize(state, RynthAiJsonContext.Default.HudState);
            if (json == _lastJson) return;
            Directory.CreateDirectory(_charFolder);
            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StatePath, overwrite: true);
            _lastJson = json;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, $"save {StatePath}"); }
    }

    /// <summary>Repairs null lists / out-of-range values after deserialization.</summary>
    private static void Normalize(HudState s)
    {
        s.ItemHudItems ??= new();
        s.ItemHudItems.RemoveAll(e => e == null || e.IsEmpty);
        s.ItemHudIconSize = Math.Clamp(s.ItemHudIconSize, 12f, 48f);
        s.MiniRemoteSlots ??= new();
        for (int i = 0; i < s.MiniRemoteSlots.Count; i++) s.MiniRemoteSlots[i] ??= new HudItemEntry();
        while (s.MiniRemoteSlots.Count < HudState.MiniRemoteSlotCount) s.MiniRemoteSlots.Add(new HudItemEntry());
        if (s.MiniRemoteSlots.Count > HudState.MiniRemoteSlotCount)
            s.MiniRemoteSlots.RemoveRange(HudState.MiniRemoteSlotCount, s.MiniRemoteSlots.Count - HudState.MiniRemoteSlotCount);
        s.MiniRemoteTab ??= string.Empty;
        // A huds.json from before the setting has no value (0): use the default.
        s.MiniRemoteSlotSize = s.MiniRemoteSlotSize <= 0f || float.IsNaN(s.MiniRemoteSlotSize)
            ? HudState.DefaultMiniRemoteSlotSize
            : Math.Clamp(s.MiniRemoteSlotSize, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize);
    }
}
