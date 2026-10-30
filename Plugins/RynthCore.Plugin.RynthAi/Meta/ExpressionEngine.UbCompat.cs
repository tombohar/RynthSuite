using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// ub-IT expression functions that were missing (2026-10-18 meta-compatibility pass), with
/// ub-IT's names, arguments and return values. Where RynthAi has no matching data (character
/// select, Decal views, ub-IT's own HUDs) the function still answers, with the value ub-IT gives
/// when there is nothing to report, so an expression that calls it keeps working.
/// </summary>
internal sealed partial class ExpressionEngine
{
    /// <summary>Corpses this character has opened (the plugin's loot bookkeeping), for
    /// hascorpsebeenopenedbyme[] and getcorpsesunopenedbyme[]. Null: none known.</summary>
    internal Func<int, bool>? CorpseOpenedByMe { get; set; }

    /// <summary>The meta manager's '/vt opt set' (VTank names, distances in landblock units, the
    /// special options), for vtsetsetting[]; null: the plain settings map (an engine without one).</summary>
    internal Func<string, string, bool>? VtOptionSetter { get; set; }

    /// <summary>The meta manager's '/vt opt get' (values read back in VTank's units), for vtgetsetting[].</summary>
    internal Func<string, string?>? VtOptionGetter { get; set; }

    // getcooldownexpiration: shared cooldowns are enchantments whose spell id has this bit.
    private const uint CooldownSpellFlag = 0x8000;

    // ub-IT's QuestRefreshCooldownSeconds default (canrefreshquests[]).
    private const double QuestRefreshCooldownSeconds = 60;

    // Character property ids (PropertyInt64): total experience, available luminance.
    private const uint QuadTotalExperience = 1;
    private const uint QuadAvailableLuminance = 6;
    // PropertyInt StackSize.
    private const uint IntStackSize = 12;

    // XP meter (xpreset/xpmeter/xpduration/xptotal/lumtotal/xpavg/lumavg). Starts on the
    // first call after login or a reset; luminance counts gains only (spending isn't earning).
    private long _xpStartMs = -1;
    private long _xpBase, _lumLast, _lumGained;

    // Functions with no RynthAi data already logged (one line each per session).
    private readonly HashSet<string> _ubNoDataLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The functions above. Returns false for any other name, so the caller's unknown-function
    /// fallback still logs real typos.
    /// </summary>
    private bool TryEvalUbCompat(string funcName, List<string> rawArgs, Func<int, string> A, out string value)
    {
        value = funcName switch
        {
            // ── Inventory actions (1 = attempted, 0 = busy or not possible) ──────
            "actiontrymove"  => EvalActionTryMove(A(0), A(1), rawArgs.Count > 2 ? A(2) : "0", rawArgs.Count > 3 ? A(3) : "1"),
            "actiontrysplit" => EvalActionTrySplit(A(0), A(1), rawArgs.Count > 2 ? A(2) : "", rawArgs.Count > 3 ? A(3) : "0"),
            "actiontrydrop"  => NoData("actiontrydrop", "0"),

            // ── Vitals of an object (current values; the selected target or yourself) ──
            "wobjectgethealthvalue"  => EvalWobjectVital(A(0), 0),
            "wobjectgetstaminavalue" => EvalWobjectVital(A(0), 1),
            "wobjectgetmanavalue"    => EvalWobjectVital(A(0), 2),

            // ── Corpses ──────────────────────────────────────────────────────────
            "hascorpsebeenopenedbyme" => EvalCorpseOpenedByMe(A(0)),
            "getcorpsesunopenedbyme"  => EvalCorpsesUnopenedByMe(),

            // ── Casting, cooldowns, spell data ───────────────────────────────────
            "iscasting"             => _host.HasGetCastBusyState && _host.GetCastBusyState() != 0 ? "1" : "0",
            "getcooldownexpiration" => EvalCooldownExpiration(A(0)),
            "spelldata"             => EvalSpellData(A(0)),

            // ── Macro state and delayed expressions ──────────────────────────────
            "vtmacroenabled" => _settings?.IsMacroRunning == true ? "1" : "0",
            "clearexec"      => EvalClearExec(A(0)),

            // ── Quests (myquests throttle; ACECustom quest bonus lives in RynthILT) ──
            "canrefreshquests"        => EvalCanRefreshQuests(),
            "testquestbonus"          => NoData("testquestbonus", "0"),
            "isrefreshingquestbonuses" => "0",
            "enlightenmentmaterials"  => EvalEnlightenmentTokens(A(0), A(1)),

            // ── XP meter ─────────────────────────────────────────────────────────
            "xpreset"    => EvalXpReset(),
            "xpduration" => FormatNumber(XpSeconds()),
            "xptotal"    => FormatNumber(XpTotal()),
            "lumtotal"   => FormatNumber(LumTotal()),
            "xpavg"      => FormatNumber(PerHour(XpTotal())),
            "lumavg"     => FormatNumber(PerHour(LumTotal())),
            "xpmeter"    => EvalXpMeterText(),

            // ── No RynthAi equivalent: ub-IT's "nothing here" values ─────────────
            "getcharacterindex" => NoData("getcharacterindex", "-1"),
            "setnextlogin"      => NoData("setnextlogin", "0"),
            "clearnextlogin"    => NoData("clearnextlogin", "1"),
            "netclients"        => NoData("netclients", "[]"),
            "statushud"         => NoData("statushud", "1"),
            "statushudcolored"  => NoData("statushudcolored", "1"),
            // Decal views: none exist here, so no view is found or shown.
            "uiviewexists"  => NoData("uiviewexists", "0"),
            "uiviewvisible" => NoData("uiviewvisible", "0"),
            "uisetvisible"  => NoData("uisetvisible", "0"),
            "uisetlabel"    => NoData("uisetlabel", "0"),
            "uigetcontrol"  => NoData("uigetcontrol", "0"),
            _ => null!,
        };
        return value != null;
    }

