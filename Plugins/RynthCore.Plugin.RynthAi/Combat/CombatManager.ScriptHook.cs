using System;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.Combat;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// The attack seam of the script-provided combat hook (RynthAi.Combat, CombatHookInterface.cs):
// with a script holding the attack kind, AttackWithMagic asks it for the spell after its debuffs
// and before its own shape choice. RynthAi keeps everything around the cast: the scan, the
// target lock, the stance, the cadence and busy gates, and the cast bookkeeping. The script only
// names the spell (and optionally another scanned monster, or the player).
public partial class CombatManager
{
    // True on a tick where AttackWithMagic sent nothing because it is waiting on the script's
    // answer: Think then ends the tick without stamping lastAttackCmd (like the wall hold).
    private bool _scriptHeld;

    /// <summary>The player's vitals, for the hook's request (set by the plugin; null in tests that don't need it).</summary>
    internal PlayerVitalsCache? Vitals { get; set; }

    /// <summary>The fight target's last known health ratio (0-1), or -1 when unknown or no target.</summary>
    internal float FightTargetHealthRatio => activeTargetId == 0 ? -1f : _worldFilter.GetHealthRatio(activeTargetId);

    /// <summary>Host tests: true after a tick that held for the script's answer.</summary>
    internal bool ScriptHeld => _scriptHeld;

    /// <summary>
    /// True when the hook handled this attack cycle: held for the answer, or cast the script's
    /// spell. False = RynthAi chooses as usual (no hook, a decline, a rejected answer, a timeout).
    /// </summary>
    private bool TryScriptAttack(WorldObject target, string element)
    {
        if (!CombatHook.IsActive(RynthAiCombat.KindAttack)) return false;   // no hook: no per-tick lambdas
        var slot = CombatHook.Attack;
        uint forTarget = unchecked((uint)activeTargetId);
        var outcome = CombatHook.Step(slot, forTarget, () => BuildAttackRequest(target), s => _host.Log(s),
            out uint spell, out uint castTarget, out uint flags);
        switch (outcome)
        {
            case CombatHook.Outcome.Native:
                return false;
            case CombatHook.Outcome.Wait:
                _scriptHeld = true;
                LastCombatSkipReason = "script-wait"; // D4 record-only
                return true;
        }

        if (castTarget == 0) castTarget = forTarget;
        string? why = ScriptAttackProblem(spell, castTarget);
        if (why != null)
        {
            CombatHook.Reject(slot, why, s => _host.Log(s));
            return false;
        }
        // A spell that costs more than max mana can never be paid, so the mana hold below would
        // never lift: rejected, and RynthAi chooses its own spell this same tick.
        bool affordable = true;
        try { affordable = ManaCanEverAfford?.Invoke((int)spell) ?? true; } catch { affordable = true; }
        if (!affordable)
        {
            CombatHook.Reject(slot, "costs more than max mana", s => _host.Log(s));
            return false;
        }
        // RynthAi's mana reserve holds the script's spell as it holds its own (ManaHoldsCast: mana
        // under the spell's cost, or under the reserve kept for Stamina to Mana): nothing is sent,
        // Think ends the tick on _manaCastHeld, and the answer is kept for the next tick. It is not
        // a rejection, so RynthAi does not cast a spell of its own choosing instead. Once the
        // request is past its deadline + StaleGraceMs, Step drops the answer and asks again.
        if (ManaHoldsCast((int)spell)) return true;
        if (!IssueScriptCast((int)spell, castTarget, flags, element))
        {
            CombatHook.Reject(slot, "the client refused the cast", s => _host.Log(s));
            return false;
        }
        CombatHook.Cast(slot, spell, castTarget, ActionPacer.LastCastSeqAtSend);
        return true;
    }

