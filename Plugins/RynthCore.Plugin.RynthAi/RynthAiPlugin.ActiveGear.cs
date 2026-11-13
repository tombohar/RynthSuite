// RynthAiPlugin.ActiveGear.cs — the Active Gear window and /ra gear (2026-11-11, a player's request:
// "is there any place that shows you what gear is activated and what is not? Like MagTools does?").
//
// One row per worn item with spells: its slot, whether its magic is on (and why not, when that can
// be told), its mana, the time its mana lasts at its rate, and its spells (on hover). The logic is
// ActiveGear/GearActivation.cs (pure, tested); this file reads the engine for it:
//   * what is worn: EquippedState (the engine's EquipmentTracker, API v87; the client's fields before it);
//   * the item's appraisal: HasAppraisalData, GetObjectSpellIds (bit 31 = on the item itself),
//     CurrentMana 107 / MaxMana 108 / ItemDifficulty 109 / activation level 115 and skill 176 (ints),
//     ManaRate 5 (float), and GetLastIdTime (when it was appraised, Unix seconds);
//   * the player's enchantments: ReadPlayerEnchantments (an item's spell on its wearer has no end time);
//   * the player's Arcane Lore and activation skill: TryGetObjectSkill.
// "Check items" (and /ra gear check) identifies the worn items again, one every 1.5 s, so the mana
// reads and the on-the-item spells are fresh.
//
// The window is a plugin window (API v89) declared in the sections manifest without a bar button;
// the dashboard's options menu (⋯ > Active gear) and /ra gear open it. Pump thread throughout,
// except the window (render thread: it reads the published view and queues Check items for the tick).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.ActiveGear;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The window's title (its panel key) and surface (Huds/SectionsManifest.cs).</summary>
    internal const string ActiveGearTitle = "Active Gear", ActiveGearSurface = "window:activegear";

    internal const string ActiveGearUsage = "Usage: /ra gear [show | hide | check | list]";

    private const uint GearCurManaKey = 107, GearMaxManaKey = 108, GearDifficultyKey = 109,
                       GearActivationLevelKey = 115, GearActivationSkillKey = 176;
    private const uint GearManaRateKey = 5;   // PropertyFloat ManaRate
    private const uint GearProcSpellKey = 55; // PropertyDataId ProcSpell (a cloak's, aetheria's or weapon's proc)
    private const uint ArcaneLoreSkill = 14;
    private const long GearViewEveryMs = 1000, GearWornTrackEveryMs = 2000, GearCheckSpacingMs = 1500;

    private volatile ActiveGearView _gearView = ActiveGearView.Empty;   // tick writes, render reads
    private long _gearViewAtMs = long.MinValue / 2;
    private long _gearDrawnMs = long.MinValue / 2;                        // render writes, tick reads
    private long _gearWornAtMs = long.MinValue / 2;
    // When each worn item was first seen worn this login (ms): an appraisal newer than that lists its
    // on-the-item spells. Dropped when the item comes off.
    private readonly Dictionary<int, long> _gearWornSince = new();
    // Check items: the ids still to identify, the next send, and how many were asked for in all.
    private readonly Queue<int> _gearCheckQueue = new();
    private long _gearNextCheckMs;
    private int _gearCheckTotal;
    private volatile bool _gearCheckWanted;                                // the window's button (render thread)
    private ActiveGearWindow? _gearWindow;                                 // render thread
    private int _gearFailures;                                             // render thread
    private readonly uint[] _gearSpellBuf = new uint[128];
    private readonly uint[] _gearEnchIds = new uint[512];
    private readonly double[] _gearEnchExp = new double[512];

    /// <summary>The window goes in the manifest on an engine with plugin windows (v89).</summary>
    private bool ActiveGearWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen;

    /// <summary>The last view (tests).</summary>
    internal ActiveGearView ActiveGearViewForTests => _gearView;

    // ── Tick ─────────────────────────────────────────────────────────────

    /// <summary>Pump thread: worn-since tracking, Check items' sends, and the view while the window shows.</summary>
    private void TickActiveGear()
    {
        if (!_loginComplete || _objectCache == null || _playerId == 0) return;
        long now = Environment.TickCount64;
        if (now - _gearWornAtMs >= GearWornTrackEveryMs) TrackWorn(now, ReadWorn());
        if (_gearCheckWanted)
        {
            _gearCheckWanted = false;
            StartGearCheck();
        }
        TickGearCheck(now);
        bool shown = now - Volatile.Read(ref _gearDrawnMs) < 2000;
        if (shown && now - _gearViewAtMs >= GearViewEveryMs) PublishGearView(now);
    }

    /// <summary>What is worn (item id -> EquipMask): the engine's tracker, or the client's fields before it.</summary>
    private Dictionary<int, int> ReadWorn()
    {
        if (EquippedState.TryGet(Host, out Dictionary<int, int> map)) return new Dictionary<int, int>(map);
        return EquippedState.ReadFresh(Host, _objectCache);
    }

    private void TrackWorn(long now, Dictionary<int, int> worn)
    {
        _gearWornAtMs = now;
        foreach (int id in worn.Keys) _gearWornSince.TryAdd(id, now);
        if (_gearWornSince.Count == worn.Count) return;
        var gone = new List<int>();
        foreach (int id in _gearWornSince.Keys) if (!worn.ContainsKey(id)) gone.Add(id);
        foreach (int id in gone) _gearWornSince.Remove(id);
    }

    /// <summary>Queues every listed worn item for an identify (the unidentified first). Returns how many.</summary>
    private int StartGearCheck()
    {
        if (!Host.HasRequestId) return 0;
        ActiveGearView view = BuildGearView(Environment.TickCount64);
        _gearCheckQueue.Clear();
        foreach (ActiveGearRow r in view.Rows) if (!r.Facts.Identified) _gearCheckQueue.Enqueue(r.Id);
        foreach (ActiveGearRow r in view.Rows) if (r.Facts.Identified) _gearCheckQueue.Enqueue(r.Id);
        _gearCheckTotal = _gearCheckQueue.Count;
        _gearNextCheckMs = 0;
        Host.Log($"[RynthAi] Active gear: identifying {_gearCheckTotal} worn item(s), one every {GearCheckSpacingMs / 1000.0:0.#} s.");
        return _gearCheckTotal;
    }

    /// <summary>One identify every 1.5 s while Check items has some left; a refused one is asked again next time.</summary>
    private void TickGearCheck(long now)
    {
        if (_gearCheckQueue.Count == 0 || now < _gearNextCheckMs) return;
        _gearNextCheckMs = now + GearCheckSpacingMs;
        int id = _gearCheckQueue.Peek();
        if (!_gearWornSince.ContainsKey(id)) { _gearCheckQueue.Dequeue(); return; }   // taken off meanwhile
        if (Host.RequestId(unchecked((uint)id))) _gearCheckQueue.Dequeue();
        if (_gearCheckQueue.Count == 0) _gearViewAtMs = long.MinValue / 2;            // the last reply shows soon
    }

    private void PublishGearView(long now)
    {
        _gearViewAtMs = now;
        _gearView = BuildGearView(now);
    }

    /// <summary>Reads every worn item and judges it (GearActivation). Pump thread.</summary>
    private ActiveGearView BuildGearView(long now)
    {
        if (!_loginComplete || _objectCache == null || _playerId == 0) return ActiveGearView.Empty;
        Dictionary<int, int> worn = ReadWorn();
        TrackWorn(now, worn);

        // The player's enchantments with no end time: what worn items cast on their wearer.
        bool enchReadable = false;
        var playerItemSpells = new HashSet<int>();
        if (Host.HasReadPlayerEnchantments)
        {
            int n = Host.ReadPlayerEnchantments(_gearEnchIds, _gearEnchExp, _gearEnchIds.Length);
            if (n >= 0)
            {
                enchReadable = true;
                for (int i = 0; i < n && i < _gearEnchIds.Length; i++)
                    if (GearActivation.IsEndless(_gearEnchExp[i])) playerItemSpells.Add(unchecked((int)_gearEnchIds[i]));
            }
        }

        int? lore = null;
        if (Host.HasGetObjectSkill && Host.TryGetObjectSkill(_playerId, ArcaneLoreSkill, out int loreBuffed, out _)) lore = loreBuffed;
        long unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var rows = new List<ActiveGearRow>();
        foreach (var (id, slot) in worn)
        {
            uint uid = unchecked((uint)id);
            string name = _objectCache[id]?.Name ?? string.Empty;
            if (name.Length == 0 && Host.HasGetObjectName && Host.TryGetObjectName(uid, out string n0)) name = n0 ?? string.Empty;
            bool identified = Host.HasHasAppraisalData ? Host.HasAppraisalData(uid) : IntProp(uid, GearMaxManaKey).HasValue;

            uint[] raw = Array.Empty<uint>();
            if (identified && Host.HasGetObjectSpellIds)
            {
                int n = Host.GetObjectSpellIds(uid, _gearSpellBuf, _gearSpellBuf.Length);
                if (n > 0)
                {
                    raw = new uint[Math.Min(n, _gearSpellBuf.Length)];
                    Array.Copy(_gearSpellBuf, raw, raw.Length);
                }
            }

            double? age = null;
            if (Host.HasGetLastIdTime)
            {
                long at = Host.GetLastIdTime(uid);
                if (at > 0) age = Math.Max(0, unixNow - at);
            }
            double? wornFor = _gearWornSince.TryGetValue(id, out long since) ? (now - since) / 1000.0 : null;

            int actSkillId = identified ? IntProp(uid, GearActivationSkillKey) ?? 0 : 0;
            int actLevel = identified ? IntProp(uid, GearActivationLevelKey) ?? 0 : 0;
            int? actSkill = null;
            if (actLevel > 0 && actSkillId > 0 && Host.HasGetObjectSkill
                && Host.TryGetObjectSkill(_playerId, unchecked((uint)actSkillId), out int b, out _)) actSkill = b;

            double? rate = null;
            if (identified && Host.HasGetObjectDoubleProperty && Host.TryGetObjectDoubleProperty(uid, GearManaRateKey, out double r)) rate = r;
            uint procSpell = 0;
            if (identified && Host.HasGetObjectDataIdProperty) Host.TryGetObjectDataIdProperty(uid, GearProcSpellKey, out procSpell);

            var facts = new GearFacts
            {
                Name = name,
                Slot = slot,
                Identified = identified,
                RawSpellIds = raw,
                CurMana = identified ? IntProp(uid, GearCurManaKey) : null,
                MaxMana = identified ? IntProp(uid, GearMaxManaKey) : null,
                ManaRate = rate,
                ReadAgeSeconds = age,
                WornForSeconds = wornFor,
                EnchantmentsReadable = enchReadable,
                PlayerItemSpells = playerItemSpells,
                ArcaneLoreNeeded = identified ? IntProp(uid, GearDifficultyKey) ?? 0 : 0,
                ArcaneLore = lore,
                ActivationSkillName = actSkillId > 0 && ItemInfo.MagItemInfoTables.Skills.TryGetValue(actSkillId, out string? sn) ? sn : string.Empty,
                ActivationLevel = actLevel,
                ActivationSkill = actSkill,
                ProcSpellId = unchecked((int)procSpell),
            };
            if (!GearActivation.Listed(facts)) continue;
            GearVerdict v = GearActivation.Judge(facts);
            var spellNames = new string[v.SpellIds.Count];
            for (int i = 0; i < spellNames.Length; i++) spellNames[i] = SpellDatabase.GetSpellNameOrId(v.SpellIds[i]);
            rows.Add(new ActiveGearRow(id, facts, v, GearActivation.SlotName(slot), spellNames));
        }
        rows.Sort((a, b) =>
        {
            int c = GearActivation.SlotOrder(a.Facts.Slot).CompareTo(GearActivation.SlotOrder(b.Facts.Slot));
            return c != 0 ? c : string.Compare(a.Facts.Name, b.Facts.Name, StringComparison.OrdinalIgnoreCase);
        });

        var pairs = new List<(GearFacts, GearVerdict)>(rows.Count);
        foreach (ActiveGearRow row in rows) pairs.Add((row.Facts, row.Verdict));
        int left = _gearCheckQueue.Count;
        return new ActiveGearView(rows.ToArray(), GearActivation.Summary(pairs), true, enchReadable,
            left > 0 ? _gearCheckTotal - left : -1, _gearCheckTotal, Host.HasRequestId);
    }

    private int? IntProp(uint id, uint key) =>
        Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(id, key, out int v) ? v : null;

    /// <summary>Logout: the view and the per-login records go.</summary>
    private void ResetActiveGear()
    {
        _gearView = ActiveGearView.Empty;
        _gearWornSince.Clear();
        _gearCheckQueue.Clear();
        _gearCheckTotal = 0;
        _gearWornAtMs = long.MinValue / 2;
        _gearViewAtMs = long.MinValue / 2;
    }

    // ── /ra gear ─────────────────────────────────────────────────────────

    /// <summary>
    /// "/ra gear" opens the window and says the summary; "show" / "hide" open or close it; "check"
    /// identifies the worn items again; "list" prints a line per item. Every form replies. Pump thread.
    /// </summary>
    private void HandleGearCommand(string[] parts)
    {
        string arg = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2).Trim().ToLowerInvariant() : string.Empty;
        if (!_loginComplete || _objectCache == null || _playerId == 0)
        {
            ReplyFail("[RynthAi] Active gear: not in the world yet. " + ActiveGearUsage);
            return;
        }
        switch (arg)
        {
            case "":
            case "show":
            case "open":
            {
                PublishGearView(Environment.TickCount64);
                ReplyOk("[RynthAi] Active gear: " + _gearView.Summary + GearWindowNote(RynthPanelOpen.Open));
                return;
            }
            case "hide":
            case "close":
            {
                if (!Host.HasSetPanelOpen) { ReplyFail("[RynthAi] Active gear: this RynthCore has no plugin windows (plugin API 89+). " + ActiveGearUsage); return; }
                int open = Host.SetPanelOpen(ActiveGearTitle, RynthPanelOpen.Close);
                if (open < 0) ReplyFail("[RynthAi] The Active Gear window isn't there yet (RynthCore adds it a few seconds after login).");
                else ReplyOk("[RynthAi] Active Gear window closed.");
                return;
            }
            case "check":
            case "id":
            {
                if (!Host.HasRequestId) { ReplyFail("[RynthAi] Active gear: this RynthCore can't identify items. " + ActiveGearUsage); return; }
                int n = StartGearCheck();
                ReplyOk(n == 0
                    ? "[RynthAi] Active gear: no worn items with spells to identify."
                    : $"[RynthAi] Active gear: identifying {n} worn {(n == 1 ? "item" : "items")}, one every {GearCheckSpacingMs / 1000.0:0.#} s.");
                return;
            }
            case "list":
            {
                PublishGearView(Environment.TickCount64);
                ActiveGearView view = _gearView;
                ChatLine("[RynthAi] Active gear: " + view.Summary);
                foreach (ActiveGearRow r in view.Rows) ChatLine("[RynthAi]   " + ActiveGearText.ListLine(r));
                return;
            }
            case "help":
            case "?":
                ChatLine("[RynthAi] /ra gear — which worn items have their magic on, their mana and how long it lasts (opens the Active Gear window)");
                ChatLine("[RynthAi] /ra gear check | list | hide — identify the worn items again, a line per item in chat, close the window");
                return;
            default:
                ReplyFail($"[RynthAi] Active gear: unknown option '{arg}'. {ActiveGearUsage}");
                return;
        }
    }

    /// <summary>" (window open)" after the summary, or why it can't open.</summary>
    private string GearWindowNote(RynthPanelOpen mode)
    {
        if (!Host.HasSetPanelOpen) return " (/ra gear list for each item; the window needs RynthCore with plugin windows)";
        int open = Host.SetPanelOpen(ActiveGearTitle, mode);
        return open < 0 ? " (the window isn't there yet: RynthCore adds it a few seconds after login)"
             : open == 1 ? " (Active Gear window open)" : " (Active Gear window closed)";
    }

    /// <summary>The dashboard's ⋯ > Active gear: the window open or closed. Any thread.</summary>
    internal void ToggleActiveGearWindow()
    {
        if (Host.HasSetPanelOpen) Host.SetPanelOpen(ActiveGearTitle, RynthPanelOpen.Toggle);
    }

    // ── Window (render thread) ───────────────────────────────────────────

    /// <summary>
    /// RynthPluginRenderEmbed "window:activegear": the Active Gear window body, docked or popped out.
    /// 1 drawn, 0 not ready, -2 failed (three draws in a row threw).
    /// </summary>
    internal int RenderActiveGear(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        Volatile.Write(ref _gearDrawnMs, Environment.TickCount64);
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _gearWindow ??= new ActiveGearWindow(() => _gearCheckWanted = true);
            want = _gearWindow.Draw(_gearView);
            _gearFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderActiveGear");
            return ++_gearFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }
}
