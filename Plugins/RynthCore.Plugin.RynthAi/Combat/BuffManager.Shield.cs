using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The shield's armor spells (2026-10-28). Impenetrability and the seven Banes are cast on the
/// player, and ACE puts them on every worn piece of armor and a worn shield
/// (WorldObject_Magic.TryCastItemEnchantment_WithRedirects). But buffing holds a wand, and ACE won't
/// wield a caster beside a shield: the wand swap puts the shield into a pack first, so the
/// self-cast never reached it. The shield now gets its own casts, aimed at it, after the armor's.
///
///   * Which shield: one in the Items list (the Shield action, or a shield filed as a weapon) that
///     is in use: in the off hand, or put into a pack by buffing's own wand swap (combat puts it
///     back). A listed shield that was in a pack before (not worn) is left alone, as armor in a
///     pack is; an unlisted shield is never a target. No listed shield: nothing here runs.
///   * Which spells: the armor's, Impenetrability and the Banes still in the buff list (the
///     Protections and Banes ticks, or a buff profile), at the level the armor gets. Weapon
///     enchantments never go on the shield. Buff the shield (LegacyUiSettings.BuffShield, on by
///     default) turns it off.
///   * When: on the armor's rules (Rebuff With, Also Refresh Under in a batch, Force Rebuff), on the
///     shield's own timers, recorded from the "You cast X on &lt;shield&gt;" line.
///   * Never at a shield being moved (its pack move or wield not settled, a swap under way).
///   * A cast at the shield that gets no answer twice in a row, or that the server says can't be
///     cast on it, stops shield buffing for ShieldParkTime; the armor is never held up by it.
/// </summary>
public partial class BuffManager
{
    /// <summary>The shield's armor spells: keyed by (shield id, spell family), <see cref="ShieldKey"/>.</summary>
    private readonly Dictionary<long, ItemSpellRecord> _shieldSpellTimers = new();
    internal static long ShieldKey(int shieldId, int family) => ((long)unchecked((uint)shieldId) << 32) | unchecked((uint)family);

    // The shield the pending cast is aimed at. Means something only while _pendingSpellId is set:
    // every other cast clears it when it is sent.
    private int _pendingShieldId;
    private bool PendingIsShield => _pendingSpellId != 0 && _pendingShieldId != 0;

    // Families confirmed on the shield in this force/batch run, at their level (ClearRunFamilies).
    private readonly Dictionary<int, int> _shieldRunLevels = new();
    // A family the server refused on the shield (no components, no mana): rested, the armor's not touched.
    private readonly Dictionary<int, DateTime> _shieldFamilyRestUntil = new();
    // Casts at the shield with no answer in a row; at ShieldNoShowsToPark the shield is parked.
    private int _shieldNoShows;
    private int _shieldParkedId;
    private DateTime _shieldParkedUntil = DateTime.MinValue;
    internal const int ShieldNoShowsToPark = 2;
    internal static readonly TimeSpan ShieldParkTime = TimeSpan.FromMinutes(30);

    // The off-hand item buffing's wand swap put into a pack (combat puts it back on), and when.
    // Seen in the off hand again after StowedSightingGraceMs, it is forgotten: the next time it is
    // in a pack, something else put it there.
    private int _stowedShieldId;
    private DateTime _stowedShieldAt = DateTime.MinValue;
    private const double StowedSightingGraceMs = 10000;

    // ShieldToBuff walks the inventory: its answer is kept ShieldReadMs (the arbiter asks every tick),
    // and worked out again at once when the Items list or the cache changes. Tests set ShieldReadMs 0.
    internal long ShieldReadMs = 1000;
    private int _shieldChoice;
    private string _shieldChoiceWhy = "";
    private long _shieldChoiceAt = long.MinValue / 2;
    private List<ItemRule>? _shieldChoiceRules;
    private int _shieldChoiceRuleCount;
    private WorldObjectCache? _shieldChoiceCache;
    private string _shieldChoiceLogged = "";