    /// <summary>Why the script's answer can't be cast, or null when it can.</summary>
    private string? ScriptAttackProblem(uint spell, uint castTarget)
    {
        if (_spellManager == null || !_spellManager.IsKnownSpellId((int)spell)) return $"spell {spell} isn't known";
        // A script spell the server refused outright (no components): parked like a vital level.
        if (_scriptParkedUntil.TryGetValue((int)spell, out DateTime parked) && RynthClock.Now < parked)
            return $"spell {spell} was refused by the server, parked for {(parked - RynthClock.Now).TotalSeconds:0} s more";
        uint player = _playerId != 0 ? _playerId : _host.GetPlayerId();
        if (castTarget == player) return null;
        // Monster rule Priority 0 = IGNORE: not a target for a script either, even one attacking us.
        // Checked before the fight-target match: a fight target whose rule changed to 0 is still
        // the active target until Think drops it.
        if (IsIgnored((int)castTarget)) return "the target is ignored (monster rule Priority 0)";
        bool listed = castTarget == unchecked((uint)activeTargetId);
        for (int i = 0; !listed && i < _scannedTargets.Count; i++)
            if (unchecked((uint)_scannedTargets[i].Id) == castTarget) listed = true;
        if (!listed) return "the target is not the fight target, a scanned monster or the player";
        // The same veto as RynthAi's own casts: never offensive magic at something AC says isn't attackable.
        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(castTarget)) return "the target is not attackable";
        return null;
    }

    private CombatRequestV1 BuildAttackRequest(WorldObject target)
    {
        var v = Vitals;
        bool vitalsKnown = v != null && v.MaxHealth > 0;
        return new CombatRequestV1
        {
            TargetHealth = _worldFilter.GetHealthRatio(activeTargetId),
            HealthPct = vitalsKnown ? v!.HealthPct : -1,
            StaminaPct = vitalsKnown && v!.MaxStamina > 0 ? v.StaminaPct : -1,
            ManaPct = vitalsKnown && v!.MaxMana > 0 ? v.ManaPct : -1,
            HealLine = 0,
            InCombat = 1,
            CombatMode = CurrentCombatMode,
        };
    }

    /// <summary>
    /// Sends the script's spell with RynthAi's own cast bookkeeping: the cadence guard
    /// (MarkCombatCastIssued), the attack interval and the area flag. The pending-offensive
    /// judgement (blacklist on no damage) is armed only for a single-target cast at the fight
    /// target without NOJUDGE. A damaging cast at the fight target (not NOJUDGE) is tagged for
    /// the Damage tab like RynthAi's own: wielded wand, element and level from the spell, the
    /// fight's cast count and the last-fight snapshot; a single-target one also runs the
    /// kill-shot prediction (EvaluateKillShot), as RynthAi's own cast does.
    /// </summary>
    private bool IssueScriptCast(int spellId, uint castTarget, uint flags, string fallbackElement)
    {
        bool area = (flags & RynthAiCombat.FlagArea) != 0;
        bool noJudge = (flags & RynthAiCombat.FlagNoJudge) != 0;
        string name = SpellTableStub.GetById(spellId)?.Name ?? $"spell {spellId}";
        bool ok;
        try { ok = _host.CastSpellFor(castTarget, spellId, "Combat", name + " (script)"); }
        catch { ok = false; }
        if (!ok) return false;
        ActionPacer.MarkScriptCast(spellId);

        MarkCombatCastIssued();
        StartCastChatTrace(spellId, unchecked((int)castTarget));   // the server's answers, logged as for RynthAi's own casts
        SessionAttackCasts++;
        CastsSinceLastKill++;
        _lastSpellCast = RynthClock.Now;
        _scriptCastSpellId = spellId;
        _scriptCastAt = RynthClock.Now;
        _scriptCastSent = ActionPacer.CastsSent;   // includes this cast: any later send changes it
        bool atFight = castTarget == unchecked((uint)activeTargetId);
        bool judged = atFight && !area && !noJudge;
        _lastCastWasRing = !judged;   // anything else gets no per-target judgement
        if (atFight) NoteCastSent(activeTargetId);   // the in-flight hold (Target Lock off)
        _wallFaceAt = DateTime.MinValue;
        if (judged)
        {
            // What the NO DAMAGE report names as the last cast (ReportNoDamageCasts).
            _lastOffensiveCastLine = $"script id={spellId} '{name}' target=0x{castTarget:X8}";
            _pendingOffensiveSpellId  = spellId;
            _pendingOffensiveCastAt   = RynthClock.Now;
            _pendingOffensiveTargetId = activeTargetId;
            NoteOffensiveSeq();
        }
        // As the native block: a throw here must not fail the cast that already went out (the
        // slot would stay Decided and the same answer be cast again).
        // A single-target damaging cast at the fight target also takes part in RynthAi's kill-shot
        // swap (Think arms it, the deferred swap moves on while the projectile flies), with the
        // weapon, element and tier just tagged. Area, nojudge and off-target casts never do.
        _predictKillSwap = false;
        if (atFight && !noJudge)
        {
            try
            {
                TagScriptCast(spellId, area, fallbackElement);
                if (!area) EvaluateKillShot(_lastCastWeaponId, _lastCastElement, _lastCastTier, isRing: false);
            }
            catch { }
        }
        LastCombatSkipReason = "script-cast"; // D4 record-only
        string tags = (area ? " (area)" : "") + (noJudge ? " (nojudge)" : "");
        _host.Log($"[CombatCast] script id={spellId} '{name}' at 0x{castTarget:X8}{tags} " +
                  $"fight=0x{activeTargetId:X8} owner='{CombatHook.Owner}'");
        return true;
    }

    /// <summary>
    /// The damage attribution RynthAi's own cast does (AttackWithMagic), for a script's cast at
    /// the fight target: the weapon, element and tier the pushed damage is filed under, the
    /// Damage tab's latest tier, the casts-to-kill count and the last-fight snapshot.
    /// </summary>
    private void TagScriptCast(int spellId, bool area, string fallbackElement)
    {
        int castWand = InHandWeaponId();
        if (castWand == 0 || !IsWandObject(_worldFilter[castWand] ?? new WorldObject(0, ""))) castWand = FindWandInItems();
        _lastCastWeaponId = unchecked((uint)castWand);
        // No element in the spell (a Life drain, say) on a caster with no War or Void: the plan's
        // element (a weapon-weakness pick like "Slash") would be wrong, so it is filed under
        // "Physical", the key RynthAi's own damage path already uses for vital drains.
        _lastCastElement = ScriptSpellElement(spellId) ?? (AttackMagicTrained ? fallbackElement : "Physical");
        int castLevel = SpellTableStub.GetById(spellId)?.Level ?? 0;
        if (castLevel <= 0 && SpellDatabase.TryGetCategoryAndPower(spellId, out _, out int castPower))
            castLevel = SpellDatabase.LevelFromPower(castPower);
        if (castLevel <= 0)
            castLevel = Math.Max(_spellManager?.GetHighestSpellTier(AcSkillType.WarMagic) ?? 0,
                                 _spellManager?.GetHighestSpellTier(AcSkillType.VoidMagic) ?? 0);
        _lastCastTier = area ? -castLevel : castLevel;   // area casts filed with the rings (negative)
        if (_fightTargetWcid != 0) _damageStore?.NoteCast(_fightTargetWcid, _lastCastTier, _fightTargetName);
        _fightCastCount++;
        if (_fightTargetWcid != 0)
        {
            _lastFightWcid     = _fightTargetWcid;
            _lastFightName     = _worldFilter[activeTargetId]?.Name ?? _fightTargetName;
            _lastFightWeaponId = _lastCastWeaponId;
            _lastFightElement  = _lastCastElement;
            _lastFightTier     = _lastCastTier;
            _lastFightCount    = _fightCastCount;
            StampRecentFight();
        }
    }

    /// <summary>
    /// The element of a war/void attack spell, from RynthAi's own shape tables (base name, tier-7
    /// lore names, ring lore names, wall categories), else from its name's words; null when
    /// none of those says.
    /// </summary>
    internal static string? ScriptSpellElement(int spellId)
    {
        string? name = SpellTableStub.GetById(spellId)?.Name;
        if (string.IsNullOrEmpty(name)) return null;
        string n = name.Trim();
        if (n.StartsWith("Incantation of ", StringComparison.OrdinalIgnoreCase)) n = n.Substring(15).Trim();
        int sp = n.LastIndexOf(' ');
        if (sp > 0 && IsRoman(n.Substring(sp + 1))) n = n.Substring(0, sp);

        // Blade and Slash share their spells: answer Slash (the Damage tab's element name).
        foreach (var kv in SpellShapes)
            if (!kv.Key.Equals("Blade", StringComparison.OrdinalIgnoreCase)
                && Array.Exists(kv.Value, b => b.Equals(n, StringComparison.OrdinalIgnoreCase)))
                return kv.Key;
        foreach (var table in new[] { WarTier7Lore, RingLoreNames })
            foreach (var kv in table)
                if (!kv.Key.Equals("Blade", StringComparison.OrdinalIgnoreCase)
                    && Array.Exists(kv.Value, b => b.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return kv.Key;
        if (SpellDatabase.TryGetCategoryAndPower(spellId, out int category, out _))
            foreach (var kv in WallCategories)
                if (kv.Value == category && !kv.Key.Equals("Blade", StringComparison.OrdinalIgnoreCase)) return kv.Key;
        string byWords = PetChoice.ElementFromName(name);
        return byWords.Length > 0 ? byWords : null;

        static bool IsRoman(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if (c != 'I' && c != 'V' && c != 'X') return false;
            return true;
        }
    }

    // -- Refusal memory for script casts --

    // The script's last cast, while it is still the last cast this plugin sent (ActionPacer counts
    // every send: combat, buffs and heals, nav, commands, meta): a hard refusal arriving soon after
    // it is the script spell's, and parks that spell. Forgotten once any other cast goes out, the
    // spell's own success line arrives, or the window passes.
    private int _scriptCastSpellId;
    private DateTime _scriptCastAt = DateTime.MinValue;
    private long _scriptCastSent;
    private readonly System.Collections.Generic.Dictionary<int, DateTime> _scriptParkedUntil = new();

    /// <summary>How long a script spell the server refused outright is rejected (as BuffManager parks a vital level).</summary>
    internal const double ScriptParkSec = 600.0;
    /// <summary>A refusal later than this after the script's cast is not attributed to it.</summary>
    internal const double ScriptRefusalWindowMs = 2_000.0;

    /// <summary>Host tests: true while <paramref name="spellId"/> is parked after a refusal.</summary>
    internal bool ScriptSpellParked(int spellId) => _scriptParkedUntil.TryGetValue(spellId, out DateTime u) && RynthClock.Now < u;

    /// <summary>A chat line: park the script's last spell on a hard refusal (missing components, unknown spell).</summary>
    private void NoteScriptCastRefusal(string text)
    {
        if (_scriptCastSpellId == 0) return;
        if (ActionPacer.CastsSent != _scriptCastSent
            || (RynthClock.Now - _scriptCastAt).TotalMilliseconds > ScriptRefusalWindowMs) { _scriptCastSpellId = 0; return; }
        string lo = text.ToLowerInvariant();
        string? own = SpellTableStub.GetById(_scriptCastSpellId)?.Name;
        if (own != null && lo.Contains("ou cast " + own.ToLowerInvariant())) { _scriptCastSpellId = 0; return; }   // it landed
        if (!(lo.Contains("missing some required") || lo.Contains("you do not have the")
              || lo.Contains("have all the components for this spell"))) return;
        int spell = _scriptCastSpellId;
        _scriptCastSpellId = 0;
        _scriptParkedUntil[spell] = RynthClock.Now.AddSeconds(ScriptParkSec);
        _host.Log($"[CombatCast] script spell id={spell} '{SpellTableStub.GetById(spell)?.Name}' refused: '{text.Trim()}'. " +
                  $"Rejected for {ScriptParkSec / 60:0} min (RynthAi chooses instead while the script asks for it).");
    }
}
