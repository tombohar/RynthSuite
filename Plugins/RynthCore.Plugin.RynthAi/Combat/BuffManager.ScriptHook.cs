using System;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// The heal seam of the script-provided combat hook (RynthAi.Combat, CombatHookInterface.cs):
// CheckVitals asks the script for the heal spell (and its target: the player, or a monster for a
// drain) before kits. The cast itself goes through CastVital, the same steps as Heal Self.
public partial class BuffManager
{
    /// <summary>
    /// True when the hook handled this vitals check: waiting for the answer, or the answer was
    /// cast (or its wand swap is in flight). False = the native order (kit, Heal Self, potion).
    /// </summary>
    private bool TryScriptHeal(int healLine, bool inCombat)
    {
        // AC just said "You're too busy!": the same pacing as a Heal Self (AttemptVitalCast).
        if (RynthClock.Now < _busyBackoffUntil && (RynthClock.Now - _lastCastAttempt).TotalMilliseconds < BusyHealRetryMs)
        {
            _vitalWhy = $"AC too busy, waiting {(_busyBackoffUntil - RynthClock.Now).TotalMilliseconds:0} ms";
            _healWaitKind = "too-busy backoff";
            return true;
        }

        var slot = CombatHook.Heal;
        // The request names RynthAi's fight target when it opened (informational: the monster with
        // live health data, e.g. for a drain). A fight target change does not re-ask (the heal keeps
        // its deadline); the answer's target is checked as things stand when it comes. The
        // answer's target 0 still means the player.
        var cm = _combatManager;
        uint fight = cm == null ? 0u : unchecked((uint)cm.activeTargetId);
        var outcome = CombatHook.Step(slot, fight, () => BuildHealRequest(healLine, inCombat), s => _host.Log(s),
            out uint spell, out uint target, out _);
        switch (outcome)
        {
            case CombatHook.Outcome.Native:
                return false;
            case CombatHook.Outcome.Wait:
                _vitalWhy = $"waiting for '{CombatHook.Owner}' to choose the heal";
                _healWaitKind = "script choosing the heal";
                return true;
        }

        uint player = (uint)_host.GetPlayerId();
        if (target == 0) target = player;
        string? why = ScriptHealProblem(spell, target, player);
        if (why != null)
        {
            CombatHook.Reject(slot, why, s => _host.Log(s));
            return false;
        }
        string name = SpellTableStub.GetById((int)spell)?.Name ?? $"spell {spell}";
        bool handled = CastVital((int)spell, target, LabelHealing, name + " (script)", out bool sent);
        if (!handled)
        {
            CombatHook.Reject(slot, _vitalWhy.Length > 0 ? _vitalWhy : "it couldn't be cast", s => _host.Log(s));
            return false;
        }
        // A script-chosen heal takes no early nudge (HeldNudgeUrgent): its result can come later
        // than RynthAi's own heals' (Vitality Siphon: 2.42-2.75 s after the incantation, 2026-10-10
        // log), or never (a resisted or dead target), and an early nudge steps the character.
        if (sent && _cast.Current is { } sentCast && sentCast.SpellId == (int)spell) sentCast.Script = true;
        if (sent) ActionPacer.MarkScriptCast((int)spell);   // the stuck-cast breaker gives it the same room
        // Handled: cast, or a swap step to Magic mode is in flight (the next check asks again).
        CombatHook.Cast(slot, sent ? spell : 0, sent ? target : 0, sent ? ActionPacer.LastCastSeqAtSend : -1);
        if (sent) _host.Log($"[RynthAi] Vitals: script heal id={spell} '{name}' at 0x{target:X8} (owner '{CombatHook.Owner}').");
        return true;
    }

    /// <summary>Why the script's heal can't be cast, or null when it can.</summary>
    private string? ScriptHealProblem(uint spell, uint target, uint player)
    {
        if (!_spellManager.IsKnownSpellId((int)spell)) return $"spell {spell} isn't known";
        // The refusal memory RynthAi's own heal honours (AttemptVitalCast): a level the server
        // said there are no components for.
        if (LevelLacksComponents((int)spell)) return $"spell {spell} has no components (refused by the server; RynthAi uses a lower level)";
        if (target == player) return null;
        // A heal cast at anything but the player has to take health from the target and give it
        // to the caster (a vital transfer: Drain Health, Vitality Siphon...). Any other spell at a
        // monster heals or buffs the monster.
        if (!IsDrainToCaster((int)spell)) return $"spell {spell} at a monster is not a drain (a heal may target a monster only with a vital transfer to the caster)";
        var cm = _combatManager;
        // Monster rule Priority 0 = never attacked: a drain at one is refused too, even when it is
        // still the fight target (its rule changed before Think dropped it) or still scanned.
        if (cm != null && cm.IsIgnored(unchecked((int)target))) return "the target is ignored (monster rule Priority 0)";
        bool listed = cm != null && unchecked((uint)cm.activeTargetId) == target;
        if (!listed && cm != null)
            foreach (var t in cm.ScannedTargets)
                if (unchecked((uint)t.Id) == target) { listed = true; break; }
        if (!listed) return "the target is not the player, the fight target or a scanned monster";
        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(target)) return "the target is not attackable";
        return null;
    }

    /// <summary>
    /// True for a vital transfer from the target to the caster (Drain Health Other, Vitality
    /// Siphon, Soul Shroud, Drain Mana/Stamina...), read from the spell catalog: a
    /// "...Transfertocaster" family, cast on Other, described as taking the target's vital and
    /// giving it to the caster. A spell the catalog doesn't have (a custom server's) counts by its
    /// name: "Drain " or "Siphon".
    /// </summary>
    internal static bool IsDrainToCaster(int spellId)
    {
        SpellCatalog.EnsureLoaded();
        var e = SpellCatalog.Get(spellId);
        if (e != null)
            return e.FamilyName.EndsWith("Transfertocaster", StringComparison.OrdinalIgnoreCase)
                && e.Target.Equals("Other", StringComparison.OrdinalIgnoreCase)
                && e.Description.Contains("the target's", StringComparison.OrdinalIgnoreCase)
                && e.Description.Contains("to the caster", StringComparison.OrdinalIgnoreCase);
        string name = SpellTableStub.GetById(spellId)?.Name ?? "";
        return name.Contains("Drain ", StringComparison.OrdinalIgnoreCase) || name.Contains("Siphon", StringComparison.OrdinalIgnoreCase);
    }

    private CombatRequestV1 BuildHealRequest(int healLine, bool inCombat)
    {
        bool known = _vitals.MaxHealth > 0;
        var cm = _combatManager;
        float fightHealth = cm?.FightTargetHealthRatio ?? -1f;
        return new CombatRequestV1
        {
            TargetHealth = fightHealth,
            HealthPct = known ? _vitals.HealthPct : -1,
            StaminaPct = _vitals.MaxStamina > 0 ? _vitals.StaminaPct : -1,
            ManaPct = _vitals.MaxMana > 0 ? _vitals.ManaPct : -1,
            HealLine = healLine,
            InCombat = inCombat ? 1 : 0,
            CombatMode = CurrentCombatMode,
        };
    }
}
