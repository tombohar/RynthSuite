using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using RynthCore.Loot.VTank;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Character + host context that VTank rules need to evaluate beyond the item
/// itself: buffed/base skill values, character level, main-pack empty slots,
/// item spell IDs/palettes. Built once per evaluation pass and re-used across
/// rules (cheap lookups cached internally).
/// </summary>
public sealed class VTankLootContext
{
    public RynthCoreHost Host { get; }
    public uint PlayerId { get; }

    private readonly Dictionary<uint, (int Buffed, int Base)> _skillCache = new();
    private readonly Dictionary<uint, string[]> _spellNameCache = new();
    private readonly Dictionary<uint, uint[]> _spellIdCache = new();
    private int? _level;
    private int? _mainPackEmptySlots;

    public VTankLootContext(RynthCoreHost host, uint playerId)
    {
        Host = host;
        PlayerId = playerId;
    }

    public (int Buffed, int Base) GetSkill(uint stypeSkill)
    {
        if (_skillCache.TryGetValue(stypeSkill, out var cached)) return cached;
        (int Buffed, int Base) result = (0, 0);
        if (PlayerId != 0 && Host.HasGetObjectSkill
            && Host.TryGetObjectSkill(PlayerId, stypeSkill, out int buffed, out int training))
            result = (buffed, training); // VTank "Base" == advancement class check; we use training as a proxy
        _skillCache[stypeSkill] = result;
        return result;
    }

    private readonly Dictionary<uint, int> _baseSkillCache = new();

    /// <summary>
    /// The character's base skill (no enchantments), for VTank's "Character Base Skill" node. It
    /// compared the training class (2 trained, 3 specialized) with skill levels like 300 until
    /// 2026-10-31. An engine without the base read answers the buffed level.
    /// </summary>
    public int GetBaseSkill(uint stypeSkill)
    {
        if (_baseSkillCache.TryGetValue(stypeSkill, out int cached)) return cached;
        int result = 0;
        if (PlayerId != 0 && Host.HasGetObjectSkillBuffed
            && Host.TryGetObjectSkillLevel(PlayerId, stypeSkill, raw: 1, out int level))
            result = level;
        else
            result = GetSkill(stypeSkill).Buffed;
        _baseSkillCache[stypeSkill] = result;
        return result;
    }

    public int GetLevel()
    {
        if (_level.HasValue) return _level.Value;
        int lvl = 0;
        if (PlayerId != 0 && Host.HasGetObjectIntProperty
            && Host.TryGetObjectIntProperty(PlayerId, 25 /* STypeInt.LEVEL */, out int v))
            lvl = v;
        _level = lvl;
        return lvl;
    }

    public int GetMainPackEmptySlots()
    {
        if (_mainPackEmptySlots.HasValue) return _mainPackEmptySlots.Value;
        int used = 0;
        if (Cache != null && PlayerId != 0)
        {
            int playerIdSigned = unchecked((int)PlayerId);
            foreach (var wo in Cache.GetDirectInventory(forceRefresh: false))
            {
                if (wo.Container != playerIdSigned) continue;
                if (wo.ObjectClass == AcObjectClass.Container) continue;
                if (wo.ObjectClass == AcObjectClass.Foci) continue;
                if (wo.Values(LongValueKey.EquippedSlots, 0) > 0) continue;
                used++;
            }
        }
        int slots = Math.Max(0, 102 - used);
        _mainPackEmptySlots = slots;
        return slots;
    }

    private readonly Dictionary<uint, (uint[] SubIds, uint[] Offsets)> _paletteCache = new();

    public (uint[] SubIds, uint[] Offsets) GetItemPalettes(uint itemId)
    {
        if (_paletteCache.TryGetValue(itemId, out var cached)) return cached;
        var empty = (Array.Empty<uint>(), Array.Empty<uint>());
        if (!Host.HasGetObjectPalettes) { _paletteCache[itemId] = empty; return empty; }
        var subIds = new uint[16];
        var offsets = new uint[16];
        int n = Host.GetObjectPalettes(itemId, subIds, offsets, 16);
        if (n <= 0) { _paletteCache[itemId] = empty; return empty; }
        int take = Math.Min(n, 16);
        var ids2 = new uint[take];
        var offs2 = new uint[take];
        Array.Copy(subIds, ids2, take);
        Array.Copy(offsets, offs2, take);
        var result = (ids2, offs2);
        _paletteCache[itemId] = result;
        return result;
    }

    public uint[] GetItemSpellIds(uint itemId)
    {
        if (_spellIdCache.TryGetValue(itemId, out var ids)) return ids;
        uint[] result = Array.Empty<uint>();
        if (Host.HasGetObjectSpellIds)
        {
            uint[] buf = new uint[64];
            int n = Host.GetObjectSpellIds(itemId, buf, buf.Length);
            if (n > 0)
            {
                int take = Math.Min(n, buf.Length);
                result = new uint[take];
                Array.Copy(buf, result, take);
            }
        }
        _spellIdCache[itemId] = result;
        return result;
    }

    public string[] GetItemSpellNames(uint itemId)
    {
        if (_spellNameCache.TryGetValue(itemId, out var cached)) return cached;
        uint[] ids = GetItemSpellIds(itemId);
        var names = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            SpellInfo? info = SpellTableStub.GetById((int)ids[i]);
            names[i] = info?.Name ?? string.Empty;
        }
        _spellNameCache[itemId] = names;
        return names;
    }

    internal WorldObjectCache? Cache { get; set; }
}

