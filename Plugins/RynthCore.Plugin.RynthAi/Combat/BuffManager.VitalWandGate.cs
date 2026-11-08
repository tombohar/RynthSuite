// ============================================================================
//  RynthSuite - Combat/BuffManager.VitalWandGate.cs
//  No wand swap for a vital spell while a kit or potion can do the job.
//
//  Kits are the standard heal, and the shield stays on: putting the shield away for a wand costs more
//  damage than a small heal spell buys back. A melee character under Heal At used to put its shield away
//  and pull a wand for Heal Self with kits in the pack (the wand was skipped only at Emergency Heal At).
//
//  Out of Magic mode with no caster in hand, a vital spell (Heal Self, Stamina to Health, Revitalize,
//  Stamina to Mana, the script hook's heal) would start a weapon swap to the wand. VitalSpellWouldSwap
//  refuses it (CastVital: not handled), so the chain falls through to the potion, the kit, and the kit
//  despite Kit Min Success. The exceptions:
//  - nothing else can restore that vital (no usable kit, no potion Use Potions may drink): the swap is
//    the only heal there is, so it goes;
//  - a caster already wielded needs only the stance change (no weapon moves): allowed;
//  - no wand found (FindWandInItems 0): the Magic stance goes bare-handed and no weapon moves: allowed;
//  - a swap already under way finishes (refusing it mid-way left the shield in the pack);
//  - health at or under Emergency Heal At (2026-11-07): a kit at low health and vitae can be a 5-8%
//    chance, and refusing the spell there killed Drakkon; the surest heal goes;
//  - Stamina to Mana never swaps in a fight (InCombatNow), potion or not; out of a fight it may, even
//    with a mana potion carried (buffing is about to swap to the wand anyway).
//  Buffing (EnsureMagicMode forBuff) is not gated and still swaps, and so does the HP failsafe's
//  Lifestone Recall (it calls EnsureMagicMode itself, with the macro off).
//
//  A kit the gate counts is one the kit picks would use: the live pack walk (ConsumableLookup,
//  GetDirectInventory), minus a kit its line called used up (BuffManager.KitUsedUp.cs), minus a kit the
//  server refused KitRefusedStreakMax times within KitRefusedStreakMs (a UseDone error or "You're too
//  busy!" is no answer the kit wait drops, so such a kit would block every spell heal forever). A streak
//  with no refusal for KitRefusedStreakMs ages out; a kit landing, or the macro stopping, ends them all.
//
//  The stamina sources share one order with a last resort (AttemptStaminaRestore): with Revitalize
//  refused here (or not cast at all for the between-fights top-off), a stamina kit skipped for Stamina
//  Kit Min Success % would leave nothing.
//
//  Logging: one "wand gate" line when the gate starts refusing a vital's spell, one when that ends (the
//  swap is allowed again, the spell isn't asked for WandGateQuietMs, or the macro stops), and one when a
//  refused kit stops counting.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    internal const int KitRefusedStreakMax = 3;
    internal const double KitRefusedStreakMs = 10_000;
    /// <summary>A refusal episode with no refusal this long is over (logged).</summary>
    internal const double WandGateQuietMs = 5000;
    private readonly Dictionary<int, (int Count, DateTime First, DateTime Last)> _kitRefusedStreak = new();
    // The vitals whose spell the gate is refusing now (by label): the spell's name and the last refusal.
    private readonly Dictionary<string, (string What, DateTime Last)> _wandGateRefusing = new();

    /// <summary>Times the gate refused a vital spell's wand swap (tests, status).</summary>
    internal int WandGateRefusals { get; private set; }

    /// <summary>True when the vital spell for <paramref name="label"/> must not start a wand swap now (see the header).</summary>
    private bool VitalSpellWouldSwap(string label, string what, out string why)
    {
        why = "";
        string allowed;
        if (_wandSwap.Active) allowed = "a wand swap is already under way";
        else if (CurrentCombatMode == CombatMode.Magic) allowed = "in Magic stance";
        else
        {
            int wandId = FindWandInItems();
            if (wandId == 0) allowed = "no wand found (bare-handed Magic)";
            else if (IsBuffWandWielded(wandId)) allowed = "a caster is in hand (the stance change only)";
            else if (label == LabelMana)
            {
                if (!InCombatNow()) allowed = "out of a fight";
                else
                {
                    why = "no wand swap for Stamina to Mana in a fight (the shield stays on)";
                    return NoteWandGateRefusal(label, what, "Stamina to Mana never swaps in a fight");
                }
            }
            else if (label != LabelStamina && _vitals.HealthPct <= _settings.EmergencyHealAt)
            {
                // At Emergency Heal At the surest heal goes: a kit's success chance falls with health and vitae
                // (Drakkon's was 5-8% at low health), and at 9% the gate sent that kit instead of Stamina to
                // Health and he died (2026-11-07).
                allowed = $"health {_vitals.HealthPct}% at or under Emergency Heal At {_settings.EmergencyHealAt}%";
            }
            else
            {
                bool stamina = label == LabelStamina;
                bool kit =stamina ? HasUsableStaminaKit() : HasUsableHealthKit();   // Healing and the script heal
                bool potion = !kit && (stamina ? HasAnyPotion("StaminaPotion", "Stamina") : HasAnyPotion("HealthPotion"));
                if (!kit && !potion) allowed = $"no {(stamina ? "stamina" : "healing")} kit or potion is carried";
                else
                {
                    why = "no wand swap for a vital spell (kits are the standard heal; the shield stays on)";
                    return NoteWandGateRefusal(label, what, kit ? "a kit is carried" : "a potion is carried");
                }
            }
        }
        if (_wandGateRefusing.Remove(label, out var ep))
            _host.Log($"[RynthAi] Vitals: wand gate ended for {ep.What}: {allowed}; its wand swap may go.");
        return false;
    }

    /// <summary>A refusal: logged when an episode for that vital starts. Always true (the caller returns it).</summary>
    private bool NoteWandGateRefusal(string label, string what, string because)
    {
        WandGateRefusals++;
        if (!_wandGateRefusing.ContainsKey(label))
            _host.Log($"[RynthAi] Vitals: wand gate: no wand swap for {what} - {because} " +
                      $"(health {_vitals.HealthPct}%, stamina {_vitals.StaminaPct}%, mana {_vitals.ManaPct}%); the kit or potion goes instead.");
        _wandGateRefusing[label] = (what, Now);
        return true;
    }

    /// <summary>Every heartbeat (macro running): an episode with no refusal for WandGateQuietMs is over.</summary>
    private void TickWandGateLog()
    {
        if (_wandGateRefusing.Count == 0) return;
        List<string>? done = null;
        foreach (var kv in _wandGateRefusing)
            if ((Now - kv.Value.Last).TotalMilliseconds >= WandGateQuietMs) (done ??= new()).Add(kv.Key);
        if (done == null) return;
        foreach (string label in done)
        {
            _host.Log($"[RynthAi] Vitals: wand gate ended for {_wandGateRefusing[label].What}: not asked for in {WandGateQuietMs / 1000:0} s.");
            _wandGateRefusing.Remove(label);
        }
    }

    /// <summary>Ends every refusal episode (the macro stopped), logged.</summary>
    private void EndWandGateEpisodes(string why)
    {
        if (_wandGateRefusing.Count == 0) return;
        foreach (var kv in _wandGateRefusing)
            _host.Log($"[RynthAi] Vitals: wand gate ended for {kv.Value.What}: {why}.");
        _wandGateRefusing.Clear();
    }

    private void NoteKitRefusedStreak(int kitId)
    {
        if (kitId == 0) return;
        DateTime now = Now;
        PruneKitRefusedStreaks(now);
        var next = _kitRefusedStreak.TryGetValue(kitId, out var s) && (now - s.First).TotalMilliseconds <= KitRefusedStreakMs
            ? (s.Count + 1, s.First, now)
            : (1, now, now);
        _kitRefusedStreak[kitId] = next;
        if (next.Item1 == KitRefusedStreakMax)
            _host.Log($"[RynthAi] Vitals: wand gate: kit 0x{(uint)kitId:X8} '{_kitInFlightName}' refused {KitRefusedStreakMax} times in " +
                      $"{KitRefusedStreakMs / 1000:0} s: it no longer counts as a heal, a vital spell may swap to the wand.");
    }

    /// <summary>Drops streaks with no refusal for KitRefusedStreakMs: the kit counts for the gate again.</summary>
    private void PruneKitRefusedStreaks(DateTime now)
    {
        if (_kitRefusedStreak.Count == 0) return;
        List<int>? stale = null;
        foreach (var kv in _kitRefusedStreak)
            if ((now - kv.Value.Last).TotalMilliseconds > KitRefusedStreakMs) (stale ??= new()).Add(kv.Key);
        if (stale != null) foreach (int id in stale) _kitRefusedStreak.Remove(id);
    }

    /// <summary>Ends every refusal streak (a kit landed, or the macro stopped).</summary>
    private void ClearKitRefusedStreaks() => _kitRefusedStreak.Clear();

    /// <summary>A kit the gate does not count: called used up, or refused KitRefusedStreakMax times with the last
    /// refusal under KitRefusedStreakMs ago.</summary>
    private bool KitOutForGate(int id)
    {
        if (KitUsedUp(id)) return true;
        PruneKitRefusedStreaks(Now);
        return _kitRefusedStreak.TryGetValue(id, out var s) && s.Count >= KitRefusedStreakMax;
    }

    /// <summary>A healing kit AttemptHealthKitUse could pick (a listed one, else the pack's).</summary>
    private bool HasUsableHealthKit()
    {
        if (_worldObjectCache == null) return false;
        foreach (var r in _settings.ConsumableRules)
            if (r.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase)
                && !(r.Name ?? "").Contains("Stamina Kit", StringComparison.OrdinalIgnoreCase)
                && ConsumableLookup.Resolve(_worldObjectCache, r, KitOutForGate) is WorldObject wo
                && IsUseOnSelfKit(wo))
                return true;
        return HasPackKit("Healing Kit");
    }

    /// <summary>A stamina kit AttemptStaminaKitUse could pick.</summary>
    private bool HasUsableStaminaKit() => _host.HasUseObjectOn && HasPackKit("Stamina Kit");

    /// <summary>A kit named "... <paramref name="suffix"/>" in the pack the gate still counts (BestPack*Kit's match).</summary>
    private bool HasPackKit(string suffix)
    {
        if (_worldObjectCache == null) return false;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
            if ((wo.Name ?? "").EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !KitOutForGate(wo.Id))
                return true;
        return false;
    }

    /// <summary>A potion of <paramref name="types"/> AttemptPotion could drink (listed or in the pack), with Use Potions on.</summary>
    private bool HasAnyPotion(params string[] types)
    {
        if (!_settings.UsePotions || _worldObjectCache == null) return false;
        if (HasListedPotion(types)) return true;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string? kind = LegacyWeaponsUi.PotionType(wo);
            if (kind == null || Array.FindIndex(types, t => t.Equals(kind, StringComparison.OrdinalIgnoreCase)) < 0) continue;
            if (IsKitName(wo.Name)) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The stamina sources in order, shared by the stamina step, the mana step's refill (AttemptManaRefill)
    /// and the buffing mana floor: a kit, a listed potion first when <paramref name="listedPotionFirst"/>,
    /// Revitalize (unless <paramref name="allowCast"/> is false: the between-fights top-off out of Magic),
    /// any potion, then the kit Stamina Kit Min Success % skipped as the last resort.
    /// </summary>
    private bool AttemptStaminaRestore(bool listedPotionFirst = false, bool allowCast = true)
    {
        if (AttemptStaminaKit(false, out bool underMinSuccess)
            || (listedPotionFirst && AttemptPotion(true, "StaminaPotion", "Stamina"))
            || (allowCast && AttemptVitalCast("Revitalize Self"))
            || AttemptPotion("StaminaPotion", "Stamina"))
            return true;
        // Last resort, as the heal chain's: the stamina kit Stamina Kit Min Success % skipped. With
        // Revitalize refused by the wand gate (or not cast) and no potion, nothing else restores stamina.
        if (underMinSuccess && AttemptStaminaKit(true, out _))
        {
            _host.Log("[RynthAi] Vitals: no spell or potion could restore stamina; using the stamina kit despite Stamina Kit Min Success %.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="wandId"/> (FindWandInItems' pick) is in the player's hand: EnsureMagicMode
    /// then needs no weapon move, only the stance change. Shared by EnsureMagicMode and VitalSpellWouldSwap.
    /// </summary>
    private bool IsBuffWandWielded(int wandId)
    {
        if (wandId == 0) return false;
        // Primary check: CurrentWieldedLocation (stype=10) - works even before the
        // phys-obj offset probe fires, mirrors CombatManager.EquipWeaponAndSetStance.
        bool alreadyWielded = false;
        var wandObj = _worldObjectCache?[wandId];
        if (wandObj != null)
            alreadyWielded = wandObj.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        // Secondary check via API if primary didn't confirm
        if (EquippedState.IsWielded(_host, wandId) is bool trackedWand) alreadyWielded = trackedWand;   // the server's word
        else if (!alreadyWielded && _host.HasGetObjectWielderInfo)
        {
            uint playerId = _host.GetPlayerId();
            if (playerId != 0 &&
                _host.TryGetObjectWielderInfo((uint)wandId, out uint wielder, out _) &&
                wielder == playerId && _worldObjectCache?.InPackWalk(wandId) != true)   // a stale record (the wand in a pack)
                alreadyWielded = true;
        }
        return alreadyWielded;
    }
}
