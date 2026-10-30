using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// game.Character events. The engine raises combat mode changes, and enchantment changes on
/// API v70+ (older engines never call OnEnchantmentAdded/Removed); the rest is found by
/// polling in <see cref="CharacterTick"/>: vitals and portal space every tick (one cheap SDK
/// call each), level, experience and vitae every 500 ms, enchantments every 500 ms on older
/// engines. Level, experience and enchantments only while some script listens (a poll that
/// nobody listens to forgets its baseline, so a script that starts listening later doesn't
/// get a burst of stale changes).
///
/// Death has no engine event: health reaching 0, vitae dropping, or a death line in chat,
/// whichever comes first; OnDeath fires once per death (30 s window), with the chat line as
/// Text when one came.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private uint _chPlayer;
    private long _chBaselineAt;
    private bool _chVitalsKnown;
    private uint _chHp, _chStam, _chMana;
    private bool _chPortalKnown, _chPortal;
    private long _chNextSlow;

    private bool _chEnchKnown, _chEnchDirty;
    private long _chEnchNextCheck;
    private Dictionary<uint, double> _chEnch = new();
    private Dictionary<uint, double> _chEnchScratch = new();
    /// <summary>Spell ids the engine reported as added since the last diff (API v70+).</summary>
    private readonly HashSet<uint> _chEnchEventAdds = new();

    private bool _chLevelKnown; private int _chLevel;
    private bool _chXpKnown; private long _chXp;
    private bool _chLumKnown; private long _chLum;
    private bool _chVitaeKnown; private float _chVitae;

    private long _chDeathPendingAt, _chLastDeathAt = long.MinValue / 2, _chDeathTextAt = long.MinValue / 2;
    private string _chDeathText = string.Empty;

    private const long ChSlowPollMs = 500;
    /// <summary>With engine enchantment events: a safety re-read this often (events do the rest).</summary>
    private const long ChEnchReconcileMs = 10000;
    private const long ChDeathWindowMs = 30000;
    private const long ChLoginSettleMs = 10000;

    private void ChReset(uint playerId)
    {
        _chPlayer = playerId;
        _chBaselineAt = Environment.TickCount64;
        _chVitalsKnown = _chPortalKnown = _chEnchKnown = _chLevelKnown = _chXpKnown = _chVitaeKnown = _chLumKnown = false;
        _chEnchDirty = false;
        _chEnchNextCheck = 0;
        _chEnchEventAdds.Clear();
        _chEnch.Clear();
        _chEnchDuration.Clear();
        _chEnchLastGood.Clear();
        _chKnownSpells.Clear();
        _chKnownSpellsAt = 0;
        _chDeathPendingAt = 0;
        _chNextSlow = 0;
    }

    private bool ChListens(string key)
    {
        foreach (var c in _scripts.Values)
            if (c.Host.Loaded && c.HasHandlers(key)) return true;
        return false;
    }

    partial void CharacterOnLogin() { _chCombatMode = -1; ChReset(Host.GetPlayerId()); }

    partial void CharacterOnLogout() { _chCombatMode = -1; ChReset(0); }

    partial void CharacterTick()
    {
        if (GameState != "InGame") return;
        uint pid = Host.GetPlayerId();
        if (pid == 0) return;
        if (pid != _chPlayer) ChReset(pid);
        long now = Environment.TickCount64;
        bool settled = now - _chBaselineAt >= ChLoginSettleMs;

        // Combat mode (every tick): the engine's change event can miss a change, so the
        // client's current mode is watched too; ChCombatModeChanged drops duplicates.
        if (Host.HasGetCurrentCombatMode)
        {
            int mode = Host.GetCurrentCombatMode();
            if (_chCombatMode <= 0) _chCombatMode = mode;
            else if (mode > 0 && mode != _chCombatMode) ChCombatModeChanged(mode, _chCombatMode);
        }

        // Portal space (every tick).
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        if (Host.HasIsPortaling)
        {
            if (_chPortalKnown && portaling != _chPortal)
                FireAll(portaling ? "Character.OnPortalSpaceEntered" : "Character.OnPortalSpaceExited",
                    s => DynValue.NewTable(new Table(s)));
            _chPortal = portaling;
            _chPortalKnown = true;
        }

        // Vitals (every tick). A read with every max at 0 is "not loaded yet", not a change.
        if (ChReadVitals(out uint hp, out uint maxHp, out uint st, out uint maxSt, out uint mana, out uint maxMana)
            && (maxHp | maxSt | maxMana) != 0)
        {
            if (_chVitalsKnown)
            {
                if (hp != _chHp) ChFireVital(1, hp, _chHp);
                if (st != _chStam) ChFireVital(3, st, _chStam);     // VitalId numbering (1, 3, 5)
                if (mana != _chMana) ChFireVital(5, mana, _chMana);
                if (_chHp > 0 && hp == 0 && maxHp > 0 && !portaling && settled) ChDeathSignal(now);
            }
            _chHp = hp; _chStam = st; _chMana = mana;
            _chVitalsKnown = true;
        }

        if (_chDeathPendingAt != 0 && now >= _chDeathPendingAt)
            ChFireDeath(now, now - _chDeathTextAt < 5000 ? _chDeathText : string.Empty);

        // Enchantments. On API v70+ the engine's events mark them dirty and the diff runs on
        // the next tick; a slow re-read catches anything an event could miss. Older engines
        // poll every 500 ms. Either way one diff against the last read decides what fired,
        // so an event and a poll can never report the same change twice.
        if (_chEnchDirty || now >= _chEnchNextCheck)
        {
            if (ChListens("Character.OnEnchantmentsChanged"))
            {
                if (ChPollEnchantments())
                {
                    _chEnchDirty = false;
                    _chEnchNextCheck = now + (Host.HasEnchantmentEvents ? ChEnchReconcileMs : ChSlowPollMs);
                }
                // The read raced AC's own update: an event waiting keeps it dirty for the next tick.
                else if (!_chEnchDirty) _chEnchNextCheck = now + ChSlowPollMs;
            }
            else
            {
                _chEnchKnown = false;
                _chEnchDirty = false;
                _chEnchEventAdds.Clear();
                _chEnchNextCheck = now + ChSlowPollMs;
            }
        }

        if (now < _chNextSlow) return;
        _chNextSlow = now + ChSlowPollMs;

        // Vitae: always (a drop is a death signal); OnVitaeChanged when listened to.
        if (Host.HasGetVitae)
        {
            float vitae = Host.GetVitae(pid);
            if (_chVitaeKnown && Math.Abs(vitae - _chVitae) > 0.0001f)
            {
                if (vitae < _chVitae && settled) ChDeathSignal(now);
                float old = _chVitae;
                FireAll("Character.OnVitaeChanged", s =>
                {
                    var t = new Table(s);
                    t["Vitae"] = (double)vitae;
                    t["OldVitae"] = (double)old;
                    return DynValue.NewTable(t);
                });
            }
            _chVitae = vitae;
            _chVitaeKnown = true;
        }

        if (ChListens("Character.OnLevelChanged"))
        {
            int level = ChInt(ChIntLevel);
            if (level > 0)
            {
                if (_chLevelKnown && level != _chLevel)
                {
                    int old = _chLevel;
                    FireAll("Character.OnLevelChanged", s =>
                    {
                        var t = new Table(s);
                        t["Level"] = (double)level;
                        t["OldLevel"] = (double)old;
                        return DynValue.NewTable(t);
                    });
                }
                _chLevel = level;
                _chLevelKnown = true;
            }
        }
        else _chLevelKnown = false;

        if (ChListens("Character.OnTotalExperienceChanged"))
        {
            long xp = ChInt64(ChInt64TotalXp);
            if (xp > 0)
            {
                if (_chXpKnown && xp != _chXp)
                {
                    long old = _chXp;
                    FireAll("Character.OnTotalExperienceChanged", s =>
                    {
                        var t = new Table(s);
                        t["TotalExperience"] = (double)xp;
                        t["OldTotalExperience"] = (double)old;
                        return DynValue.NewTable(t);
                    });
                }
                _chXp = xp;
                _chXpKnown = true;
            }
        }
        else _chXpKnown = false;

        if (ChListens("Character.OnAvailableLuminanceChanged"))
        {
            long lum = ChInt64(ChInt64AvailableLum);
            if (_chLumKnown && lum != _chLum)
            {
                long old = _chLum;
                FireAll("Character.OnAvailableLuminanceChanged", s =>
                {
                    var t = new Table(s);
                    t["AvailableLuminance"] = (double)lum;
                    t["OldAvailableLuminance"] = (double)old;
                    return DynValue.NewTable(t);
                });
            }
            _chLum = lum;
            _chLumKnown = true;
        }
        else _chLumKnown = false;
    }

    private void ChFireVital(int vital, uint value, uint oldValue)
    {
        FireAll("Character.OnVitalChanged", s =>
        {
            var t = new Table(s);
            t["Type"] = (double)vital;
            t["TypeName"] = VitalNameById[vital];
            t["Value"] = (double)value;
            t["OldValue"] = (double)oldValue;
            return DynValue.NewTable(t);
        });
    }

    // ── Enchantments ────────────────────────────────────────────────────────

    /// <summary>
    /// Diffs the enchantment registry against the last read: a new spell id is "Added", a
    /// spell whose expiry moved later (recast) is "Added" with Refreshed = true, a spell that
    /// is gone is "Removed". One event per enchantment, like UtilityBelt. On API v70+ it runs
    /// when the engine reports a change, and a spell the engine reported as added that was
    /// already there is "Added" with Refreshed = true even when its expiry didn't move (a
    /// recast inside the same second). False when the registry couldn't be read this time.
    /// </summary>
    private bool ChPollEnchantments()
    {
        var list = ChReadEnchantments(out bool fresh);
        if (!fresh) return false;
        var cur = _chEnchScratch;
        cur.Clear();
        foreach (var (id, exp) in list)
            if (!cur.TryGetValue(id, out double old) || exp > old) cur[id] = exp;

        if (_chEnchKnown)
        {
            double serverNow = Host.HasGetServerTime ? Host.GetServerTime() : 0;
            double unixNow = UnixNow();
            foreach (var kv in cur)
            {
                if (!_chEnch.TryGetValue(kv.Key, out double oldExp))
                    ChFireEnchantment("Added", kv.Key, kv.Value, refreshed: false, serverNow, unixNow);
                else if ((kv.Value > oldExp + 1.0 && !ChPermanent(oldExp)) || _chEnchEventAdds.Contains(kv.Key))
                    ChFireEnchantment("Added", kv.Key, kv.Value, refreshed: true, serverNow, unixNow);
            }
            foreach (var kv in _chEnch)
                if (!cur.ContainsKey(kv.Key))
                    ChFireEnchantment("Removed", kv.Key, kv.Value, refreshed: false, serverNow, unixNow);
        }
        _chEnchEventAdds.Clear();
        _chEnchScratch = _chEnch;
        _chEnch = cur;
        _chEnchKnown = true;
        return true;
    }

    private void ChFireEnchantment(string type, uint spellId, double expiry, bool refreshed, double serverNow, double unixNow)
    {
        FireAll("Character.OnEnchantmentsChanged", s =>
        {
            var t = new Table(s);
            t["Type"] = type == "Added" ? 0.0 : 1.0;   // UB's AddRemoveEventType
            t["TypeName"] = type;
            t["SpellId"] = (double)spellId;
            string? name = ChSpellName(spellId);
            if (name != null) t["Name"] = name;
            t["Refreshed"] = refreshed;
            var layered = new Table(s);
            layered["Id"] = (double)spellId;   // UB's LayeredSpellId (the layer isn't known here)
            t["LayeredSpellId"] = DynValue.NewTable(layered);
            t["Enchantment"] = ChEnchantmentTable(s, spellId, expiry, serverNow, unixNow);
            return DynValue.NewTable(t);
        });
    }

    // The engine's enchantment events (API v70+; spell ids, layer stripped). They reach the
    // plugin after AC applied the change, so the diff on the next tick reads the new state.
    partial void CharacterOnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        uint id = spellId & 0xFFFF;
        if (durationSeconds > 0) _chEnchDuration[id] = durationSeconds;
        if (_chEnchKnown) _chEnchEventAdds.Add(id);
        _chEnchDirty = true;
    }

    partial void CharacterOnEnchantmentRemoved(uint enchantmentId) => _chEnchDirty = true;

    // ── Combat mode ─────────────────────────────────────────────────────────

    private int _chCombatMode = -1;

    partial void CharacterOnCombatModeChange(int currentMode, int previousMode) =>
        ChCombatModeChanged(currentMode, _chCombatMode > 0 ? _chCombatMode : previousMode);

    private void ChCombatModeChanged(int currentMode, int previousMode)
    {
        if (currentMode == _chCombatMode) return;   // already reported (engine event or the tick's poll)
        _chCombatMode = currentMode;
        FireAll("Character.OnCombatModeChanged", s =>
        {
            var t = new Table(s);
            t["NewMode"] = (double)currentMode;
            t["OldMode"] = (double)previousMode;
            t["NewModeName"] = LuaEnums.CombatModeName(currentMode);
            t["OldModeName"] = LuaEnums.CombatModeName(previousMode);
            return DynValue.NewTable(t);
        });
    }

    // ── Death ───────────────────────────────────────────────────────────────

    private static readonly string[] DeathLineStarts =
    {
        "You have died", "You died", "You were killed", "You have been killed", "You've been killed", "You are dead",
    };

    partial void CharacterOnChatText(string text, int chatType)
    {
        if (GameState != "InGame" || text.Length > 200 || text.IndexOf('"') >= 0) return;   // tells and chat quote their text
        string line = text.Trim();
        bool death = false;
        foreach (string p in DeathLineStarts)
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { death = true; break; }
        if (!death && (line.EndsWith("killed you!", StringComparison.OrdinalIgnoreCase)
                       || line.EndsWith("killed you.", StringComparison.OrdinalIgnoreCase)))
            death = true;
        if (!death) return;

        long now = Environment.TickCount64;
        _chDeathText = line;
        _chDeathTextAt = now;
        if (now - _chLastDeathAt >= ChDeathWindowMs) ChFireDeath(now, line);
    }

    partial void CharacterOnUpdateHealth(uint targetId, float ratio, uint current, uint max)
    {
        if (GameState != "InGame" || targetId == 0 || targetId != Host.GetPlayerId()) return;
        long now = Environment.TickCount64;
        if (max > 0 && current == 0 && now - _chBaselineAt >= ChLoginSettleMs) ChDeathSignal(now);
    }

    /// <summary>A non-chat death signal: fire a moment later, so a death line arriving meanwhile becomes the Text.</summary>
    private void ChDeathSignal(long now)
    {
        if (now - _chLastDeathAt < ChDeathWindowMs || _chDeathPendingAt != 0) return;
        _chDeathPendingAt = now + 1000;
    }

    private void ChFireDeath(long now, string text)
    {
        _chDeathPendingAt = 0;
        _chLastDeathAt = now;
        FireAll("Character.OnDeath", s =>
        {
            var t = new Table(s);
            t["Text"] = text;
            t["KillerId"] = 0.0;   // UB's field; the killer isn't known here
            return DynValue.NewTable(t);
        });
    }
}
