// ============================================================================
//  RynthSuite - Combat/CombatManager.DirtyFighting.cs
//  Dirty Fighting rotation (2026-11-11): melee and missile attacks aim at the
//  height whose Dirty Fighting effect the target is missing, in the player's
//  order (High, Medium, Low by default), then at a chosen default height once
//  all three are on, and back again when one is about to run out.
//
//  What is on a target comes from this character's own landing lines in
//  combat chat ("Dirty Fighting! <me> delivers a <spell> to <target>!"), the
//  only signal the server sends the attacker; the line names the target, so it
//  is matched to the target being fought by name. Durations are the server's
//  20 s. The height choice itself is DirtyFightingHeights.Choose (pure, tested).
//  Off, or with Dirty Fighting untrained, the fixed Attack Height applies.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    private readonly DirtyFightingTracker _dfTracker = new();
    private string _dfOwnName = string.Empty;
    private uint _dfOwnNameFor;
    // The last height the rotation picked, per target, so a change is logged once.
    private int _dfLoggedTarget;
    private int _dfLoggedHeight = -1;

    /// <summary>Dirty Fighting trained or specialized (an unread skill table counts as not).</summary>
    internal bool DirtyFightingTrained =>
        _charSkills != null && _charSkills[AcSkillType.DirtyFighting].Training >= 2;

    private string OwnNameForDf()
    {
        if (_playerId == 0) return string.Empty;
        if (_dfOwnNameFor == _playerId && _dfOwnName.Length > 0) return _dfOwnName;
        string name = string.Empty;
        try
        {
            if (_host.HasGetObjectName && _host.TryGetObjectName(_playerId, out string n)) name = n ?? string.Empty;
        }
        catch { }
        if (name.Length == 0) name = _worldFilter[unchecked((int)_playerId)]?.Name ?? string.Empty;
        _dfOwnName = name;
        _dfOwnNameFor = _playerId;
        return name;
    }

    /// <summary>
    /// Every chat line (RynthAiPlugin.OnChatWindowText): a landing line of ours records the effect
    /// on the target it names, when that is the target being fought (or the one the server's
    /// attack is bound to). Recorded whether or not the rotation is on, so the status shows it.
    /// </summary>
    public void NoteDirtyFightingChat(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("Dirty Fighting! ", StringComparison.Ordinal)) return;
        if (!DirtyFightingHeights.TryParseLanding(text, OwnNameForDf(), out int height, out string targetName)) return;

        int id = 0;
        foreach (int candidate in new[] { activeTargetId, _serverAttackTargetId })
        {
            if (candidate == 0) continue;
            if (string.Equals(_worldFilter[candidate]?.Name, targetName, StringComparison.Ordinal)) { id = candidate; break; }
        }
        if (id == 0)
        {
            _host.Log($"[DFHeight] {DirtyFightingHeights.HeightName(height)} landed on '{targetName}', not the target being fought: not tracked");
            return;
        }
        _dfTracker.Landed(id, height, RynthClock.Now);
        _host.Log($"[DFHeight] {DirtyFightingHeights.HeightName(height)} landed on 0x{unchecked((uint)id):X8} '{targetName}' " +
                  $"({_dfTracker.Get(id)?.Describe(_settings.DfHeightOrder, RynthClock.Now)})");
    }

    /// <summary>The height (0 Low, 1 Medium, 2 High) this attack at the active target aims at.</summary>
    private int AttackHeightForTarget(bool isMissile)
    {
        int fixedHeight = isMissile ? _settings.MissileAttackHeight : _settings.MeleeAttackHeight;
        if (!_settings.DfHeightRotation) return Math.Clamp(fixedHeight, 0, 2);

        int target = activeTargetId;
        DateTime now = RynthClock.Now;
        int h = DirtyFightingHeights.Choose(true, DirtyFightingTrained, fixedHeight, _settings.DfHeightOrder,
            _settings.DfDefaultHeight, _dfTracker.Get(target), now);
        if (target != _dfLoggedTarget || h != _dfLoggedHeight)
        {
            _dfLoggedTarget = target;
            _dfLoggedHeight = h;
            _host.Log($"[DFHeight] 0x{unchecked((uint)target):X8} '{_worldFilter[target]?.Name}': attacking {DirtyFightingHeights.HeightName(h)}" +
                      (DirtyFightingTrained ? $" ({_dfTracker.Get(target)?.Describe(_settings.DfHeightOrder, now) ?? "nothing on it yet"})"
                                            : " (Dirty Fighting not trained: the fixed height)"));
        }
        return h;
    }

    /// <summary>
    /// The server's chance per damaging hit for this character: 25%, less when Dirty Fighting is
    /// below the best weapon skill for the current stance (an estimate: the server uses the
    /// wielded weapon's skill).
    /// </summary>
    private double DirtyFightingChance()
    {
        if (_charSkills == null || !DirtyFightingTrained) return 0;
        int df = _charSkills[AcSkillType.DirtyFighting].Buffed;
        bool missile = CurrentCombatMode == CombatMode.Missile;
        int best = 0;
        foreach (AcSkillType s in missile
                     ? new[] { AcSkillType.MissileWeapons }
                     : new[] { AcSkillType.HeavyWeapons, AcSkillType.LightWeapons, AcSkillType.FinesseWeapons, AcSkillType.TwoHandedCombat })
        {
            var info = _charSkills[s];
            if (info.Training >= 2 && info.Buffed > best) best = info.Buffed;
        }
        return DirtyFightingHeights.ChancePerHit(true, df, best);
    }

    /// <summary>One line for the settings page and /ra dfheight status.</summary>
    public string DirtyFightingStatus()
    {
        if (!DirtyFightingTrained)
            return "Dirty Fighting is not trained: attacks use the fixed Attack Height.";
        string head = (_settings.DfHeightRotation ? "On" : "Off") +
                      $", order {DirtyFightingHeights.OrderLabel(_settings.DfHeightOrder)}, then " +
                      (_settings.DfDefaultHeight >= 0 ? DirtyFightingHeights.HeightName(_settings.DfDefaultHeight) : "the Attack Height setting") +
                      $"; about {DirtyFightingChance() * 100:0}% per hit.";
        int target = activeTargetId;
        if (target == 0) return head + " No target.";
        DateTime now = RynthClock.Now;
        DfTargetEffects? fx = _dfTracker.Get(target);
        string on = fx?.Describe(_settings.DfHeightOrder, now) ?? "nothing landed yet";
        string next = _settings.DfHeightRotation
            ? $"; next attack {DirtyFightingHeights.HeightName(AttackHeightPreview())}"
            : string.Empty;
        return $"{head} Target '{_worldFilter[target]?.Name}': {on}{next}.";
    }

    /// <summary>The height the rotation would pick now, without logging (the status line).</summary>
    private int AttackHeightPreview()
    {
        bool isMissile = CurrentCombatMode == CombatMode.Missile;
        int fixedHeight = isMissile ? _settings.MissileAttackHeight : _settings.MeleeAttackHeight;
        return DirtyFightingHeights.Choose(_settings.DfHeightRotation, DirtyFightingTrained, fixedHeight,
            _settings.DfHeightOrder, _settings.DfDefaultHeight, _dfTracker.Get(activeTargetId), RynthClock.Now);
    }
}