    /// <summary>Impenetrability (Brogard's Defiance) and the Banes: the armor spells, which a shield takes too.</summary>
    internal static bool IsArmorItemSpell(string name) =>
        name.IndexOf("Impenetrability", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Brogard's Defiance", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf(" Bane", StringComparison.OrdinalIgnoreCase) >= 0
        || name.StartsWith("Bane", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Buffing's wand swap put <paramref name="id"/> (the off hand) into a pack: combat puts it back
    /// (OffhandStowed), and a shield there is still the one in use, so it is buffed in the pack.
    /// </summary>
    internal void OffhandPutAway(int id)
    {
        if (id != 0)
        {
            _stowedShieldId = id;
            _stowedShieldAt = RynthClock.Now;
            _shieldChoiceAt = long.MinValue / 2;
        }
        OffhandStowed?.Invoke(id);
    }

    /// <summary>The Items list's shields the cache knows, in list order.</summary>
    private List<int> ListedShields()
    {
        var found = new List<int>();
        var cache = _worldObjectCache;
        if (cache == null) return found;
        List<ItemRule> rules;
        try { rules = new List<ItemRule>(_settings.ItemRules); }
        catch { return found; }   // the list edited mid-copy: next read
        foreach (var r in rules)
        {
            if (r.Id == 0 || found.Contains(r.Id)) continue;
            var wo = cache[r.Id];
            if (wo == null) continue;
            if (CombatManager.IsShieldItem(wo) || (WeaponList.IsShieldRule(r) && wo.ObjectClass == AcObjectClass.Armor))
                found.Add(r.Id);
        }
        return found;
    }

    private bool InInventory(int id)
    {
        if (_worldObjectCache == null || id == 0) return false;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
            if (wo.Id == id) return true;
        return false;
    }

    /// <summary>
    /// The listed shield to buff now (0 = none) and where it is: the one in the off hand, else the
    /// one buffing put into a pack for the wand. <paramref name="fresh"/> reads it again now.
    /// </summary>
    internal int ShieldToBuff(out string where, bool fresh = false)
    {
        long now = KitClockMs();
        var rules = _settings.ItemRules;
        if (!fresh && ReferenceEquals(rules, _shieldChoiceRules) && rules.Count == _shieldChoiceRuleCount
            && ReferenceEquals(_worldObjectCache, _shieldChoiceCache) && now >= _shieldChoiceAt && now - _shieldChoiceAt < ShieldReadMs)
        {
            where = _shieldChoiceWhy;
            return _shieldChoice;
        }
        int id = FindShieldToBuff(out where, out bool anyListed);
        _shieldChoice = id;
        _shieldChoiceWhy = where;
        _shieldChoiceAt = now;
        _shieldChoiceRules = rules;
        _shieldChoiceRuleCount = rules.Count;
        _shieldChoiceCache = _worldObjectCache;
        // One log line when the answer changes, only for a character with a shield listed.
        string line = anyListed ? $"{id:X8}|{where}" : "";
        if (line != _shieldChoiceLogged)
        {
            _shieldChoiceLogged = line;
            if (anyListed)
                _host.Log(id != 0
                    ? $"[Buff] shield: 0x{(uint)id:X8} '{ItemName(id)}' ({where}) takes the armor spells"
                    : $"[Buff] shield: none buffed ({where})");
        }
        return id;
    }

    private int FindShieldToBuff(out string where, out bool anyListed)
    {
        anyListed = false;
        if (!_settings.BuffShield) { where = "Buff the shield is off"; return 0; }
        var listed = ListedShields();
        anyListed = listed.Count > 0;
        if (!anyListed) { where = "no shield listed"; return 0; }
        foreach (int id in listed)
        {
            if (!IsWieldedLive(id)) continue;
            if (id == _stowedShieldId && !ShieldMidSwap(id, out _)
                && (RynthClock.Now - _stowedShieldAt).TotalMilliseconds > StowedSightingGraceMs)
                _stowedShieldId = 0;   // back in the off hand
            where = "in the off hand";
            return id;
        }
        if (_stowedShieldId != 0 && listed.Contains(_stowedShieldId) && InInventory(_stowedShieldId))
        {
            where = "put into a pack for the wand";
            return _stowedShieldId;
        }
        where = "the listed shield is in a pack, not worn";
        return 0;
    }

    /// <summary>
    /// True while <paramref name="shieldId"/> may be on its way into or out of a hand: the wand swap
    /// under way, the shield's move into a pack not confirmed, or a swap the shared gate says just
    /// moved it (combat's off-hand wield or dequip, a held multi-step swap).
    /// </summary>
    private bool ShieldMidSwap(int shieldId, out string why)
    {
        if (_wandSwap.Active) { why = "the wand swap is under way"; return true; }
        if (_pendingWieldId != 0) { why = "the wand's wield is in flight"; return true; }
        if (_bowDequipPendingId == shieldId && shieldId != 0) { why = "its move into a pack is in flight"; return true; }
        if (_weaponSwapGate != null && _weaponSwapGate.MayBeMoving(shieldId))
        {
            why = $"a swap is moving it ({_weaponSwapGate.LastSwapBy})";
            return true;
        }
        why = "";
        return false;
    }

    private bool ShieldParked(int shieldId) =>
        shieldId != 0 && shieldId == _shieldParkedId && RynthClock.Now < _shieldParkedUntil;

    /// <summary>A family resting after a refusal: on the shield, or for the armor too (no components, a parked family).</summary>
    private bool ShieldFamilyResting(int family)
    {
        DateTime now = RynthClock.Now;
        return (_shieldFamilyRestUntil.TryGetValue(family, out DateTime s) && now < s)
               || (_buffFailCooldownUntil.TryGetValue(family, out DateTime a) && now < a);
    }

    /// <summary>The armor spells of <paramref name="desiredBuffs"/> this character can cast, resolved like the armor's.</summary>
    private List<(SpellInfo Spell, string BaseName)> ShieldSpells(List<string> desiredBuffs)
    {
        var list = new List<(SpellInfo, string)>();
        foreach (string baseName in desiredBuffs)
        {
            if (!IsArmorItemSpell(baseName)) continue;   // weapon enchantments never go on the shield
            AcSkillType skill = SkillFor(baseName);
            if (!IsSkillUsable(skill)) continue;
            int spellId = FindBestSpellId(baseName, skill);
            if (spellId == 0) continue;
            var spell = SpellTableStub.GetById(spellId);
            if (spell == null || !IsArmorItemSpell(spell.Name)) continue;
            list.Add((spell, baseName));
        }
        return list;
    }

    /// <summary>IsBuffActive for the shield: its own timer, the armor's rules.</summary>
    private bool IsShieldBuffActive(int shieldId, SpellInfo spell, int? rebufferSecOverride = null)
    {
        int level = GetSpellLevel(spell);
        if (_isForceRebuffing)
        {
            // Cast on the shield in this run: done, unless a higher level is castable now.
            if (_shieldRunLevels.TryGetValue(spell.Family, out int ran)) return UpgradeParked(spell.Family) || level <= ran;
            if (!_isAutoBatchRebuff) return false;
            rebufferSecOverride = Math.Max(_settings.RebuffTopOffSecondsRemaining, _settings.RebuffSecondsRemaining);
        }
        int rebufferSec = Math.Max(0, rebufferSecOverride ?? _settings.RebuffSecondsRemaining);
        if (!_shieldSpellTimers.TryGetValue(ShieldKey(shieldId, spell.Family), out ItemSpellRecord? rec)) return false;
        if (rec.SpellLevel < level && !UpgradeParked(spell.Family)) return false;   // a higher level: recast, as the armor's
        return RynthClock.Now < rec.ExpiresAt.AddSeconds(-rebufferSec);
    }

    /// <summary>AnyBuffBelowThreshold's shield half: an armor spell on the shield to buff under <paramref name="thresholdSec"/>.</summary>
    private bool ShieldBuffBelowThreshold(List<string> desiredBuffs, int thresholdSec)
    {
        int shield = ShieldToBuff(out _);
        if (shield == 0 || ShieldParked(shield)) return false;
        foreach (var (spell, _) in ShieldSpells(desiredBuffs))
        {
            if (ShieldFamilyResting(spell.Family)) continue;
            if (!IsShieldBuffActive(shield, spell, thresholdSec)) return true;
        }
        return false;
    }

    /// <summary>
    /// CheckAndCastSelfBuffs' last step, once every buff on the character is on: the next armor
    /// spell the shield needs, aimed at the shield. True when it acted (a cast, or the tick yielded
    /// to the wand swap or a move of the shield).
    /// </summary>
    private bool CastShieldBuffs(List<string> desiredBuffs, bool diagnose)
    {
        // This step runs on every buff tick: the kept answer first (no inventory walk while
        // nothing is due), a fresh read only once a spell is due on the shield.
        int shield = ShieldToBuff(out string where);
        if (shield == 0) return false;
        if (ShieldParked(shield))
        {
            LastBuffSkipReason = $"shield buffs parked ({(_shieldParkedUntil - RynthClock.Now).TotalMinutes:0} min left)";
            return false;
        }
        var spells = ShieldSpells(desiredBuffs);
        if (!spells.Exists(s => !IsShieldBuffActive(shield, s.Spell) && !ShieldFamilyResting(s.Spell.Family))) return false;
        int read = ShieldToBuff(out where, fresh: true);
        if (read != shield) return read != 0;   // another shield (next tick), or none any more

        foreach (var (spell, baseName) in spells)
        {
            if (IsShieldBuffActive(shield, spell)) continue;
            if (ShieldFamilyResting(spell.Family))
            {
                LastBuffSkipReason = $"resting on the shield: {baseName}";
                if (diagnose) DiagSkip(baseName + "@shield", $"[FR] skip '{baseName}' on the shield — resting after a refusal");
                continue;
            }

            if (!EnsureMagicMode(forBuff: true))
            {
                if (_wandSwapExhausted)
                {
                    // The wand can't be wielded now (see the self-buff loop): rest this family on the
                    // shield so buffing lets go and combat runs.
                    _shieldFamilyRestUntil[spell.Family] = RynthClock.Now.AddSeconds(BuffFailCooldownSec);
                    LastBuffSkipReason = $"wand-swap exhausted: {baseName} on the shield";
                    continue;
                }
                LastBuffSkipReason = "magic-mode swap in progress (yielding tick)";
                return true;
            }

            // The wand swap may just have put the shield into a pack: where is it now?
            int now = ShieldToBuff(out where, fresh: true);
            if (now != shield) return now != 0;   // another shield (next tick), or none any more
            if (ShieldMidSwap(shield, out string moving))
            {
                LastBuffSkipReason = $"the shield is being moved: {moving}";
                return true;
            }

            // The mana reserve holds these as it holds every buff (see the self-buff loop).
            if (_fastChainTick && BuffNeedsManaFirst(spell.Id, out _, out _))
            {
                _fastChainTick = false;
                _chain.HoldForAnimation();
                LastBuffSkipReason = "mana reserve before " + baseName + " on the shield: Stamina to Mana waits for the animation";
                return true;
            }
            if (BuffWaitsForMana(spell.Id, baseName))
                return true;

            SendShieldCast(shield, spell, baseName, where, diagnose);
            return true;
        }
        return false;
    }

    private void SendShieldCast(int shield, SpellInfo spell, string baseName, string where, bool diagnose)
    {
        int spellId = spell.Id;
        string shieldName = ItemName(shield);
        _pendingSpellId = spellId;
        _pendingShieldId = shield;
        _pendingUpgradeSpellId = 0;   // the upgrade bookkeeping is the armor's
        _host.WriteToChat($"[RynthAi] Casting: {spell.Name} on {shieldName}", 5);
        if (diagnose) _host.Log($"[FR] CAST '{baseName}' on the shield 0x{(uint)shield:X8} '{shieldName}' ({where}) resolvedSpellId={spellId} (pending now set)");
        NoteBuffCastMana(SpellManaCost(spellId));
        _host.Log($"[Buff] mana {_vitals.CurrentMana}/{_vitals.MaxMana} ({_vitals.ManaPct}%) before {spell.Name} on the shield '{shieldName}' ({where}) (cost {SpellManaCost(spellId)}, reserve {ManaReserve()}, Get Mana At {_settings.GetManaAt}%)");
        bool fast = _fastChainTick;
        if (fast)
            _host.Log($"[BuffChain] '{spell.Name}' on the shield goes without waiting for the animation: {_fastChainWhy}");
        int seqAtSend = UseDoneSeqNow();
        bool castOk = _host.CastSpellFor(unchecked((uint)shield), spellId, "Buff", $"{spell.Name} on the shield");
        _lastCastAttempt = RynthClock.Now;
        if (castOk)
        {
            NoteCastIssued(spellId);
            _chain.Sent(spellId, spell.Name, seqAtSend, ActionPacer.Started, ActionPacer.AttacksSent, MoveAudit.MovementSends, fast);
        }
        else
        {
            _chain.ClientRefused(spellId, spell.Name, fast, s => _host.Log(s));
            _pendingSpellId = 0;   // the packet never went out: no answer will come
            _pendingShieldId = 0;
            LastBuffSkipReason = $"local CastSpell rejected: {baseName} on the shield (id={spellId})";
        }
    }

    /// <summary>An armor spell landed on the shield: its timer, and in a force/batch run the family is done on it.</summary>
    private void ShieldSpellLanded(int shieldId, SpellInfo spell)
    {
        RecordShieldSpell(shieldId, spell);
        int level = GetSpellLevel(spell);
        if (_isForceRebuffing && (shieldId == _pendingShieldId || shieldId == ShieldToBuff(out _))
            && (!_shieldRunLevels.TryGetValue(spell.Family, out int ran) || level > ran))
            _shieldRunLevels[spell.Family] = level;
    }

    private void RecordShieldSpell(int shieldId, SpellInfo spell)
    {
        int level = GetSpellLevel(spell);
        double duration = GetCustomSpellDuration(level);
        var now = RynthClock.Now;
        _shieldSpellTimers[ShieldKey(shieldId, spell.Family)] = new ItemSpellRecord
        {
            CastAt = now,
            ExpiresAt = now.AddSeconds(duration),
            SpellName = spell.Name,
            SpellLevel = level,
        };
        _host.Log($"[BuffDiag] RECORD '{spell.Name}' on the shield 0x{(uint)shieldId:X8} (fam={spell.Family}, lvl={level}, durSec={duration:F0}, expiresAt={now.AddSeconds(duration):HH:mm:ss})");
        SaveBuffTimers();
    }

    /// <summary>"Flame Bane VI on Kite Shield, refreshing Flame Bane V" -> "Kite Shield"; "" with no target.</summary>
    internal static string CastTarget(string afterCast)
    {
        int on = afterCast.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (on < 0) return "";
        string rest = afterCast.Substring(on + 4);
        int comma = rest.IndexOf(',');
        if (comma >= 0) rest = rest.Substring(0, comma);
        return rest.Trim().TrimEnd('.', '!').Trim();
    }

    private bool NamesShield(string target, int shieldId)
    {
        string name = _worldObjectCache?[shieldId]?.Name ?? "";
        return name.Length == 0 || target.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The listed shield a cast line names (the pending one first), 0 = none.</summary>
    private int ListedShieldNamed(string target)
    {
        if (target.Length == 0) return 0;
        var listed = ListedShields();
        if (_pendingShieldId != 0 && listed.Contains(_pendingShieldId) && NamesShield(target, _pendingShieldId)
            && (_worldObjectCache?[_pendingShieldId]?.Name ?? "").Length > 0)
            return _pendingShieldId;
        foreach (int id in listed)
        {
            string name = _worldObjectCache?[id]?.Name ?? "";
            if (name.Length > 0 && target.Equals(name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return 0;
    }

    /// <summary>
    /// OnChatWindowText's "You cast X on Y" for the shield, before the armor's handling: the pending
    /// shield cast's result, a late line for a listed shield, or a self-cast that reached a worn
    /// shield. Records the shield's timer. True when the line is done with here.
    /// </summary>
    private bool ShieldCastLine(SpellInfo spell, string afterCast)
    {
        if (!IsArmorItemSpell(spell.Name)) return false;
        string target = CastTarget(afterCast);
        var pending = PendingIsShield ? SpellTableStub.GetById(_pendingSpellId) : null;
        if (pending != null && pending.Family == spell.Family)
        {
            if (target.Length > 0 && !NamesShield(target, _pendingShieldId))
            {
                // A late line of the armor's own cast of this family: the armor's; the shield's cast waits.
                RecordItemSpellCast(spell);
                _host.Log($"[BuffChat] '{spell.Name}' on '{target}' is the armor's, not pending={_pendingSpellId} on the shield - recorded, pending kept.");
                return true;
            }
            ShieldSpellLanded(_pendingShieldId, spell);
            _shieldFamilyRestUntil.Remove(spell.Family);
            _shieldNoShows = 0;
            NoteCastAccepted();
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} (on the shield 0x{(uint)_pendingShieldId:X8}) via ou-cast match name='{spell.Name}'");
            _pendingSpellId = 0;
            _pendingShieldId = 0;
            _onCastResolved?.Invoke($"cast '{spell.Name}' on the shield");
            return true;
        }

        int named = ListedShieldNamed(target);
        if (named == 0) return false;
        ShieldSpellLanded(named, spell);
        // A self-cast of this family that reached a worn shield: the armor's result as well.
        var selfPending = _pendingSpellId != 0 && !PendingIsShield ? SpellTableStub.GetById(_pendingSpellId) : null;
        if (selfPending != null && selfPending.Family == spell.Family) return false;
        _host.Log($"[BuffChat] '{spell.Name}' on the shield '{target}' - a late line, recorded{(_pendingSpellId != 0 ? $"; pending={_pendingSpellId} kept" : "")}.");
        return true;
    }

    /// <summary>The pending shield cast was refused or fizzled: the armor's timers and families are left alone.</summary>
    private void ShieldCastRefused(SpellInfo? spell, string why, double restSec)
    {
        if (spell != null && restSec > 0)
            _shieldFamilyRestUntil[spell.Family] = RynthClock.Now.AddSeconds(restSec);
        _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} on the shield 0x{(uint)_pendingShieldId:X8} - {why}" +
                  (restSec > 0 ? $"; {spell?.Name} rests on the shield {restSec:0}s." : "; tried again."));
    }

    /// <summary>The pending shield cast got no answer in time (the tick's no-chat give-up).</summary>
    private void ShieldCastNoShow(SpellInfo? spell)
    {
        int shield = _pendingShieldId;
        _shieldNoShows++;
        if (_shieldNoShows >= ShieldNoShowsToPark)
        {
            _shieldNoShows = 0;
            ParkShield(shield, $"{ShieldNoShowsToPark} casts on it got no answer");
        }
        else
            _host.Log($"[BuffChat] NO-CHAT TIMEOUT (shield) pending={_pendingSpellId} ('{spell?.Name}') on 0x{(uint)shield:X8} - " +
                      $"no answer in {NoChatResolveTimeoutMs:0}ms ({_shieldNoShows}/{ShieldNoShowsToPark} before shield buffing rests).");
    }

    private void ParkShield(int shield, string why)
    {
        _shieldParkedId = shield;
        _shieldParkedUntil = RynthClock.Now + ShieldParkTime;
        _host.Log($"[Buff] shield 0x{(uint)shield:X8} '{ItemName(shield)}': {why} - not buffing it for {ShieldParkTime.TotalMinutes:0} min.");
        _host.WriteToChat($"[RynthAi] Not buffing the shield '{ItemName(shield)}' for {ShieldParkTime.TotalMinutes:0} min ({why}).", 2);
    }

    /// <summary>Force Rebuff: the shield starts over too.</summary>
    private void ResetShieldBuffs()
    {
        _shieldSpellTimers.Clear();
        _shieldRunLevels.Clear();
        _shieldFamilyRestUntil.Clear();
        _shieldNoShows = 0;
        _shieldParkedId = 0;
        _shieldParkedUntil = DateTime.MinValue;
        _pendingShieldId = 0;
    }

    /// <summary>bufftimers.txt lines for the shield: "shield|id|family|castTicks|expiresTicks|level|name".</summary>
    private void SaveShieldTimers(List<string> lines)
    {
        foreach (var kvp in _shieldSpellTimers)
        {
            var info = kvp.Value;
            uint id = unchecked((uint)(kvp.Key >> 32));
            int family = unchecked((int)(kvp.Key & 0xFFFFFFFF));
            lines.Add($"shield|{id}|{family}|{info.CastAt.Ticks}|{info.ExpiresAt.Ticks}|{info.SpellLevel}|{info.SpellName}");
        }
    }

    /// <summary>One "shield|..." line of bufftimers.txt; false when the line is something else.</summary>
    private bool LoadShieldTimer(string[] p)
    {
        if (p[0] != "shield") return false;
        if (p.Length < 7) return true;
        if (!uint.TryParse(p[1], out uint id) || !int.TryParse(p[2], out int family)
            || !long.TryParse(p[3], out long cast) || !long.TryParse(p[4], out long expires) || !int.TryParse(p[5], out int level))
            return true;
        var expiresAt = new DateTime(expires);
        if (expiresAt > RynthClock.Now)
            _shieldSpellTimers[ShieldKey(unchecked((int)id), family)] = new ItemSpellRecord
            {
                CastAt = new DateTime(cast), ExpiresAt = expiresAt, SpellLevel = level, SpellName = p[6],
            };
        return true;
    }

    /// <summary>/ra buffs and /ra buffs item: the shield's timers.</summary>
    private void PrintShieldSpellTimers()
    {
        if (_shieldSpellTimers.Count == 0) return;
        _host.WriteToChat($"[RynthAi] -- Shield spells ({_shieldSpellTimers.Count}) --", 1);
        foreach (var kvp in _shieldSpellTimers)
        {
            var info = kvp.Value;
            int id = unchecked((int)(uint)(kvp.Key >> 32));
            TimeSpan left = info.ExpiresAt - RynthClock.Now;
            _host.WriteToChat(left.TotalSeconds <= 0
                ? $"[RynthAi]   {ItemName(id)}: {info.SpellName} (Lvl {info.SpellLevel}): EXPIRED"
                : $"[RynthAi]   {ItemName(id)}: {info.SpellName} (Lvl {info.SpellLevel}): {Math.Round(left.TotalMinutes, 1)}m left / {Math.Round((info.ExpiresAt - info.CastAt).TotalMinutes, 0)}m total", 1);
        }
    }

    /// <summary>Shield spell timers recorded (tests, status).</summary>
    internal int ShieldSpellCount => _shieldSpellTimers.Count;
}
