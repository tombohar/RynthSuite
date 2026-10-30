using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>game.Character</c> (UtilityBelt's shape): who you are, vitals, skills, attributes,
/// burden, combat mode, inventory and equipment, enchantments and the spellbook, plus the
/// character events. Values are read live through the SDK on every access (an
/// <c>__index</c> metatable); events the engine doesn't raise are found by polling in
/// <see cref="CharacterTick"/> (vitals and portal space every tick, the rest every 500 ms and
/// only while a script listens).
///
/// Enum-valued fields are numbers, so they compare with the enum globals the way UB scripts
/// do (<c>game.Character.CombatMode == CombatMode.Melee</c>), with a name twin
/// (<c>CombatModeName</c>, <c>TrainingName</c>, <c>TypeName</c>); arguments take either a
/// number (LuaEnums numbering) or a name. Everything here runs on the plugin pump thread.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    // ── AC property ids (STypes) ────────────────────────────────────────────
    private const uint ChIntEncumbrance = 5;          // EncumbranceVal: burden units carried
    private const uint ChIntStackSize = 12;
    private const uint ChIntLevel = 25;
    private const uint ChIntAugCarrying = 230;        // AugmentationIncreasedCarryingCapacity
    private const uint ChInt64TotalXp = 1;
    private const uint ChInt64UnassignedXp = 2;
    private const uint ChInt64AvailableLum = 6;
    private const uint ChInt64MaxLum = 7;
    private const uint ChItemTypeContainer = 0x200;

    private static readonly string[] CharacterEventNames =
    {
        "OnVitalChanged", "OnPortalSpaceEntered", "OnPortalSpaceExited", "OnDeath",
        "OnCombatModeChanged", "OnEnchantmentsChanged", "OnLevelChanged", "OnVitaeChanged",
        "OnTotalExperienceChanged", "OnAvailableLuminanceChanged",
    };

    private static readonly string[] TrainingNames = { "Unusable", "Untrained", "Trained", "Specialized" };
    private static string TrainingName(int t) => t >= 0 && t < TrainingNames.Length ? TrainingNames[t] : "Unusable";

    private static readonly Dictionary<int, string> SkillNameById = Reverse(LuaEnums.SkillId);
    private static readonly Dictionary<int, string> AttributeNameById = Reverse(LuaEnums.AttributeId);
    private static readonly Dictionary<int, string> VitalNameById = Reverse(LuaEnums.VitalId);

    private static Dictionary<int, string> Reverse(Dictionary<string, int> map)
    {
        var r = new Dictionary<int, string>();
        foreach (var kv in map) r[kv.Value] = kv.Key;
        return r;
    }

    // ── Registration ────────────────────────────────────────────────────────

    partial void RegisterCharacterApi(Script s, ScriptContext ctx, Table game)
    {
        var ch = new Table(s);
        foreach (string e in CharacterEventNames)
            ch[e] = ctx.Event("Character." + e).CreateTable(s);

        // Vitals / Skills / Attributes: live objects, made once per script.
        Table vitals = MakeVitals(s);
        Table skills = MakeStatTable(s, "Skills", LuaEnums.SkillId, SkillNameById, MakeSkillMeta(s));
        Table attributes = MakeStatTable(s, "Attributes", LuaEnums.AttributeId, AttributeNameById, MakeAttributeMeta(s));
        ch["Vitals"] = DynValue.NewTable(vitals);
        ch["Skills"] = DynValue.NewTable(skills);
        ch["Attributes"] = DynValue.NewTable(attributes);
        // The same objects on game.Character.Weenie (UB keeps the stats there).
        s.Registry.Set("rynth.character.Vitals", DynValue.NewTable(vitals));
        s.Registry.Set("rynth.character.Skills", DynValue.NewTable(skills));
        s.Registry.Set("rynth.character.Attributes", DynValue.NewTable(attributes));

        ch["Skill"] = DynValue.NewCallback((c, a) =>
        {
            int id = ResolveStat(LuaArgs.At(LuaArgs.Of(a, ch), 0), LuaEnums.SkillId, SkillNameById, "Skill", "\"WarMagic\" or SkillId.WarMagic");
            return skills.Get(id);
        });
        ch["Attribute"] = DynValue.NewCallback((c, a) =>
        {
            int id = ResolveStat(LuaArgs.At(LuaArgs.Of(a, ch), 0), LuaEnums.AttributeId, AttributeNameById, "Attribute", "\"Strength\" or AttributeId.Strength");
            return attributes.Get(id);
        });
        ch["Vital"] = DynValue.NewCallback((c, a) =>
        {
            int id = ResolveStat(LuaArgs.At(LuaArgs.Of(a, ch), 0), LuaEnums.VitalId, VitalNameById, "Vital", "\"Health\" or VitalId.Health");
            return vitals.Get(VitalNameById[id]);
        });

        // Enchantments and the spellbook.
        DynValue enchantments = DynValue.NewCallback((c, a) => ChEnchantmentList(s));
        ch["Enchantments"] = enchantments;
        // UB's names: every enchantment the client knows is active here (no layered/inactive split).
        ch["ActiveEnchantments"] = enchantments;
        ch["AllEnchantments"] = enchantments;
        ch["HasEnchantment"] = DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(ChHasEnchantment(LuaArgs.At(LuaArgs.Of(a, ch), 0))));
        ch["KnowsSpell"] = DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(ChKnowsSpell(ChSpellArg(LuaArgs.At(LuaArgs.Of(a, ch), 0), "KnowsSpell"))));
        ch["IsBusy"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(ChBusy()));
        ch["SpellBook"] = MakeSpellBook(s);

        ch["Trade"] = CreateTradeTable(s, ctx);

        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
            ChProperty(s, ctx, a.Count > 1 ? a[1].CastToString() ?? "" : ""));
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
        {
            uint pid = Host.GetPlayerId();
            if (pid == 0) return DynValue.NewString("Character (not logged in)");
            Host.TryGetObjectName(pid, out string name);
            return DynValue.NewString($"Character {name} (0x{pid:X8})");
        });
        ch.MetaTable = mt;

        // Inventory / Equipment / Containers (UB: list properties; still callable with a
        // filter), GetInventory / GetFirstInventory / GetInventoryCount, InventoryCount: Lua
        // glue over a few raw helpers, so filter functions run as plain Lua (they may even sleep).
        InstallInventoryGlue(s, ctx, ch, game);
        game["Character"] = DynValue.NewTable(ch);
    }

    /// <summary>game.Character.&lt;key&gt; for everything that isn't a raw field.</summary>
    private DynValue ChProperty(Script s, ScriptContext ctx, string key)
    {
        uint pid = Host.GetPlayerId();
        switch (key)
        {
            case "Id": return DynValue.NewNumber(pid);
            case "Name": return DynValue.NewString(pid != 0 && Host.TryGetObjectName(pid, out string n) ? n : string.Empty);
            case "Weenie": return WrapObject(s, ctx, pid);
            case "Level": return DynValue.NewNumber(ChInt(ChIntLevel));
            case "CombatMode": return DynValue.NewNumber(Host.GetCurrentCombatMode());
            case "CombatModeName": return DynValue.NewString(LuaEnums.CombatModeName(Host.GetCurrentCombatMode()));
            case "InPortalSpace": return DynValue.NewBoolean(Host.HasIsPortaling && Host.IsPortaling());
            case "Vitae": return DynValue.NewNumber(pid != 0 && Host.HasGetVitae ? Host.GetVitae(pid) : 1.0);
            case "Busy": return DynValue.NewBoolean(ChBusy());
            case "Health": return DynValue.NewNumber(ChVitalCurrent(1));
            case "Stamina": return DynValue.NewNumber(ChVitalCurrent(3));
            case "Mana": return DynValue.NewNumber(ChVitalCurrent(5));
            case "MaxHealth": return DynValue.NewNumber(ChVitalMax(1));
            case "MaxStamina": return DynValue.NewNumber(ChVitalMax(3));
            case "MaxMana": return DynValue.NewNumber(ChVitalMax(5));
            case "BurdenUnits": return DynValue.NewNumber(ChInt(ChIntEncumbrance));
            case "MaxBurdenUnits": return DynValue.NewNumber(ChBurdenCapacity());
            case "Burden":
            {
                long cap = ChBurdenCapacity();
                return DynValue.NewNumber(cap > 0 ? ChInt(ChIntEncumbrance) * 100.0 / cap : 0);
            }
            case "TotalExperience": return DynValue.NewNumber(ChInt64(ChInt64TotalXp));
            case "UnassignedExperience": return DynValue.NewNumber(ChInt64(ChInt64UnassignedXp));
            case "AvailableLuminance": return DynValue.NewNumber(ChInt64(ChInt64AvailableLum));
            case "MaximumLuminance": return DynValue.NewNumber(ChInt64(ChInt64MaxLum));
            default: return DynValue.Nil;
        }
    }

    // ── SDK reads ───────────────────────────────────────────────────────────

    private int ChInt(uint stype)
    {
        uint pid = Host.GetPlayerId();
        return pid != 0 && Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(pid, stype, out int v) ? v : 0;
    }

    private long ChInt64(uint stype)
    {
        uint pid = Host.GetPlayerId();
        return pid != 0 && Host.HasGetObjectQuadProperty && Host.TryGetObjectQuadProperty(pid, stype, out long v) ? v : 0;
    }

    private bool ChBusy() => Host.GetBusyState() != 0 || Host.GetCastBusyState() != 0;

    /// <summary>AC's carrying capacity: 150 per point of Strength, +30 per point per capacity augmentation.</summary>
    private long ChBurdenCapacity()
    {
        uint str = ChAttribute(1, raw: false);
        if (str == 0) return 0;
        int augs = Math.Max(0, ChInt(ChIntAugCarrying));
        return str * (150L + 30L * augs);
    }

    private bool ChReadVitals(out uint hp, out uint maxHp, out uint st, out uint maxSt, out uint mana, out uint maxMana)
    {
        hp = maxHp = st = maxSt = mana = maxMana = 0;
        return Host.HasGetPlayerVitals && Host.TryGetPlayerVitals(out hp, out maxHp, out st, out maxSt, out mana, out maxMana);
    }

    private uint ChVitalCurrent(int vital)
    {
        if (!ChReadVitals(out uint hp, out _, out uint st, out _, out uint mana, out _)) return 0;
        return vital switch { 1 => hp, 3 => st, _ => mana };
    }

    /// <summary>Buffed maximum (InqAttribute2nd, live), else the vitals snapshot's max.</summary>
    private uint ChVitalMax(int vital)
    {
        uint pid = Host.GetPlayerId();
        uint stype2nd = vital switch { 1 => 1u, 3 => 3u, _ => 5u };   // VitalId is AC's max-vital id
        if (pid != 0 && Host.HasGetObjectAttribute2ndBaseLevel && Host.TryGetObjectAttribute2ndBaseLevel(pid, stype2nd, out uint max) && max > 0)
            return max;
        if (!ChReadVitals(out _, out uint maxHp, out _, out uint maxSt, out _, out uint maxMana)) return 0;
        return vital switch { 1 => maxHp, 3 => maxSt, _ => maxMana };
    }

    /// <summary>Unbuffed maximum (from attributes and raised points, no enchantments).</summary>
    private uint ChVitalBase(int vital)
    {
        if (!Host.HasGetPlayerBaseVitals || !Host.TryGetPlayerBaseVitals(out uint hp, out uint st, out uint mana)) return 0;
        return vital switch { 1 => hp, 3 => st, _ => mana };
    }

    private int ChSkillCurrent(int skill)
    {
        uint pid = Host.GetPlayerId();
        if (pid == 0) return 0;
        if (Host.HasGetObjectSkillBuffed && Host.TryGetObjectSkillLevel(pid, (uint)skill, 0, out int level)) return level;
        return Host.HasGetObjectSkill && Host.TryGetObjectSkill(pid, (uint)skill, out int buffed, out _) ? buffed : 0;
    }

    private int ChSkillBase(int skill)
    {
        uint pid = Host.GetPlayerId();
        return pid != 0 && Host.HasGetObjectSkillBuffed && Host.TryGetObjectSkillLevel(pid, (uint)skill, 1, out int level) ? level : 0;
    }

    private int ChSkillTraining(int skill)
    {
        uint pid = Host.GetPlayerId();
        return pid != 0 && Host.HasGetObjectSkill && Host.TryGetObjectSkill(pid, (uint)skill, out _, out int training) ? training : 0;
    }

    private uint ChAttribute(int attribute, bool raw)
    {
        uint pid = Host.GetPlayerId();
        return pid != 0 && Host.HasGetObjectAttribute && Host.TryGetObjectAttribute(pid, (uint)attribute, raw ? 1 : 0, out uint v) ? v : 0;
    }

    // ── Vitals / Skills / Attributes objects ────────────────────────────────

    /// <summary>A number (LuaEnums id) or a name ("WarMagic", "war magic") → id; anything else is an error.</summary>
    private static int ResolveStat(DynValue v, Dictionary<string, int> byName, Dictionary<int, string> byId, string what, string example)
    {
        if (v.Type == DataType.Number && byId.ContainsKey((int)v.Number)) return (int)v.Number;
        if (v.Type == DataType.String && byName.TryGetValue(v.String.Replace(" ", ""), out int id)) return id;
        throw new ScriptRuntimeException($"{what}({(v.IsNil() ? "" : v.ToPrintString())}): unknown {what.ToLowerInvariant()}; use a name or number, e.g. {example}");
    }

    private static int StatId(CallbackArguments a) =>
        a.Count > 0 && a[0].Type == DataType.Table && a[0].Table.Get("Id").Type == DataType.Number ? (int)a[0].Table.Get("Id").Number : 0;

    private static string KeyOf(CallbackArguments a) => a.Count > 1 ? a[1].CastToString() ?? "" : "";

    /// <summary>A table holding one live object per id (numeric keys, so pairs() lists them); names resolve through __index.</summary>
    private static Table MakeStatTable(Script s, string label, Dictionary<string, int> byName, Dictionary<int, string> byId, Table objMeta)
    {
        var t = new Table(s);
        foreach (var kv in byId)
        {
            var o = new Table(s);
            o["Id"] = (double)kv.Key;
            o["Type"] = (double)kv.Key;   // UB's name for the id
            o["Name"] = kv.Value;
            o.MetaTable = objMeta;
            t.Set(kv.Key, DynValue.NewTable(o));
        }
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            DynValue k = a.Count > 1 ? a[1] : DynValue.Nil;
            if (k.Type == DataType.String && byName.TryGetValue(k.String.Replace(" ", ""), out int id)) return t.Get(id);
            return DynValue.Nil;
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString(label));
        t.MetaTable = mt;
        return t;
    }

    private Table MakeSkillMeta(Script s)
    {
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            int id = StatId(a);
            return KeyOf(a) switch
            {
                "Current"    => DynValue.NewNumber(ChSkillCurrent(id)),
                "Base"       => DynValue.NewNumber(ChSkillBase(id)),
                "Training"     => DynValue.NewNumber(ChSkillTraining(id)),
                "TrainingName" => DynValue.NewString(TrainingName(ChSkillTraining(id))),
                "IsTrained"  => DynValue.NewBoolean(ChSkillTraining(id) >= 2),
                _            => DynValue.Nil,
            };
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
        {
            int id = StatId(a);
            string name = SkillNameById.TryGetValue(id, out string? n) ? n : id.ToString();
            return DynValue.NewString($"Skill {name}: {ChSkillCurrent(id)} (base {ChSkillBase(id)}, {TrainingName(ChSkillTraining(id))})");
        });
        return mt;
    }

    private Table MakeAttributeMeta(Script s)
    {
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            int id = StatId(a);
            return KeyOf(a) switch
            {
                "Current" => DynValue.NewNumber(ChAttribute(id, raw: false)),
                "Base"    => DynValue.NewNumber(ChAttribute(id, raw: true)),
                _         => DynValue.Nil,
            };
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
        {
            int id = StatId(a);
            string name = AttributeNameById.TryGetValue(id, out string? n) ? n : id.ToString();
            return DynValue.NewString($"Attribute {name}: {ChAttribute(id, false)} (base {ChAttribute(id, true)})");
        });
        return mt;
    }

    /// <summary>Vitals.Health / .Stamina / .Mana (raw fields), Vitals[VitalId] through __index.</summary>
    private Table MakeVitals(Script s)
    {
        var objMeta = new Table(s);
        objMeta["__index"] = DynValue.NewCallback((c, a) =>
        {
            int id = StatId(a);
            return KeyOf(a) switch
            {
                "Current" => DynValue.NewNumber(ChVitalCurrent(id)),
                "Max"     => DynValue.NewNumber(ChVitalMax(id)),
                "Base"    => DynValue.NewNumber(ChVitalBase(id)),
                _         => DynValue.Nil,
            };
        });
        objMeta["__tostring"] = DynValue.NewCallback((c, a) =>
        {
            int id = StatId(a);
            return DynValue.NewString($"{VitalNameById[id]} {ChVitalCurrent(id)}/{ChVitalMax(id)}");
        });

        var t = new Table(s);
        foreach (var kv in VitalNameById)
        {
            var o = new Table(s);
            o["Id"] = (double)kv.Key;
            o["Type"] = (double)kv.Key;   // UB's name for the id
            o["Name"] = kv.Value;
            o.MetaTable = objMeta;
            t[kv.Value] = DynValue.NewTable(o);
        }
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            DynValue k = a.Count > 1 ? a[1] : DynValue.Nil;
            if (k.Type == DataType.Number && VitalNameById.TryGetValue((int)k.Number, out string? name)) return t.Get(name);
            if (k.Type == DataType.String && LuaEnums.VitalId.TryGetValue(k.String, out int id)) return t.Get(VitalNameById[id]);
            return DynValue.Nil;
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString(
            $"Vitals: Health {ChVitalCurrent(1)}/{ChVitalMax(1)}, Stamina {ChVitalCurrent(3)}/{ChVitalMax(3)}, Mana {ChVitalCurrent(5)}/{ChVitalMax(5)}"));
        t.MetaTable = mt;
        return t;
    }

    // ── Spellbook ───────────────────────────────────────────────────────────

    private readonly HashSet<uint> _chKnownSpells = new();
    private long _chKnownSpellsAt;
    private readonly Dictionary<uint, string?> _chSpellNames = new();

    private static uint ChSpellArg(DynValue v, string what)
    {
        if (v.Type == DataType.Number && v.Number > 0) return (uint)v.Number & 0xFFFF;
        throw new ScriptRuntimeException($"{what}(spellId) needs a spell id (a number)");
    }

    /// <summary>
    /// The spellbook snapshot the engine reads on AC's main thread (ReadKnownSpells), refreshed
    /// at most every 3 s (1 s while it is still empty). The per-spell IsSpellKnown call is not
    /// used: it answers "known" for spells the character doesn't have.
    /// </summary>
    private void ChRefreshKnownSpells()
    {
        long now = Environment.TickCount64;
        if (now - _chKnownSpellsAt < (_chKnownSpells.Count > 0 ? 3000 : 1000)) return;
        _chKnownSpellsAt = now;
        if (!Host.HasReadKnownSpells || Host.GetPlayerId() == 0) return;
        int cap = 4096, n;
        uint[] buf;
        while (true)
        {
            buf = new uint[cap];
            n = Host.ReadKnownSpells(buf, cap);
            if (n < cap || cap >= 65536) break;   // a full buffer means the list was cut short
            cap *= 2;
        }
        if (n <= 0) return;                         // cold or unavailable: keep what we had
        _chKnownSpells.Clear();
        for (int i = 0; i < Math.Min(n, buf.Length); i++)
            if (buf[i] != 0) _chKnownSpells.Add(buf[i]);
    }

    /// <summary>For game.Actions' CastSpell check: null while the spellbook snapshot is still empty.</summary>
    internal bool? KnowsSpellIfLoaded(uint spellId)
    {
        ChRefreshKnownSpells();
        return _chKnownSpells.Count == 0 ? null : _chKnownSpells.Contains(spellId);
    }

    private bool ChKnowsSpell(uint spellId)
    {
        ChRefreshKnownSpells();
        return _chKnownSpells.Contains(spellId);
    }

    /// <summary>Spell name from RynthAi's spell table (null without RynthAi or for unknown ids).</summary>
    private string? ChSpellName(uint spellId)
    {
        if (_chSpellNames.TryGetValue(spellId, out string? cached)) return cached;
        if (!_ai.Available) return null;   // not cached: RynthAi may load later
        string? name = null;
        if (_ai.Evaluate($"spellname[{spellId}]", out string r) && r.Length > 0
            && !r.StartsWith("Unknown Spell", StringComparison.OrdinalIgnoreCase))
            name = r;
        _chSpellNames[spellId] = name;
        return name;
    }

    private DynValue MakeSpellBook(Script s)
    {
        var sb = new Table(s);
        sb["IsKnown"] = DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(ChKnowsSpell(ChSpellArg(LuaArgs.At(LuaArgs.Of(a, sb), 0), "SpellBook.IsKnown"))));
        sb["KnownSpellIds"] = DynValue.NewCallback((c, a) =>
        {
            ChRefreshKnownSpells();
            var ids = new List<uint>(_chKnownSpells);
            ids.Sort();
            var t = new Table(s);
            for (int i = 0; i < ids.Count; i++) t.Set(i + 1, DynValue.NewNumber(ids[i]));
            return DynValue.NewTable(t);
        });
        sb["Name"] = DynValue.NewCallback((c, a) =>
        {
            string? name = ChSpellName(ChSpellArg(LuaArgs.At(LuaArgs.Of(a, sb), 0), "SpellBook.Name"));
            return name == null ? DynValue.Nil : DynValue.NewString(name);
        });
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            switch (KeyOf(a))
            {
                case "Count":
                    ChRefreshKnownSpells();
                    return DynValue.NewNumber(_chKnownSpells.Count);
                case "KnownSpellsIds":   // UB's property (sorted list)
                {
                    ChRefreshKnownSpells();
                    var ids = new List<uint>(_chKnownSpells);
                    ids.Sort();
                    return IdList(s, ids);
                }
                default:
                    return DynValue.Nil;
            }
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString($"SpellBook ({_chKnownSpells.Count} spells)"));
        sb.MetaTable = mt;
        return DynValue.NewTable(sb);
    }

    // ── Enchantments ────────────────────────────────────────────────────────

    private const int ChMaxEnchantments = 512;
    private readonly uint[] _chEnchIds = new uint[ChMaxEnchantments];
    private readonly double[] _chEnchExp = new double[ChMaxEnchantments];
    private readonly List<(uint SpellId, double Expiry)> _chEnchLastGood = new();
    /// <summary>Durations seen in OnEnchantmentAdded (the registry read only has expiry times).</summary>
    private readonly Dictionary<uint, double> _chEnchDuration = new();

    /// <summary>
    /// The player's enchantments (spell id, expiry in server seconds; double.MaxValue for
    /// permanent ones). The engine refuses a read that races AC's own update (-1); then the
    /// last good read is used.
    /// </summary>
    private List<(uint SpellId, double Expiry)> ChReadEnchantments(out bool fresh)
    {
        fresh = false;
        if (!Host.HasReadPlayerEnchantments || Host.GetPlayerId() == 0) return _chEnchLastGood;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int n = Host.ReadPlayerEnchantments(_chEnchIds, _chEnchExp, ChMaxEnchantments);
            if (n < 0) continue;
            _chEnchLastGood.Clear();
            for (int i = 0; i < Math.Min(n, ChMaxEnchantments); i++)
                _chEnchLastGood.Add((_chEnchIds[i], _chEnchExp[i]));
            fresh = true;
            break;
        }
        return _chEnchLastGood;
    }

    private static bool ChPermanent(double expiry) => expiry >= double.MaxValue / 2;

    /// <summary>{SpellId, Name, Remaining, ExpiresAt, Duration, Permanent} for one enchantment.</summary>
    private DynValue ChEnchantmentTable(Script s, uint spellId, double expiry, double serverNow, double unixNow)
    {
        var t = new Table(s);
        t["SpellId"] = (double)spellId;
        string? name = ChSpellName(spellId);
        if (name != null) t["Name"] = name;
        bool permanent = ChPermanent(expiry);
        t["Permanent"] = permanent;
        if (permanent)
        {
            t["Remaining"] = double.PositiveInfinity;
            t["ExpiresAt"] = double.PositiveInfinity;
            t["Duration"] = -1.0;
        }
        else
        {
            if (serverNow > 0)
            {
                double remaining = Math.Max(0, expiry - serverNow);
                t["Remaining"] = remaining;
                t["ExpiresAt"] = unixNow + remaining;
            }
            if (_chEnchDuration.TryGetValue(spellId, out double d) && d > 0) t["Duration"] = d;
        }
        return DynValue.NewTable(t);
    }

    private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private DynValue ChEnchantmentList(Script s)
    {
        var list = ChReadEnchantments(out _);
        double serverNow = Host.HasGetServerTime ? Host.GetServerTime() : 0;
        double unixNow = UnixNow();
        var t = new Table(s);
        int i = 1;
        foreach (var (id, exp) in list)
            t.Set(i++, ChEnchantmentTable(s, id, exp, serverNow, unixNow));
        return LuaLists.Wrap(s, t);
    }

    /// <summary>HasEnchantment(spellId) or HasEnchantment("Spell Name") (names need RynthAi).</summary>
    private bool ChHasEnchantment(DynValue v)
    {
        if (v.Type == DataType.Number)
        {
            uint id = (uint)v.Number & 0xFFFF;
            foreach (var e in ChReadEnchantments(out _)) if (e.SpellId == id) return true;
            return false;
        }
        if (v.Type == DataType.String)
        {
            foreach (var e in ChReadEnchantments(out _))
                if (string.Equals(ChSpellName(e.SpellId), v.String, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        throw new ScriptRuntimeException("HasEnchantment(spellId) needs a spell id (or a spell name)");
    }

    // ── Inventory / Equipment ───────────────────────────────────────────────

    private readonly uint[] _chIdBuf = new uint[512];

    /// <summary>What the character carries in packs: main-pack items, side packs, and the side packs' contents.</summary>
    private List<uint> ChPackIds(bool containersOnly)
    {
        var result = new List<uint>();
        uint pid = Host.GetPlayerId();
        if (pid == 0 || !Host.HasGetContainerContents) return result;
        // Warms the engine's physics-object offset probe that container reads rely on (as RynthAi does).
        Host.TryGetObjectPosition(pid, out _, out _, out _, out _);
        int n = Host.GetContainerContents(pid, _chIdBuf);
        var packs = new List<uint>();
        for (int i = 0; i < n; i++)
        {
            uint id = _chIdBuf[i];
            bool isPack = Host.TryGetItemType(id, out uint flags) && (flags & ChItemTypeContainer) != 0;
            if (isPack) packs.Add(id);
            if (!containersOnly || isPack) result.Add(id);
        }
        if (containersOnly) return result;
        foreach (uint pack in packs)
        {
            int m = Host.GetContainerContents(pack, _chIdBuf);
            for (int i = 0; i < m; i++) result.Add(_chIdBuf[i]);
        }
        return result;
    }

    private uint ChWielderOf(uint id)
    {
        if (Host.HasGetObjectWielderInfo && Host.TryGetObjectWielderInfo(id, out uint w, out _) && w != 0) return w;
        if (Host.HasGetObjectOwnershipInfo && Host.TryGetObjectOwnershipInfo(id, out _, out uint w2, out _)) return w2;
        return 0;
    }

    private static DynValue IdArray(Script s, List<uint> ids)
    {
        var t = new Table(s);
        for (int i = 0; i < ids.Count; i++) t.Set(i + 1, DynValue.NewNumber(ids[i]));
        return DynValue.NewTable(t);
    }

    /// <summary>
    /// Inventory([filter]), Equipment([filter]), Containers([filter]), InventoryCount(nameOrFilter).
    /// Packs come from the engine's container lists; worn/wielded items aren't in those, so
    /// they come from game.World's object list (every object whose wielder is you).
    /// </summary>
    private void InstallInventoryGlue(Script s, ScriptContext ctx, Table ch, Table game)
    {
        DynValue packIds = DynValue.NewCallback((c, a) => IdArray(s, ChPackIds(containersOnly: false)));
        DynValue containerIds = DynValue.NewCallback((c, a) => IdArray(s, ChPackIds(containersOnly: true)));
        DynValue wielderOf = DynValue.NewCallback((c, a) =>
            DynValue.NewNumber(ChWielderOf(LuaArgs.Id(a.Count > 0 ? a[0] : DynValue.Nil))));
        DynValue nameOf = DynValue.NewCallback((c, a) =>
        {
            uint id = LuaArgs.Id(a.Count > 0 ? a[0] : DynValue.Nil);
            return id != 0 && Host.TryGetObjectName(id, out string n) ? DynValue.NewString(n) : DynValue.Nil;
        });
        DynValue stackOf = DynValue.NewCallback((c, a) =>
        {
            uint id = LuaArgs.Id(a.Count > 0 ? a[0] : DynValue.Nil);
            int n = id != 0 && Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(id, ChIntStackSize, out int v) ? v : 0;
            return DynValue.NewNumber(n > 0 ? n : 1);
        });
        DynValue wrap = DynValue.NewCallback((c, a) => WrapObject(s, ctx, LuaArgs.Id(a.Count > 0 ? a[0] : DynValue.Nil)));
        DynValue wieldedIds = DynValue.NewCallback((c, a) => IdArray(s, WorldWieldedIds(Host.GetPlayerId())));
        var classNames = new Table(s);
        for (int i = 0; i < LuaEnums.ObjectClass.Length; i++) classNames.Set(DynValue.NewNumber(i), DynValue.NewString(LuaEnums.ObjectClass[i]));

        DynValue install = s.LoadString(InventoryGlue, null, "game.Character");
        s.Call(install, DynValue.NewTable(ch), DynValue.NewTable(game), packIds, containerIds, wielderOf,
            nameOf, stackOf, wrap, DynValue.NewTable(classNames), wieldedIds, LuaLists.MakeFunction(s));
    }

    private const string InventoryGlue = """
local C, G, packIds, containerIds, wielderOf, nameOf, stackOf, wrap, classNames, wieldedIds, makeList = ...

-- Exact ObjectClass names ("Portal", "Monster") filter by class, as game.World does.
local classByName = {}
for i, n in pairs(classNames) do classByName[n] = i end

-- A filter is a name substring, an ObjectClass value or function(wo) -> bool.
-- Returns how to test ('id': by object id, cheap; 'wo': on the object) and the test.
local function filterOf(f, what)
  if f == nil then return nil, nil end
  local t = type(f)
  if t == 'string' and classByName[f] ~= nil then
    f, t = classByName[f], 'number'
  end
  if t == 'string' then
    local needle = string.lower(f)
    return 'id', function(id)
      local n = nameOf(id)
      return n ~= nil and string.find(string.lower(n), needle, 1, true) ~= nil
    end
  elseif t == 'number' then
    return 'wo', function(wo)
      local oc = wo.ObjectClass
      if type(oc) == 'string' then return oc == classNames[f] end
      return oc == f
    end
  elseif t == 'function' then
    return 'wo', function(wo) return f(wo) and true or false end
  end
  error(what .. '(filter): a filter is a name, an ObjectClass value or a function(wo) that returns true', 0)
end

local function add(out, seen, id, kind, test, wo)
  if seen[id] then return end
  if kind == 'id' and not test(id) then return end
  wo = wo or wrap(id)
  if wo == nil then return end
  if kind == 'wo' and not test(wo) then return end
  seen[id] = true
  out[#out + 1] = wo
end

local function fromPacks(ids, kind, test, out, seen)
  for i = 1, #ids do add(out, seen, ids[i], kind, test) end
end

-- Worn and wielded items aren't in any container list: game.World's known objects
-- whose wielder is you.
local function fromEquipment(kind, test, out, seen)
  local ids = wieldedIds()
  for i = 1, #ids do add(out, seen, ids[i], kind, test) end
end

local function inventory(a, what)
  local kind, test = filterOf(a, what)
  local out, seen = {}, {}
  fromPacks(packIds(), kind, test, out, seen)
  fromEquipment(kind, test, out, seen)
  return out
end

local function equipment(a)
  local kind, test = filterOf(a, 'Equipment')
  local out = {}
  fromEquipment(kind, test, out, {})
  return out
end

local function containers(a)
  local kind, test = filterOf(a, 'Containers')
  local out = {}
  fromPacks(containerIds(), kind, test, out, {})
  return out
end

local function count(a, what)
  if a == nil then error(what .. '(nameOrFilter) needs a name, an ObjectClass value or a function(wo)', 0) end
  local items = inventory(a, what)
  local n = 0
  for i = 1, #items do n = n + stackOf(items[i].Id) end
  return n
end

-- Inventory / Equipment / Containers: UB's list properties. The list can still be called
-- with a filter, RynthLua's older form: game.Character.Inventory("Potion").
local function callable(build)
  return function(a, b)
    if rawequal(a, C) then a = b end
    return makeList(build(a))
  end
end
local lists = {
  Inventory = function(a) return inventory(a, 'Inventory') end,
  Equipment = equipment,
  Containers = containers,
}
local mt = getmetatable(C)
local clrIndex = mt.__index
mt.__index = function(t, k)
  local build = lists[k]
  if build ~= nil then return makeList(build(nil), callable(build)) end
  return clrIndex(t, k)
end

-- UB's searches: GetInventory(filter) (a list), GetFirstInventory(filter) (the first match or
-- nil), GetInventoryCount(filter) (stack sizes added up). A filter is a name, an ObjectClass
-- or a function(wo).
function C.GetInventory(a, b)
  if rawequal(a, C) then a = b end
  if a == nil then error('GetInventory(filter) needs a name, an ObjectClass value or a function(wo)', 0) end
  return makeList(inventory(a, 'GetInventory'))
end

function C.GetFirstInventory(a, b)
  if rawequal(a, C) then a = b end
  if a == nil then error('GetFirstInventory(filter) needs a name, an ObjectClass value or a function(wo)', 0) end
  return inventory(a, 'GetFirstInventory')[1]
end

function C.GetInventoryCount(a, b)
  if rawequal(a, C) then a = b end
  return count(a, 'GetInventoryCount')
end

function C.InventoryCount(a, b)
  if rawequal(a, C) then a = b end
  return count(a, 'InventoryCount')
end
""";
}