/// <summary>What <see cref="VTankLootEvaluator.Decide"/> found for an item.</summary>
public enum VTankLootVerdict
{
    /// <summary>No rule takes the item.</summary>
    NoMatch,
    /// <summary>A rule takes it.</summary>
    Match,
    /// <summary>The first rule that could take it needs the item's identify data, which isn't in yet.</summary>
    NeedsId,
}

/// <summary>
/// Evaluates a VTankLootRule against a WorldObject. All matching logic is here
/// so the LootSdk types stay pure-data and the editor can reference them
/// without dragging in plugin/host dependencies.
/// </summary>
public static class VTankLootEvaluator
{
    public static bool Match(VTankLootRule rule, WorldObject? item, VTankLootContext? ctx = null)
    {
        if (item is null) return false;
        if (rule.Conditions.Count == 0) return true; // unconditional rule

        try
        {
            uint itemId = unchecked((uint)item.Id);
            foreach (var cond in rule.Conditions)
            {
                if (!Match(cond, item, itemId, ctx))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The first rule, in list order, that matches the item (rule Priority is not consulted),
    /// or null when none does; <paramref name="index"/> is its position, or -1.
    /// The item's identify data is taken as in (see <see cref="Decide"/> for an item without it).
    /// </summary>
    public static VTankLootRule? FirstMatch(VTankLootProfile profile, WorldObject? item, VTankLootContext? ctx, out int index)
        => Decide(profile, item, ctx, hasIdData: true, out index) == VTankLootVerdict.Match ? profile.Rules[index] : null;

    /// <summary>
    /// The corpse looter's decision, as VTank makes it: the rules in list order, the first that
    /// matches wins. For an item whose identify data hasn't arrived (<paramref name="hasIdData"/>
    /// false), a rule that nothing seen so far rules out but that needs the ID to decide stops the
    /// search with <see cref="VTankLootVerdict.NeedsId"/>: judged now its ID-only values read 0, it
    /// fails, and a later rule takes the item (a +9 crossbow's Keep rule failed and the Salvage rule
    /// on its gold took it). A rule that a condition known without the ID (class, name, value,
    /// material) already fails is passed over, whatever order its conditions are in. A condition
    /// that throws (bad data, unknown node type) fails its rule. <paramref name="index"/> is the
    /// matched rule (Match) or the rule that needs the ID (NeedsId); -1 for NoMatch.
    /// </summary>
    public static VTankLootVerdict Decide(VTankLootProfile profile, WorldObject? item, VTankLootContext? ctx, bool hasIdData, out int index)
    {
        index = -1;
        if (item is null) return VTankLootVerdict.NoMatch;
        uint itemId = unchecked((uint)item.Id);
        for (int i = 0; i < profile.Rules.Count; i++)
        {
            VTankLootRule rule = profile.Rules[i];
            bool failed = false, waitsForId = false;
            foreach (var cond in rule.Conditions)
            {
                if (!hasIdData && ConditionNeedsId(cond)) { waitsForId = true; continue; }
                bool ok;
                try { ok = Match(cond, item, itemId, ctx); }
                catch { ok = false; }
                if (!ok) { failed = true; break; }
            }
            if (failed) continue;
            index = i;
            return waitsForId ? VTankLootVerdict.NeedsId : VTankLootVerdict.Match;
        }
        return VTankLootVerdict.NoMatch;
    }

    /// <summary>
    /// The first switched-off rule before <paramref name="end"/> (-1: all of them) that would take
    /// the item if it were on: every condition but its DisabledRule node passes, the item's ID taken
    /// as in. -1 when none would. A rule that is only a DisabledRule node (LootSnob's "leave this
    /// disabled" separators have a name condition too) never counts. For /ra loot why and the
    /// looter's log: a wand left because "(W) War (Max 385)" is switched off in the player's copy of
    /// LootSnob read the same as one no rule wants (2026-11-01). <paramref name="keepsOnly"/>: Keep
    /// and Keep # rules only (LootSnob ships its Salvage rules off, so nearly every item with a
    /// material has a switched-off Salvage rule that would take it).
    /// </summary>
    public static int FirstSwitchedOffMatch(VTankLootProfile profile, WorldObject? item, VTankLootContext? ctx, int end = -1, bool keepsOnly = false)
    {
        if (item is null) return -1;
        uint itemId = unchecked((uint)item.Id);
        int stop = end < 0 || end > profile.Rules.Count ? profile.Rules.Count : end;
        for (int i = 0; i < stop; i++)
        {
            VTankLootRule rule = profile.Rules[i];
            if (rule.Enabled) continue;
            if (keepsOnly && rule.Action != VTankLootAction.Keep && rule.Action != VTankLootAction.KeepUpTo) continue;
            bool any = false, failed = false;
            foreach (var cond in rule.Conditions)
            {
                if (cond.NodeType == VTankNodeTypes.DisabledRule) continue;
                any = true;
                bool ok;
                try { ok = Match(cond, item, itemId, ctx); }
                catch { ok = false; }
                if (!ok) { failed = true; break; }
            }
            if (any && !failed) return i;
        }
        return -1;
    }

    // STypeInts the CreateObject message carries (the engine answers them before any ID:
    // RynthCore PwdLayout.IntStypes). Everything else, workmanship (105) included, comes with the ID.
    private static readonly HashSet<int> CreateIntKeys = new()
    {
        1, 4, 5, 6, 7, 9, 10, 11, 12, 16, 18, 19, 50, 51, 91, 92, 94, 95, 131, 133, 151, 152, 280,
    };

    /// <summary>
    /// True when the condition reads something only the item's identify (appraisal) data holds:
    /// stats, spells, wield requirements, workmanship, ratings, damage. False for what the
    /// CreateObject message already gave (name, class, value, burden, material, icon, WCID, stack,
    /// palettes) and for the character and disabled nodes.
    /// </summary>
    public static bool ConditionNeedsId(VTankLootCondition cond)
    {
        var d = cond.DataLines;
        switch (cond.NodeType)
        {
            case VTankNodeTypes.ObjectClass:
            case VTankNodeTypes.DisabledRule:
            case VTankNodeTypes.DamagePercentGE:        // always fails, ID or not
            case VTankNodeTypes.AnySimilarColor:
            case VTankNodeTypes.SimilarColorArmorType:
            case VTankNodeTypes.SlotSimilarColor:
            case VTankNodeTypes.SlotExactPalette:       // palettes come with the object
            case VTankNodeTypes.CharacterSkillGE:
            case VTankNodeTypes.CharacterMainPackEmptySlotsGE:
            case VTankNodeTypes.CharacterLevelGE:
            case VTankNodeTypes.CharacterLevelLE:
            case VTankNodeTypes.CharacterBaseSkill:
                return false;
            case VTankNodeTypes.StringValueMatch:
                return !(d.Count > 1 && TryInt(d[1], out int skey) && skey == (int)StringValueKey.Name);
            case VTankNodeTypes.LongValKeyLE:
            case VTankNodeTypes.LongValKeyGE:
            case VTankNodeTypes.LongValKeyE:
            case VTankNodeTypes.LongValKeyNE:
            case VTankNodeTypes.LongValKeyFlagExists:
                return !(d.Count > 1 && TryInt(d[1], out int lkey) && IsCreateIntKey(lkey));
            case VTankNodeTypes.DoubleValKeyLE:
            case VTankNodeTypes.DoubleValKeyGE:
                // UseRadius and VTank's SalvageWorkmanship (the engine's float 280) come with the object.
                return !(d.Count > 1 && TryInt(d[1], out int dkey) && (dkey == (int)DoubleValueKey.UseRadius || unchecked((uint)dkey) == 0x0A000009));
            default:
                return true;   // spells, damage, buffed values, ratings; unknown types fail anyway
        }
    }

    private static bool IsCreateIntKey(int key)
    {
        uint k = unchecked((uint)key);
        if (k == WorldObjectCache.DecalTypeKey) return true;                       // WCID
        if (k is 0x0D000001 or 0x0D000002 or 0x0D000008 or 0x0D000029 or 0x0D00002A) return true;   // icon, container, spell, overlays
        uint stype = WorldObjectCache.DecalIntStype(k);
        if (stype != 0) return CreateIntKeys.Contains(unchecked((int)stype));
        return CreateIntKeys.Contains(key);
    }

    private static bool TryInt(string s, out int v)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    /// <summary>
    /// One condition on its own, same semantics as inside <see cref="Match(VTankLootRule, WorldObject?, VTankLootContext?)"/>.
    /// AutoVendor judges conditions one at a time so it can tell "no" from "can't tell".
    /// Throws on a malformed data line or an unsupported node type (the caller decides what that means).
    /// </summary>
    public static bool MatchCondition(VTankLootCondition cond, WorldObject item, VTankLootContext? ctx)
        => Match(cond, item, unchecked((uint)item.Id), ctx);

    /// <summary>
    /// One condition in words with the value it read, for /ra loot why: "Buffed Missile Damage
    /// >= 86: read 84.0 (max 0 + mod 2.65 -> 55.0 + elemental 22 + cantrips 7)". Never throws.
    /// </summary>
    public static string Explain(VTankLootCondition cond, WorldObject item, VTankLootContext? ctx)
    {
        // Plain ASCII: AC's chat font has no ≥ ≤ ≠.
        string what = VTankNodeTypes.DisplayName(cond.NodeType).Replace("≥", ">=").Replace("≤", "<=").Replace("≠", "!=")
                    + " " + Clip(string.Join(", ", cond.DataLines), 60);
        try { return what.Trim() + ": read " + Clip(ReadValue(cond, item, unchecked((uint)item.Id), ctx), 160); }
        catch (Exception ex) { return what.Trim() + ": can't evaluate (" + ex.GetType().Name + ")"; }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";

    private static string ReadValue(VTankLootCondition cond, WorldObject item, uint itemId, VTankLootContext? ctx)
    {
        var d = cond.DataLines;
        switch (cond.NodeType)
        {
            case VTankNodeTypes.StringValueMatch:
                return "'" + item.Values((StringValueKey)ReadInt(d, 1), string.Empty) + "'";
            case VTankNodeTypes.LongValKeyLE:
            case VTankNodeTypes.LongValKeyGE:
            case VTankNodeTypes.LongValKeyE:
            case VTankNodeTypes.LongValKeyNE:
            case VTankNodeTypes.LongValKeyFlagExists:
                return item.Values(ReadInt(d, 1), 0).ToString(CultureInfo.InvariantCulture) + " (key " + ReadInt(d, 1).ToString(CultureInfo.InvariantCulture) + ")";
            case VTankNodeTypes.DoubleValKeyLE:
            case VTankNodeTypes.DoubleValKeyGE:
                return F(item.Values(FloatKey(ReadInt(d, 1)), 0.0)) + " (key " + ReadInt(d, 1).ToString(CultureInfo.InvariantCulture) + ")";
            case VTankNodeTypes.ObjectClass:
                return item.ObjectClass + " (" + ((int)item.ObjectClass).ToString(CultureInfo.InvariantCulture) + ")";
            case VTankNodeTypes.SpellNameMatch:
            case VTankNodeTypes.SpellMatch:
            case VTankNodeTypes.SpellCountGE:
            {
                if (ctx is null) return "no spell list";
                string[] names = ctx.GetItemSpellNames(itemId);
                return names.Length == 0 ? "no spells" : names.Length + " spell(s): " + string.Join(", ", names);
            }
            case VTankNodeTypes.MinDamageGE:
            {
                int max = item.Values(44, 0);
                double v = item.Values(DoubleValueKey.DamageVariance, 0.0);
                return F(max - v * max) + " (max " + max + ", variance " + F(v) + ")";
            }
            case VTankNodeTypes.BuffedMedianDamageGE:
            {
                int max = BuffedLong(item, itemId, ctx, MaxDamageKey);
                double v = item.Values(DoubleValueKey.DamageVariance, 0.0);
                return F((max - v * max + max) / 2.0) + " (buffed max " + max + ", variance " + F(v) + ")";
            }
            case VTankNodeTypes.BuffedMissileDamageGE:
            {
                double mod = item.Values(DoubleValueKey.DamageMod, 0.0);
                if (mod <= 0) return "no damage modifier (not a launcher)";
                int raw = item.Values(MaxDamageKey, 0);
                int buffed = BuffedLong(item, itemId, ctx, MaxDamageKey);
                return F(BuffedMissileDamage(item, itemId, ctx)) + " (max " + raw + " + mod " + F(mod) + " -> " + F((mod - 1) * 100 / 3)
                     + " + elemental " + item.Values(ElementalDamageBonusKey, 0) + " + cantrips " + (buffed - raw) + ")";
            }
            case VTankNodeTypes.BuffedLongValKeyGE:
            {
                int key = ReadInt(d, 1);
                int raw = item.Values(key, 0), buffed = BuffedLong(item, itemId, ctx, key);
                return buffed + (buffed != raw ? " (" + raw + " + cantrips " + (buffed - raw) + ")" : "") + " (key " + key + ")";
            }
            case VTankNodeTypes.BuffedDoubleValKeyGE:
            {
                int key = ReadInt(d, 1);
                double raw = item.Values(FloatKey(key), 0.0), buffed = BuffedDouble(item, itemId, ctx, key);
                return F(buffed) + (buffed != raw ? " (" + F(raw) + " with cantrips)" : "") + " (key " + key + ")";
            }
            case VTankNodeTypes.CalcdBuffedTinkedDamageGE:
                return F(BuffedTinkedDamage(item, itemId, ctx)) + " (" + TinksLeft(item) + " tink(s) left)";
            case VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE:
            {
                MatchTinkedTargetMelee(item, itemId, ctx, d, out double dot);
                return (dot < 0 ? "defense/attack target out of reach" : "damage " + F(dot)) + " (" + TinksLeft(item) + " tink(s) left, melee "
                     + F(BuffedDouble(item, itemId, ctx, 29)) + ", attack " + F(BuffedDouble(item, itemId, ctx, AttackBonusKey)) + ")";
            }
            case VTankNodeTypes.TotalRatingsGE:
                return (item.Values(370, 0) + item.Values(371, 0) + item.Values(372, 0) + item.Values(373, 0)
                      + item.Values(374, 0) + item.Values(375, 0) + item.Values(376, 0) + item.Values(379, 0)).ToString(CultureInfo.InvariantCulture);
            case VTankNodeTypes.CharacterSkillGE:
                return ctx is null ? "no character" : ctx.GetSkill(unchecked((uint)ReadInt(d, 1))).Buffed.ToString(CultureInfo.InvariantCulture);
            case VTankNodeTypes.CharacterBaseSkill:
                return ctx is null ? "no character" : ctx.GetBaseSkill(unchecked((uint)ReadInt(d, 0))).ToString(CultureInfo.InvariantCulture);
            case VTankNodeTypes.CharacterLevelGE:
            case VTankNodeTypes.CharacterLevelLE:
                return ctx is null ? "no character" : ctx.GetLevel().ToString(CultureInfo.InvariantCulture);
            case VTankNodeTypes.CharacterMainPackEmptySlotsGE:
                return ctx is null ? "no character" : ctx.GetMainPackEmptySlots() + " free";
            case VTankNodeTypes.SlotExactPalette:
            {
                if (ctx is null) return "no palettes";
                var (subIds, _) = ctx.GetItemPalettes(itemId);
                int slot = ReadInt(d, 0);
                return subIds.Length == 0 ? "no palette data" : slot >= 0 && slot < subIds.Length ? (subIds[slot] & 0x00FFFFFFu).ToString(CultureInfo.InvariantCulture) : "no slot " + slot;
            }
            case VTankNodeTypes.DisabledRule:
                return d.Count > 0 && string.Equals(d[0].Trim(), "true", StringComparison.OrdinalIgnoreCase) ? "rule disabled" : "enabled";
            case VTankNodeTypes.DamagePercentGE:
                return "never matches (as VTank)";
            default:
                return Match(cond, item, itemId, ctx) ? "passes" : "fails";
        }
    }

    private static bool Match(VTankLootCondition cond, WorldObject item, uint itemId, VTankLootContext? ctx)
    {
        var d = cond.DataLines;
        return cond.NodeType switch
        {
            VTankNodeTypes.SpellNameMatch                  => MatchSpellNameMatch(itemId, ctx, d),
            VTankNodeTypes.StringValueMatch                => MatchStringValue(item, d),
            VTankNodeTypes.LongValKeyLE                    => Long(d) is var (val,key) && item.Values((LongValueKey)key, 0) <= val,
            VTankNodeTypes.LongValKeyGE                    => Long(d) is var (val,key) && item.Values((LongValueKey)key, 0) >= val,
            // VTank compares double keys as floats: 1.2 + 0.09 must still be >= 1.29.
            VTankNodeTypes.DoubleValKeyLE                  => Double(d) is var (val,key) && (float)item.Values(FloatKey(key), 0.0) <= (float)val,
            VTankNodeTypes.DoubleValKeyGE                  => Double(d) is var (val,key) && (float)item.Values(FloatKey(key), 0.0) >= (float)val,
            VTankNodeTypes.DamagePercentGE                 => false, // VTank source returns false unconditionally
            VTankNodeTypes.ObjectClass                     => ScrollLearner.ClassMatches(item, ReadInt(d, 0)),   // Scroll (42) also matches scroll Books
            VTankNodeTypes.SpellCountGE                    => ctx is null || ctx.GetItemSpellIds(itemId).Length >= ReadInt(d, 0),
            VTankNodeTypes.SpellMatch                      => MatchSpellMatch(itemId, ctx, d),
            VTankNodeTypes.MinDamageGE                     => MatchMinDamageGE(item, d),
            VTankNodeTypes.LongValKeyFlagExists            => Long(d) is var (flag,fkey) && (item.Values((LongValueKey)fkey, 0) & flag) != 0,
            VTankNodeTypes.LongValKeyE                     => Long(d) is var (val,key) && item.Values((LongValueKey)key, 0) == val,
            VTankNodeTypes.LongValKeyNE                    => Long(d) is var (val,key) && item.Values((LongValueKey)key, 0) != val,
            VTankNodeTypes.AnySimilarColor                 => true, // needs portal.dat — optimistic
            VTankNodeTypes.SimilarColorArmorType           => true, // needs portal.dat — optimistic
            VTankNodeTypes.SlotSimilarColor                => true, // needs portal.dat — optimistic
            VTankNodeTypes.SlotExactPalette                => MatchSlotExactPalette(itemId, ctx, d),
            VTankNodeTypes.CharacterSkillGE                => ctx is null || ctx.GetSkill(unchecked((uint)ReadInt(d, 1))).Buffed >= ReadInt(d, 0),
            VTankNodeTypes.CharacterMainPackEmptySlotsGE   => ctx is null || ctx.GetMainPackEmptySlots() >= ReadInt(d, 0),
            VTankNodeTypes.CharacterLevelGE                => ctx is null || ctx.GetLevel() >= ReadInt(d, 0),
            VTankNodeTypes.CharacterLevelLE                => ctx is null || ctx.GetLevel() <= ReadInt(d, 0),
            VTankNodeTypes.CharacterBaseSkill              => MatchCharacterBaseSkill(ctx, d),
            VTankNodeTypes.BuffedMedianDamageGE            => MatchBuffedMedianDamageGE(item, itemId, ctx, d),
            // As floats, like the double keys: 2.65 - 1 is 1.6499999..., and a 385 crossbow must still reach 77.
            VTankNodeTypes.BuffedMissileDamageGE           => (float)BuffedMissileDamage(item, itemId, ctx) >= (float)ReadDouble(d, 0),
            VTankNodeTypes.BuffedLongValKeyGE              => DoubleKey(d) is var (val,key) && BuffedLong(item, itemId, ctx, key) >= val,
            VTankNodeTypes.BuffedDoubleValKeyGE            => DoubleKey(d) is var (val,key) && (float)BuffedDouble(item, itemId, ctx, key) >= (float)val,
            VTankNodeTypes.CalcdBuffedTinkedDamageGE       => (float)BuffedTinkedDamage(item, itemId, ctx) >= (float)ReadDouble(d, 0),
            VTankNodeTypes.TotalRatingsGE                  => MatchTotalRatingsGE(item, d),
            VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE => MatchTinkedTargetMelee(item, itemId, ctx, d, out _),
            VTankNodeTypes.DisabledRule                    => MatchDisabledRule(d),
            _ => throw new InvalidOperationException($"Unsupported VTank loot node type {cond.NodeType}."),
        };
    }

    // ── Matchers needing more than a one-liner ───────────────────────────────

    // NOTE: all three regex matchers go through Meta.RegexCache rather than
    // `new Regex(...)`. ClassifyItemAgainstProfile runs this evaluator against
    // every loot rule for every corpse item on the plugin tick thread, and a
    // loot-snob profile has hundreds of spell-name / string regex rules. Compiling
    // each pattern per match (the old code) cost hundreds of regex *compilations*
    // per item — the dominant cost of "scanning which items to keep", and it
    // stalled the shared tick (combat/nav) while looting. The cache reuses the
    // constructed interpreter instance; match semantics are identical (a bad
    // pattern caches null ⇒ no match, same as the old throw→outer-catch→false).
    private static bool MatchSpellNameMatch(uint itemId, VTankLootContext? ctx, IList<string> d)
    {
        if (ctx is null) return true;
        Regex? rx = Meta.RegexCache.Get(d[0], RegexOptions.IgnoreCase);
        if (rx is null) return false;
        foreach (string n in ctx.GetItemSpellNames(itemId))
            if (!string.IsNullOrEmpty(n) && rx.IsMatch(n)) return true;
        return false;
    }

    private static bool MatchStringValue(WorldObject item, IList<string> d)
    {
        string pattern = d[0];
        int key = ReadInt(d, 1);
        string value = item.Values((StringValueKey)key, string.Empty);
        return Meta.RegexCache.IsMatch(value, pattern, RegexOptions.IgnoreCase);
    }

    private static bool MatchSpellMatch(uint itemId, VTankLootContext? ctx, IList<string> d)
    {
        if (ctx is null) return true;
        string pos = d[0];
        string neg = d[1];
        int count = ReadInt(d, 2);
        Regex? rxPos = Meta.RegexCache.Get(pos, RegexOptions.IgnoreCase);
        if (rxPos is null) return false;
        Regex? rxNeg = string.IsNullOrWhiteSpace(neg)
            ? null
            : Meta.RegexCache.Get(neg, RegexOptions.IgnoreCase);
        int c = 0;
        foreach (string name in ctx.GetItemSpellNames(itemId))
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (rxPos.IsMatch(name) && (rxNeg is null || !rxNeg.IsMatch(name)))
                if (++c >= count) return true;
        }
        return false;
    }

    private static bool MatchMinDamageGE(WorldObject item, IList<string> d)
    {
        double threshold = ReadDouble(d, 0);
        int maxDamage = item.Values(44, 0);   // Damage: the weapon profile's max (54 is WeaponEncumbrance; until 2026-10-07)
        if (maxDamage == 0) return false;
        double variance = item.Values(DoubleValueKey.DamageVariance, 0.0);
        double minDamage = maxDamage - (variance * maxDamage);
        return minDamage >= threshold;
    }

    private static bool MatchSlotExactPalette(uint itemId, VTankLootContext? ctx, IList<string> d)
    {
        int slot = ReadInt(d, 0);
        uint targetPaletteId = (uint)ReadInt(d, 1);
        if (ctx is null) return true;
        var (subIds, _) = ctx.GetItemPalettes(itemId);
        if (subIds.Length == 0) return true;
        if (slot < 0 || slot >= subIds.Length) return false;
        uint actual = subIds[slot];
        return (actual & 0x00FFFFFFu) == (targetPaletteId & 0x00FFFFFFu);
    }

    private static bool MatchCharacterBaseSkill(VTankLootContext? ctx, IList<string> d)
    {
        int skillId = ReadInt(d, 0);
        int min = ReadInt(d, 1);
        int max = ReadInt(d, 2);
        if (ctx is null) return true;
        int baseVal = ctx.GetBaseSkill(unchecked((uint)skillId));
        return baseVal >= min && baseVal <= max;
    }

    private static bool MatchBuffedMedianDamageGE(WorldObject item, uint itemId, VTankLootContext? ctx, IList<string> d)
    {
        double threshold = ReadDouble(d, 0);
        int maxDamage = BuffedLong(item, itemId, ctx, MaxDamageKey);
        if (maxDamage == 0) return false;
        double variance = item.Values(DoubleValueKey.DamageVariance, 0.0);
        double minDamage = maxDamage - (variance * maxDamage);
        return (minDamage + maxDamage) / 2.0 >= threshold;
    }

    // ── Buffed values (VTank's "Buffed ..." and "Calc'd ..." nodes) ─────────────
    //
    // "Buffed" is the item as it will be once wielded: its value plus what its own cantrips add
    // (Legendary Blood Thirst +10 damage, Epic Defender +7% melee defense, ...). Its other item
    // spells (Blood Drinker, Defender VI ...) add nothing: the character casts those anyway.
    // Only the cantrips below change a buffed value (the same table Mag-Tools shows buffed values
    // with); a key with none of them reads as the plain value. Ids are retail's (SpellData.txt).

    /// <summary>Decal's MaxDamage (0x0D000022): the weapon profile's max damage, property 44.</summary>
    internal const int MaxDamageKey = unchecked((int)0x0D000022);
    private const int AttackBonusKey = unchecked((int)0x0A00000C);

    // (spell id, amount added)
    private static readonly (uint Spell, int Add)[] MaxDamageCantrips =
        { (2598, 2), (2586, 4), (4661, 7), (6089, 10) };                                   // Minor..Legendary Blood Thirst
    private static readonly (uint Spell, int Add)[] ArmorLevelCantrips =
        { (2604, 20), (2592, 40), (4667, 60), (6095, 80) };                               // Minor..Legendary Impenetrability
    private static readonly (uint Spell, double Add)[] MeleeDefenseCantrips =
        { (2600, 0.03), (3985, 0.04), (2588, 0.05), (4663, 0.07), (6091, 0.09) };          // Minor Defender, Mukkir Sense, Major..Legendary Defender
    private static readonly (uint Spell, double Add)[] AttackCantrips =
        { (2603, 0.03), (2591, 0.05), (4666, 0.07), (6094, 0.09) };                       // Minor..Legendary Heart Thirst
    private static readonly (uint Spell, double Add)[] ElementalVsMonsterCantrips =
        { (3251, 0.01), (3250, 0.03), (4670, 0.05), (6098, 0.07) };                       // Minor..Legendary Spirit Thirst
    // Hermetic Link cantrips multiply the mana conversion bonus.
    private static readonly (uint Spell, double Mul)[] ManaConversionCantrips =
        { (3201, 1.05), (3199, 1.10), (3202, 1.15), (3200, 1.20), (6086, 1.25), (6087, 1.30) };

    private static uint[] ItemSpells(uint itemId, VTankLootContext? ctx)
        => ctx is null ? Array.Empty<uint>() : ctx.GetItemSpellIds(itemId);

    /// <summary>A long key as VTank's Buffed Long Key reads it: the value plus the item's own cantrips.</summary>
    internal static int BuffedLong(WorldObject item, uint itemId, VTankLootContext? ctx, int key)
    {
        int value = item.Values(key, 0);
        (uint Spell, int Add)[]? table = key switch
        {
            MaxDamageKey or 44 => MaxDamageCantrips,
            28 => ArmorLevelCantrips,
            _ => null,
        };
        if (table is null) return value;
        foreach (uint s in ItemSpells(itemId, ctx))
            foreach (var (spell, add) in table)
                if (s == spell) value += add;
        return value;
    }

    /// <summary>A double key as VTank's Buffed Double Key reads it: the value plus (or, for mana
    /// conversion, times) the item's own cantrips.</summary>
    internal static double BuffedDouble(WorldObject item, uint itemId, VTankLootContext? ctx, int key)
    {
        double value = item.Values(FloatKey(key), 0.0);
        if (key == 144)
        {
            foreach (uint s in ItemSpells(itemId, ctx))
                foreach (var (spell, mul) in ManaConversionCantrips)
                    if (s == spell) value *= mul;
            return value;
        }
        (uint Spell, double Add)[]? table = key switch
        {
            29 => MeleeDefenseCantrips,
            AttackBonusKey or 62 => AttackCantrips,
            152 => ElementalVsMonsterCantrips,
            _ => null,
        };
        if (table is null) return value;
        foreach (uint s in ItemSpells(itemId, ctx))
            foreach (var (spell, add) in table)
                if (s == spell) value += add;
        return value;
    }

    /// <summary>
    /// VTank's "Calc'd Buffed Missile Damage" of a bow, crossbow or atlatl: buffed max damage
    /// (0 on launchers) + damage modifier as a percentage over 1, divided by 3 + the elemental
    /// damage bonus. A 385 crossbow with a 2.65 modifier and +22 is 0 + 165/3 + 22 = 77, LootSnob's
    /// "Xbow (Max 385)"; Legendary Blood Thirst makes it 87 ("OD +10"). -1 for an item with no damage
    /// modifier (not a launcher). This read max damage x the wand's elemental modifier (152) until
    /// 2026-10-31, which is 0 on every launcher, so no missile damage rule ever matched.
    /// </summary>
    internal static double BuffedMissileDamage(WorldObject item, uint itemId, VTankLootContext? ctx)
    {
        double mod = item.Values(DoubleValueKey.DamageMod, 0.0);
        if (mod <= 0) return -1;
        int elemental = item.Values(ElementalDamageBonusKey, 0);
        return BuffedLong(item, itemId, ctx, MaxDamageKey) + (mod - 1.0) * 100.0 / 3.0 + elemental;
    }

    private const int ElementalDamageBonusKey = 204;      // ElementalDamageBonus (Decal ElementalDmgBonus)
    private const int NumTimesTinkeredKey = 171;
    private const int ImbuedEffectKey = 179;
    private const int MaterialTypeKey = 131;

    /// <summary>Damage over time as VTank figures it: 10% crits at double damage.</summary>
    private static double DamageOverTime(double maxDamage, double variance)
        => maxDamage * ((1 - 0.1) * (2 - variance) / 2 + 0.1 * 2);

    /// <summary>Tinks the item can still take: 10 less those done, one kept for an imbue it lacks; none without a material.</summary>
    private static int TinksLeft(WorldObject item)
    {
        if (item.Values(MaterialTypeKey, 0) == 0) return 0;
        int left = Math.Max(0, 10 - item.Values(NumTimesTinkeredKey, 0));
        if (item.Values(ImbuedEffectKey, 0) == 0) left = Math.Max(0, left - 1);
        return left;
    }

    /// <summary>Spends <paramref name="tinks"/> on damage (iron +1 or granite variance x0.8, whichever
    /// helps more) and returns the damage over time with Blood Drinker's 24.</summary>
    private static double TinkedDamage(int maxDamage, double variance, int tinks)
    {
        for (int i = 0; i < tinks; i++)
        {
            double iron = DamageOverTime(maxDamage + 1 + 24, variance);
            double granite = DamageOverTime(maxDamage + 24, variance * 0.8);
            if (iron >= granite) maxDamage++;
            else variance *= 0.8;
        }
        return DamageOverTime(maxDamage + 24, variance);
    }

    /// <summary>VTank's "Calc'd Buffed Tinked Damage": the melee weapon's damage over time with every
    /// tink it has left on damage. -1 with no max damage.</summary>
    internal static double BuffedTinkedDamage(WorldObject item, uint itemId, VTankLootContext? ctx)
    {
        int maxDamage = BuffedLong(item, itemId, ctx, MaxDamageKey);
        if (maxDamage == 0) return -1;
        return TinkedDamage(maxDamage, item.Values(DoubleValueKey.DamageVariance, 0.0), TinksLeft(item));
    }

    /// <summary>
    /// VTank's "Calc'd Buffed Tinked Target Melee" (data: damage over time, melee defense, attack):
    /// can the weapon's tinks left reach the melee defense target (brass, +0.01 each), then the
    /// attack target (velvet, +0.01 each), and with the tinks still left on damage reach the damage
    /// target? Targets are the item's own units (1.17 = +17%). <paramref name="dot"/> is the damage
    /// reached, or -1 when a target is out of reach. This always failed until 2026-10-31.
    /// </summary>
    internal static bool MatchTinkedTargetMelee(WorldObject item, uint itemId, VTankLootContext? ctx, IList<string> d, out double dot)
    {
        dot = -1;
        double targetDot = ReadDouble(d, 0), targetDef = ReadDouble(d, 1), targetAtk = ReadDouble(d, 2);
        int maxDamage = BuffedLong(item, itemId, ctx, MaxDamageKey);
        if (maxDamage == 0) return false;
        int tinks = TinksLeft(item);
        double def = BuffedDouble(item, itemId, ctx, 29);
        while (tinks > 0 && (float)def < (float)targetDef) { def += 0.01; tinks--; }
        if ((float)def < (float)targetDef) return false;
        double atk = BuffedDouble(item, itemId, ctx, AttackBonusKey);
        while (tinks > 0 && (float)atk < (float)targetAtk) { atk += 0.01; tinks--; }
        if ((float)atk < (float)targetAtk) return false;
        dot = TinkedDamage(maxDamage, item.Values(DoubleValueKey.DamageVariance, 0.0), tinks);
        return (float)dot >= (float)targetDot;
    }

    private static bool MatchTotalRatingsGE(WorldObject item, IList<string> d)
    {
        double threshold = ReadDouble(d, 0);
        int total =
            item.Values(370, 0) + item.Values(371, 0) + item.Values(372, 0) + item.Values(373, 0) +
            item.Values(374, 0) + item.Values(375, 0) + item.Values(376, 0) + item.Values(379, 0);
        return total >= threshold;
    }

    private static bool MatchDisabledRule(IList<string> d)
    {
        string raw = d.Count > 0 ? d[0] : "false";
        bool isDisabled = string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return !isDisabled;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int ReadInt(IList<string> d, int idx)
        => int.Parse(d[idx], NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ReadDouble(IList<string> d, int idx)
        => double.Parse(d[idx], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    // (value, key) pair for the LongValKey* family.
    private static (int Val, int Key) Long(IList<string> d) => (ReadInt(d, 0), ReadInt(d, 1));
    private static (double Val, int Key) Double(IList<string> d) => (ReadDouble(d, 0), ReadInt(d, 1));
    // Buffed long/double helpers store value as double in slot 0 (matching VTank's "Read" of doubles).
    private static (double Val, int Key) DoubleKey(IList<string> d) => (ReadDouble(d, 0), ReadInt(d, 1));

    // A double key as the STypeFloat it reads. VTank files the appraisal's armor and weapon
    // profiles under Decal's 0x0A0000xx keys (uTank2 LootPlugins.DoubleValueKey), which no
    // object property has: read raw they were always 0, so LootSnob's "AttackBonus >=" rules
    // never matched (until 2026-10-05). Range (0x0A00000D) and the physics keys stay unmapped.
    private static DoubleValueKey FloatKey(int key) => unchecked((uint)key) switch
    {
        0x0A000000 => (DoubleValueKey)13,   // SlashProt     → ArmorModVsSlash
        0x0A000001 => (DoubleValueKey)14,   // PierceProt    → ArmorModVsPierce
        0x0A000002 => (DoubleValueKey)15,   // BludgeonProt  → ArmorModVsBludgeon
        0x0A000003 => (DoubleValueKey)18,   // AcidProt      → ArmorModVsAcid
        0x0A000004 => (DoubleValueKey)19,   // LightningProt → ArmorModVsElectric
        0x0A000005 => (DoubleValueKey)17,   // FireProt      → ArmorModVsFire
        0x0A000006 => (DoubleValueKey)16,   // ColdProt      → ArmorModVsCold
        0x0A000009 => (DoubleValueKey)280,  // SalvageWorkmanship → the engine's workmanship read (as wobjectgetdoubleprop)
        0x0A00000B => DoubleValueKey.DamageVariance,   // Variance
        0x0A00000C => DoubleValueKey.WeaponOffense,    // AttackBonus
        0x0A00000E => DoubleValueKey.DamageMod,        // DamageBonus
        _ => (DoubleValueKey)key,
    };
}
