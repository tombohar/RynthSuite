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

    private static bool Match(VTankLootCondition cond, WorldObject item, uint itemId, VTankLootContext? ctx)
    {
        var d = cond.DataLines;
        return cond.NodeType switch
        {
            VTankNodeTypes.SpellNameMatch                  => MatchSpellNameMatch(itemId, ctx, d),
            VTankNodeTypes.StringValueMatch                => MatchStringValue(item, d),
            VTankNodeTypes.LongValKeyLE                    => Long(d) is var (val,key) && item.Values((LongValueKey)key, 0) <= val,
            VTankNodeTypes.LongValKeyGE                    => Long(d) is var (val,key) && item.Values((LongValueKey)key, 0) >= val,
            VTankNodeTypes.DoubleValKeyLE                  => Double(d) is var (val,key) && item.Values((DoubleValueKey)key, 0.0) <= val,
            VTankNodeTypes.DoubleValKeyGE                  => Double(d) is var (val,key) && item.Values((DoubleValueKey)key, 0.0) >= val,
            VTankNodeTypes.DamagePercentGE                 => false, // VTank source returns false unconditionally
            VTankNodeTypes.ObjectClass                     => (int)item.ObjectClass == ReadInt(d, 0),
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
            VTankNodeTypes.BuffedMedianDamageGE            => MatchBuffedMedianDamageGE(item, d),
            VTankNodeTypes.BuffedMissileDamageGE           => MatchBuffedMissileDamageGE(item, d),
            VTankNodeTypes.BuffedLongValKeyGE              => DoubleKey(d) is var (val,key) && item.Values((LongValueKey)key, 0) >= val,
            VTankNodeTypes.BuffedDoubleValKeyGE            => DoubleKey(d) is var (val,key) && item.Values((DoubleValueKey)key, 0.0) >= val,
            VTankNodeTypes.CalcdBuffedTinkedDamageGE       => item.Values(54, 0) >= ReadDouble(d, 0),
            VTankNodeTypes.TotalRatingsGE                  => MatchTotalRatingsGE(item, d),
            VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE => false, // not implemented
            VTankNodeTypes.DisabledRule                    => MatchDisabledRule(d),
            _ => throw new InvalidOperationException($"Unsupported VTank loot node type {cond.NodeType}."),
        };
    }

    // ── Matchers needing more than a one-liner ───────────────────────────────

    private static bool MatchSpellNameMatch(uint itemId, VTankLootContext? ctx, IList<string> d)
    {
        if (ctx is null) return true;
        Regex rx = new(d[0], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (string n in ctx.GetItemSpellNames(itemId))
            if (!string.IsNullOrEmpty(n) && rx.IsMatch(n)) return true;
        return false;
    }

    private static bool MatchStringValue(WorldObject item, IList<string> d)
    {
        string pattern = d[0];
        int key = ReadInt(d, 1);
        string value = item.Values((StringValueKey)key, string.Empty);
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool MatchSpellMatch(uint itemId, VTankLootContext? ctx, IList<string> d)
    {
        if (ctx is null) return true;
        string pos = d[0];
        string neg = d[1];
        int count = ReadInt(d, 2);
        Regex rxPos = new(pos, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Regex? rxNeg = string.IsNullOrWhiteSpace(neg)
            ? null
            : new Regex(neg, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        int maxDamage = item.Values(54, 0);
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
        var (_, baseVal) = ctx.GetSkill(unchecked((uint)skillId));
        return baseVal >= min && baseVal <= max;
    }

    private static bool MatchBuffedMedianDamageGE(WorldObject item, IList<string> d)
    {
        double threshold = ReadDouble(d, 0);
        int maxDamage = item.Values(54, 0);
        if (maxDamage == 0) return false;
        double variance = item.Values(DoubleValueKey.DamageVariance, 0.0);
        double minDamage = maxDamage - (variance * maxDamage);
        return (minDamage + maxDamage) / 2.0 >= threshold;
    }

    private static bool MatchBuffedMissileDamageGE(WorldObject item, IList<string> d)
    {
        double threshold = ReadDouble(d, 0);
        int maxDamage = item.Values(54, 0);
        if (maxDamage == 0) return false;
        double elemBonus = item.Values((DoubleValueKey)152, 1.0);
        if (elemBonus < 1.0) elemBonus = 1.0;
        return maxDamage * elemBonus >= threshold;
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
}