    /// <summary>The fixed answer of a function RynthAi has no data for; logged once per session.</summary>
    private string NoData(string funcName, string result)
    {
        if (_ubNoDataLogged.Add(funcName))
            _host.Log($"[RynthAi] Expression {funcName}[]: ub-IT feature with no RynthAi equivalent; returns {result}.");
        return result;
    }

    // ── Inventory actions ────────────────────────────────────────────────────

    private bool IsBusy() => _host.HasGetBusyState && _host.GetBusyState() > 0;

    private int StackSizeOf(uint id)
    {
        if (_worldObjectCache != null)
        {
            int n = _worldObjectCache.GetIntProperty(unchecked((int)id), IntStackSize, 0);
            if (n > 0) return n;
        }
        return 1;
    }

    /// <summary>actiontrymove[obj, container, slot=0, addToStack=1]: into a stack of the same
    /// name in that container when there is one (and addToStack), else into the slot.</summary>
    private string EvalActionTryMove(string objArg, string destArg, string slotArg, string stackArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint id, out _) || id == 0) return "0";
        if (!TryParseWobjectHandle(destArg, out uint dest, out _) || dest == 0) return "0";
        if (IsBusy()) return "0";

        if (ToBool(stackArg) && _worldObjectCache != null && _host.HasMergeStackInternal)
        {
            string name = _worldObjectCache[unchecked((int)id)]?.Name ?? "";
            foreach (var wo in _worldObjectCache.GetContainedItems(unchecked((int)dest)))
            {
                if (unchecked((uint)wo.Id) == id || !string.Equals(wo.Name, name, StringComparison.Ordinal)) continue;
                if (StackSizeOf(unchecked((uint)wo.Id)) <= 1 && StackSizeOf(id) <= 1) continue;
                return _host.MergeStackInternal(id, unchecked((uint)wo.Id)) ? "1" : "0";
            }
        }

        if (!_host.HasMoveItemInternal) return "0";
        int slot = (int)Math.Max(0, ToDouble(slotArg));
        return _host.MoveItemInternal(id, dest, slot, StackSizeOf(id)) ? "1" : "0";
    }

    /// <summary>actiontrysplit[obj, newStackSize, container=player, slot=0]: splits newStackSize off the stack.</summary>
    private string EvalActionTrySplit(string objArg, string countArg, string destArg, string slotArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint id, out _) || id == 0) return "0";
        int count = (int)ToDouble(countArg);
        if (count < 1 || count >= StackSizeOf(id)) return "0";
        uint dest = TryParseWobjectHandle(destArg, out uint d, out _) && d != 0 ? d : _playerId;
        if (dest == 0 || IsBusy() || !_host.HasSplitStackInternal) return "0";
        int slot = (int)Math.Max(0, ToDouble(slotArg));
        return _host.SplitStackInternal(id, dest, slot, count) ? "1" : "0";
    }

    // ── Vitals ───────────────────────────────────────────────────────────────

    /// <summary>Current health (0), stamina (1) or mana (2) of the object; -1 when unknown. The
    /// game reports these for yourself and for the target you've selected (as ub-IT notes).</summary>
    private string EvalWobjectVital(string objArg, int which)
    {
        if (!TryParseWobjectHandle(objArg, out uint id, out _) || id == 0) return "-1";
        uint hp, st, mp;
        if (id == _playerId)
        {
            if (!_host.TryGetPlayerVitals(out hp, out _, out st, out _, out mp, out _)) return "-1";
        }
        else if (!_host.TryGetTargetVitals(id, out hp, out _, out st, out _, out mp, out _))
        {
            return "-1";
        }
        uint v = which switch { 0 => hp, 1 => st, _ => mp };
        return v.ToString(CultureInfo.InvariantCulture);
    }

    // ── Corpses ──────────────────────────────────────────────────────────────

    private string EvalCorpseOpenedByMe(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint id, out _) || id == 0) return "0";
        return CorpseOpenedByMe?.Invoke(unchecked((int)id)) == true ? "1" : "0";
    }

    /// <summary>Corpses in view this character hasn't opened, as a list of objects.</summary>
    private string EvalCorpsesUnopenedByMe()
    {
        if (_worldObjectCache == null) return "[]";
        var items = new List<string>();
        var seen = new HashSet<int>();
        foreach (var wo in _worldObjectCache.GetLandscape())
        {
            if (wo.ObjectClass != AcObjectClass.Corpse || !seen.Add(wo.Id)) continue;
            if (CorpseOpenedByMe?.Invoke(wo.Id) == true) continue;
            items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name));
        }
        return NewList(items);
    }

    // ── Casting, cooldowns, spell data ───────────────────────────────────────

    /// <summary>Seconds until shared cooldown <paramref name="idArg"/> ends; 0 when it isn't running.</summary>
    private string EvalCooldownExpiration(string idArg)
    {
        int want = (int)ToDouble(idArg);
        if (want <= 0 || !_host.HasReadPlayerEnchantments || !_host.HasGetServerTime) return "0";
        double now = _host.GetServerTime();
        if (now <= 0) return "0";
        const int Max = 512;
        uint[] ids = new uint[Max];
        double[] expiry = new double[Max];
        int count = _host.ReadPlayerEnchantments(ids, expiry, Max);
        for (int i = 0; i < count; i++)
        {
            if ((ids[i] & CooldownSpellFlag) == 0 || (ids[i] & 0xFFF) != want) continue;
            double left = expiry[i] - now;
            return left > 0 ? ((int)Math.Ceiling(left)).ToString(CultureInfo.InvariantCulture) : "0";
        }
        return "0";
    }

    /// <summary>spelldata[id]: a dictionary of what RynthAi's spell table knows (Id, Name, Family,
    /// Difficulty, Category, Power, Level). Empty for an unknown spell, as in ub-IT.</summary>
    private string EvalSpellData(string idArg)
    {
        int id = (int)ToDouble(idArg);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (id > 0 && SpellDatabase.HasSpell(id))
        {
            d["Id"] = id.ToString(CultureInfo.InvariantCulture);
            d["Name"] = SpellDatabase.GetSpellName(id);
            if (SpellDatabase.TryGetSpellMeta(id, out int family, out int difficulty))
            {
                d["Family"] = family.ToString(CultureInfo.InvariantCulture);
                d["Difficulty"] = difficulty.ToString(CultureInfo.InvariantCulture);
            }
            if (SpellDatabase.TryGetCategoryAndPower(id, out int category, out int power))
            {
                d["Category"] = category.ToString(CultureInfo.InvariantCulture);
                d["Power"] = power.ToString(CultureInfo.InvariantCulture);
                d["Level"] = SpellDatabase.LevelFromPower(power).ToString(CultureInfo.InvariantCulture);
            }
        }
        return NewDict(d);
    }

    // ── Delayed expressions ──────────────────────────────────────────────────

    /// <summary>clearexec[id]: cancels the delayexec that returned that id. 1 if one was pending.</summary>
    private string EvalClearExec(string idArg)
    {
        long id = (long)ToDouble(idArg);
        int removed = _delayedExecs.RemoveAll(e => e.Id == id);
        return removed > 0 ? "1" : "0";
    }

    // ── Quests ───────────────────────────────────────────────────────────────

    private string EvalCanRefreshQuests()
    {
        if (_questTracker == null) return "1";
        if (_questTracker.IsRefreshing) return "0";
        return (DateTime.UtcNow - _questTracker.LastRefreshUtc).TotalSeconds >= QuestRefreshCooldownSeconds ? "1" : "0";
    }

    /// <summary>enlightenmentmaterials[current, target]: ub-IT's token count. Levels 6-150 cost
    /// (level - 5) tokens each; levels 1-5 cost none and later levels cost other items.</summary>
    private static string EvalEnlightenmentTokens(string curArg, string tgtArg)
    {
        int cur = (int)ToDouble(curArg), tgt = (int)ToDouble(tgtArg);
        long tokens = 0;
        for (int level = Math.Max(cur + 1, 6); level <= Math.Min(tgt, 150); level++)
            tokens += level - 5;
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    // ── XP meter ─────────────────────────────────────────────────────────────

    private long ReadQuad(uint stype)
        => _playerId != 0 && _host.HasGetObjectQuadProperty && _host.TryGetObjectQuadProperty(_playerId, stype, out long v) ? v : 0;

    /// <summary>Starts the meter on first use and folds in luminance gained since the last look.</summary>
    private void SampleXpMeter()
    {
        long lum = ReadQuad(QuadAvailableLuminance);
        if (_xpStartMs < 0)
        {
            _xpStartMs = TickMs();
            _xpBase = ReadQuad(QuadTotalExperience);
            _lumLast = lum;
            _lumGained = 0;
            return;
        }
        if (lum > _lumLast) _lumGained += lum - _lumLast;
        _lumLast = lum;
    }

    /// <summary>Forgets the meter when the character changes (called from SetPlayerId).</summary>
    private void ResetXpMeter() => _xpStartMs = -1;

    private string EvalXpReset()
    {
        ResetXpMeter();
        SampleXpMeter();
        return "1";
    }

    private double XpSeconds()
    {
        SampleXpMeter();
        return Math.Max(0, (TickMs() - _xpStartMs) / 1000.0);
    }

    private double XpTotal()
    {
        SampleXpMeter();
        return Math.Max(0, ReadQuad(QuadTotalExperience) - _xpBase);
    }

    private double LumTotal()
    {
        SampleXpMeter();
        return _lumGained;
    }

    private double PerHour(double amount)
    {
        double s = XpSeconds();
        return s <= 0 ? 0 : amount / s * 3600;
    }

    /// <summary>ub-IT's meter text: "500m XP and 2k LUM, 51s, 35.39b XP/hr and 140k LUM/hr".</summary>
    private string EvalXpMeterText()
    {
        double xp = XpTotal(), lum = LumTotal(), s = XpSeconds();
        string head = lum > 0 ? $"{ShortNumber(xp)} XP and {ShortNumber(lum)} LUM" : $"{ShortNumber(xp)} XP";
        string rate = lum > 0 ? $"{ShortNumber(PerHour(xp))} XP/hr and {ShortNumber(PerHour(lum))} LUM/hr" : $"{ShortNumber(PerHour(xp))} XP/hr";
        return $"{head}, {ShortDuration(s)}, {rate}";
    }

    private static string FormatNumber(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>1234 -> 1.23k, 5.6e8 -> 560m, as ub-IT's formatExperience.</summary>
    private static string ShortNumber(double v)
    {
        string[] units = { "", "k", "m", "b", "t", "q" };
        int u = 0;
        while (Math.Abs(v) >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return (u == 0 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                       : v.ToString("0.##", CultureInfo.InvariantCulture)) + units[u];
    }

    private static string ShortDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
